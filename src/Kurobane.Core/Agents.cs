using System.Text.Json.Nodes;

namespace Kurobane.Core;

public sealed record AgentInfo(string Alias, bool Enabled, string ModelProvider, IReadOnlyList<string> Channels);

/// <summary>Agent list read from GET /api/config (the `agents` map), not a dedicated endpoint.</summary>
public static class AgentsApi
{
    public static async Task<IReadOnlyList<AgentInfo>> ListAsync(ZeroClawClient client, CancellationToken ct)
    {
        var json = await client.GetAsync("/api/config", ct).ConfigureAwait(false);
        var agents = json["agents"] as JsonObject;
        var result = new List<AgentInfo>();
        if (agents is null) return result;
        foreach (var kv in agents)
        {
            var a = kv.Value as JsonObject;
            if (a is null) continue;
            var channels = (a["channels"] as JsonArray)?.Where(c => c is not null).Select(c => c!.GetValue<string>()).ToList()
                ?? new List<string>();
            result.Add(new AgentInfo(
                kv.Key,
                a["enabled"]?.GetValue<bool>() ?? true,
                a["model_provider"]?.GetValue<string>() ?? "",
                channels));
        }
        return result.OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
