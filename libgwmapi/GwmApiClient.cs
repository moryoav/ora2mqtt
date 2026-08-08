using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace libgwmapi;

public partial class GwmApiClient
{
    public static readonly string H5HttpClientName = "eu-h5-gateway";
    public static readonly string AppHttpClientName = "eu-app-gateway";
    private readonly HttpClient _h5Client;
    private readonly HttpClient _appClient;
    private readonly Uri _h5V2Base;
    private readonly ILogger<GwmApiClient> _logger;

    // My GWM app identity (v2). terminal/brand pair is validated by the backend
    // (mismatch -> 551008). appId 1 / enterpriseId CC01 / secVersion 2.0 as sent by the app.
    private static void AddIdentityHeaders(HttpClient client)
    {
        void Set(string name, string fallback)
        {
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.Add(name, Header(name, fallback));
        }
        Set("rs", "2");
        Set("terminal", "GW_APP_GWM");
        Set("brand", "6");
        Set("appId", "1");
        Set("enterpriseId", "CC01");
        Set("channel", "APP");
        Set("cVer", "1.3.0");
        Set("secVersion", "2.0");
        Set("systemType", "1");
    }

    public GwmApiClient(IHttpClientFactory factory, ILoggerFactory loggerFactory)
        : this(factory.CreateClient(H5HttpClientName), factory.CreateClient(AppHttpClientName), loggerFactory)
    {
    }

    public GwmApiClient(HttpClient h5Client, HttpClient appClient, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GwmApiClient>();
        _h5Client = h5Client;
        AddIdentityHeaders(_h5Client);
        _h5Client.DefaultRequestHeaders.Add("language", Header("language", "en"));
        _h5Client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("GWM_H5_BASE")
            ?? "https://eu-h5-gateway.gwmcloud.com/app-api/api/v1.0/");
        _h5V2Base = new Uri("https://eu-h5-gateway.gwmcloud.com/app-api/api/v2.0/");

        _appClient = appClient;
        AddIdentityHeaders(_appClient);
        _appClient.BaseAddress = new Uri("https://eu-app-gateway.gwmcloud.com/app-api/api/v1.0/");

        AddExtraHeaders(_h5Client);
        AddExtraHeaders(_appClient);
        LogHeaders();
    }

    //allows probing which client identity the GWM backend still accepts,
    //e.g. GWM_HEADER_TERMINAL=GW_APP_GWM GWM_HEADER_BRAND=6
    private static string Header(string name, string fallback)
    {
        return Environment.GetEnvironmentVariable($"GWM_HEADER_{name.ToUpperInvariant()}") ?? fallback;
    }

    //GWM_EXTRA_HEADERS="enterpriseId=1,sign=deadbeef" - probe headers we do not send yet
    private static void AddExtraHeaders(HttpClient client)
    {
        var extra = Environment.GetEnvironmentVariable("GWM_EXTRA_HEADERS");
        if (String.IsNullOrWhiteSpace(extra)) return;
        foreach (var pair in extra.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            var name = pair[..separator].Trim();
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.Add(name, pair[(separator + 1)..]);
        }
    }

    private void LogHeaders()
    {
        _logger.LogInformation("GWM client identity: terminal={Terminal} brand={Brand} rs={Rs} systemType={SystemType} cver='{Cver}'",
            _h5Client.DefaultRequestHeaders.GetValues("terminal").First(),
            _h5Client.DefaultRequestHeaders.GetValues("brand").First(),
            _h5Client.DefaultRequestHeaders.GetValues("rs").First(),
            _h5Client.DefaultRequestHeaders.GetValues("systemType").First(),
            _h5Client.DefaultRequestHeaders.GetValues("cVer").First());
    }

    private void SetOnBoth(string name, string value)
    {
        foreach (var client in new[] { _h5Client, _appClient })
        {
            client.DefaultRequestHeaders.Remove(name);
            if (value is not null) client.DefaultRequestHeaders.Add(name, value);
        }
    }

    public string Language
    {
        get => _h5Client.DefaultRequestHeaders.GetValues("language").FirstOrDefault();
        set => SetOnBoth("language", value);
    }

    public string Country
    {
        get => _h5Client.DefaultRequestHeaders.GetValues("country").FirstOrDefault();
        set
        {
            SetOnBoth("country", value);
            //the app sends regionCode next to country (both "DE" for Germany)
            SetOnBoth("regionCode", value);
        }
    }

    // the app sends the deviceId header on every request
    public string DeviceId
    {
        set => SetOnBoth("deviceId", value);
    }

    public bool HasAccessToken
    {
        get
        {
            if (!_h5Client.DefaultRequestHeaders.TryGetValues("accessToken", out var token))
                return false;
            return token.Any(x => !String.IsNullOrEmpty(x));
        }
    }

    public void SetAccessToken(string accessToken)
    {
        _h5Client.DefaultRequestHeaders.Remove("accessToken");
        _h5Client.DefaultRequestHeaders.Add("accessToken", accessToken);

        _appClient.DefaultRequestHeaders.Remove("accessToken");
        _appClient.DefaultRequestHeaders.Add("accessToken", accessToken);
    }

    /// <summary>
    /// Sends a raw request and returns the unparsed response, for exploring
    /// endpoints whose request and response shapes are not known yet.
    /// </summary>
    public async Task<string> SendRawAsync(string method, string path, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await _h5Client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return $"{(int)response.StatusCode} {request.RequestUri}\n{content}";
    }

    private async Task PostH5Async<T>(string url, T body, CancellationToken cancellationToken)
    {
        var response = await _h5Client.PostAsJsonAsync(url, body, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    private async Task PostAppAsync<T>(string url, T body, CancellationToken cancellationToken)
    {
        var response = await _appClient.PostAsJsonAsync(url, body, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    private async Task<TOut> PostH5Async<TIn, TOut>(string url, TIn body, CancellationToken cancellationToken)
    {
        var response = await _h5Client.PostAsJsonAsync(url, body, cancellationToken);
        return await GetResponseAsync<TOut>(response, cancellationToken);
    }

    // v2 auth endpoints live under /app-api/api/v2.0/ on the same h5 host; posting to an
    // absolute uri overrides the client's v1.0 base while keeping identity headers + signing.
    private async Task<TOut> PostH5V2Async<TIn, TOut>(string path, TIn body, CancellationToken cancellationToken)
    {
        var response = await _h5Client.PostAsJsonAsync(new Uri(_h5V2Base, path), body, cancellationToken);
        return await GetResponseAsync<TOut>(response, cancellationToken);
    }

    private async Task PostH5V2Async<TIn>(string path, TIn body, CancellationToken cancellationToken)
    {
        var response = await _h5Client.PostAsJsonAsync(new Uri(_h5V2Base, path), body, cancellationToken);
        await CheckResponseAsync(response, cancellationToken);
    }

    private async Task<T> GetH5Async<T>(string url, CancellationToken cancellationToken)
    {
        var response = await _h5Client.GetAsync(url, cancellationToken);
        return await GetResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T> GetAppAsync<T>(string url, CancellationToken cancellationToken)
    {
        var response = await _appClient.GetAsync(url, cancellationToken);
        return await GetResponseAsync<T>(response, cancellationToken);
    }

    private async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ReadGwmResponseAsync<GwmResponse>(response, cancellationToken);
    }

    private async Task<T> GetResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var result = await ReadGwmResponseAsync<GwmResponse<T>>(response, cancellationToken);
        return result.Data;
    }

    private async Task<T> ReadGwmResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : GwmResponse
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(content);
        }

        T result;
        try
        {
            result = JsonSerializer.Deserialize<T>(content);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
            throw;
        }

        if (result is null)
        {
            response.EnsureSuccessStatusCode();
            throw new JsonException("GWM response body was empty.");
        }

        CheckResponse(result);
        response.EnsureSuccessStatusCode();
        return result;
    }

    private void CheckResponse(GwmResponse response)
    {
        if (response.Code != "000000")
        {
            throw new GwmApiException(response.Code, response.Description);
        }
    }

    private class GwmResponse
    {
        [JsonPropertyName("code")]
        public string Code { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }
    }

    private class GwmResponse<T>:GwmResponse
    {

        [JsonPropertyName("data")]
        public T Data { get; set; }
    }

    private class GwmArrayResponse<T>:GwmResponse
    {

        [JsonPropertyName("data")]
        public T[] Data { get; set; }
    }
}
