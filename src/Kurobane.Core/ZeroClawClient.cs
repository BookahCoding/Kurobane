using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kurobane.Core;

/// <summary>
/// Low-level HTTP client for a ZeroClaw gateway. Owns the bearer token and the base address;
/// every request carries <c>Authorization: Bearer &lt;token&gt;</c> once paired.
/// Construct with an <see cref="HttpMessageHandler"/> to inject tests.
/// </summary>
public sealed class ZeroClawClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private string? _token;

    public ZeroClawClient(Uri baseUri, HttpMessageHandler? handler = null, HttpClient? http = null)
    {
        _baseUri = NormalizeBaseUri(baseUri);
        _http = http ?? new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = _baseUri };
    }

    /// <summary>Trailing-slash base address, e.g. https://100.64.0.2:42617/</summary>
    public Uri BaseUri => _baseUri;

    public string? Token => _token;

    public void SetToken(string? token) => _token = string.IsNullOrWhiteSpace(token) ? null : token;

    public static Uri NormalizeBaseUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/", Query = "", Fragment = "" };
        return builder.Uri;
    }

    /// <summary>True when a bearer token is set and a request was built with it.</summary>
    public bool HasToken => _token is { Length: > 0 };

    public HttpRequestMessage Request(HttpMethod method, string relativePath, JsonNode? body = null)
    {
        var path = relativePath.TrimStart('/');
        var req = new HttpRequestMessage(method, new Uri(_baseUri, path));
        if (_token is { Length: > 0 } t)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        return req;
    }

    public async Task<JsonNode> SendAsync(HttpMethod method, string relativePath, JsonNode? body,
        CancellationToken ct, HttpStatusCode[]? accept = null)
    {
        using var req = Request(method, relativePath, body);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContent, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && (accept is null || !accept.Contains(resp.StatusCode)))
            throw new ZeroClawApiException(resp.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException e)
        {
            throw new ZeroClawApiException(resp.StatusCode, $"Non-JSON response ({(int)resp.StatusCode}): {text[..Math.Min(200, text.Length)]}", e);
        }
    }

    public Task<JsonNode> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonNode> PostAsync(string path, JsonNode? body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, body, ct);
    public Task<JsonNode> PutAsync(string path, JsonNode? body, CancellationToken ct) => SendAsync(HttpMethod.Put, path, body, ct);
    public Task<JsonNode> DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);
    public Task<JsonNode> PatchAsync(string path, JsonNode? body, CancellationToken ct) => SendAsync(new HttpMethod("PATCH"), path, body, ct);

    /// <summary>POST /api/pair with a pairing code. On success stores and returns the bearer token.</summary>
    public async Task<string> PairAsync(string code, string deviceName, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["code"] = code.Trim(),
            ["device_name"] = deviceName,
            ["device_type"] = "windows-desktop",
        };
        var resp = await SendAsync(HttpMethod.Post, "/api/pair", body, ct,
            accept: [HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests]).ConfigureAwait(false);
        if (resp["paired"]?.GetValue<bool>() != true)
            throw new ZeroClawPairException(resp["error"]?.GetValue<string>() ?? "Invalid or expired pairing code",
                resp["retry_after"]?.GetValue<int>());
        var token = resp["token"]?.GetValue<string>()
            ?? throw new ZeroClawPairException("Gateway paired but returned no token");
        SetToken(token);
        return token;
    }

    /// <summary>GET /api/status — used as the "test connection" probe after pairing.</summary>
    public Task<JsonNode> StatusAsync(CancellationToken ct) => GetAsync("/api/status", ct);

    /// <summary>GET /api/health.</summary>
    public Task<JsonNode> HealthAsync(CancellationToken ct) => GetAsync("/api/health", ct);

    public void Dispose() => _http.Dispose();
}

public sealed class ZeroClawApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Body { get; }
    public ZeroClawApiException(HttpStatusCode statusCode, string body, Exception? inner = null)
        : base($"ZeroClaw gateway returned {(int)statusCode} {statusCode}: {Shorten(body)}", inner)
    {
        StatusCode = statusCode;
        Body = body;
    }
    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "…";
}

public sealed class ZeroClawPairException : Exception
{
    public int? RetryAfterSeconds { get; }
    public ZeroClawPairException(string message, int? retryAfter = null) : base(message) => RetryAfterSeconds = retryAfter;
}
