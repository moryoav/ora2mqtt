using libgwmapi.DTO.User;
using libgwmapi.DTO.UserAuth;

namespace libgwmapi;

public partial class GwmApiClient
{
    public Task CheckSecurityPasswordAsync(CheckSecurityPassword request, CancellationToken cancellationToken)
    {
        return PostH5Async("userAuth/checkSecurityPassword", request, cancellationToken);
    }

    public Task<RefreshTokenResponse> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        // refreshToken stayed on v1.0 when the rest of auth moved to v2 - v2.0 answers 404.
        // It needs the signed headers (handled by GwmSigningHandler) and the expired
        // accessToken header alongside the body.
        return PostH5Async<RefreshTokenRequest, RefreshTokenResponse>("userAuth/refreshToken", request, cancellationToken);
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