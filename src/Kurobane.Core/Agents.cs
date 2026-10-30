using System.Text.Json.Nodes;

namespace Kurobane.Core;

public sealed record AgentInfo(string Alias, bool Enabled, string ModelProvider, IReadOnlyList<string> Channels);

/// <summary>Agent list read from GET /api/config (the `agents` map), not a dedicated endpoint.</summary>
public static class AgentsApi
{
    public static async Task<IReadOnlyList<AgentInfo>> ListAsync(ZeroClawClient client, CancellationToken ct)
    {
        var json = await client.GetAsync("/api/config", ct).ConfigureAwait(false);
        return Parse(json);
    }

    public static IReadOnlyList<AgentInfo> Parse(JsonNode? json)
    {
        var agents = json?["agents"] as JsonObject;
        var result = new List<AgentInfo>();
        if (agents is null) return result;
        foreach (var kv in agents)
        {
            var a = kv.Value as JsonObject;
            if (a is null) continue;
            var channels = new List<string>();
            if (a["channels"] is JsonArray arr)
            {
                foreach (var c in arr)
                {
                    if (c is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                        channels.Add(v.GetValue<string>());
                }
            }
            var enabled = true;
            if (a["enabled"] is JsonValue en && en.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                enabled = en.GetValue<bool>();
            result.Add(new AgentInfo(
                kv.Key,
                enabled,
                a["model_provider"] is JsonValue mp && mp.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? mp.GetValue<string>() : "",
                channels));
        }
        return result.OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
