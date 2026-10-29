using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kurobane.Core;
using Xunit;

namespace Kurobane.Tests;

public class ConfigApiTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Uri, string? Body)> Calls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(),
                request.Content is null ? null : request.Content.ReadAsStringAsync().Result));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SetPropPutsPathAndValue()
    {
        var handler = new StubHandler(_ => Json("""{"path":"gateway.port","value":"42617","warnings":[]}"""));
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        var resp = await ConfigApi.SetPropAsync(client, "gateway.port", JsonValue.Create(42617), default);
        Assert.Equal("42617", resp["value"]!.GetValue<string>());
        var call = handler.Calls[0];
        Assert.Equal("PUT", call.Method);
        Assert.Equal("/api/config/prop", new Uri(call.Uri).AbsolutePath);
        Assert.Contains("\"path\":\"gateway.port\"", call.Body);
        Assert.Contains("\"value\":42617", call.Body);
    }

    [Fact]
    public async Task GetPropEncodesPathInQuery()
    {
        var handler = new StubHandler(_ => Json("""{"path":"agents.default.enabled","value":"true","warnings":[]}"""));
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        await ConfigApi.GetPropAsync(client, "agents.default.enabled", default);
        Assert.Contains("/api/config/prop?path=agents.default.enabled", handler.Calls[0].Uri);
    }

    [Fact]
    public async Task AgentsAreReadFromConfigMap()
    {
        var handler = new StubHandler(_ => Json("""{"agents":{
            "default":{"enabled":true,"channels":["telegram.bot"],"model_provider":"zai.main"},
            "coder":{"enabled":false,"channels":[],"model_provider":""}
        }}"""));
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        var agents = await AgentsApi.ListAsync(client, default);
        Assert.Equal(2, agents.Count);
        Assert.Equal("coder", agents[0].Alias); // ordered by alias
        Assert.False(agents[0].Enabled);
        Assert.Equal("default", agents[1].Alias);
        Assert.Equal("zai.main", agents[1].ModelProvider);
        Assert.Equal(new[] { "telegram.bot" }, agents[1].Channels);
    }

    [Theory]
    [InlineData("****")]
    [InlineData("***masked***")]
    public void MaskedSecretDetection(string masked) => Assert.True(ConfigApi.IsMaskedSecretValue(JsonValue.Create(masked)));

    [Fact]
    public void RealValuesAreNotMasked() => Assert.False(ConfigApi.IsMaskedSecretValue(JsonValue.Create("sk-real-key")));
}
