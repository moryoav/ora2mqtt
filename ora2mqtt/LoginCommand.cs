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
        var callingCode = config.Country switch { "GB" => "+44", "EE" => "+372", _ => "+49" };
        var request = new EuLoginWithPasswordRequest
        {
            Account = Email,
            Password = Password,
            Country = config.Country,
            CountryCode = callingCode,
            DeviceId = config.DeviceId,
        };
        LoginAccountResponse token;
        if (String.IsNullOrEmpty(Code))
        {
            try
            {
                token = await client.LoginWithPasswordAsync(request, cancellationToken);
                _logger.LogInformation("v2 password login succeeded, no verification code needed");
            }
            catch (GwmApiException e) when (e.Code is "110641" or "308103")
            {
                _logger.LogWarning("GWM wants a verification code ({Message}). Requesting one...", e.Message);
                await client.GetVerifyCodeAsync(new EuGetVerifyCodeRequest { Account = Email, CountryCode = callingCode }, cancellationToken);
                _logger.LogInformation("verification code sent, rerun with --code <code>");
                return 2;
            }
        }
        else
        {
            await client.CheckVerifyCodeAsync(new EuCheckVerifyCodeRequest { Account = Email, CountryCode = callingCode, VerifyCode = Code }, cancellationToken);
            request.VerifyCode = Code;
            request.ValidCodeMode = "1";
            token = await client.LoginWithPasswordAsync(request, cancellationToken);
            _logger.LogInformation("v2 verify-code login succeeded");
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
