using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Kurobane.Core;

namespace Kurobane.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISecretStore _secrets;
    private readonly UpdateFlow _updates;

    public SettingsWindow(AppSettings settings, ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        InitializeComponent();
        var server = settings.ActiveServer ?? new KurobaneConnection();
        NameBox.Text = server.Name;
        UrlBox.Text = server.BaseUrl;
        AgentBox.Text = string.IsNullOrWhiteSpace(server.AgentAlias) ? "default" : server.AgentAlias;
        ThemeBox.ItemsSource = ThemeCatalog.All;
        ThemeBox.SelectedItem = ThemeCatalog.Get(settings.ThemeId);
        SelectByTag(AppearanceBox, settings.Appearance);
        SelectByTag(ScaleBox, settings.TextScalePercent.ToString());
        UpdateOnStartBox.IsChecked = settings.CheckUpdatesOnStart;
        Loaded += async (_, _) => await LoadAgentsAsync();

        var verifier = new ReleaseSignatureVerifier(UpdateTrust.PublisherPublicKeySpkiBase64);
        var checker = new UpdateChecker(new HttpClientHandler(), App.UpdateRepo, UpdateLauncher.CurrentVersion);
        var installer = new UpdateInstaller(new HttpClientHandler(), verifier, App.UpdateWorkDir);
        _updates = new UpdateFlow(ct => checker.CheckAsync(ct), installer.PrepareAsync, verifier.IsConfigured);
        _updates.Changed += () => Dispatcher.BeginInvoke(RefreshUpdateUi);
        RefreshUpdateUi();
    }

    private void RefreshUpdateUi()
    {
        var cur = UpdateLauncher.CurrentVersion;
        UpdateCurrent.Text = cur == default ? "Current version: unknown" : $"Current version: {cur}";
        UpdateCheck.IsEnabled = _updates.CanCheck;
        UpdateDownload.IsEnabled = _updates.CanDownload;
        UpdateInstall.IsEnabled = _updates.CanInstall;
        UpdatePage.IsEnabled = _updates.Release is not null;
        UpdateStatus.Text = _updates.Message;
    }

    private async void OnUpdateCheck(object sender, RoutedEventArgs e)
    {
        UpdateCheck.IsEnabled = false;
        await _updates.CheckAsync(default);
        RefreshUpdateUi();
    }

    private async void OnUpdateDownload(object sender, RoutedEventArgs e)
    {
        UpdateDownload.IsEnabled = false;
        var progress = new Progress<long>(_ => { });
        await _updates.PrepareAsync(progress, default);
        RefreshUpdateUi();
    }

    private void OnUpdateInstall(object sender, RoutedEventArgs e)
    {
        if (_updates.Staged is not { } staged) return;
        if (!UpdateLauncher.InstallDirWritable())
        {
            MessageBox.Show(this,
                "The install folder is not writable by your account (for example under Program Files). " +
                "Run the newer installer from the release page instead.", "Cannot update in place",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Install Kurobane {staged.Version} now and restart?", "Install update",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            _updates.Discard();
            RefreshUpdateUi();
            return;
        }
        UpdateLauncher.StartHelper(staged);
        Application.Current.Shutdown();
    }

    private void OnUpdatePage(object sender, RoutedEventArgs e)
    {
        if (_updates.Release is { } release)
        {
            try { UpdateLauncher.OpenReleasePage(release); }
            catch (Exception ex) { UpdateStatus.Text = ex.Message; }
        }
    }

    private static void SelectByTag(ComboBox box, string? tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        box.SelectedIndex = 0;
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        PairButton.IsEnabled = false;
        StatusText.Text = "Pairing…";
        try
        {
            var server = CurrentFromFields();
            var token = await server.PairAsync(_secrets, CodeBox.Password, ct: default);
            _settings.Servers.RemoveAll(s => s.BaseUrl.TrimEnd('/').Equals(server.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            _settings.Servers.Add(server);
            _settings.ActiveServerId = server.TokenKey;
            _settings.Save();
            StatusText.Text = "Paired. Token stored encrypted on this PC (DPAPI).";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally { PairButton.IsEnabled = true; }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        StatusText.Text = "Testing…";
        try
        {
            var server = CurrentFromFields();
            using var client = server.CreateClient(_secrets);
            if (!client.HasToken) { StatusText.Text = "Pair first — no token stored for this address."; return; }
            var status = await client.StatusAsync(default);
            var version = status["version"]?.GetValue<string>() ?? "?";
            var model = status["model"]?.GetValue<string>() ?? "?";
            StatusText.Text = $"Connected. ZeroClaw v{version}, model {model}.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { TestButton.IsEnabled = true; }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var server = CurrentFromFields();
        var existing = _settings.Servers.FirstOrDefault(s => s.TokenKey == server.TokenKey && server.TokenKey.Length > 0)
            ?? _settings.Servers.FirstOrDefault(s => s.BaseUrl.TrimEnd('/').Equals(server.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Name = server.Name; existing.BaseUrl = server.BaseUrl; existing.AgentAlias = server.AgentAlias;
            if (existing.TokenKey.Length > 0) _settings.ActiveServerId = existing.TokenKey;
        }
        else if (!string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            _settings.Servers.Add(server);
            // A server without a stored token is not usable yet; keep the last paired one active.
            if (server.TokenKey.Length > 0) _settings.ActiveServerId = server.TokenKey;
        }
        _settings.Servers.RemoveAll(s => s.TokenKey.Length == 0
            && _settings.Servers.Any(o => o != s && o.TokenKey.Length > 0
                && o.BaseUrl.TrimEnd('/').Equals(s.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
        _settings.ThemeId = (ThemeBox.SelectedItem as ThemeDefinition)?.Id ?? "washi-ink";
        _settings.Appearance = (AppearanceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        _settings.TextScalePercent = int.TryParse((ScaleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var s) ? s : 100;
        _settings.CheckUpdatesOnStart = UpdateOnStartBox.IsChecked == true;
        _settings.Save();
        App.ApplyAppearance();
        DialogResult = true;
        Close();
    }

    private async Task LoadAgentsAsync()
    {
        try
        {
            var server = CurrentFromFields();
            if (!server.IsConfigured) return;
            using var client = server.CreateClient(_secrets);
            if (!client.HasToken) return;
            var agents = await AgentsApi.ListAsync(client, default);
            if (agents.Count == 0) return;
            var aliases = agents.Select(a => a.Enabled ? a.Alias : a.Alias + " (disabled)").ToList();
            var current = AgentBox.Text.Trim();
            if (current.Length > 0 && !aliases.Any(a => a == current || a == current + " (disabled)"))
                aliases.Insert(0, current);
            AgentList.ItemsSource = aliases;
            AgentList.SelectedItem = aliases.FirstOrDefault(a => a == current || a == current + " (disabled)");
            AgentList.Visibility = Visibility.Visible;
        }
        catch { /* the typed alias stays usable */ }
    }

    private void OnAgentPicked(object sender, SelectionChangedEventArgs e)
    {
        if (AgentList.SelectedItem is not string picked) return;
        const string suffix = " (disabled)";
        AgentBox.Text = picked.EndsWith(suffix, StringComparison.Ordinal) ? picked[..^suffix.Length] : picked;
    }

    private KurobaneConnection CurrentFromFields()
    {
        var existing = _settings.ActiveServer;
        return new KurobaneConnection
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "My ZeroClaw" : NameBox.Text.Trim(),
            BaseUrl = UrlBox.Text.Trim(),
            AgentAlias = string.IsNullOrWhiteSpace(AgentBox.Text) ? "default" : AgentBox.Text.Trim(),
            DeviceName = "Kurobane on Windows",
            TokenKey = existing?.TokenKey ?? "",
        };
    }
}
