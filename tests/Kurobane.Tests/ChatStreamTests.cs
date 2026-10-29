using System.Text.Json.Nodes;
using Kurobane.Core;
using Xunit;

namespace Kurobane.Tests;

public class ChatStreamTests
{
    [Fact]
    public void WsUriUsesWsSchemeForHttpBaseAndKeepsPort()
    {
        var uri = ChatStream.BuildWsUri(new Uri("http://100.64.0.2:42617/"), "default", "s1", "My chat");
        Assert.Equal("agent_alias=default&session_id=s1&name=My%20chat", uri.Query.TrimStart('?'));
    }

    [Fact]
    public void WsUriUsesWssForHttpsBase()
    {
        var uri = ChatStream.BuildWsUri(new Uri("https://zc.example.com/"), "coder", "s 2", null);
        Assert.Equal("wss", uri.Scheme);
        Assert.DoesNotContain("name=", uri.Query);
        Assert.Contains("agent_alias=coder", uri.Query);
        Assert.Contains("session_id=s%202", uri.Query.TrimStart('?'));
    }

    private static JsonObject Frame(string type, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in fields)
            o[k] = v switch
            {
                null => null,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(v.ToString()),
            };
        return o;
    }

    [Fact]
    public void ClassifyMapsEveryKnownFrameType()
    {
        Assert.IsType<WsEvent.SessionStart>(ChatStream.Classify(Frame("session_start", ("session_id", "s1"), ("resumed", true), ("message_count", 3), ("name", "n"))));
        Assert.IsType<WsEvent.Connected>(ChatStream.Classify(Frame("connected")));
        var chunk = Assert.IsType<WsEvent.Chunk>(ChatStream.Classify(Frame("chunk", ("content", "hello"))));
        Assert.Equal("hello", chunk.Content);
        Assert.IsType<WsEvent.Thinking>(ChatStream.Classify(Frame("thinking", ("content", "hmm"))));
        var tool = Assert.IsType<WsEvent.ToolCall>(ChatStream.Classify(Frame("tool_call", ("id", "t1"), ("name", "shell"), ("args", "{}"))));
        Assert.Equal("shell", tool.Name);
        Assert.IsType<WsEvent.ToolResult>(ChatStream.Classify(Frame("tool_result", ("id", "t1"), ("name", "shell"), ("output", "ok"))));
        var appr = Assert.IsType<WsEvent.ApprovalRequest>(ChatStream.Classify(Frame("approval_request", ("request_id", "r1"), ("tool", "shell"), ("arguments_summary", "rm -rf"), ("timeout_secs", 30))));
        Assert.Equal("r1", appr.RequestId);
        Assert.Equal(30, appr.TimeoutSecs);
        var done = Assert.IsType<WsEvent.Done>(ChatStream.Classify(Frame("done", ("full_response", "all done"), ("model", "glm-5"), ("provider", "zai.default"), ("cost_usd", 0.01), ("tokens_used", 42))));
        Assert.Equal("all done", done.FullResponse);
        Assert.Equal(0.01, done.CostUsd);
        Assert.Equal(42, done.TokensUsed);
        Assert.IsType<WsEvent.Aborted>(ChatStream.Classify(Frame("aborted")));
        Assert.IsType<WsEvent.AgentStart>(ChatStream.Classify(Frame("agent_start", ("model_provider", "zai.default"), ("model", "glm-5"))));
        Assert.IsType<WsEvent.AgentEnd>(ChatStream.Classify(Frame("agent_end", ("model_provider", "zai.default"), ("model", "glm-5"))));
        var err = Assert.IsType<WsEvent.Error>(ChatStream.Classify(Frame("error", ("message", "boom"), ("code", "X"))));
        Assert.Equal("boom", err.Message);
        Assert.Equal("X", err.Code);
        Assert.IsType<WsEvent.Other>(ChatStream.Classify(Frame("cron_result", ("x", 1))));
    }
}
