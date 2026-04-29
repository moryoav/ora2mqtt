using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using libgwmapi;
using libgwmapi.DTO.UserAuth;
using libgwmapi.DTO.Vehicle;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "dump",
    "ac-on",
    "lock",
    "unlock",
    "window-close",
    "window-close-legacy"
};

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AcProbe <config-path> [vin] [dump|ac-on|lock|unlock|window-close|window-close-legacy]");
    return 1;
}

var configPath = args[0];
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    return 1;
}

string? requestedVin = null;
string command = "dump";
if (args.Length >= 2)
{
    if (commands.Contains(args[1]))
    {
        command = args[1];
    }
    else
    {
        requestedVin = args[1];
    }
}

if (args.Length >= 3)
{
    command = args[2];
}

if (!commands.Contains(command))
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return 1;
}

var deserializer = new DeserializerBuilder()
    .WithCaseInsensitivePropertyMatching()
    .IgnoreUnmatchedProperties()
    .Build();

ProbeOptions config;
using (var reader = File.OpenText(configPath))
{
    config = deserializer.Deserialize<ProbeOptions>(reader);
}

if (config.Account is null)
{
    Console.Error.WriteLine("Config is missing the Account section.");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Error).AddConsole());
var client = CreateClient(config, loggerFactory);
client.SetAccessToken(config.Account.AccessToken);

try
{
    await RefreshTokenAsync(client, config, CancellationToken.None);

    var user = await client.GetUserBaseInfoAsync(CancellationToken.None);
    Console.WriteLine($"Security PIN exists in account: {user.IsSecurityPasswordExist}");

    if (!String.IsNullOrWhiteSpace(config.Account.SecurityPin))
    {
        await client.CheckSecurityPasswordAsync(new CheckSecurityPassword(config.Account.SecurityPin), CancellationToken.None);
        Console.WriteLine("Security PIN accepted by GWM API.");
    }
    else
    {
        Console.WriteLine("Security PIN is not configured in this config file.");
    }

    var vehicles = await client.AcquireVehiclesAsync(CancellationToken.None);
    if (vehicles.Length == 0)
    {
        Console.Error.WriteLine("No vehicles returned by the account.");
        return 1;
    }

    var vehicle = requestedVin is null
        ? vehicles[0]
        : vehicles.FirstOrDefault(x => requestedVin.Equals(x.Vin, StringComparison.OrdinalIgnoreCase));
    if (vehicle is null)
    {
        Console.Error.WriteLine($"Requested VIN not found: {requestedVin}");
        return 1;
    }

    Console.WriteLine($"Using vehicle VIN ending with: {vehicle.Vin[^6..]}");

    var basics = await client.GetVehicleBasicsInfoAsync(vehicle.Vin, CancellationToken.None);
    var status = await client.GetLastVehicleStatusAsync(vehicle.Vin, CancellationToken.None);

    PrintSummary(vehicle, basics, status);

    switch (command.ToLowerInvariant())
    {
        case "dump":
            PrintJson("Vehicle", vehicle);
            PrintJson("VehicleBasicsInfo", basics);
            PrintJson("VehicleStatus", status);
            return 0;

        case "ac-on":
            return await ProbeAcOnAsync(client, config, vehicle, CancellationToken.None);

        case "lock":
            EnsureSecurityPin(config);
            return await ProbeCommandAsync(
                client,
                config,
                vehicle,
                "Door lock candidate",
                new ProbeSendCmd<DoorLockInstructions>
                {
                    Instructions = new DoorLockInstructions
                    {
                        X05 = new DoorLockInstruction
                        {
                            OperationTime = "0",
                            SwitchOrder = "2"
                        }
                    },
                    RemoteType = "0",
                    SecurityPassword = new CheckSecurityPassword(config.Account.SecurityPin).Md5Hash,
                    Type = 2,
                    Vin = vehicle.Vin
                },
                CancellationToken.None);

        case "unlock":
            EnsureSecurityPin(config);
            return await ProbeCommandAsync(
                client,
                config,
                vehicle,
                "Door unlock candidate",
                new ProbeSendCmd<DoorLockInstructions>
                {
                    Instructions = new DoorLockInstructions
                    {
                        X05 = new DoorLockInstruction
                        {
                            OperationTime = "0",
                            SwitchOrder = "1"
                        }
                    },
                    RemoteType = "0",
                    SecurityPassword = new CheckSecurityPassword(config.Account.SecurityPin).Md5Hash,
                    Type = 2,
                    Vin = vehicle.Vin
                },
                CancellationToken.None);

        case "window-close":
            EnsureSecurityPin(config);
            return await ProbeCommandAsync(
                client,
                config,
                vehicle,
                "Window close candidate (rightFront/rightBack)",
                new ProbeSendCmd<WindowInstructions>
                {
                    Instructions = new WindowInstructions
                    {
                        X08 = new WindowInstruction
                        {
                            SwitchOrder = "0",
                            Window = new WindowTargets
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
                    SecurityPassword = new CheckSecurityPassword(config.Account.SecurityPin).Md5Hash,
                    Type = 2,
                    Vin = vehicle.Vin
                },
                CancellationToken.None);

        case "window-close-legacy":
            EnsureSecurityPin(config);
            return await ProbeCommandAsync(
                client,
                config,
                vehicle,
                "Window close candidate (rearFront/rearBack)",
                new ProbeSendCmd<LegacyWindowInstructions>
                {
                    Instructions = new LegacyWindowInstructions
                    {
                        X08 = new LegacyWindowInstruction
                        {
                            SwitchOrder = "0",
                            Window = new LegacyWindowTargets
                            {
                                LeftFront = "0",
                                LeftBack = "0",
                                RearFront = "0",
                                RearBack = "0",
                                SkyLight = String.Empty
                            }
                        }
                    },
                    RemoteType = "0",
                    SecurityPassword = new CheckSecurityPassword(config.Account.SecurityPin).Md5Hash,
                    Type = 2,
                    Vin = vehicle.Vin
                },
                CancellationToken.None);

        default:
            Console.Error.WriteLine($"Unhandled command: {command}");
            return 1;
    }
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 4;
}
catch (GwmApiException ex)
{
    Console.Error.WriteLine($"GWM API error: code={ex.Code}, message={ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 3;
}

static GwmApiClient CreateClient(ProbeOptions config, ILoggerFactory loggerFactory)
{
    var certHandler = new CertificateHandler();
    var httpHandler = new HttpClientHandler
    {
        ClientCertificateOptions = ClientCertificateOption.Manual
    };
    using (var cert = certHandler.CertificateWithPrivateKey)
    {
        var pkcs12 = new X509Certificate2(cert.Export(X509ContentType.Pkcs12));
        httpHandler.ClientCertificates.Add(pkcs12);
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in certHandler.Chain)
        {
            if (cert.Issuer != cert.Subject)
            {
                store.Add(cert);
            }
        }
    }

    var httpLogger = loggerFactory.CreateLogger<HttpClient>();
    var httpOptions = new HttpClientFactoryOptions
    {
        ShouldRedactHeaderValue = header => "accessToken".Equals(header, StringComparison.InvariantCultureIgnoreCase)
    };
    var h5Client = new HttpClient(new LoggingHttpMessageHandler(httpLogger, httpOptions)
    {
        InnerHandler = new HttpClientHandler()
    });
    var appClient = new HttpClient(new LoggingHttpMessageHandler(httpLogger, httpOptions)
    {
        InnerHandler = httpHandler
    });

    return new GwmApiClient(h5Client, appClient, loggerFactory)
    {
        Country = config.Country
    };
}

static async Task RefreshTokenAsync(GwmApiClient client, ProbeOptions config, CancellationToken cancellationToken)
{
    try
    {
        await client.GetUserBaseInfoAsync(cancellationToken);
        return;
    }
    catch (GwmApiException ex)
    {
        Console.WriteLine($"Access token check failed: code={ex.Code}, message={ex.Message}");
    }

    var refresh = new RefreshTokenRequest
    {
        DeviceId = config.DeviceId,
        AccessToken = config.Account.AccessToken,
        RefreshToken = config.Account.RefreshToken
    };
    client.SetAccessToken(String.Empty);
    var response = await client.RefreshTokenAsync(refresh, cancellationToken);
    config.Account.AccessToken = response.AccessToken;
    config.Account.RefreshToken = response.RefreshToken;
    client.SetAccessToken(response.AccessToken);
    Console.WriteLine("Access token refreshed in memory.");
}

static void EnsureSecurityPin(ProbeOptions config)
{
    if (String.IsNullOrWhiteSpace(config.Account.SecurityPin))
    {
        throw new InvalidOperationException("Config is missing Account.SecurityPin.");
    }
}

static async Task<int> ProbeAcOnAsync(GwmApiClient client, ProbeOptions config, Vehicle vehicle, CancellationToken cancellationToken)
{
    EnsureSecurityPin(config);
    return await ProbeCommandAsync(
        client,
        config,
        vehicle,
        "A/C on probe",
        new ProbeSendCmd<SendCmdInstruction>
        {
            Instructions = new SendCmdInstruction
            {
                X04 = new Instruction0x04
                {
                    AirConditioner = new AirConditionerInstruction
                    {
                        OperationTime = "30",
                        SwitchOrder = "1",
                        Temperature = "22"
                    }
                }
            },
            RemoteType = "0",
            SecurityPassword = new CheckSecurityPassword(config.Account.SecurityPin).Md5Hash,
            Type = 2,
            Vin = vehicle.Vin
        },
        cancellationToken);
}

static async Task<int> ProbeCommandAsync<TInstructions>(
    GwmApiClient client,
    ProbeOptions config,
    Vehicle vehicle,
    string label,
    ProbeSendCmd<TInstructions> request,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"Executing: {label}");

    var beforeStatus = await client.GetLastVehicleStatusAsync(vehicle.Vin, cancellationToken);
    var beforeBasics = await client.GetVehicleBasicsInfoAsync(vehicle.Vin, cancellationToken);
    Console.WriteLine("State before command:");
    PrintSummary(vehicle, beforeBasics, beforeStatus);
    PrintJson("Request", request);

    await client.SendRawCmdAsync(request, cancellationToken);
    Console.WriteLine($"SendCmd accepted. seqNo={request.SeqNo}");

    RemoteCtrlResultT5[] latestResult = Array.Empty<RemoteCtrlResultT5>();
    for (var attempt = 1; attempt <= 18; attempt++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        latestResult = await client.GetRemoteCtrlResultAsync(request.SeqNo, cancellationToken);
        Console.WriteLine($"Remote result poll {attempt}: {latestResult.Length} record(s)");
        if (latestResult.Length == 0)
        {
            continue;
        }

        PrintJson("RemoteCtrlResultT5", latestResult);
        if (latestResult.Any(IsTerminalResult))
        {
            break;
        }
    }

    var afterStatus = await client.GetLastVehicleStatusAsync(vehicle.Vin, cancellationToken);
    var afterBasics = await client.GetVehicleBasicsInfoAsync(vehicle.Vin, cancellationToken);
    Console.WriteLine("State after command:");
    PrintSummary(vehicle, afterBasics, afterStatus);

    return 0;
}

static void PrintSummary(Vehicle vehicle, VehicleBasicsInfo basics, VehicleStatus status)
{
    Console.WriteLine($"AgreementVersion: {vehicle.AgreementVersion}");
    Console.WriteLine($"HasScyPwd: {vehicle.HasScyPwd ?? "<null>"}");
    Console.WriteLine($"HasWinControl: {vehicle.HasWinControl ?? "<null>"}");
    Console.WriteLine($"HasSsWin: {vehicle.HasSsWin ?? "<null>"}");
    Console.WriteLine($"Remote: {vehicle.Remote ?? "<null>"}");
    Console.WriteLine($"Status.Command: {Serialize(status.Command)}");
    Console.WriteLine($"Lock (2208001): {GetStatusValue(status, "2208001")}");
    Console.WriteLine($"A/C (2202001): {GetStatusValue(status, "2202001")}");
    Console.WriteLine($"Window FL (2210001): {GetStatusValue(status, "2210001")}");
    Console.WriteLine($"Window FR (2210002): {GetStatusValue(status, "2210002")}");
    Console.WriteLine($"Window RL (2210003): {GetStatusValue(status, "2210003")}");
    Console.WriteLine($"Window RR (2210004): {GetStatusValue(status, "2210004")}");
    Console.WriteLine($"Config.LeftFrontWindow: {basics.Config?.LeftFrontWindow}");
    Console.WriteLine($"Config.RightFrontWindow: {basics.Config?.RightFrontWindow}");
    Console.WriteLine($"Config.LeftBackWindow: {basics.Config?.LeftBackWindow}");
    Console.WriteLine($"Config.RightBackWindow: {basics.Config?.RightBackWindow}");
    Console.WriteLine($"Config.SkyLight: {basics.Config?.SkyLight}");
    Console.WriteLine($"Config.PowerGear: {basics.Config?.PowerGear}");
    Console.WriteLine($"Config.AirConditionerTemperature: {basics.Config?.AirConditionerTemperature}");
    Console.WriteLine();
}

static string GetStatusValue(VehicleStatus status, string code)
{
    return status.Items.FirstOrDefault(x => x.Code == code)?.Value?.ToString() ?? "<missing>";
}

static bool IsTerminalResult(RemoteCtrlResultT5 result)
{
    if (String.Equals(result.ResultCode, "2000", StringComparison.OrdinalIgnoreCase) &&
        result.ResultMsg?.Contains("in process", StringComparison.OrdinalIgnoreCase) == true)
    {
        return false;
    }

    return result.HwResult.HasValue ||
           !String.IsNullOrWhiteSpace(result.ResultCode) ||
           !String.IsNullOrWhiteSpace(result.ResultMsg);
}

static void PrintJson<T>(string label, T value)
{
    Console.WriteLine($"{label}:");
    Console.WriteLine(Serialize(value));
    Console.WriteLine();
}

static string Serialize<T>(T value)
{
    return JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true
    });
}

sealed class ProbeOptions
{
    public string DeviceId { get; set; } = String.Empty;

    public string Country { get; set; } = String.Empty;

    public ProbeAccountOptions Account { get; set; } = new();
}

sealed class ProbeAccountOptions
{
    public string AccessToken { get; set; } = String.Empty;

    public string RefreshToken { get; set; } = String.Empty;

    public string SecurityPin { get; set; } = String.Empty;
}

sealed class ProbeSendCmd<TInstructions>
{
    [JsonPropertyName("instructions")]
    public TInstructions Instructions { get; set; } = default!;

    [JsonPropertyName("remoteType")]
    public string RemoteType { get; set; } = String.Empty;

    [JsonPropertyName("securityPassword")]
    public string SecurityPassword { get; set; } = String.Empty;

    [JsonPropertyName("seqNo")]
    public string SeqNo { get; set; } = Guid.NewGuid().ToString("N") + "1234";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("vin")]
    public string Vin { get; set; } = String.Empty;
}

sealed class DoorLockInstructions
{
    [JsonPropertyName("0x05")]
    public DoorLockInstruction X05 { get; set; } = new();
}

sealed class DoorLockInstruction
{
    [JsonPropertyName("operationTime")]
    public string OperationTime { get; set; } = String.Empty;

    [JsonPropertyName("switchOrder")]
    public string SwitchOrder { get; set; } = String.Empty;
}

sealed class WindowInstructions
{
    [JsonPropertyName("0x08")]
    public WindowInstruction X08 { get; set; } = new();
}

sealed class WindowInstruction
{
    [JsonPropertyName("switchOrder")]
    public string SwitchOrder { get; set; } = String.Empty;

    [JsonPropertyName("window")]
    public WindowTargets Window { get; set; } = new();
}

sealed class WindowTargets
{
    [JsonPropertyName("leftFront")]
    public string LeftFront { get; set; } = String.Empty;

    [JsonPropertyName("leftBack")]
    public string LeftBack { get; set; } = String.Empty;

    [JsonPropertyName("rightFront")]
    public string RightFront { get; set; } = String.Empty;

    [JsonPropertyName("rightBack")]
    public string RightBack { get; set; } = String.Empty;

    [JsonPropertyName("skyLight")]
    public string SkyLight { get; set; } = String.Empty;
}

sealed class LegacyWindowInstructions
{
    [JsonPropertyName("0x08")]
    public LegacyWindowInstruction X08 { get; set; } = new();
}

sealed class LegacyWindowInstruction
{
    [JsonPropertyName("switchOrder")]
    public string SwitchOrder { get; set; } = String.Empty;

    [JsonPropertyName("window")]
    public LegacyWindowTargets Window { get; set; } = new();
}

sealed class LegacyWindowTargets
{
    [JsonPropertyName("leftFront")]
    public string LeftFront { get; set; } = String.Empty;

    [JsonPropertyName("leftBack")]
    public string LeftBack { get; set; } = String.Empty;

    [JsonPropertyName("rearFront")]
    public string RearFront { get; set; } = String.Empty;

    [JsonPropertyName("rearBack")]
    public string RearBack { get; set; } = String.Empty;

    [JsonPropertyName("skyLight")]
    public string SkyLight { get; set; } = String.Empty;
}
