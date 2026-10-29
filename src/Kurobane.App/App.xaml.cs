using System.IO;
using System.Net.Http;
using System.Windows;
using Kurobane.Core;

namespace Kurobane.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static DpapiSecretStore Secrets { get; private set; } = new(
        Path.Combine(AppSettings.SettingsDirectory, "secrets"));

    /// <summary>Downloaded + verified update packages. Local (not roaming) data, under %LOCALAPPDATA%\Kurobane\updates.</summary>
    public static string UpdateWorkDir { get; } =
        Environment.GetEnvironmentVariable("KUROBANE_DATA_DIR") is { Length: > 0 } d
            ? Path.Combine(d, "updates")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kurobane", "updates");

    public const string UpdateRepo = "BookahCoding/Kurobane";

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--apply-update"))
        {
            UpdateLauncher.RunApplyMode(e.Args);
            Shutdown();
            return;
        }
        Settings = AppSettings.Load();
        ThemeService.Apply(Settings.ThemeId, Settings.AppearanceMode);
        UpdateLauncher.CleanStaleWork(UpdateWorkDir);
        new MainWindow().Show();
        if (Settings.CheckUpdatesOnStart)
            _ = CheckForUpdatesAtStartupAsync();
    }

    public static void ApplyAppearance() => ThemeService.Apply(Settings.ThemeId, Settings.AppearanceMode);

    private static async Task CheckForUpdatesAtStartupAsync()
    {
        try
        {
            await Task.Delay(StartupUpdateCheck.Delay);
            var checker = new UpdateChecker(new HttpClientHandler(), UpdateRepo, UpdateLauncher.CurrentVersion);
            var release = await StartupUpdateCheck.FindNewerAsync(checker.CheckAsync, CancellationToken.None);
            if (release is not null && Current.MainWindow is MainWindow mw)
                _ = mw.Dispatcher.BeginInvoke(() => mw.ShowUpdateBanner(release));
        }
        catch { /* silent at start-up by design */ }
    }
}
