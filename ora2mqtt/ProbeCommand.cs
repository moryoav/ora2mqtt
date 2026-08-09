using CommandLine;
using YamlDotNet.Serialization;

namespace ora2mqtt;

[Verb("probe", HelpText = "send a raw request, for exploring unknown endpoints")]
public class ProbeCommand : BaseCommand
{
    [Option("path", Required = true, HelpText = "path relative to the v1.0 base, e.g. ../v2.0/userAuth/captcha/gen")]
    public string Path { get; set; }

    [Option("method", Default = "POST", HelpText = "http method")]
    public string Method { get; set; }

    [Option("body", HelpText = "raw json body")]
    public string Body { get; set; }

    [Option("token", Default = false, HelpText = "send the access token from the config file")]
    public bool WithToken { get; set; }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        Setup();
        var deserializer = new DeserializerBuilder()
            .WithCaseInsensitivePropertyMatching()
            .Build();
        Ora2MqttOptions config;
        using (var file = File.OpenText(ConfigFile))
        {
            config = deserializer.Deserialize<Ora2MqttOptions>(file);
        }

        var client = ConfigureApiClient(config);
        client.SetAccessToken(WithToken ? config.Account.AccessToken : "");
        Console.WriteLine(await client.SendRawAsync(Method, Path, Body, cancellationToken));
        return 0;
    }
}
