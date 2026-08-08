using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace libgwmapi;

/// <summary>
/// Adds the GWM request signature headers (gwm-auth-appkey/nonce/timestamp/sign) that the
/// My GWM app started requiring in 2026-08. The signature is plain SHA-256 (no HMAC) over:
///
///   METHOD + relativePath
///     + "gwm-auth-appkey:" + appkey + "gwm-auth-nonce:" + nonce + "gwm-auth-timestamp:" + ts
///     + params + secret
///
/// then whitespace-stripped, Uri.encodeComponent'd (Dart semantics), and hex-SHA-256'd.
/// params = sorted "key=value" of query params; for a JSON POST the whole body is one param
/// "json=" + rawBody. Reverse-engineered from libapp.so (RequestSign.getHeaders) and confirmed
/// against a Frida capture of the running app.
/// </summary>
public sealed class GwmSigningHandler : DelegatingHandler
{
    // EUHost.baseHost(): field_b = appkey, field_f = secret (prod)
    private readonly string _appKey;
    private readonly string _secret;
    private readonly Func<string> _timestamp;
    private readonly Func<string> _nonce;

    // Dart Uri.encodeComponent leaves these unescaped
    private static readonly bool[] Unreserved = BuildUnreserved();

    public GwmSigningHandler(string appKey = "1874226830",
                             string secret = "1eb6caa16ff203c96daf7f06309b8998",
                             Func<string> timestamp = null, Func<string> nonce = null)
    {
        _appKey = appKey;
        _secret = secret;
        _timestamp = timestamp ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        _nonce = nonce ?? DefaultNonce;
    }

    private string DefaultNonce()
        => Sha256Hex(_timestamp()).Substring(0, 16);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ts = _timestamp();
        var nonce = _nonce();
        var method = request.Method.Method.ToUpperInvariant();
        var path = request.RequestUri!.AbsolutePath;               // relative path, not full URL

        // params: sorted query params + JSON body as the single "json" key
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        foreach (string key in query)
        {
            if (key is not null) map[key] = query[key] ?? string.Empty;
        }
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrEmpty(body)) map["json"] = body;   // sign the exact bytes we send
        }
        var parameters = string.Concat(map.Select(kv => kv.Key + "=" + kv.Value.Replace(" ", "")));

        var raw = method + path
                + "gwm-auth-appkey:" + _appKey
                + "gwm-auth-nonce:" + nonce
                + "gwm-auth-timestamp:" + ts
                + parameters + _secret;
        raw = StripWhitespace(raw);
        var sign = Sha256Hex(EncodeComponent(raw));

        request.Headers.Remove("gwm-auth-appkey");
        request.Headers.Remove("gwm-auth-nonce");
        request.Headers.Remove("gwm-auth-timestamp");
        request.Headers.Remove("gwm-auth-sign");
        request.Headers.TryAddWithoutValidation("gwm-auth-appkey", _appKey);
        request.Headers.TryAddWithoutValidation("gwm-auth-nonce", nonce);
        request.Headers.TryAddWithoutValidation("gwm-auth-timestamp", ts);
        request.Headers.TryAddWithoutValidation("gwm-auth-sign", sign);

        return await base.SendAsync(request, cancellationToken);
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is not (' ' or '\t' or '\r' or '\n' or '\f' or '\v')) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string EncodeComponent(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b < 128 && Unreserved[b]) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static bool[] BuildUnreserved()
    {
        var t = new bool[128];
        for (var c = 'A'; c <= 'Z'; c++) t[c] = true;
        for (var c = 'a'; c <= 'z'; c++) t[c] = true;
        for (var c = '0'; c <= '9'; c++) t[c] = true;
        foreach (var c in "-_.!~*'()") t[c] = true;
        return t;
    }
}
