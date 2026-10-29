using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Kurobane.Core;

namespace Kurobane.App;

public partial class ConfigWindow : Window
{
    private readonly ZeroClawClient _client;
    private JsonNode? _loaded;

    public ConfigWindow(ZeroClawClient client)
    {
        _client = client;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        ConfigStatus.Text = "Loading…";
        try
        {
            _loaded = await ConfigApi.GetConfigAsync(_client, default);
            ConfigBox.Text = _loaded.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AgentList.ItemsSource = await AgentsApi.ListAsync(_client, default);
            ConfigStatus.Text = "Loaded (secrets masked).";
        }
        catch (Exception ex) { ConfigStatus.Text = ex.Message; }
    }

    private async void OnReload(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var path = PromptDialog.Show(this, "Set one config property", "Dotted path, e.g. gateway.port or agents.default.enabled:", "gateway.port");
        if (string.IsNullOrWhiteSpace(path)) return;
        var valueText = PromptDialog.Show(this, "Value for " + path.Trim(), "Value (JSON: number, true/false, or \"text\"):", "");
        if (valueText is null) return;
        JsonNode value;
        try { value = JsonNode.Parse(valueText) ?? JsonValue.Create(valueText); }
        catch (JsonException) { value = JsonValue.Create(valueText); }
        if (ConfigApi.IsMaskedSecretValue(value))
        {
            MessageBox.Show(this, "Refusing to write the masked-secret placeholder back. Leave it empty to keep the current secret.", "Masked value", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, $"Write {path.Trim()} = {value.ToJsonString()} on the gateway?", "Confirm change",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyButton.IsEnabled = false;
        try
        {
            await ConfigApi.SetPropAsync(_client, path.Trim(), value, default);
            ConfigStatus.Text = $"Saved {path.Trim()}.";
        }
        catch (Exception ex) { ConfigStatus.Text = ex.Message; }
        finally { ApplyButton.IsEnabled = true; }
    }
}
