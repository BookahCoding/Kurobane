using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kurobane.Core;
using Xunit;

namespace Kurobane.Tests;

public class ZeroClawClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void NormalizeBaseUriAddsTrailingSlashAndDropsQuery()
    {
        var uri = ZeroClawClient.NormalizeBaseUri(new Uri("http://100.64.0.2:42617/dashboard?x=1#frag"));
        Assert.Equal("http://100.64.0.2:42617/dashboard/", uri.ToString());
    }

    [Fact]
    public async Task RequestsCarryBearerTokenOnceSet()
    {
        var handler = new StubHandler(_ => Json("{\"health\":{\"status\":\"ok\"}}"));
        using var client = new ZeroClawClient(new Uri("http://gw.local:42617/"), handler);
        client.SetToken("tok-1");
        var resp = await client.GetAsync("/api/health", default);
        Assert.Equal("ok", resp["health"]!["status"]!.GetValue<string>());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("tok-1", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal("/api/health", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PairStoresTokenAndSendsDeviceMetadata()
    {
        string? seenBody = null;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/pair")
            {
                seenBody = req.Content!.ReadAsStringAsync().Result;
                return Json("{\"paired\":true,\"persisted\":true,\"token\":\"tok-xyz\"}");
            }
            return Json("{}", HttpStatusCode.NotFound);
        });
        using var client = new ZeroClawClient(new Uri("http://gw.local:42617/"), handler);
        var token = await client.PairAsync("123456", "Kurobane on Windows", default);
        Assert.Equal("tok-xyz", token);
        Assert.Equal("tok-xyz", client.Token);
        Assert.Contains("\"code\":\"123456\"", seenBody);
        Assert.Contains("\"device_type\":\"windows-desktop\"", seenBody);
    }

    [Fact]
    public async Task PairThrowsPairExceptionOnWrongCode()
    {
        var handler = new StubHandler(_ => Json("{\"paired\":false,\"error\":\"Invalid or expired pairing code\"}", HttpStatusCode.BadRequest));
        using var client = new ZeroClawClient(new Uri("http://gw.local:42617/"), handler);
        var ex = await Assert.ThrowsAsync<ZeroClawPairException>(() => client.PairAsync("000000", "dev", default));
        Assert.Contains("Invalid or expired", ex.Message);
        Assert.Null(client.Token);
    }

    [Fact]
    public async Task NonSuccessStatusThrowsApiExceptionWithBody()
    {
        var handler = new StubHandler(_ => Json("{\"error\":\"Unauthorized\"}", HttpStatusCode.Unauthorized));
        using var client = new ZeroClawClient(new Uri("http://gw.local:42617/"), handler);
        var ex = await Assert.ThrowsAsync<ZeroClawApiException>(() => client.GetAsync("/api/status", default));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Unauthorized", ex.Body);
    }
}
