using System.Text.Json.Nodes;

namespace Kurobane.Core;

/// <summary>
/// ZeroClaw config access: GET /api/config returns the whole (secret-masked) config;
/// PUT /api/config/prop?path=... sets one property; PATCH /api/config applies JSON Patch ops.
/// Secrets come back masked — the client must never write the masked placeholder back.
/// </summary>
public static class ConfigApi
{
    public static async Task<JsonNode> GetConfigAsync(ZeroClawClient client, CancellationToken ct)
        => await client.GetAsync("/api/config", ct).ConfigureAwait(false);

    public static async Task<JsonNode> GetPropAsync(ZeroClawClient client, string path, CancellationToken ct)
        => await client.GetAsync($"/api/config/prop?path={Uri.EscapeDataString(path)}", ct).ConfigureAwait(false);

    public static async Task<JsonNode> SetPropAsync(ZeroClawClient client, string path, JsonNode value, CancellationToken ct)
    {
        var body = new JsonObject { ["path"] = path, ["value"] = value.DeepClone() };
        return await client.PutAsync("/api/config/prop", body, ct).ConfigureAwait(false);
    }

    public static async Task DeletePropAsync(ZeroClawClient client, string path, CancellationToken ct)
        => await client.DeleteAsync($"/api/config/prop?path={Uri.EscapeDataString(path)}", ct).ConfigureAwait(false);

    public static async Task<JsonNode> PatchAsync(ZeroClawClient client, JsonArray ops, CancellationToken ct)
        => await client.PatchAsync("/api/config", ops, ct).ConfigureAwait(false);

    /// <summary>True for the gateway's masked-secret placeholder, which must never be written back.</summary>
    public static bool IsMaskedSecretValue(JsonNode? value)
        => value is not null && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            && value.GetValue<string>().Contains("****", StringComparison.Ordinal);
}
