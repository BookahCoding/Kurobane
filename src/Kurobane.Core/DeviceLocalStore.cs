using System.Text.Json;

namespace Kurobane.Core;

/// <summary>
/// Composer drafts and project labels kept on this PC only, under the settings directory.
/// Never the secrets folder. Labels are not synced to the gateway.
/// </summary>
public sealed class DeviceLocalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string NewChatSessionKey = "new";

    private readonly string _draftsPath;
    private readonly string _labelsPath;
    private Dictionary<string, string> _drafts;
    private Dictionary<string, string> _labels;

    public DeviceLocalStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _draftsPath = Path.Combine(directory, "drafts.json");
        _labelsPath = Path.Combine(directory, "project-labels.json");
        _drafts = Load(_draftsPath);
        _labels = Load(_labelsPath);
    }

    public static string Key(string serverId, string sessionKey) => serverId.Trim() + "\n" + sessionKey;

    public string GetDraft(string serverId, string sessionKey) =>
        _drafts.TryGetValue(Key(serverId, sessionKey), out var text) ? text : "";

    public void SetDraft(string serverId, string sessionKey, string? text)
    {
        var key = Key(serverId, sessionKey);
        if (string.IsNullOrEmpty(text)) _drafts.Remove(key);
        else _drafts[key] = text;
        Save(_draftsPath, _drafts);
    }

    public string? GetLabel(string serverId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        return _labels.TryGetValue(Key(serverId, sessionId), out var label) && !string.IsNullOrWhiteSpace(label)
            ? label : null;
    }

    public void SetLabel(string serverId, string sessionId, string? label)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var key = Key(serverId, sessionId);
        if (string.IsNullOrWhiteSpace(label)) _labels.Remove(key);
        else _labels[key] = label.Trim();
        Save(_labelsPath, _labels);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void Save(string path, Dictionary<string, string> map)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(map, JsonOptions));
    }
}
