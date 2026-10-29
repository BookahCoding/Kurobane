using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using Kurobane.Core;

namespace Kurobane.App;

public partial class MainWindow : Window
{
    private ChatStream? _stream;
    private CancellationTokenSource? _runCts;
    private string _sessionId = "";
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshSessionsAsync();
    }

    public void ShowUpdateBanner(UpdateRelease release)
    {
        UpdateBanner.Content = $"Update to Kurobane {release.Version} is available — open Settings → Updates";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private KurobaneConnection? Server => App.Settings.ActiveServer;

    private ZeroClawClient? Client => Server is { } s && s.IsConfigured ? s.CreateClient(App.Secrets) : null;

    private async Task RefreshSessionsAsync()
    {
        if (Client is not { } client) { ServerLabel.Text = "Open Settings to connect to a gateway."; return; }
        ServerLabel.Text = $"{Server!.Name}\n{Server.BaseUrl} · agent {Server.AgentAlias}";
        try
        {
            var sessions = await SessionsApi.ListAsync(client, default);
            SessionList.ItemsSource = sessions;
        }
        catch (Exception ex) { ServerLabel.Text += "\n" + ex.Message; }
    }

    private async void OnNewChat(object sender, RoutedEventArgs e)
    {
        _sessionId = "kb-" + Guid.NewGuid().ToString("N")[..12];
        ChatTitle.Text = "New chat — " + _sessionId;
        MessagesPanel.Children.Clear();
        await RefreshSessionsAsync();
        Composer.Focus();
    }

    private async void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not GatewaySession session) return;
        _sessionId = session.SessionId;
        ChatTitle.Text = session.DisplayName + (session.AgentAlias is { Length: > 0 } a ? $"  (agent: {a})" : "");
        MessagesPanel.Children.Clear();
        if (Client is not { } client) return;
        try
        {
            foreach (var m in await SessionsApi.MessagesAsync(client, session.SessionId, default))
                AppendMessage(m.Role, m.Content);
        }
        catch (Exception ex) { AppendMessage("system", "Could not load history: " + ex.Message); }
    }

    private async void OnSessionDoubleClick(object sender, MouseButtonEventArgs e) => await RefreshSessionsAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshSessionsAsync();

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not GatewaySession session || Client is null) return;
        if (MessageBox.Show(this, $"Delete session {session.DisplayName}? This cannot be undone.", "Delete session",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await SessionsApi.DeleteAsync(Client, session.SessionId, default); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        await RefreshSessionsAsync();
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not GatewaySession session || Client is null) return;
        var name = PromptDialog.Show(this, "Rename session", "Name:", session.Name ?? session.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await SessionsApi.RenameAsync(Client, session.SessionId, name.Trim(), default); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        await RefreshSessionsAsync();
    }

    private async void OnAbort(object sender, RoutedEventArgs e)
    {
        if (Client is null || _sessionId.Length == 0) return;
        try { await SessionsApi.AbortAsync(Client, _sessionId, default); RunStatus.Text = "Abort requested."; }
        catch (Exception ex) { RunStatus.Text = ex.Message; }
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        if (Client is not { } client) { MessageBox.Show(this, "Connect first (Settings…)."); return; }
        new ConfigWindow(client).ShowDialog();
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(App.Settings, App.Secrets).ShowDialog();
        await RefreshSessionsAsync();
    }

    private async void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_running) return;
        var text = Composer.Text.Trim();
        if (text.Length == 0) return;
        if (Server is not { IsConfigured: true } server || Client is null) { MessageBox.Show(this, "Connect to a gateway first (Settings…)."); return; }
        if (_sessionId.Length == 0) OnNewChat(this, new RoutedEventArgs());

        Composer.Clear();
        AppendMessage("user", text);
        var reply = AppendMessage("assistant", "");
        SendButton.IsEnabled = false;
        _runCts = new CancellationTokenSource();
        try
        {
            _stream = ChatStream.Create(server.BaseUri, App.Secrets.Get(server.TokenKey),
                server.AgentAlias, _sessionId, null);
            await _stream.ConnectAsync(_runCts.Token);
            await _stream.SendMessageAsync(text, _runCts.Token);
            await foreach (var evt in _stream.ReadAsync(_runCts.Token))
            {
                switch (evt)
                {
                    case WsEvent.Chunk chunk: AppendToLast(reply, chunk.Content); break;
                    case WsEvent.Thinking t: RunStatus.Text = "thinking…"; break;
                    case WsEvent.ToolCall tc: AppendMessage("tool", $"🔧 {tc.Name} {tc.Args}"); break;
                    case WsEvent.ToolResult tr: RunStatus.Text = $"tool {tr.Name} done"; break;
                    case WsEvent.ApprovalRequest ar:
                        AppendMessage("approval", $"⚠ {ar.Tool} wants: {ar.ArgumentsSummary} (auto-denies in {ar.TimeoutSecs}s)");
                        AskApproval(ar);
                        break;
                    case WsEvent.Done d:
                        RunStatus.Text = $"done · {d.Model} · {d.TokensUsed} tokens" + (d.CostUsd is { } c ? $" · ${c:0.####}" : "");
                        if (reply.Blocks.Count == 0 && d.FullResponse.Length > 0) AppendToLast(reply, d.FullResponse);
                        break;
                    case WsEvent.Aborted: RunStatus.Text = "aborted"; break;
                    case WsEvent.Error err: AppendMessage("error", err.Message); RunStatus.Text = err.Message; break;
                }
            }
        }
        catch (OperationCanceledException) { RunStatus.Text = "cancelled"; }
        catch (Exception ex) { AppendMessage("error", ex.Message); RunStatus.Text = ex.Message; }
        finally
        {
            _running = false;
            SendButton.IsEnabled = true;
            _runCts?.Dispose(); _runCts = null;
            await RefreshSessionsAsync();
        }
    }

    private void AskApproval(WsEvent.ApprovalRequest ar)
    {
        if (_stream is null) return;
        var dlg = MessageBox.Show(this, $"{ar.Tool}\n\n{ar.ArgumentsSummary}\n\nApprove this action?", "ZeroClaw approval",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        var decision = dlg switch
        {
            MessageBoxResult.Yes => "approve",
            MessageBoxResult.No => "deny",
            _ => "deny",
        };
        _ = Task.Run(async () =>
        {
            try { await _stream.SendApprovalAsync(ar.RequestId, decision, CancellationToken.None); }
            catch { /* socket may be gone */ }
        });
    }

    private FlowDocument AppendMessage(string role, string text)
    {
        var header = new TextBlock
        {
            Text = role switch { "user" => "You", "assistant" => "ZeroClaw", "tool" => "Tool", "approval" => "Approval", "error" => "Error", _ => "System" },
            FontWeight = FontWeights.SemiBold,
            Foreground = role switch { "user" => (Brush)FindResource("Brush.Text"), "assistant" => (Brush)FindResource("Brush.Accent"), "error" => (Brush)FindResource("Brush.Danger"), _ => (Brush)FindResource("Brush.TextMuted") },
            Margin = new Thickness(0, 8, 0, 0),
        };
        var doc = new FlowDocument();
        if (text.Length > 0)
            foreach (var block in MarkdownLite.Render(text))
                doc.Blocks.Add(block);
        var viewer = new FlowDocumentScrollViewer
        {
            Document = doc,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Focusable = false,
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(header);
        panel.Children.Add(viewer);
        MessagesPanel.Children.Add(panel);
        ChatScroll.ScrollToEnd();
        return doc;
    }

    private void AppendToLast(FlowDocument target, string delta)
    {
        if (target.Blocks.LastBlock is Paragraph last)
            last.Inlines.Add(new Run(delta));
        else
            target.Blocks.Add(new Paragraph(new Run(delta)));
        ChatScroll.ScrollToEnd();
    }
}

internal static class PromptDialog
{
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var w = new Window
        {
            Title = title, Width = 380, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, ResizeMode = ResizeMode.NoResize, Background = (Brush)Application.Current.FindResource("Brush.Bg"),
        };
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 8) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(ok);
        var caption = new TextBlock { Text = label, Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted") };
        Grid.SetRow(caption, 0); Grid.SetRow(tb, 1); Grid.SetRow(panel, 2);
        grid.Children.Add(caption); grid.Children.Add(tb); grid.Children.Add(panel);
        w.Content = grid;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; w.Close(); };
        w.ShowDialog();
        return result;
    }
}
