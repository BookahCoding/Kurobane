using System.Windows;
using System.Windows.Controls;
using Kurobane.Core;

namespace Kurobane.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISecretStore _secrets;

    public SettingsWindow(AppSettings settings, ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        InitializeComponent();
        var server = settings.ActiveServer ?? new KurobaneConnection();
        NameBox.Text = server.Name;
        UrlBox.Text = server.BaseUrl;
        AgentBox.Text = string.IsNullOrWhiteSpace(server.AgentAlias) ? "default" : server.AgentAlias;
        SelectByTag(AppearanceBox, settings.Appearance);
        SelectByTag(ScaleBox, settings.TextScalePercent.ToString());
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
            _settings.ActiveServerId = existing.TokenKey;
        }
        else if (!string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            _settings.Servers.Add(server);
            _settings.ActiveServerId = server.TokenKey;
        }
        _settings.Appearance = (AppearanceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dark";
        _settings.TextScalePercent = int.TryParse((ScaleBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var s) ? s : 100;
        _settings.Save();
        App.ApplyAppearance();
        DialogResult = true;
        Close();
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
