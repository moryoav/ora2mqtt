using CommandLine;
using libgwmapi;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace ora2mqtt;

public abstract class BaseCommand
{
    [Option('d', "debug", Default = false, HelpText = "enable debug logging")]
    public bool Debug { get; set; }

    [Option('c', "config", Default = "ora2mqtt.yml", HelpText = "path to yaml config file")]
    public string ConfigFile { get; set; }

    protected ILoggerFactory LoggerFactory { get; private set; }

    protected void Setup()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(x =>
            x.SetMinimumLevel(Debug ? LogLevel.Trace : LogLevel.Information)
                .AddSimpleConsole(o =>
                {
                    o.IncludeScopes = false;
                    o.SingleLine = true;
                    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                }));
    }

    protected GwmApiClient ConfigureApiClient(Ora2MqttOptions options)
    {
        var httpLogger = LoggerFactory.CreateLogger<HttpClient>();
        var httpOptions = new HttpClientFactoryOptions
        {
            ShouldRedactHeaderValue = x => "accessToken".Equals(x, StringComparison.InvariantCultureIgnoreCase)
        };

        HttpClient Build(X509Certificate2 clientCert)
        {
            var inner = new HttpClientHandler { ClientCertificateOptions = ClientCertificateOption.Manual };
            if (clientCert is not null) inner.ClientCertificates.Add(clientCert);
            return new HttpClient(new GwmSigningHandler
            {
                InnerHandler = new LoggingHttpMessageHandler(httpLogger, httpOptions) { InnerHandler = inner }
            });
        }

        // h5-gateway (auth) does not enforce mTLS; the app-gateway does.
        var h5Client = Build(null);

        // app-gateway now wants the enrolled per-install certificate; the shared bootstrap
        // certificate only still works on the common gateway (applyCertificate).
        X509Certificate2 enrolled = null;
        if (!string.IsNullOrEmpty(options.Account.ClientCertificate) &&
            !string.IsNullOrEmpty(options.Account.ClientCertificateKey))
        {
            enrolled = CertificateEnrollment.Load(options.Account.ClientCertificate, options.Account.ClientCertificateKey);
        }
        var appClient = Build(enrolled);

        // bootstrap certificate for applyCertificate
        X509Certificate2 bootstrap = null;
        if (Environment.GetEnvironmentVariable("GWM_NO_CLIENT_CERT") != "1")
        {
            using var cert = new CertificateHandler().CertificateWithPrivateKey;
            bootstrap = new X509Certificate2(cert.Export(X509ContentType.Pkcs12));
        }
        var certificateClient = Build(bootstrap);

        return new GwmApiClient(h5Client, appClient, certificateClient, LoggerFactory)
        {
            Country = options.Country,
            DeviceId = options.DeviceId
        };
    }

    // enroll a per-install client certificate for the mTLS app-gateway (needs a valid token)
    protected async Task EnrollCertificateAsync(GwmApiClient client, Ora2MqttOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(options.Account.ClientCertificate)) return;
        var csr = CertificateEnrollment.GenerateCsr(options.Country, options.DeviceId);
        client.SetCertificateDeviceId(csr.EnrollmentDeviceId);
        var response = await client.ApplyCertificateAsync(
            new libgwmapi.DTO.AppAuth.ApplyCertificateRequest
            {
                Csr = csr.Csr,
                Phone = options.Account.GwId,
            }, cancellationToken);
        options.Account.ClientCertificate = response.Encoded;
        options.Account.ClientCertificateKey = csr.PrivateKey;
    }

    protected async Task SaveConfigAsync(Ora2MqttOptions options, CancellationToken cancellationToken)
    {
        var serializer = new Serializer();
        await using var configFile = File.OpenWrite(ConfigFile);
        configFile.SetLength(0);
        await using var writer = new StreamWriter(configFile);
        serializer.Serialize(writer, options);
        await writer.FlushAsync();
        await configFile.FlushAsync(cancellationToken);
    }
}