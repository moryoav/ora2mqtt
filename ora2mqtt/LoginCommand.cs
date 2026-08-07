using CommandLine;
using libgwmapi;
using libgwmapi.DTO.UserAuth;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace ora2mqtt;

[Verb("login", HelpText = "non interactive login, useful for debugging backend changes")]
public class LoginCommand : BaseCommand
{
    private ILogger<LoginCommand> _logger;

    [Option('e', "email", Required = true, HelpText = "account mail address")]
    public string Email { get; set; }

    [Option('p', "password", HelpText = "account password, omit when using --code")]
    public string Password { get; set; }

    [Option("code", HelpText = "4 digit verification code, requests a new one when omitted")]
    public string Code { get; set; }

    [Option("dry-run", Default = false, HelpText = "do not write the tokens to the config file")]
    public bool DryRun { get; set; }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        Setup();
        _logger = LoggerFactory.CreateLogger<LoginCommand>();
        if (!File.Exists(ConfigFile))
        {
            _logger.LogError($"config file ({ConfigFile}) missing");
            return 1;
        }
        var deserializer = new DeserializerBuilder()
            .WithCaseInsensitivePropertyMatching()
            .Build();
        Ora2MqttOptions config;
        using (var file = File.OpenText(ConfigFile))
        {
            config = deserializer.Deserialize<Ora2MqttOptions>(file);
        }

        var client = ConfigureApiClient(config);
        LoginAccountResponse token;
        if (String.IsNullOrEmpty(Code))
        {
            try
            {
                token = await client.LoginAccountAsync(new LoginAccountRequest
                {
                    Country = config.Country,
                    IsEncrypt = false,
                    DeviceId = config.DeviceId,
                    Model = "ora2mqtt",
                    PushToken = "",
                    Account = Email,
                    Password = Password,
                }, cancellationToken);
                _logger.LogInformation("password login succeeded, no verification code needed");
            }
            catch (GwmApiException e) when (e.Code == "110641")
            {
                _logger.LogWarning("GWM wants a verification code ({Message}). Requesting one...", e.Message);
                await client.GetSmsCodeAsync(new GetSmsCode { Email = Email }, cancellationToken);
                _logger.LogInformation("verification code sent, rerun with --code <code>");
                return 2;
            }
        }
        else
        {
            token = await client.LoginWithSmsAsync(new LoginWithSmsRequest
            {
                Email = Email,
                Country = config.Country,
                DeviceId = config.DeviceId,
                Model = "ora2mqtt",
                //loginAccount sends an empty string here, only loginWithSMS left it
                //null - GWM answers a generic error once the code check has passed
                PushToken = "",
                SmsCode = Code,
            }, cancellationToken);
            _logger.LogInformation("sms login succeeded");
        }

        config.Account.AccessToken = token.AccessToken;
        config.Account.RefreshToken = token.RefreshToken;
        config.Account.GwId = token.GwId;
        config.Account.BeanId = token.BeanId;
        if (DryRun)
        {
            _logger.LogInformation("dry run, config not written");
            return 0;
        }
        await SaveConfigAsync(config, cancellationToken);
        _logger.LogInformation("tokens written to {ConfigFile}", ConfigFile);
        return 0;
    }
}
