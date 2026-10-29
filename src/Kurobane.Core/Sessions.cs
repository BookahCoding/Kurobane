using System.Text.Json.Nodes;

namespace Kurobane.Core;

public sealed record GatewaySession(
    string SessionId,
    string SessionKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivity,
    int MessageCount,
    string? AgentAlias,
    string? ChannelId,
    string? Name)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : SessionId;
}

public sealed record SessionMessage(string Role, string Content, DateTimeOffset? CreatedAt);

public static class SessionsApi
{
    public static async Task<IReadOnlyList<GatewaySession>> ListAsync(ZeroClawClient client, CancellationToken ct)
    {
        var json = await client.GetAsync("/api/sessions", ct).ConfigureAwait(false);
        var list = json["sessions"] as JsonArray ?? new JsonArray();
        var result = new List<GatewaySession>(list.Count);
        foreach (var item in list)
        {
            if (item is null) continue;
            result.Add(new GatewaySession(
                item["session_id"]?.GetValue<string>() ?? "",
                item["session_key"]?.GetValue<string>() ?? item["session_id"]?.GetValue<string>() ?? "",
                ParseTime(item["created_at"]),
                ParseTime(item["last_activity"]),
                item["message_count"]?.GetValue<int>() ?? 0,
                item["agent_alias"]?.GetValue<string>(),
                item["channel_id"]?.GetValue<string>(),
                item["name"]?.GetValue<string>()));
        }
        return result;
    }

    public static async Task<IReadOnlyList<SessionMessage>> MessagesAsync(ZeroClawClient client, string sessionId, CancellationToken ct)
    {
        var json = await client.GetAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}/messages", ct).ConfigureAwait(false);
        var list = json["messages"] as JsonArray ?? new JsonArray();
        var result = new List<SessionMessage>(list.Count);
        foreach (var m in list)
        {
            if (m is null) continue;
            result.Add(new SessionMessage(
                m["role"]?.GetValue<string>() ?? "",
                m["content"]?.GetValue<string>() ?? "",
                ParseTime(m["created_at"])));
        }
        return result;
    }

    public static Task DeleteAsync(ZeroClawClient client, string sessionId, CancellationToken ct)
        => client.DeleteAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}", ct);

    public static Task RenameAsync(ZeroClawClient client, string sessionId, string name, CancellationToken ct)
        => client.PutAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}",
            new JsonObject { ["name"] = name }, ct);

    public static async Task AbortAsync(ZeroClawClient client, string sessionId, CancellationToken ct)
        => await client.PostAsync($"/api/sessions/{Uri.EscapeDataString(sessionId)}/abort", new JsonObject(), ct).ConfigureAwait(false);

    internal static DateTimeOffset ParseTime(JsonNode? node)
        => node is not null && DateTimeOffset.TryParse(node.GetValue<string>(), out var t) ? t : DateTimeOffset.MinValue;
}
