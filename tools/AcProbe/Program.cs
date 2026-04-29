using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using libgwmapi;
using libgwmapi.DTO.UserAuth;
using libgwmapi.DTO.Vehicle;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AcProbe <config-path> [vin]");
    return 1;
}

var configPath = args[0];
var requestedVin = args.Length > 1 ? args[1] : null;
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
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

if (String.IsNullOrWhiteSpace(config.Account.SecurityPin))
{
    Console.Error.WriteLine("Config is missing Account.SecurityPin.");
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

    await client.CheckSecurityPasswordAsync(new CheckSecurityPassword(config.Account.SecurityPin), CancellationToken.None);
    Console.WriteLine("Security PIN accepted by GWM API.");

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

    var beforeStatus = await client.GetLastVehicleStatusAsync(vehicle.Vin, CancellationToken.None);
    Console.WriteLine($"A/C state before command: {GetStatusValue(beforeStatus, "2202001")}");

    var request = new SendCmd
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
    };

    await client.SendCmdAsync(request, CancellationToken.None);
    Console.WriteLine($"SendCmd accepted. seqNo={request.SeqNo}");

    RemoteCtrlResultT5[]? latestResult = null;
    for (var attempt = 1; attempt <= 18; attempt++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        latestResult = await client.GetRemoteCtrlResultAsync(request.SeqNo, CancellationToken.None);
        Console.WriteLine($"Remote result poll {attempt}: {latestResult.Length} record(s)");
        if (latestResult.Length == 0)
        {
            continue;
        }

        Console.WriteLine(JsonSerializer.Serialize(latestResult, new JsonSerializerOptions { WriteIndented = true }));
        if (latestResult.Any(IsTerminalResult))
        {
            break;
        }
    }

    var afterStatus = await client.GetLastVehicleStatusAsync(vehicle.Vin, CancellationToken.None);
    Console.WriteLine($"A/C state after command: {GetStatusValue(afterStatus, "2202001")}");

    return 0;
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
