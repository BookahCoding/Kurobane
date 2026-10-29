using System.Windows;

namespace Kurobane.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static DpapiSecretStore Secrets { get; private set; } = new(
        System.IO.Path.Combine(AppSettings.SettingsDirectory, "secrets"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = AppSettings.Load();
        ApplyAppearance();
    }

    public static void ApplyAppearance()
    {
        var dark = Settings.Appearance?.Trim().ToLowerInvariant() switch
        {
            "light" => false,
            "dark" => true,
            _ => IsSystemDark(),
        };
        var dict = (ResourceDictionary)Current.Resources.MergedDictionaries[0];
        dict["BackgroundBrush"] = dark ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x14, 0x18)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xF3, 0xEE));
        dict["PanelBrush"] = dark ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x23)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFA));
        dict["TextBrush"] = dark ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xE6, 0xE3)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x20, 0x1C));
        dict["MutedTextBrush"] = dark ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x97, 0x92)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x67, 0x60));
        dict["AccentBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0x3A, 0x2E)); // kurobane red
        dict["BorderBrush"] = dark ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x3B)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xD8, 0xCE));
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }
}
