using libgwmapi.DTO.User;
using libgwmapi.DTO.UserAuth;

namespace libgwmapi;

public partial class GwmApiClient
{
    public Task<CustomerServicePhone> GetCustomerServicePhoneAsync(string countryCode, CancellationToken cancellationToken)
    {
        return GetH5Async<CustomerServicePhone>($"userAuth/customerServicePhone?countryCode={countryCode}",
            cancellationToken);
    }

    public Task GetSmsCodeAsync(GetSmsCode request, CancellationToken cancellationToken)
    {
        return PostH5Async("userAuth/getSMSCode", request, cancellationToken);
    }

    public Task<LoginAccountResponse> LoginWithSmsAsync(LoginWithSmsRequest request, CancellationToken cancellationToken)
    {
        return PostH5Async<LoginWithSmsRequest, LoginAccountResponse>("userAuth/loginWithSMS", request, cancellationToken);
    }

    public Task<LoginAccountResponse> LoginAccountAsync(LoginAccountRequest request, CancellationToken cancellationToken)
    {
        return PostH5Async<LoginAccountRequest, LoginAccountResponse>("userAuth/loginAccount", request, cancellationToken);
    }

    public Task CheckSecurityPasswordAsync(CheckSecurityPassword request, CancellationToken cancellationToken)
    {
        return PostH5Async("userAuth/checkSecurityPassword", request, cancellationToken);
    }

    public Task AddAppDeviceInfoAsync(AddAppDevice request, CancellationToken cancellationToken)
    {
        return PostH5Async("userAuth/addAppDeviceInfo", request, cancellationToken);
    }

    public Task<RefreshTokenResponse> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        // v2: refreshToken works again once requests are signed
        return PostH5V2Async<RefreshTokenRequest, RefreshTokenResponse>("userAuth/refreshToken", request, cancellationToken);
    }

    // --- v2 (My GWM) auth ---

    public Task<LoginAccountResponse> LoginWithPasswordAsync(EuLoginWithPasswordRequest request, CancellationToken cancellationToken)
    {
        return PostH5V2Async<EuLoginWithPasswordRequest, LoginAccountResponse>("userAuth/loginWithPassword", request, cancellationToken);
    }

    public Task GetVerifyCodeAsync(EuGetVerifyCodeRequest request, CancellationToken cancellationToken)
    {
        return PostH5V2Async("userAuth/getVerifyCode", request, cancellationToken);
    }

    public Task CheckVerifyCodeAsync(EuCheckVerifyCodeRequest request, CancellationToken cancellationToken)
    {
        return PostH5V2Async("userAuth/checkVerifyCode", request, cancellationToken);
    }
}