using System.Globalization;
using System.Text;
using System.Text.Json;
using CommandLine;
using libgwmapi;
using MQTTnet;
using YamlDotNet.Serialization;
using libgwmapi.DTO.UserAuth;
using libgwmapi.DTO.Vehicle;
using Microsoft.Extensions.Logging;
using ora2mqtt.Logging;
using System.Text.Json.Serialization;

namespace ora2mqtt;

[Verb("run", true, HelpText = "default")]
public class RunCommand:BaseCommand
{
    private const string DefaultAcTemperature = "22";
    private const string DefaultAcOperationTime = "30";
    private const string LockCommandPayload = "LOCK";
    private const string UnlockCommandPayload = "UNLOCK";
    private const string WindowClosePayload = "PRESS";
    private const string RemoteCommandPendingResultCode = "2000";
    private const int RemoteCommandResultMaxPolls = 18;
    private const int RemoteCommandStatusMaxLength = 240;
    private static readonly TimeSpan RemoteCommandResultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiscoveryRepublishInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromMinutes(15);

    private ILogger _logger;
    private readonly Dictionary<string, AcSettings> _acSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastRemoteCommandStatuses = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _discoveryPublishRequested = true;
    private DateTime _lastDiscoveryPublishUtc = DateTime.MinValue;

    [Option('i', "interval", Default = 10, HelpText = "GWM API polling interval")]
    public int Intervall { get; set; }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        Setup();
        _logger = LoggerFactory.CreateLogger<RunCommand>();
        if (!File.Exists(ConfigFile))
        {
            _logger.LogError($"config file ({ConfigFile}) missing");
            return 1;
        }
        Ora2MqttOptions config;
        var deserializer = new DeserializerBuilder()
            .WithCaseInsensitivePropertyMatching()
            .Build();
        using (var file = File.OpenText(ConfigFile))
        {
            config = deserializer.Deserialize<Ora2MqttOptions>(file);
        }

        var api = GetGwmApiClient(config);

        // enroll the mTLS client certificate on first run, then rebuild the client so the
        // app-gateway calls use it
        if (string.IsNullOrEmpty(config.Account.ClientCertificate))
        {
            try
            {
                await EnrollCertificateAsync(api, config, cancellationToken);
                await SaveConfigAsync(config, cancellationToken);
                _logger.LogInformation("enrolled mTLS client certificate");
                api = GetGwmApiClient(config);
            }
            catch (GwmApiException e)
            {
                _logger.LogWarning("certificate enrollment failed: {Code} {Message}", e.Code, e.Message);
            }
        }

        using var mqtt = await ConnectMqttAsync(config, api, cancellationToken);

        var discoveryEnabled = config.Mqtt.HomeAssistantDiscoveryTopic is not null;
        _logger.LogInformation("Starting run loop. Interval={Interval}s, HA-Discovery={Enabled}, DiscoveryRepublishInterval={Republish}",
            Intervall, discoveryEnabled, DiscoveryRepublishInterval);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Intervall));
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshTokenAsync(api, config, cancellationToken);
                var shouldPublishDiscovery = discoveryEnabled && ShouldPublishDiscoveryNow();
                if (shouldPublishDiscovery)
                {
                    _logger.LogInformation("Publishing HA discovery (requested={Requested}, ageSinceLastPublish={Age})",
                        _discoveryPublishRequested,
                        _lastDiscoveryPublishUtc == DateTime.MinValue ? "never" : (DateTime.UtcNow - _lastDiscoveryPublishUtc).ToString());
                }
                await PublishStatusAsync(mqtt, api, config.Mqtt, shouldPublishDiscovery, cancellationToken);
                if (shouldPublishDiscovery)
                {
                    _lastDiscoveryPublishUtc = DateTime.UtcNow;
                    _discoveryPublishRequested = false;
                }
                if (consecutiveFailures > 0)
                {
                    _logger.LogInformation("Recovered after {Count} consecutive failures", consecutiveFailures);
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var backoff = GetFailureBackoff(consecutiveFailures);
                _logger.LogWarning(ex,
                    "Cycle failed (#{Count} consecutive). Sleeping {Backoff} and retrying. " +
                    "Common causes: transient GWM-cloud 5xx, network blip, MQTT publish error",
                    consecutiveFailures, backoff);
                try
                {
                    //back off instead of hammering the API on every interval - a dead
                    //refresh token would otherwise produce thousands of requests per day
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
        _logger.LogInformation("Run loop stopped");
        return 0;
    }

    private TimeSpan GetFailureBackoff(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var seconds = Intervall * Math.Pow(2, exponent);
        return seconds >= MaxFailureBackoff.TotalSeconds
            ? MaxFailureBackoff
            : TimeSpan.FromSeconds(seconds);
    }

    private bool ShouldPublishDiscoveryNow()
    {
        if (_discoveryPublishRequested) return true;
        if (_lastDiscoveryPublishUtc == DateTime.MinValue) return true;
        return (DateTime.UtcNow - _lastDiscoveryPublishUtc) >= DiscoveryRepublishInterval;
    }

    private async Task<IMqttClient> ConnectMqttAsync(Ora2MqttOptions config, GwmApiClient api, CancellationToken cancellationToken)
    {
        var options = config.Mqtt;
        var factory = new MqttClientFactory(new MqttLogger(LoggerFactory));
        var client = factory.CreateMqttClient();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host)
            .WithTlsOptions(new MqttClientTlsOptions { UseTls = options.UseTls })
            .WithCleanSession(true);
        if (!String.IsNullOrEmpty(options.Username) && !String.IsNullOrEmpty(options.Password))
        {
            builder = builder.WithCredentials(options.Username, options.Password);
        }

        // Re-subscribe on every (re)connect — MQTT CleanSession=true loses subscriptions on disconnect.
        client.ConnectedAsync += async e =>
        {
            _logger.LogInformation("MQTT connected to {Host} (resultCode={Result})", options.Host, e.ConnectResult.ResultCode);
            try
            {
                if (options.HomeAssistantDiscoveryTopic is not null)
                {
                    var statusTopic = $"{options.HomeAssistantDiscoveryTopic}/status";
                    await client.SubscribeAsync(statusTopic, cancellationToken: cancellationToken);
                    _logger.LogInformation("Subscribed to {Topic} (HA birth/LWT)", statusTopic);
                }
                await client.SubscribeAsync("GWM/+/command/#", cancellationToken: cancellationToken);
                _logger.LogInformation("Subscribed to GWM/+/command/#");

                // Trigger discovery republish on (re)connect — broker may have dropped retained state.
                _discoveryPublishRequested = true;
                _logger.LogInformation("Discovery republish requested due to (re)connect");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-subscribe after connect");
            }
        };

        client.DisconnectedAsync += async e =>
        {
            _logger.LogWarning("MQTT disconnected. Reason={Reason}, WasConnected={WasConnected}, Exception={Exception}",
                e.Reason, e.ClientWasConnected, e.Exception?.Message ?? "none");
            if (!e.ClientWasConnected || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            // Persistent retry — DisconnectedAsync fires only ONCE per drop; if the
            // single ConnectAsync below fails, no new disconnect event will trigger
            // another attempt. So loop here until we reconnect or get cancelled.
            var attempt = 0;
            while (!cancellationToken.IsCancellationRequested && !client.IsConnected)
            {
                attempt++;
                try
                {
                    await Task.Delay(ReconnectDelay, cancellationToken);
                    _logger.LogInformation("MQTT reconnect attempt {Attempt} to {Host}", attempt, options.Host);
                    await client.ConnectAsync(client.Options, cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("MQTT reconnect attempt {Attempt} failed: {Message} — retrying in {Delay}s",
                        attempt, ex.Message, ReconnectDelay.TotalSeconds);
                }
            }
        };

        client.ApplicationMessageReceivedAsync += x => OnMessageAsync(x, client, api, config, cancellationToken);

        _logger.LogInformation("Connecting to MQTT {Host} (TLS={Tls}, User={User})",
            options.Host, options.UseTls, string.IsNullOrEmpty(options.Username) ? "<none>" : options.Username);
        await client.ConnectAsync(builder.Build(), cancellationToken);
        return client;
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs arg, IMqttClient mqtt, GwmApiClient api, Ora2MqttOptions config, CancellationToken cancellationToken)
    {
        var options = config.Mqtt;
        if (options.HomeAssistantDiscoveryTopic is not null &&
            arg.ApplicationMessage.Topic == $"{options.HomeAssistantDiscoveryTopic}/status")
        {
            var birthPayload = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload.FirstSpan).Trim();
            if (string.Equals(birthPayload, "online", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("HA birth message received (payload=online) — requesting discovery republish on next tick");
                _discoveryPublishRequested = true;
            }
            else
            {
                _logger.LogInformation("HA status message received (payload={Payload}) — ignored", birthPayload);
            }
            return;
        }

        var topicParts = arg.ApplicationMessage.Topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (topicParts.Length < 4 || !"GWM".Equals(topicParts[0], StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var vin = topicParts[1];
        var payload = System.Text.Encoding.UTF8.GetString(arg.ApplicationMessage.Payload.FirstSpan);

        if (arg.ApplicationMessage.Topic.EndsWith("/command/ac", StringComparison.OrdinalIgnoreCase))
        {
            const string commandName = "A/C";
            try
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: received MQTT command", cancellationToken);
                var command = JsonSerializer.Deserialize<AcCommand>(payload);
                if (command is null)
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - payload could not be deserialized", cancellationToken);
                    _logger.LogError("Failed to process AC command for {Vin}: payload could not be deserialized", vin);
                    return;
                }
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: loading current settings", cancellationToken);
                await EnsureAcSettingsLoadedAsync(api, vin, cancellationToken);
                var settings = GetAcSettings(vin);
                settings.TargetTemperature = NormalizeTemperature(command.Temperature, settings.TargetTemperature);
                settings.OperationTime = NormalizeOperationTime(command.OperationTime, settings.OperationTime);

                if ("1".Equals(command.SwitchOrder, StringComparison.Ordinal))
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: updating vehicle defaults", cancellationToken);
                    await UpdateVehicleAcDefaultsAsync(api, vin, settings, cancellationToken);
                }

                await ExecuteRemoteCommandAsync(
                    mqtt,
                    api,
                    vin,
                    commandName,
                    () => SendAcCommandAsync(api, config, vin, command.SwitchOrder, settings.TargetTemperature, settings.OperationTime, cancellationToken),
                    cancellationToken);
                await PublishStatusAsync(mqtt, api, options, false, cancellationToken);
            }
            catch (Exception ex)
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - {FormatRemoteCommandException(ex)}", cancellationToken);
                _logger.LogError(ex, "Failed to process AC command on topic {Topic}", arg.ApplicationMessage.Topic);
            }

            return;
        }

        if (arg.ApplicationMessage.Topic.EndsWith("/command/ac/mode", StringComparison.OrdinalIgnoreCase))
        {
            const string commandName = "A/C mode";
            try
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: received MQTT command", cancellationToken);
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: loading current settings", cancellationToken);
                await EnsureAcSettingsLoadedAsync(api, vin, cancellationToken);
                var settings = GetAcSettings(vin);
                var mode = payload.Trim().ToLowerInvariant();
                var switchOrder = mode switch
                {
                    "cool" => "1",
                    "off" => "0",
                    _ => null
                };

                if (switchOrder is null)
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - unsupported mode '{payload}'", cancellationToken);
                    _logger.LogError("Failed to process AC mode command for {Vin}: unsupported mode '{Mode}'", vin, payload);
                    return;
                }

                if ("1".Equals(switchOrder, StringComparison.Ordinal))
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: updating vehicle defaults", cancellationToken);
                    await UpdateVehicleAcDefaultsAsync(api, vin, settings, cancellationToken);
                }

                await ExecuteRemoteCommandAsync(
                    mqtt,
                    api,
                    vin,
                    commandName,
                    () => SendAcCommandAsync(api, config, vin, switchOrder, settings.TargetTemperature, settings.OperationTime, cancellationToken),
                    cancellationToken);
                await PublishStatusAsync(mqtt, api, options, false, cancellationToken);
            }
            catch (Exception ex)
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - {FormatRemoteCommandException(ex)}", cancellationToken);
                _logger.LogError(ex, "Failed to process AC mode command on topic {Topic}", arg.ApplicationMessage.Topic);
            }

            return;
        }

        if (arg.ApplicationMessage.Topic.EndsWith("/command/ac/temperature", StringComparison.OrdinalIgnoreCase))
        {
            const string commandName = "A/C temperature";
            try
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: received MQTT command", cancellationToken);
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: loading current settings", cancellationToken);
                await EnsureAcSettingsLoadedAsync(api, vin, cancellationToken);
                var settings = GetAcSettings(vin);
                settings.TargetTemperature = NormalizeTemperature(payload, settings.TargetTemperature);

                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: updating vehicle defaults", cancellationToken);
                await UpdateVehicleAcDefaultsAsync(api, vin, settings, cancellationToken);

                if (settings.IsOn)
                {
                    await ExecuteRemoteCommandAsync(
                        mqtt,
                        api,
                        vin,
                        commandName,
                        () => SendAcCommandAsync(api, config, vin, "1", settings.TargetTemperature, settings.OperationTime, cancellationToken),
                        cancellationToken);
                }
                else
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: saved; A/C is off so no remote command was sent", cancellationToken);
                }

                await PublishStatusAsync(mqtt, api, options, false, cancellationToken);
            }
            catch (Exception ex)
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - {FormatRemoteCommandException(ex)}", cancellationToken);
                _logger.LogError(ex, "Failed to process AC temperature command on topic {Topic}", arg.ApplicationMessage.Topic);
            }

            return;
        }

        if (arg.ApplicationMessage.Topic.EndsWith("/command/lock", StringComparison.OrdinalIgnoreCase))
        {
            const string commandName = "Door lock";
            try
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: received MQTT command", cancellationToken);
                var switchOrder = payload.Trim().ToUpperInvariant() switch
                {
                    LockCommandPayload => "2",
                    UnlockCommandPayload => "1",
                    _ => null
                };

                if (switchOrder is null)
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - unsupported payload '{payload}'", cancellationToken);
                    _logger.LogError("Failed to process lock command for {Vin}: unsupported payload '{Payload}'", vin, payload);
                    return;
                }

                await ExecuteRemoteCommandAsync(
                    mqtt,
                    api,
                    vin,
                    commandName,
                    () => SendLockCommandAsync(api, config, vin, switchOrder, cancellationToken),
                    cancellationToken);
                await PublishStatusAsync(mqtt, api, options, false, cancellationToken);
            }
            catch (Exception ex)
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - {FormatRemoteCommandException(ex)}", cancellationToken);
                _logger.LogError(ex, "Failed to process lock command on topic {Topic}", arg.ApplicationMessage.Topic);
            }

            return;
        }

        if (arg.ApplicationMessage.Topic.EndsWith("/command/windows/close", StringComparison.OrdinalIgnoreCase))
        {
            const string commandName = "Window close";
            try
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: received MQTT command", cancellationToken);
                if (!String.IsNullOrWhiteSpace(payload) &&
                    !WindowClosePayload.Equals(payload.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - unsupported payload '{payload}'", cancellationToken);
                    _logger.LogError("Failed to process window close command for {Vin}: unsupported payload '{Payload}'", vin, payload);
                    return;
                }

                await ExecuteRemoteCommandAsync(
                    mqtt,
                    api,
                    vin,
                    commandName,
                    () => SendWindowCloseCommandAsync(api, config, vin, cancellationToken),
                    cancellationToken);
                await PublishStatusAsync(mqtt, api, options, false, cancellationToken);
            }
            catch (Exception ex)
            {
                await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - {FormatRemoteCommandException(ex)}", cancellationToken);
                _logger.LogError(ex, "Failed to process window close command on topic {Topic}", arg.ApplicationMessage.Topic);
            }
        }
    }

    private GwmApiClient GetGwmApiClient(Ora2MqttOptions options)
    {
        var client = ConfigureApiClient(options);
        client.SetAccessToken(options.Account.AccessToken);
        return client;
    }

    private async Task RefreshTokenAsync(GwmApiClient client, Ora2MqttOptions options, CancellationToken cancellationToken)
    {
        try
        {
            //check token
            await client.GetUserBaseInfoAsync(cancellationToken);
            return;
        }
        catch (GwmApiException e)
        {
            _logger.LogError($"Access token expired ({e.Message}). Trying to refresh token...");
        }

        var refresh = new RefreshTokenRequest
        {
            DeviceId = options.DeviceId,
            AccessToken = options.Account.AccessToken,
            RefreshToken = options.Account.RefreshToken,
        };
        client.SetAccessToken("");
        RefreshTokenResponse response;
        try
        {
            response = await client.RefreshTokenAsync(refresh, cancellationToken);
        }
        catch
        {
            //put the old token back - otherwise every following cycle fails with
            //"Empty accessToken" and forces another refresh, even if the token still works
            client.SetAccessToken(options.Account.AccessToken);
            throw;
        }
        options.Account.AccessToken = response.AccessToken;
        options.Account.RefreshToken = response.RefreshToken;
        await SaveConfigAsync(options, cancellationToken);
        client.SetAccessToken(options.Account.AccessToken);
    }

    private async Task PublishStatusAsync(IMqttClient mqtt, GwmApiClient gwm, Ora2MqttMqttOptions options, bool publishHaDiscovery, CancellationToken cancellationToken)
    {
        var vehicles = await gwm.AcquireVehiclesAsync(cancellationToken);
        foreach (var vehicle in vehicles)
        {
            var statusTask = gwm.GetLastVehicleStatusAsync(vehicle.Vin, cancellationToken);
            var basicsTask = gwm.GetVehicleBasicsInfoAsync(vehicle.Vin, cancellationToken);
            await Task.WhenAll(statusTask, basicsTask);

            var status = await statusTask;
            var basics = await basicsTask;
            var topicPrefix = $"GWM/{vehicle.Vin}/status";
            UpdateAcSettings(vehicle.Vin, status, basics);
            if (publishHaDiscovery)
            {
                await PublishHaDiscoveryAsync(mqtt, options, vehicle, status, cancellationToken);
            }
            await PublishMessageAsync(mqtt, $"{topicPrefix}/AcquisitionTime", status.AcquisitionTime, cancellationToken);
            await PublishMessageAsync(mqtt, $"{topicPrefix}/UpdateTime", status.UpdateTime, cancellationToken);
            await PublishLastRemoteCommandStatusAsync(mqtt, vehicle.Vin, cancellationToken);
            await PublishAcStatusAsync(mqtt, vehicle.Vin, topicPrefix, status, basics, cancellationToken);
            if (status.Latitude.HasValue && status.Longitude.HasValue)
            {
                await PublishMessageAsync(mqtt, $"{topicPrefix}/Latitude", status.Latitude.Value, cancellationToken);
                await PublishMessageAsync(mqtt, $"{topicPrefix}/Longitude", status.Longitude.Value, cancellationToken);
                await PublishMessageAsync(mqtt, $"{topicPrefix}/Location", JsonSerializer.Serialize(new
                {
                    latitude = status.Latitude.Value,
                    longitude = status.Longitude.Value
                }), cancellationToken);
            }

            foreach (var item in status.Items)
            {
                if (item.Value is null) continue;
                await PublishMessageAsync(mqtt, $"{topicPrefix}/items/{item.Code}/value", item.Value.ToString(), cancellationToken);
                if (item.Unit is not null)
                    await PublishMessageAsync(mqtt, $"{topicPrefix}/items/{item.Code}/unit", item.Unit, cancellationToken);
            }
        }
    }

    private Task PublishHaDiscoveryAsync(IMqttClient mqtt, Ora2MqttMqttOptions options, Vehicle vehicle, VehicleStatus status, CancellationToken cancellationToken)
    {
        var topicPrefix = $"GWM/{vehicle.Vin}/status";
        var json = JsonSerializer.Serialize(new
        {
            dev = new
            {
                ids = vehicle.Vin,
                name = vehicle.AppShowSeriesName,
                mf = vehicle.BrandName,
                mdl = vehicle.Vtype,
                sn = status.DeviceId
            },
            o = new
            {
                name = "ora2mqtt",
                url = "https://github.com/zivillian/ora2mqtt"
            },
            cmps = new
            {
                location = new
                {
                    p="device_tracker",
                    icon="mdi:map-marker",
                    json_attributes_topic=$"{topicPrefix}/Location",
                    unique_id=$"gwm_{vehicle.Vin}_location",
                    name="Location"
                },
                acquisition=new
                {
                    p = "sensor",
                    device_class = "timestamp",
                    unique_id = $"gwm_{vehicle.Vin}_AcquisitionTime",
                    state_topic = $"{topicPrefix}/AcquisitionTime",
                    name = "Acquisition",
                    value_template = "{{ (value|int // 1000) | timestamp_utc }}"
                },
                status_2013021 = new
                {
                    p="sensor",
                    device_class = "battery",
                    unique_id= $"gwm_{vehicle.Vin}_2013021",
                    unit_of_measurement="%",
                    state_topic= $"{topicPrefix}/items/2013021/value",
                    state_class= "measurement",
                    name="SOC"
                },
                status_2013022 = new
                {
                    p = "sensor",
                    device_class = "duration",
                    unique_id = $"gwm_{vehicle.Vin}_2013022",
                    unit_of_measurement = "min",
                    state_topic = $"{topicPrefix}/items/2013022/value",
                    state_class = "measurement",
                    name = "Remaining Charging Time"
                },
                status_2011501 = new
                {
                    p="sensor",
                    device_class = "distance",
                    unique_id = $"gwm_{vehicle.Vin}_2011501",
                    unit_of_measurement ="km",
                    state_topic = $"{topicPrefix}/items/2011501/value",
                    state_class = "measurement",
                    name="Range"
                },
                status_2041301 = new
                {
                    p="sensor",
                    unique_id = $"gwm_{vehicle.Vin}_2041301",
                    unit_of_measurement ="%",
                    state_topic = $"{topicPrefix}/items/2041301/value",
                    state_class = "measurement",
                    name="SOCE",
                    icon= "mdi:battery-heart-variant"
                },
                status_2101001 = new
                {
                    p="sensor",
                    device_class = "pressure",
                    unique_id = $"gwm_{vehicle.Vin}_2101001",
                    unit_of_measurement ="kPa",
                    state_topic = $"{topicPrefix}/items/2101001/value",
                    state_class = "measurement",
                    name="Tire Pressure FL",
                    icon= "mdi:car-tire-alert"
                },
                status_2101002 = new
                {
                    p="sensor",
                    device_class = "pressure",
                    unique_id = $"gwm_{vehicle.Vin}_2101002",
                    unit_of_measurement ="kPa",
                    state_topic = $"{topicPrefix}/items/2101002/value",
                    state_class = "measurement",
                    name="Tire Pressure FR",
                    icon= "mdi:car-tire-alert"
                },
                status_2101003 = new
                {
                    p="sensor",
                    device_class = "pressure",
                    unique_id = $"gwm_{vehicle.Vin}_2101003",
                    unit_of_measurement ="kPa",
                    state_topic = $"{topicPrefix}/items/2101003/value",
                    state_class = "measurement",
                    name="Tire Pressure RL",
                    icon= "mdi:car-tire-alert"
                },
                status_2101004 = new
                {
                    p="sensor",
                    device_class = "pressure",
                    unique_id = $"gwm_{vehicle.Vin}_2101004",
                    unit_of_measurement ="kPa",
                    state_topic = $"{topicPrefix}/items/2101004/value",
                    state_class = "measurement",
                    name="Tire Pressure RR",
                    icon= "mdi:car-tire-alert"
                },
                status_2101005 = new
                {
                    p="sensor",
                    device_class = "temperature",
                    unique_id = $"gwm_{vehicle.Vin}_2101005",
                    unit_of_measurement ="°C",
                    state_topic = $"{topicPrefix}/items/2101005/value",
                    state_class = "measurement",
                    name="Tire Temperature FL"
                },
                status_2101006 = new
                {
                    p="sensor",
                    device_class = "temperature",
                    unique_id = $"gwm_{vehicle.Vin}_2101006",
                    unit_of_measurement ="°C",
                    state_topic = $"{topicPrefix}/items/2101006/value",
                    state_class = "measurement",
                    name="Tire Temperature FR"
                },
                status_2101007 = new
                {
                    p="sensor",
                    device_class = "temperature",
                    unique_id = $"gwm_{vehicle.Vin}_2101007",
                    unit_of_measurement ="°C",
                    state_topic = $"{topicPrefix}/items/2101007/value",
                    state_class = "measurement",
                    name="Tire Temperature RL"
                },
                status_2101008 = new
                {
                    p="sensor",
                    device_class = "temperature",
                    unique_id = $"gwm_{vehicle.Vin}_2101008",
                    unit_of_measurement ="°C",
                    state_topic = $"{topicPrefix}/items/2101008/value",
                    state_class = "measurement",
                    name="Tire Temperature RR"
                },
                status_2103010 = new
                {
                    p = "sensor",
                    device_class = "distance",
                    unique_id = $"gwm_{vehicle.Vin}_2103010",
                    unit_of_measurement = "km",
                    state_topic = $"{topicPrefix}/items/2103010/value",
                    state_class = "measurement",
                    name = "Odometer",
                    icon="mdi:counter"
                },
                status_2201001 = new
                {
                    p = "sensor",
                    device_class = "temperature",
                    unique_id = $"gwm_{vehicle.Vin}_2201001",
                    unit_of_measurement = "°C",
                    state_topic = $"{topicPrefix}/items/2201001/value",
                    state_class = "measurement",
                    name = "Interior Temperature",
                    value_template = "{{ value|int / 10 }}"
                },
                status_2202001 = new
                {
                    p = "binary_sensor",
                    unique_id = $"gwm_{vehicle.Vin}_2202001",
                    state_topic = $"{topicPrefix}/items/2202001/value",
                    name = "A/C Status",
                    payload_off = "0",
                    payload_on = "1",
                    icon= "mdi:air-conditioner"
                },
                ac_switch = new
                {
                    p = "switch",
                    unique_id = $"gwm_{vehicle.Vin}_ac_switch",
                    state_topic = $"{topicPrefix}/items/2202001/value",
                    state_off = "0",
                    state_on = "1",
                    command_topic = $"GWM/{vehicle.Vin}/command/ac",
                    name = "A/C Control",
                    payload_off = "{\"switchOrder\":\"0\",\"temperature\":\"22\",\"operationTime\":\"30\"}",
                    payload_on = "{\"switchOrder\":\"1\",\"temperature\":\"22\",\"operationTime\":\"30\"}",
                    icon = "mdi:air-conditioner",
                    optimistic = false,
                    retain = false
                },
                ac_climate = new
                {
                    p = "climate",
                    unique_id = $"gwm_{vehicle.Vin}_ac_climate",
                    name = "A/C Climate",
                    modes = new[] { "off", "cool" },
                    mode_command_topic = $"GWM/{vehicle.Vin}/command/ac/mode",
                    mode_state_topic = $"{topicPrefix}/ac/mode",
                    action_topic = $"{topicPrefix}/ac/action",
                    temperature_command_topic = $"GWM/{vehicle.Vin}/command/ac/temperature",
                    temperature_state_topic = $"{topicPrefix}/ac/targetTemperature",
                    current_temperature_topic = $"{topicPrefix}/items/2201001/value",
                    current_temperature_template = "{{ value|float / 10 }}",
                    temperature_unit = "C",
                    temp_step = 1.0,
                    min_temp = 16,
                    max_temp = 32,
                    icon = "mdi:air-conditioner",
                    optimistic = false,
                    retain = false
                },
                door_lock = new
                {
                    p = "lock",
                    unique_id = $"gwm_{vehicle.Vin}_door_lock",
                    state_topic = $"{topicPrefix}/items/2208001/value",
                    command_topic = $"GWM/{vehicle.Vin}/command/lock",
                    name = "Door Lock",
                    payload_lock = LockCommandPayload,
                    payload_unlock = UnlockCommandPayload,
                    state_locked = "0",
                    state_unlocked = "1",
                    icon = "mdi:car-door-lock",
                    optimistic = false,
                    retain = false
                },
                windows_close = new
                {
                    p = "button",
                    unique_id = $"gwm_{vehicle.Vin}_windows_close",
                    command_topic = $"GWM/{vehicle.Vin}/command/windows/close",
                    name = "Close Windows",
                    payload_press = WindowClosePayload,
                    icon = "mdi:window-closed-variant",
                    retain = false
                },
                remote_command_status = new
                {
                    p = "sensor",
                    unique_id = $"gwm_{vehicle.Vin}_remote_command_status",
                    state_topic = $"{topicPrefix}/commandStatus",
                    name = "Remote Command Status",
                    icon = "mdi:progress-clock",
                    entity_category = "diagnostic",
                    force_update = true
                },
                status_2208001 = new
                {
                    p = "binary_sensor",
                    device_class = "lock",
                    unique_id = $"gwm_{vehicle.Vin}_2208001",
                    state_topic = $"{topicPrefix}/items/2208001/value",
                    name = "Lock",
                    payload_off = "0",
                    payload_on = "1"
                },
                status_2210001 = new
                {
                    p = "binary_sensor",
                    device_class = "window",
                    unique_id = $"gwm_{vehicle.Vin}_2210001",
                    state_topic = $"{topicPrefix}/items/2210001/value",
                    name = "Window FL",
                    payload_off = "1",
                    payload_on = "3"
                },
                status_2210002 = new
                {
                    p = "binary_sensor",
                    device_class = "window",
                    unique_id = $"gwm_{vehicle.Vin}_2210002",
                    state_topic = $"{topicPrefix}/items/2210002/value",
                    name = "Window FR",
                    payload_off = "1",
                    payload_on = "3"
                },
                status_2210003 = new
                {
                    p = "binary_sensor",
                    device_class = "window",
                    unique_id = $"gwm_{vehicle.Vin}_2210003",
                    state_topic = $"{topicPrefix}/items/2210003/value",
                    name = "Window RL",
                    payload_off = "1",
                    payload_on = "3"
                },
                status_2210004 = new
                {
                    p = "binary_sensor",
                    device_class = "window",
                    unique_id = $"gwm_{vehicle.Vin}_2210004",
                    state_topic = $"{topicPrefix}/items/2210004/value",
                    name = "Window RR",
                    payload_off = "1",
                    payload_on = "3"
                },
                status_2078020 = new
                {
                    p = "binary_sensor",
                    device_class = "running",
                    unique_id = $"gwm_{vehicle.Vin}_2078020",
                    state_topic = $"{topicPrefix}/items/2078020/value",
                    name = "Air Circulation",
                    payload_off = "0",
                    payload_on = "1"
                },
                status_2222001 = new
                {
                    p = "binary_sensor",
                    unique_id = $"gwm_{vehicle.Vin}_2222001",
                    state_topic = $"{topicPrefix}/items/2222001/value",
                    name = "Front defroster",
                    payload_off = "0",
                    payload_on = "1",
                    icon= "mdi:car-defrost-front"
                },
                status_2042082 = new
                {
                    p = "binary_sensor",
                    device_class= "plug",
                    unique_id = $"gwm_{vehicle.Vin}_2042082",
                    state_topic = $"{topicPrefix}/items/2042082/value",
                    name = "Charge plug",
                    payload_off = "0",
                    payload_on = "1",
                },
            }
        });
        var topic = $"{options.HomeAssistantDiscoveryTopic}/device/{vehicle.Vin}/config";
        _logger.LogInformation("Publishing HA discovery (retain=true) for VIN {Vin} to {Topic} ({Bytes} bytes)",
            vehicle.Vin, topic, json.Length);
        return PublishMessageAsync(mqtt, topic, json, cancellationToken, retain: true);
    }

    private Task PublishLastRemoteCommandStatusAsync(IMqttClient mqtt, string vin, CancellationToken cancellationToken)
    {
        if (!_lastRemoteCommandStatuses.TryGetValue(vin, out var status))
        {
            status = "No remote command has run yet";
        }

        return PublishMessageAsync(mqtt, GetRemoteCommandStatusTopic(vin), status, cancellationToken);
    }

    private Task PublishRemoteCommandStatusAsync(IMqttClient mqtt, string vin, string status, CancellationToken cancellationToken)
    {
        var normalizedStatus = NormalizeRemoteCommandStatus(status);
        _lastRemoteCommandStatuses[vin] = normalizedStatus;
        return PublishMessageAsync(mqtt, GetRemoteCommandStatusTopic(vin), normalizedStatus, cancellationToken);
    }

    private static string GetRemoteCommandStatusTopic(string vin)
    {
        return $"GWM/{vin}/status/commandStatus";
    }

    private static string NormalizeRemoteCommandStatus(string status)
    {
        status = String.Join(" ", (status ?? String.Empty).Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
        if (status.Length <= RemoteCommandStatusMaxLength)
        {
            return status;
        }

        return status[..(RemoteCommandStatusMaxLength - 3)] + "...";
    }

    private Task PublishMessageAsync(IMqttClient client, string topic, double payload, CancellationToken cancellationToken)
    {
        return PublishMessageAsync(client, topic, payload.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    private Task PublishMessageAsync(IMqttClient client, string topic, long payload, CancellationToken cancellationToken)
    {
        return PublishMessageAsync(client, topic, payload.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    private Task PublishMessageAsync(IMqttClient client, string topic, string payload, CancellationToken cancellationToken, bool retain = false)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();
        return client.PublishAsync(message, cancellationToken);
    }

    private async Task PublishAcStatusAsync(IMqttClient mqtt, string vin, string topicPrefix, VehicleStatus status, VehicleBasicsInfo basics, CancellationToken cancellationToken)
    {
        var itemValue = GetStatusItemValue(status, "2202001");
        var mode = "1".Equals(itemValue, StringComparison.Ordinal) ? "cool" : "off";
        var action = "1".Equals(itemValue, StringComparison.Ordinal) ? "cooling" : "off";
        var settings = GetAcSettings(vin);
        var targetTemperature = NormalizeTemperature(basics.Config?.AirConditionerTemperature, settings.TargetTemperature);

        await PublishMessageAsync(mqtt, $"{topicPrefix}/ac/mode", mode, cancellationToken);
        await PublishMessageAsync(mqtt, $"{topicPrefix}/ac/action", action, cancellationToken);
        await PublishMessageAsync(mqtt, $"{topicPrefix}/ac/targetTemperature", targetTemperature, cancellationToken);
    }

    private void UpdateAcSettings(string vin, VehicleStatus status, VehicleBasicsInfo basics)
    {
        var settings = GetAcSettings(vin);
        settings.TargetTemperature = NormalizeTemperature(basics.Config?.AirConditionerTemperature, settings.TargetTemperature);
        settings.OperationTime = NormalizeOperationTime(null, settings.OperationTime);
        settings.IsOn = "1".Equals(GetStatusItemValue(status, "2202001"), StringComparison.Ordinal);
    }

    private async Task EnsureAcSettingsLoadedAsync(GwmApiClient api, string vin, CancellationToken cancellationToken)
    {
        if (_acSettings.TryGetValue(vin, out var existing) &&
            !String.IsNullOrWhiteSpace(existing.TargetTemperature) &&
            !String.IsNullOrWhiteSpace(existing.OperationTime))
        {
            return;
        }

        var statusTask = api.GetLastVehicleStatusAsync(vin, cancellationToken);
        var basicsTask = api.GetVehicleBasicsInfoAsync(vin, cancellationToken);
        await Task.WhenAll(statusTask, basicsTask);
        UpdateAcSettings(vin, await statusTask, await basicsTask);
    }

    private async Task UpdateVehicleAcDefaultsAsync(GwmApiClient api, string vin, AcSettings settings, CancellationToken cancellationToken)
    {
        await api.ModifyVehicleRemoteCtlInfoAsync(new ModifyVecicleRemoteCtl
        {
            AirConditionerTemperature = settings.TargetTemperature,
            AirConditionerTime = settings.OperationTime,
            Vin = vin
        }, cancellationToken);
    }

    private async Task ExecuteRemoteCommandAsync(
        IMqttClient mqtt,
        GwmApiClient api,
        string vin,
        string commandName,
        Func<Task<string>> sendCommandAsync,
        CancellationToken cancellationToken)
    {
        await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: sending command to GWM", cancellationToken);
        var seqNo = await sendCommandAsync();
        if (seqNo is null)
        {
            await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: failed - account.securityPin is not configured", cancellationToken);
            return;
        }

        await PublishRemoteCommandStatusAsync(mqtt, vin, $"{commandName}: accepted by GWM, waiting for vehicle result", cancellationToken);
        await WaitForRemoteCommandResultAsync(mqtt, api, vin, commandName, seqNo, cancellationToken);
    }

    private async Task WaitForRemoteCommandResultAsync(
        IMqttClient mqtt,
        GwmApiClient api,
        string vin,
        string commandName,
        string seqNo,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RemoteCommandResultMaxPolls; attempt++)
        {
            await Task.Delay(RemoteCommandResultPollInterval, cancellationToken);
            var results = await api.GetRemoteCtrlResultAsync(seqNo, cancellationToken);
            var result = results.FirstOrDefault(x => String.Equals(x.HwCommandId, seqNo, StringComparison.OrdinalIgnoreCase))
                         ?? results.FirstOrDefault();

            if (result is null)
            {
                await PublishRemoteCommandStatusAsync(
                    mqtt,
                    vin,
                    $"{commandName}: waiting for vehicle result ({attempt}/{RemoteCommandResultMaxPolls})",
                    cancellationToken);
                continue;
            }

            await PublishRemoteCommandStatusAsync(
                mqtt,
                vin,
                FormatRemoteCommandResult(commandName, result, attempt),
                cancellationToken);

            if (!RemoteCommandPendingResultCode.Equals(result.ResultCode, StringComparison.Ordinal))
            {
                return;
            }
        }

        await PublishRemoteCommandStatusAsync(
            mqtt,
            vin,
            $"{commandName}: timed out waiting for vehicle result after {RemoteCommandResultMaxPolls * RemoteCommandResultPollInterval.TotalSeconds:0} seconds",
            cancellationToken);
    }

    private static string FormatRemoteCommandResult(string commandName, RemoteCtrlResultT5 result, int attempt)
    {
        var resultCode = String.IsNullOrWhiteSpace(result.ResultCode) ? "unknown" : result.ResultCode;
        var resultMsg = String.IsNullOrWhiteSpace(result.ResultMsg) ? "no message" : result.ResultMsg;
        if (RemoteCommandPendingResultCode.Equals(result.ResultCode, StringComparison.Ordinal))
        {
            return $"{commandName}: in progress ({attempt}/{RemoteCommandResultMaxPolls}) - {resultMsg} [{resultCode}]";
        }

        var status = IsSuccessfulRemoteCommandResult(result) ? "completed" : "failed";
        return $"{commandName}: {status} - {resultMsg} [{resultCode}]";
    }

    private static bool IsSuccessfulRemoteCommandResult(RemoteCtrlResultT5 result)
    {
        return "0".Equals(result.ResultCode, StringComparison.Ordinal)
               || "6".Equals(result.ResultCode, StringComparison.Ordinal)
               || "Success".Equals(result.ResultMsg, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatRemoteCommandException(Exception exception)
    {
        if (exception is GwmApiException gwmException)
        {
            return $"{gwmException.Message} [{gwmException.Code}]";
        }

        return exception.Message;
    }

    private async Task<string> SendLockCommandAsync(GwmApiClient api, Ora2MqttOptions config, string vin, string switchOrder, CancellationToken cancellationToken)
    {
        var securityPassword = GetSecurityPassword(config, vin, "lock");
        if (securityPassword is null)
        {
            return null;
        }

        var request = new SendCmd
        {
            Instructions = new SendCmdInstruction
            {
                X05 = new Instruction0x05
                {
                    OperationTime = "0",
                    SwitchOrder = switchOrder
                }
            },
            RemoteType = "0",
            SecurityPassword = securityPassword,
            Type = 2,
            Vin = vin
        };

        await api.SendCmdAsync(request, cancellationToken);
        return request.SeqNo;
    }

    private async Task<string> SendWindowCloseCommandAsync(GwmApiClient api, Ora2MqttOptions config, string vin, CancellationToken cancellationToken)
    {
        var securityPassword = GetSecurityPassword(config, vin, "window close");
        if (securityPassword is null)
        {
            return null;
        }

        var request = new SendCmd
        {
            Instructions = new SendCmdInstruction
            {
                X08 = new Instruction0x08
                {
                    SwitchOrder = "0",
                    Window = new WindowInstruction
                    {
                        LeftFront = "0",
                        LeftBack = "0",
                        RightFront = "0",
                        RightBack = "0",
                        SkyLight = String.Empty
                    }
                }
            },
            RemoteType = "0",
            SecurityPassword = securityPassword,
            Type = 2,
            Vin = vin
        };

        await api.SendCmdAsync(request, cancellationToken);
        return request.SeqNo;
    }

    private async Task<string> SendAcCommandAsync(GwmApiClient api, Ora2MqttOptions config, string vin, string switchOrder, string temperature, string operationTime, CancellationToken cancellationToken)
    {
        var securityPassword = GetSecurityPassword(config, vin, "AC");
        if (securityPassword is null)
        {
            return null;
        }

        var normalizedTemperature = NormalizeTemperature(temperature, DefaultAcTemperature);
        var normalizedOperationTime = NormalizeOperationTime(operationTime, DefaultAcOperationTime);

        var request = new SendCmd
        {
            Instructions = new SendCmdInstruction
            {
                X04 = new Instruction0x04
                {
                    AirConditioner = new AirConditionerInstruction
                    {
                        OperationTime = normalizedOperationTime,
                        SwitchOrder = switchOrder,
                        Temperature = normalizedTemperature
                    }
                }
            },
            RemoteType = "0",
            SecurityPassword = securityPassword,
            Type = 2,
            Vin = vin
        };

        await api.SendCmdAsync(request, cancellationToken);
        return request.SeqNo;
    }

    private string GetSecurityPassword(Ora2MqttOptions config, string vin, string commandName)
    {
        if (String.IsNullOrWhiteSpace(config.Account.SecurityPin))
        {
            _logger.LogError("Failed to process {CommandName} command for {Vin}: account.securityPin is not configured", commandName, vin);
            return null;
        }

        return new CheckSecurityPassword(config.Account.SecurityPin).Md5Hash;
    }

    private AcSettings GetAcSettings(string vin)
    {
        if (!_acSettings.TryGetValue(vin, out var settings))
        {
            settings = new AcSettings
            {
                TargetTemperature = DefaultAcTemperature,
                OperationTime = DefaultAcOperationTime
            };
            _acSettings[vin] = settings;
        }

        return settings;
    }

    private static string NormalizeTemperature(string value, string fallback)
    {
        if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        return fallback ?? DefaultAcTemperature;
    }

    private static string NormalizeOperationTime(string value, string fallback)
    {
        if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        return fallback ?? DefaultAcOperationTime;
    }

    private static string GetStatusItemValue(VehicleStatus status, string code)
    {
        return status.Items.FirstOrDefault(x => code.Equals(x.Code, StringComparison.Ordinal))?.Value?.ToString();
    }

    private class AcCommand
    {
        [JsonPropertyName("temperature")]
        public string Temperature { get; set; }

        [JsonPropertyName("operationTime")]
        public string OperationTime { get; set; }

        [JsonPropertyName("switchOrder")]
        public string SwitchOrder { get; set; }
    }

    private sealed class AcSettings
    {
        public bool IsOn { get; set; }

        public string OperationTime { get; set; }

        public string TargetTemperature { get; set; }
    }
}
