using System.Text.Json.Serialization;

namespace libgwmapi.DTO.UserAuth;

// v2 (My GWM) auth request bodies. Field names are the GWM API's; the login response
// reuses LoginAccountResponse (accessToken/refreshToken/gwId/beanId).

public class EuLoginWithPasswordRequest
{
    [JsonPropertyName("account")]
    public string Account { get; set; }

    [JsonPropertyName("accountType")]
    public string AccountType { get; set; } = "2"; // 2 = e-mail

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } // calling code, e.g. "+49"

    [JsonPropertyName("agreement")]
    public int[] Agreement { get; set; } = { 1, 2 };

    [JsonPropertyName("password")]
    public string Password { get; set; }

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; }

    [JsonPropertyName("appType")]
    public string AppType { get; set; } = "0";

    [JsonPropertyName("pushToken")]
    public string PushToken { get; set; } = "";

    [JsonPropertyName("country")]
    public string Country { get; set; }

    [JsonPropertyName("verifyCode")]
    public string VerifyCode { get; set; }

    [JsonPropertyName("validCodeMode")]
    public string ValidCodeMode { get; set; }
}

public class EuGetVerifyCodeRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "17";

    [JsonPropertyName("account")]
    public string Account { get; set; }

    [JsonPropertyName("accountType")]
    public string AccountType { get; set; } = "2";

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; }

    [JsonPropertyName("validCodeMode")]
    public int ValidCodeMode { get; set; } = 1;

    [JsonPropertyName("operateCode")]
    public string OperateCode { get; set; } = "";

    [JsonPropertyName("captchaType")]
    public string CaptchaType { get; set; } = "";

    [JsonPropertyName("captchaId")]
    public string CaptchaId { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";
}

public class EuCheckVerifyCodeRequest
{
    [JsonPropertyName("account")]
    public string Account { get; set; }

    [JsonPropertyName("verifyCode")]
    public string VerifyCode { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "17";

    [JsonPropertyName("accountType")]
    public string AccountType { get; set; } = "2";

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; }

    [JsonPropertyName("validCodeMode")]
    public int ValidCodeMode { get; set; } = 1;
}
