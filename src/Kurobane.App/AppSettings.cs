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
    /// <summary>Theme family id (see ThemeCatalog), e.g. washi-ink, great-wave, kirifuri.</summary>
    public string ThemeId { get; set; } = "washi-ink";
    /// <summary>light | dark | system</summary>
    public string Appearance { get; set; } = "system";
    /// <summary>100, 115 or 130 percent.</summary>
    public int TextScalePercent { get; set; } = 100;
    /// <summary>One quiet, anonymous check for a newer version when the app starts (nothing is downloaded until asked).</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    public AppearanceMode AppearanceMode => Appearance?.Trim().ToLowerInvariant() switch
    {
        "light" => AppearanceMode.Light,
        "dark" => AppearanceMode.Dark,
        _ => AppearanceMode.System
    };

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
