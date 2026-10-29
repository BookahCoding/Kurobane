using System.IO;
using System.Text.Json;
using Kurobane.Core;

namespace Kurobane.App;

/// <summary>
/// Non-secret app settings, persisted as JSON under %APPDATA%\Kurobane.
/// Bearer tokens never appear here; they live in the DPAPI secret store.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public List<KurobaneConnection> Servers { get; set; } = new();
    public string? ActiveServerId { get; set; }
    /// <summary>light | dark | system</summary>
    public string Appearance { get; set; } = "dark";
    /// <summary>100, 115 or 130 percent.</summary>
    public int TextScalePercent { get; set; } = 100;

    public KurobaneConnection? ActiveServer =>
        Servers.FirstOrDefault(s => s.TokenKey == ActiveServerId) ?? Servers.FirstOrDefault();

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kurobane");

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
