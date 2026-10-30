using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kurobane.Core;

public readonly record struct AgentAliasDecision(string Alias, bool SwitchLocalToKogarashi, bool NeedsUserChoice, string? StatusLine);

public static class AgentAlias
{
    public const string Preferred = "kogarashi";

    public static AgentAliasDecision Decide(string? savedAlias, IReadOnlyList<AgentInfo> agents)
    {
        var saved = (savedAlias ?? "").Trim();
        var enabled = agents.Where(a => a.Enabled).ToList();
        var match = enabled.FirstOrDefault(a => string.Equals(a.Alias, saved, StringComparison.Ordinal));
        if (match is not null)
            return new AgentAliasDecision(match.Alias, false, false, null);
        var koga = enabled.FirstOrDefault(a => string.Equals(a.Alias, Preferred, StringComparison.Ordinal));
        if (koga is not null)
            return new AgentAliasDecision(koga.Alias, true, false,
                "Saved agent is not enabled. This PC will use kogarashi. Gateway config was not changed.");
        return new AgentAliasDecision(saved, false, enabled.Count > 0,
            enabled.Count > 0
                ? "Saved agent is not enabled. Choose an agent in Settings. Kurobane will not pick one for you."
                : null);
    }
}

public sealed record ProviderModels(string ProviderId, string Model, IReadOnlyList<string> FallbackModels);

public sealed record RiskProfileSummary(string? Level, bool? WorkspaceOnly, IReadOnlyList<string> AlwaysAsk, IReadOnlyList<string> AutoApprove);

public sealed record CostSummary(double CostUsd, bool HasCostUsd, long TotalTokens, long? InputTokens, long? OutputTokens, long UnpricedTokens, long? RequestCount)
{
    public string UsageLine
    {
        get
        {
            var parts = new List<string>();
            if (InputTokens is not null || OutputTokens is not null)
                parts.Add($"in {InputTokens ?? 0} · out {OutputTokens ?? 0}");
            parts.Add($"{TotalTokens} tokens");
            if (RequestCount is not null)
                parts.Add($"{RequestCount} requests");
            if (UnpricedTokens > 0 && CostUsd == 0)
                parts.Add($"{UnpricedTokens} unpriced tokens — the gateway did not price them");
            else if (HasCostUsd)
                parts.Add($"${CostUsd:0.####} reported by the gateway");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record CronJobItem(string Id, string? Name, string? Schedule, string? NextRun, bool? Enabled, string? LastStatus, string? Prompt, string? Command);

public sealed record CronSettingsInfo(bool? Enabled);

public sealed record MemoryItem(string Id, string? Category, string? Timestamp, string Content);

public sealed record ToolItem(string Name, string? Description);

public sealed record PluginCatalog(bool? PluginsEnabled, IReadOnlyList<string> Names);

public sealed record PairedDeviceInfo(string Name, string Type);

public sealed record GatewayProcessStatus(string? Version, string? Provider, string? Model);

public sealed record UploadResponse(string? Path, string Marker);

public static class GatewayParse
{
    public static ProviderModels ParseProvider(JsonNode? node, string? providerId)
    {
        var id = providerId?.Trim() ?? "";
        var obj = AsObject(node);
        if (obj is null) return new ProviderModels(id, "", Array.Empty<string>());
        obj = UnwrapValue(obj);
        if (LooksLikeProvider(obj)) return FromProvider(obj, id);
        if (id.Length == 0) return new ProviderModels("", "", Array.Empty<string>());
        var models = obj["providers"]?["models"] as JsonObject ?? obj["models"] as JsonObject;
        var found = FindProviderObject(models, id);
        return found is null ? new ProviderModels(id, "", Array.Empty<string>()) : FromProvider(found, id);
    }

    public static ProviderModels ParseProviderForAgent(JsonNode? config, string agentAlias)
    {
        var agent = config?["agents"]?[agentAlias] as JsonObject;
        var id = Str(agent?["model_provider"]) ?? "";
        return ParseProvider(config, id);
    }

    public static RiskProfileSummary ParseRisk(JsonNode? node, string? agentAlias = null)
    {
        var obj = AsObject(node);
        if (obj is null) return EmptyRisk();
        obj = UnwrapValue(obj);
        if (LooksLikeRisk(obj)) return FromRisk(obj);
        var agent = string.IsNullOrWhiteSpace(agentAlias) ? null : obj["agents"]?[agentAlias] as JsonObject;
        var reference = Str(agent?["risk_profile"]);
        var profiles = obj["risk_profiles"] as JsonObject;
        if (profiles is null) return EmptyRisk();
        if (!string.IsNullOrWhiteSpace(reference))
        {
            var direct = Walk(profiles, reference);
            if (direct is not null && LooksLikeRisk(direct)) return FromRisk(direct);
            foreach (var kv in profiles)
            {
                if (kv.Value is JsonObject child)
                {
                    if (string.Equals(kv.Key, reference, StringComparison.Ordinal) && LooksLikeRisk(child))
                        return FromRisk(child);
                    if (child[reference] is JsonObject nested && LooksLikeRisk(nested))
                        return FromRisk(nested);
                }
            }
        }
        return EmptyRisk();
    }

    public static CostSummary ParseCost(JsonNode? node)
    {
        var root = AsObject(node) ?? new JsonObject();
        var cost = root["cost"] as JsonObject ?? root["summary"] as JsonObject ?? root;
        var has = cost["cost_usd"] is not null || cost["session_cost_usd"] is not null
            || cost["daily_cost_usd"] is not null || cost["monthly_cost_usd"] is not null;
        double usd = 0;
        if (cost["cost_usd"] is not null) usd = Num(cost["cost_usd"]) ?? 0;
        else if (cost["session_cost_usd"] is not null) usd = Num(cost["session_cost_usd"]) ?? 0;
        else if (cost["daily_cost_usd"] is not null) usd = Num(cost["daily_cost_usd"]) ?? 0;
        else if (cost["monthly_cost_usd"] is not null) usd = Num(cost["monthly_cost_usd"]) ?? 0;
        var total = Long(cost["total_tokens"]) ?? Long(root["total_tokens"]) ?? 0;
        var unpriced = Long(cost["unpriced_tokens"]) ?? Long(root["unpriced_tokens"]) ?? SumModelField(cost, "unpriced_tokens");
        return new CostSummary(
            usd, has, total,
            Long(cost["input_tokens"]) ?? Long(root["input_tokens"]) ?? NullIfZero(SumModelField(cost, "input_tokens")),
            Long(cost["output_tokens"]) ?? Long(root["output_tokens"]) ?? NullIfZero(SumModelField(cost, "output_tokens")),
            unpriced,
            Long(cost["request_count"]) ?? Long(root["request_count"]));
    }

    /// <summary>The live cost payload stores per-model totals under <c>by_model</c> and omits a top-level unpriced count.</summary>
    private static long SumModelField(JsonObject cost, string field)
    {
        if (cost["by_model"] is not JsonObject models) return 0;
        long sum = 0;
        foreach (var kv in models)
        {
            if (kv.Value is JsonObject row && Long(row[field]) is long n) sum += n;
        }
        return sum;
    }

    private static long? NullIfZero(long value) => value == 0 ? null : value;

    public static IReadOnlyList<CronJobItem> ParseCronJobs(JsonNode? node)
    {
        JsonArray? list = node as JsonArray;
        if (node is JsonObject obj)
            list = obj["jobs"] as JsonArray ?? obj["cron"] as JsonArray ?? obj["items"] as JsonArray;
        var result = new List<CronJobItem>();
        if (list is null) return result;
        foreach (var item in list)
        {
            if (item is not JsonObject job) continue;
            var schedule = job["schedule"];
            string? scheduleText = schedule switch
            {
                JsonValue => Str(schedule),
                JsonObject so => Str(so["expr"]) ?? Str(so["expression"]) ?? Str(so["cron"]) ?? so.ToJsonString(),
                _ => null
            };
            result.Add(new CronJobItem(
                Str(job["id"]) ?? Str(job["name"]) ?? "",
                Str(job["name"]),
                scheduleText,
                Str(job["next_run"]) ?? Str(job["nextRun"]),
                Bool(job["enabled"]),
                Str(job["last_status"]) ?? Str(job["lastStatus"]),
                Str(job["prompt"]),
                Str(job["command"])));
        }
        return result;
    }

    public static CronSettingsInfo ParseCronSettings(JsonNode? node)
    {
        var obj = UnwrapValue(AsObject(node) ?? new JsonObject());
        return new CronSettingsInfo(Bool(obj["enabled"]) ?? Bool(obj["scheduler_enabled"]));
    }

    public static IReadOnlyList<MemoryItem> ParseMemory(JsonNode? node)
    {
        JsonArray? list = node as JsonArray;
        if (node is JsonObject obj)
            list = obj["entries"] as JsonArray ?? obj["memories"] as JsonArray ?? obj["items"] as JsonArray;
        var result = new List<MemoryItem>();
        if (list is null) return result;
        foreach (var item in list)
        {
            if (item is not JsonObject entry) continue;
            var content = Str(entry["content"]) ?? Str(entry["text"]) ?? "";
            var id = Str(entry["id"]) ?? Str(entry["key"]) ?? "";
            if (id.Length == 0 && content.Length == 0) continue;
            result.Add(new MemoryItem(id, Category(entry["category"]), Str(entry["timestamp"]) ?? Str(entry["created_at"]) ?? Str(entry["updated_at"]), content));
        }
        return result;
    }

    public static string TruncateContent(string content, int max = 400)
    {
        if (max < 1 || content.Length <= max) return content;
        return content[..max] + "…";
    }

    public static IReadOnlyList<string> ParseSkillNames(JsonNode? node)
    {
        var names = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }
        void FromArray(JsonArray? arr)
        {
            if (arr is null) return;
            foreach (var item in arr)
            {
                if (item is null) continue;
                if (Str(item) is { } s) Add(s);
                else if (item is JsonObject o)
                {
                    Add(Str(o["name"]) ?? Str(o["id"]) ?? Str(o["alias"]));
                    FromArray(o["skills"] as JsonArray);
                }
            }
        }
        if (node is JsonArray top) FromArray(top);
        else if (node is JsonObject obj)
        {
            FromArray(obj["skills"] as JsonArray);
            FromArray(obj["bundles"] as JsonArray);
            if (obj["bundles"] is JsonObject bundles)
            {
                foreach (var kv in bundles)
                {
                    Add(kv.Key);
                    if (kv.Value is JsonArray arr) FromArray(arr);
                    else if (kv.Value is JsonObject child) FromArray(child["skills"] as JsonArray);
                }
            }
        }
        return names;
    }

    public static IReadOnlyList<ToolItem> ParseTools(JsonNode? node)
    {
        JsonArray? list = node as JsonArray;
        if (node is JsonObject obj) list = obj["tools"] as JsonArray ?? obj["items"] as JsonArray;
        var result = new List<ToolItem>();
        if (list is null) return result;
        foreach (var item in list)
        {
            if (Str(item) is { } s) result.Add(new ToolItem(s, null));
            else if (item is JsonObject o && Str(o["name"]) is { } name)
                result.Add(new ToolItem(name, Str(o["description"])));
        }
        return result;
    }

    public static PluginCatalog ParsePlugins(JsonNode? node)
    {
        var obj = AsObject(node) ?? new JsonObject();
        var names = new List<string>();
        if (obj["plugins"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var name = Str(item) ?? (item is JsonObject o ? Str(o["name"]) ?? Str(o["id"]) : null);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        return new PluginCatalog(Bool(obj["plugins_enabled"]) ?? Bool(obj["enabled"]), names);
    }

    public static IReadOnlyList<string> ParsePersonalityNames(JsonNode? node)
    {
        var names = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (name is "agent" or "error" or "ok" or "content" or "body") return;
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }
        void From(JsonNode? n)
        {
            switch (n)
            {
                case JsonArray arr:
                    foreach (var item in arr)
                    {
                        if (Str(item) is { } s) Add(s);
                        else if (item is JsonObject o) Add(Str(o["name"]) ?? Str(o["filename"]) ?? Str(o["file"]));
                    }
                    break;
                case JsonObject obj:
                    if (obj["files"] is not null) From(obj["files"]);
                    else if (obj["names"] is not null) From(obj["names"]);
                    else if (obj["personality"] is JsonObject p) From(p);
                    else
                    {
                        foreach (var kv in obj)
                            Add(kv.Key);
                    }
                    break;
            }
        }
        From(node);
        return names;
    }

    public static IReadOnlyList<PairedDeviceInfo> ParseDevices(JsonNode? node)
    {
        JsonArray? list = node as JsonArray;
        if (node is JsonObject obj) list = obj["devices"] as JsonArray ?? obj["items"] as JsonArray;
        var result = new List<PairedDeviceInfo>();
        if (list is null) return result;
        foreach (var item in list)
        {
            if (item is not JsonObject o) continue;
            result.Add(new PairedDeviceInfo(Str(o["name"]) ?? Str(o["device_name"]) ?? "", Str(o["device_type"]) ?? Str(o["type"]) ?? ""));
        }
        return result;
    }

    public static GatewayProcessStatus ParseStatus(JsonNode? node)
    {
        var obj = AsObject(node) ?? new JsonObject();
        var provider = Str(obj["model_provider"]) ?? Str(obj["provider"]) ?? Str(obj["default_provider"]);
        var model = Str(obj["model"]) ?? Str(obj["default_model"]);
        return new GatewayProcessStatus(Str(obj["version"]), provider, model);
    }

    public static UploadResponse? ParseUpload(JsonNode? node)
    {
        var obj = AsObject(node);
        if (obj is null) return null;
        var path = Str(obj["path"]);
        var marker = Str(obj["marker"]);
        if (string.IsNullOrWhiteSpace(marker) && !string.IsNullOrWhiteSpace(path))
            marker = "[IMAGE:" + path + "]";
        if (string.IsNullOrWhiteSpace(marker)) return null;
        return new UploadResponse(path, marker);
    }

    public static string UploadPath(string agentAlias) => "/api/upload?agent=" + Uri.EscapeDataString(agentAlias);

    public static bool TryPrepareImage(ReadOnlySpan<byte> bytes, out string mime, out string? rejection)
    {
        var sniffed = Mime(bytes);
        if (sniffed is null)
        {
            mime = "";
            rejection = "This gateway only accepts PNG, JPEG, GIF, or WebP images. Other files are not uploaded.";
            return false;
        }
        mime = sniffed;
        rejection = null;
        return true;
    }

    public static string? Mime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return null;
    }

    public static async Task<UploadResponse> UploadImageAsync(ZeroClawClient client, string agentAlias, byte[] bytes, CancellationToken ct)
    {
        if (!TryPrepareImage(bytes, out var mime, out var rejection))
            throw new InvalidOperationException(rejection);
        var json = await client.PostRawAsync(UploadPath(agentAlias), bytes, mime, ct).ConfigureAwait(false);
        return ParseUpload(json) ?? throw new InvalidOperationException("Upload succeeded but the gateway returned no image marker.");
    }

    public static string UploadErrorText(Exception ex)
    {
        if (ex is ZeroClawApiException api)
        {
            if (!string.IsNullOrWhiteSpace(api.Body))
            {
                try
                {
                    var node = JsonNode.Parse(api.Body);
                    var err = Str(node?["error"]) ?? Str(node?["message"]);
                    if (!string.IsNullOrWhiteSpace(err)) return err;
                }
                catch (JsonException) { }
                return api.Body.Length <= 500 ? api.Body : api.Body[..500];
            }
        }
        return ex.Message;
    }

    private static ProviderModels FromProvider(JsonObject obj, string id)
    {
        var model = Str(obj["model"]) ?? "";
        var fallback = StringList(obj["fallback_models"]);
        if (fallback.Count == 0) fallback = StringList(obj["fallbacks"]);
        return new ProviderModels(id, model, fallback);
    }

    private static bool LooksLikeProvider(JsonObject obj) => obj["model"] is not null || obj["fallback_models"] is not null;

    private static JsonObject? FindProviderObject(JsonObject? models, string providerId)
    {
        if (models is null) return null;
        var parts = providerId.Split('.', 2);
        if (parts.Length == 2 && models[parts[0]]?[parts[1]] is JsonObject typed)
            return typed;
        if (models[providerId] is JsonObject direct) return direct;
        foreach (var kv in models)
        {
            if (kv.Value is not JsonObject child) continue;
            if (string.Equals(kv.Key, providerId, StringComparison.Ordinal) && LooksLikeProvider(child))
                return child;
            if (child[providerId] is JsonObject nested) return nested;
            if (parts.Length == 2 && string.Equals(kv.Key, parts[0], StringComparison.Ordinal) && child[parts[1]] is JsonObject leaf)
                return leaf;
        }
        return null;
    }

    private static RiskProfileSummary FromRisk(JsonObject obj) =>
        new(Str(obj["level"]), Bool(obj["workspace_only"]), StringList(obj["always_ask"]), StringList(obj["auto_approve"]));

    private static RiskProfileSummary EmptyRisk() => new(null, null, Array.Empty<string>(), Array.Empty<string>());

    private static bool LooksLikeRisk(JsonObject obj) =>
        obj["level"] is not null || obj["always_ask"] is not null || obj["auto_approve"] is not null;

    private static JsonObject? Walk(JsonObject root, string reference)
    {
        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonNode? cur = root;
        foreach (var part in parts)
        {
            cur = (cur as JsonObject)?[part];
            if (cur is null) return null;
        }
        return cur as JsonObject;
    }

    private static string? Category(JsonNode? node)
    {
        if (Str(node) is { } s) return s;
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                var value = Str(kv.Value);
                return string.IsNullOrWhiteSpace(value) ? kv.Key : value;
            }
        }
        return null;
    }

    private static JsonObject UnwrapValue(JsonObject obj)
    {
        if (obj["value"] is JsonObject value && (obj["path"] is not null || LooksLikeProvider(value) || LooksLikeRisk(value)))
            return value;
        return obj;
    }

    private static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    private static string? Str(JsonNode? node)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return v.GetValue<string>();
        return null;
    }

    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue(out double d)) return d;
        return null;
    }

    private static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue(out long n)) return n;
        if (v.TryGetValue(out double d)) return (long)d;
        return null;
    }

    private static List<string> StringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray arr) return list;
        foreach (var item in arr)
        {
            var s = Str(item);
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        }
        return list;
    }
}

public static class GatewayText
{
    public const string AlwaysWarning =
        "The server still asks on every always_ask tool, including every shell call. Always does not disable supervision.";

    public static string AlwaysButtonCaption(IReadOnlyList<string>? alwaysAsk)
    {
        var extra = alwaysAsk is { Count: > 0 }
            ? " This agent's always_ask list: " + string.Join(", ", alwaysAsk) + "."
            : "";
        return "Always — " + AlwaysWarning + extra;
    }

    public static string FormatModels(string? agentProvider, string? agentModel, IReadOnlyList<string> fallback, string? processProvider, string? processModel)
    {
        var fb = fallback.Count == 0 ? "none" : string.Join(", ", fallback);
        return $"This agent: {agentProvider} / {agentModel} (fallback: {fb})\nGateway process model: {processProvider} / {processModel}";
    }

    public static string Explain(Exception ex)
    {
        if (ex is OperationCanceledException) return "Cancelled";
        if (ex is ZeroClawApiException api && (int)api.StatusCode == 401)
            return "Pairing expired. Open Settings and pair this PC again.";
        if (ex is ZeroClawApiException apiProvider && (apiProvider.Body.Contains("provider", StringComparison.OrdinalIgnoreCase)
            || apiProvider.Message.Contains("provider", StringComparison.OrdinalIgnoreCase)))
            return "Provider failed. " + apiProvider.Message;
        if (IsUnreachable(ex)) return "Gateway unreachable.";
        return ex.Message;
    }

    public static string ExplainFrame(string? code, string message)
    {
        if (code is "401" or "unauthorized" or "auth" or "pairing_expired")
            return "Pairing expired. Open Settings and pair this PC again.";
        if ((code?.Contains("provider", StringComparison.OrdinalIgnoreCase) ?? false)
            || message.Contains("provider", StringComparison.OrdinalIgnoreCase))
            return "Provider failed. " + message;
        if (code is "unreachable" or "connection"
            || message.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
            return "Gateway unreachable.";
        return message;
    }

    public static string? UnsettledTurn(bool sawTerminal) =>
        sawTerminal ? null : "Gateway unreachable. Kurobane did not send the message again.";

    private static bool IsUnreachable(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException or WebSocketException or System.Net.Sockets.SocketException or TimeoutException)
                return true;
        }
        return false;
    }
}
