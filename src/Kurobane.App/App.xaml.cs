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

    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--apply-update"))
        {
            UpdateLauncher.RunApplyMode(e.Args);
            Shutdown();
            return;
        }
        if (!TryAcquireSingleInstance())
        {
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

    /// <summary>
    /// One Kurobane at a time: a second launch (e.g. from a pinned taskbar shortcut) brings the
    /// running window to the front instead of opening a second one.
    /// </summary>
    private static bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, @"Local\Kurobane.SingleInstance", out var acquired);
        if (acquired) return true;
        ActivateExistingWindow();
        return false;
    }

    private static void ActivateExistingWindow()
    {
        var existing = System.Diagnostics.Process.GetProcessesByName("Kurobane")
            .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
        if (existing is null) return;
        ShowWindow(existing.MainWindowHandle, SW_RESTORE);
        SetForegroundWindow(existing.MainWindowHandle);
    }

    private const int SW_RESTORE = 9;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
