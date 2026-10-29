using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kurobane.Core;

/// <summary>One event from the gateway chat WebSocket, classified from its `type` field.</summary>
public abstract record WsEvent
{
    public sealed record SessionStart(string SessionId, bool Resumed, int MessageCount, string? Name) : WsEvent;
    public sealed record Connected : WsEvent;
    public sealed record Chunk(string Content) : WsEvent;
    public sealed record Thinking(string Content) : WsEvent;
    public sealed record ToolCall(string Id, string Name, string Args) : WsEvent;
    public sealed record ToolResult(string Id, string Name, string Output) : WsEvent;
    public sealed record ApprovalRequest(string RequestId, string Tool, string ArgumentsSummary, int TimeoutSecs) : WsEvent;
    public sealed record Done(string FullResponse, string? Model, string? Provider, double? CostUsd, long? TokensUsed) : WsEvent;
    public sealed record Aborted : WsEvent;
    public sealed record AgentStart(string? ModelProvider, string? Model) : WsEvent;
    public sealed record AgentEnd(string? ModelProvider, string? Model) : WsEvent;
    public sealed record Error(string Message, string? Code) : WsEvent;
    public sealed record Other(JsonObject Raw) : WsEvent;
}

/// <summary>
/// WebSocket chat client for <c>/ws/chat?agent_alias=…&session_id=…&name=…</c>.
/// Wire protocol (zeroclaw.v1): connect, send {"type":"message","content":…},
/// receive chunk/thinking/tool_call/tool_result/approval_request/done/aborted/error frames.
/// The bearer token rides either the Authorization header or the Sec-WebSocket-Protocol
/// `bearer.&lt;token&gt;` subprotocol (browsers cannot set headers on WebSocket).
/// </summary>
public sealed class ChatStream : IAsyncDisposable
{
    public const string WsProtocol = "zeroclaw.v1";

    private readonly ClientWebSocket _ws = new();
    private readonly Uri _wsUri;
    private readonly string? _token;

    private ChatStream(Uri wsUri, string? token)
    {
        _wsUri = wsUri;
        _token = token;
    }

    public static Uri BuildWsUri(Uri baseUri, string agentAlias, string sessionId, string? name)
    {
        var scheme = baseUri.Scheme == "https" || baseUri.Scheme == "wss" ? "wss" : "ws";
        var builder = new UriBuilder(baseUri) { Scheme = scheme, Path = "/ws/chat", Query = "" };
        var query = $"agent_alias={Uri.EscapeDataString(agentAlias)}&session_id={Uri.EscapeDataString(sessionId)}";
        if (!string.IsNullOrWhiteSpace(name)) query += $"&name={Uri.EscapeDataString(name)}";
        // UriBuilder.Query unescapes; assign via the string form so %20 survives.
        return new Uri(builder.Uri.GetLeftPart(UriPartial.Authority) + "/ws/chat?" + query);
    }

    public static ChatStream Create(Uri baseUri, string? token, string agentAlias, string sessionId, string? name)
        => new(BuildWsUri(baseUri, agentAlias, sessionId, name), token);

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_token}");
        // Offer both the app protocol and the bearer subprotocol; the gateway accepts either.
        _ws.Options.AddSubProtocol(WsProtocol);
        if (_token is { Length: > 0 }) _ws.Options.AddSubProtocol($"bearer.{_token}");
        await _ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false);
    }

    public Task SendMessageAsync(string content, CancellationToken ct)
        => SendJsonAsync(new JsonObject { ["type"] = "message", ["content"] = content }, ct);

    public Task SendApprovalAsync(string requestId, string decision, CancellationToken ct)
        => SendJsonAsync(new JsonObject { ["type"] = "approval_response", ["request_id"] = requestId, ["decision"] = decision }, ct);

    private async Task SendJsonAsync(JsonObject frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    /// <summary>Reads frames until the socket closes. Each frame is classified into a <see cref="WsEvent"/>.</summary>
    public async IAsyncEnumerable<WsEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (_ws.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                    yield break;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            WsEvent? evt = null;
            try
            {
                var json = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JsonObject;
                if (json is not null) evt = Classify(json);
            }
            catch (JsonException) { /* ignore non-JSON frames */ }
            if (evt is not null) yield return evt;
        }
    }

    private static double? Num(JsonNode? n)
        => n is not null && n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.GetValue<double>() : null;

    private static long? Tokens(JsonNode? n)
        => n is not null && n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (long)n.GetValue<double>() : null;

    public static WsEvent Classify(JsonObject frame)
    {
        var type = frame["type"]?.GetValue<string>() ?? "";
        string? Str(string k) => frame[k]?.GetValue<string>();
        return type switch
        {
            "session_start" => new WsEvent.SessionStart(
                Str("session_id") ?? "", frame["resumed"]?.GetValue<bool>() ?? false,
                frame["message_count"]?.GetValue<int>() ?? 0, Str("name")),
            "connected" => new WsEvent.Connected(),
            "chunk" => new WsEvent.Chunk(Str("content") ?? ""),
            "thinking" => new WsEvent.Thinking(Str("content") ?? ""),
            "tool_call" => new WsEvent.ToolCall(Str("id") ?? "", Str("name") ?? "", frame["args"]?.ToJsonString() ?? ""),
            "tool_result" => new WsEvent.ToolResult(Str("id") ?? "", Str("name") ?? "", frame["output"]?.ToJsonString() ?? ""),
            "approval_request" => new WsEvent.ApprovalRequest(
                Str("request_id") ?? "", Str("tool") ?? "", Str("arguments_summary") ?? "",
                frame["timeout_secs"] is { } ts && ts.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (int)ts.GetValue<double>() : 0),
            "done" => new WsEvent.Done(
                Str("full_response") ?? "", Str("model"), Str("provider"),
                Num(frame["cost_usd"]), Tokens(frame["tokens_used"])),
            "aborted" => new WsEvent.Aborted(),
            "agent_start" => new WsEvent.AgentStart(Str("model_provider") ?? Str("provider"), Str("model")),
            "agent_end" => new WsEvent.AgentEnd(Str("model_provider") ?? Str("provider"), Str("model")),
            "error" => new WsEvent.Error(Str("message") ?? "Unknown gateway error", Str("code")),
            _ => new WsEvent.Other(frame),
        };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best effort */ }
        _ws.Dispose();
    }
}
