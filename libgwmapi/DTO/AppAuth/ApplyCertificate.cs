using System.Text.Json.Serialization;

namespace libgwmapi.DTO.AppAuth;

public class ApplyCertificateRequest
{
    [JsonPropertyName("csr")]
    public string Csr { get; set; }

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";
}

public class ApplyCertificateResponse
{
    // base64 DER of the signed client certificate
    [JsonPropertyName("encoded")]
    public string Encoded { get; set; }

    [JsonPropertyName("issuer")]
    public string Issuer { get; set; }

    [JsonPropertyName("notAfter")]
    public string NotAfter { get; set; }
}
