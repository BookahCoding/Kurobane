using System.Net;
using System.Text;
using Kurobane.Core;
using Xunit;

namespace Kurobane.Tests;

public class SessionsApiTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Path)> Calls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ListParsesSessionEntries()
    {
        var handler = new StubHandler(_ => Json("""{"sessions":[
            {"session_id":"abc","session_key":"gw_abc","created_at":"2026-01-01T10:00:00Z","last_activity":"2026-01-02T11:30:00Z","message_count":7,"agent_alias":"default","channel_id":null,"name":"Morning chat"},
            {"session_id":"def","session_key":"gw_def","created_at":"2026-01-03T09:00:00Z","last_activity":"2026-01-03T09:05:00Z","message_count":2,"agent_alias":"coder","channel_id":"telegram.bot"}
        ]}"""));
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        var sessions = await SessionsApi.ListAsync(client, default);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("abc", sessions[0].SessionId);
        Assert.Equal("gw_abc", sessions[0].SessionKey);
        Assert.Equal(7, sessions[0].MessageCount);
        Assert.Equal("default", sessions[0].AgentAlias);
        Assert.Equal("Morning chat", sessions[0].DisplayName);
        Assert.Equal("def", sessions[1].DisplayName); // falls back to id when unnamed
        Assert.Equal("telegram.bot", sessions[1].ChannelId);
        Assert.Equal(("GET", "/api/sessions"), handler.Calls[0]);
    }

    [Fact]
    public async Task MessagesParsesTranscript()
    {
        var handler = new StubHandler(_ => Json("""{"session_id":"abc","messages":[
            {"role":"user","content":"hi","created_at":"2026-01-01T10:00:00Z"},
            {"role":"assistant","content":"hello!","created_at":"2026-01-01T10:00:05Z"}
        ],"session_persistence":true}"""));
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        var msgs = await SessionsApi.MessagesAsync(client, "abc", default);
        Assert.Equal(2, msgs.Count);
        Assert.Equal("user", msgs[0].Role);
        Assert.Equal("hello!", msgs[1].Content);
        Assert.True(msgs[1].CreatedAt > DateTimeOffset.MinValue);
        Assert.Equal("/api/sessions/abc/messages", handler.Calls[0].Path);
    }

    [Fact]
    public async Task DeleteRenameAbortHitExpectedRoutes()
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var client = new ZeroClawClient(new Uri("http://gw.local/"), handler);
        await SessionsApi.DeleteAsync(client, "a b", default);
        await SessionsApi.RenameAsync(client, "a b", "New name", default);
        await SessionsApi.AbortAsync(client, "a b", default);
        Assert.Equal(("DELETE", "/api/sessions/a%20b"), handler.Calls[0]);
        Assert.Equal(("PUT", "/api/sessions/a%20b"), handler.Calls[1]);
        Assert.Equal(("POST", "/api/sessions/a%20b/abort"), handler.Calls[2]);
    }
}
