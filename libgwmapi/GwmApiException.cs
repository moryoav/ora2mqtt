using System.Net;

namespace libgwmapi;

public class GwmApiException : Exception
{
    public GwmApiException(string code, string description) : this(code, description, null, null)
    {
    }

    public GwmApiException(string code, string description, HttpStatusCode? statusCode, string path)
        : base(Describe(code, description, statusCode, path))
    {
        Code = code;
        StatusCode = statusCode;
        Path = path;
    }

    public string Code { get; }

    /// <summary>HTTP status, when the failure was not a well-formed GWM error body.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Request path, so a wrong API version is visible in the message.</summary>
    public string Path { get; }

    private static string Describe(string code, string description, HttpStatusCode? statusCode, string path)
    {
        var message = String.IsNullOrWhiteSpace(description) ? "(no description)" : description;
        if (!String.IsNullOrEmpty(code))
        {
            message = $"{code} {message}";
        }
        if (statusCode is null) return message;
        return String.IsNullOrEmpty(path)
            ? $"{message} [HTTP {(int)statusCode}]"
            : $"{message} [HTTP {(int)statusCode} on {path}]";
    }
}
