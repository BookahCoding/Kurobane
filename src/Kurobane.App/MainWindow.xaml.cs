using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Kurobane.Core;
using WinForms = System.Windows.Forms;

namespace Kurobane.App;

public partial class MainWindow : Window
{
    private readonly DeviceLocalStore _local = new(AppSettings.SettingsDirectory);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> _toolBodies = new(StringComparer.Ordinal);
    private Expander? _turnThinking;
    private ZeroClawClient? _pageClient;
    private readonly List<SessionRow> _sessions = new();

    private ChatStream? _stream;
    private CancellationTokenSource? _runCts;
    private WinForms.NotifyIcon? _tray;
    private RiskProfileSummary? _risk;
    private string _sessionId = "";
    private string _unsentSessionId = "";
    private string _draftKey = DeviceLocalStore.NewChatSessionKey;
    private int _streamGeneration;
    private bool _running;
    private bool _turnTerminal;
    private bool _suppressSelection;
    private bool _loadingDraft;
    private bool _advanced;
    private bool _sidebarCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        _advanced = string.Equals(App.Settings.UiMode, "advanced", StringComparison.OrdinalIgnoreCase);
        _sidebarCollapsed = App.Settings.SidebarCollapsed;
        ApplyMode();
        ApplySidebar();
        Composer.TextChanged += (_, _) =>
        {
            if (_loadingDraft) return;
            _local.SetDraft(ServerId, _draftKey, Composer.Text);
        };
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
        };
        Loaded += async (_, _) =>
        {
            EnsureTray();
            await RefreshSessionsAsync();
        };
    }

    public void ShowUpdateBanner(UpdateRelease release)
    {
        UpdateBanner.Content = $"Update to Kurobane {release.Version} is available — open Settings → Updates";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private KurobaneConnection? Server => App.Settings.ActiveServer;

    private string ServerId => Server?.TokenKey is { Length: > 0 } key ? key : Server?.BaseUrl ?? "";

    private ZeroClawClient? OpenClient() =>
        Server is { IsConfigured: true } server ? server.CreateClient(App.Secrets) : null;

    private async Task RefreshSessionsAsync()
    {
        if (OpenClient() is not { } client)
        {
            ServerLabel.Text = "Open Settings to connect to a gateway.";
            return;
        }
        using (client)
        {
            ServerLabel.Text = $"{Server!.Name}\n{Server.BaseUrl} · agent {Server.AgentAlias}";
            try
            {
                var config = await ConfigApi.GetConfigAsync(client, default);
                var decision = AgentAlias.Decide(Server.AgentAlias, AgentsApi.Parse(config));
                if (decision.SwitchLocalToKogarashi && !string.Equals(Server.AgentAlias, decision.Alias, StringComparison.Ordinal))
                {
                    Server.AgentAlias = decision.Alias;
                    App.Settings.Save();
                    ServerLabel.Text = $"{Server.Name}\n{Server.BaseUrl} · agent {Server.AgentAlias}";
                    AliasNotice.Text = decision.StatusLine ?? "";
                    AliasNotice.Visibility = Visibility.Visible;
                }
                else if (decision.NeedsUserChoice && decision.StatusLine is { Length: > 0 } line)
                {
                    AliasNotice.Text = line;
                    AliasNotice.Visibility = Visibility.Visible;
                }
                _risk = GatewayParse.ParseRisk(config, Server.AgentAlias);
                var sessions = await SessionsApi.ListAsync(client, default);
                _sessions.Clear();
                foreach (var session in sessions)
                    _sessions.Add(new SessionRow(session, _local.GetLabel(ServerId, session.SessionId)));
                ApplyFilter();
            }
            catch (Exception ex)
            {
                ServerLabel.Text += "\n" + GatewayText.Explain(ex);
            }
        }
    }

    private void ApplyFilter()
    {
        var query = SessionSearch.Text.Trim();
        IEnumerable<SessionRow> rows = _sessions;
        if (query.Length > 0)
        {
            rows = _sessions.Where(row =>
                row.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.LocalLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Session.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (row.Session.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Session.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        var view = rows.ToList();
        _suppressSelection = true;
        SessionList.ItemsSource = view;
        SessionList.SelectedItem = view.FirstOrDefault(row => row.Session.SessionId == _sessionId);
        _suppressSelection = false;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnNewChat(object sender, RoutedEventArgs e)
    {
        RememberDraft();
        if (_unsentSessionId.Length == 0)
            _unsentSessionId = "kb-" + Guid.NewGuid().ToString("N")[..12];
        _sessionId = _unsentSessionId;
        _draftKey = DeviceLocalStore.NewChatSessionKey;
        ShowChat();
        ChatTitle.Text = "New chat — " + _sessionId;
        ShowLabelCaption();
        MessagesPanel.Children.Clear();
        _pending.Clear();
        UpdateNeedsYou();
        LoadDraftIntoComposer();
        Composer.Focus();
    }

    private async void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (SessionList.SelectedItem is not SessionRow row) return;
        RememberDraft();
        _sessionId = row.Session.SessionId;
        _draftKey = _sessionId;
        ShowChat();
        ChatTitle.Text = row.Title + (row.Session.AgentAlias is { Length: > 0 } alias ? $"  (agent: {alias})" : "");
        ShowLabelCaption();
        MessagesPanel.Children.Clear();
        _pending.Clear();
        UpdateNeedsYou();
        LoadDraftIntoComposer();
        if (OpenClient() is not { } client) return;
        using (client)
        {
            try
            {
                foreach (var message in await SessionsApi.MessagesAsync(client, row.Session.SessionId, default))
                    AppendMessage(message.Role, message.Content, copyable: message.Role == "assistant");
            }
            catch (Exception ex)
            {
                AppendMessage("error", GatewayText.Explain(ex), copyable: false);
            }
        }
    }

    private async void OnSessionDoubleClick(object sender, MouseButtonEventArgs e) => await RefreshSessionsAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshSessionsAsync();

    private SessionRow? SelectedRow => SessionList.SelectedItem as SessionRow;

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || OpenClient() is not { } client) return;
        using (client)
        {
            if (MessageBox.Show(this, $"Delete session {row.Title}? This cannot be undone.", "Delete session",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { await SessionsApi.DeleteAsync(client, row.Session.SessionId, default); }
            catch (Exception ex) { MessageBox.Show(this, GatewayText.Explain(ex), "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        if (_sessionId == row.Session.SessionId)
        {
            _sessionId = "";
            MessagesPanel.Children.Clear();
            ChatTitle.Text = "No session selected";
            PcCaption.Text = "";
        }
        await RefreshSessionsAsync();
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || OpenClient() is not { } client) return;
        var name = PromptDialog.Show(this, "Rename session", "Name on the gateway:", row.Session.Name ?? row.Session.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) return;
        using (client)
        {
            try { await SessionsApi.RenameAsync(client, row.Session.SessionId, name.Trim(), default); }
            catch (Exception ex) { MessageBox.Show(this, GatewayText.Explain(ex), "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        await RefreshSessionsAsync();
    }

    private async void OnAbort(object sender, RoutedEventArgs e)
    {
        if (OpenClient() is not { } client || _sessionId.Length == 0) return;
        using (client)
        {
            try { await SessionsApi.AbortAsync(client, _sessionId, default); SetRun("Aborted"); }
            catch (Exception ex) { SetRun("Error: " + GatewayText.Explain(ex)); }
        }
    }

    private void OnLabel(object sender, RoutedEventArgs e)
    {
        if (_sessionId.Length == 0)
        {
            PcCaption.Text = "On this PC: open or start a chat before labeling it.";
            return;
        }
        var current = _local.GetLabel(ServerId, _sessionId) ?? "";
        var entered = PromptDialog.Show(this, "Label on this PC", "On this PC — stored only on this computer, not synced:", current);
        if (entered is null) return;
        _local.SetLabel(ServerId, _sessionId, entered);
        ShowLabelCaption();
        for (var i = 0; i < _sessions.Count; i++)
        {
            if (_sessions[i].Session.SessionId == _sessionId)
                _sessions[i] = new SessionRow(_sessions[i].Session, _local.GetLabel(ServerId, _sessionId));
        }
        ApplyFilter();
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        if (OpenClient() is not { } client) { MessageBox.Show(this, "Connect first (Settings…)."); return; }
        using (client)
            new ConfigWindow(client).ShowDialog();
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(App.Settings, App.Secrets).ShowDialog();
        await RefreshSessionsAsync();
    }

    private void OnToggleMode(object sender, RoutedEventArgs e)
    {
        _advanced = !_advanced;
        App.Settings.UiMode = _advanced ? "advanced" : "simple";
        App.Settings.Save();
        ApplyMode();
        if (!_advanced) ShowChat();
    }

    private void ApplyMode()
    {
        AdvancedNav.Visibility = _advanced ? Visibility.Visible : Visibility.Collapsed;
        ModeText.Text = _advanced ? "Simple" : "Advanced";
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        App.Settings.SidebarCollapsed = _sidebarCollapsed;
        App.Settings.Save();
        ApplySidebar();
    }

    private void ApplySidebar()
    {
        SidebarPanel.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarOpenNarrow.Visibility = _sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(280);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SidebarToggle_Click(sender, e);
            e.Handled = true;
        }
    }

    private void OnBackToChat(object sender, RoutedEventArgs e) => ShowChat();

    private void ShowChat()
    {
        ChatView.Visibility = Visibility.Visible;
        AdvancedView.Visibility = Visibility.Collapsed;
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
        if (Server is not { IsConfigured: true } server) { MessageBox.Show(this, "Connect to a gateway first (Settings…)."); return; }
        if (_sessionId.Length == 0) OnNewChat(this, new RoutedEventArgs());

        _loadingDraft = true;
        Composer.Clear();
        _loadingDraft = false;
        _local.SetDraft(ServerId, _draftKey, "");
        if (_draftKey == DeviceLocalStore.NewChatSessionKey)
        {
            _unsentSessionId = "";
            _draftKey = _sessionId;
        }

        AppendMessage("user", text, copyable: false);
        var reply = AppendMessage("assistant", "", copyable: true);
        _toolBodies.Clear();
        _turnThinking = null;
        SendButton.IsEnabled = false;
        _running = true;
        _turnTerminal = false;
        SetRun("Running");
        var generation = ++_streamGeneration;
        _runCts = new CancellationTokenSource();
        try
        {
            _stream = ChatStream.Create(server.BaseUri, App.Secrets.Get(server.TokenKey), server.AgentAlias, _sessionId, null);
            await _stream.ConnectAsync(_runCts.Token);
            await _stream.SendMessageAsync(text, _runCts.Token);
            await foreach (var evt in _stream.ReadAsync(_runCts.Token))
            {
                switch (evt)
                {
                    case WsEvent.Chunk chunk:
                        AppendToLast(reply.Document, reply.Raw, chunk.Content);
                        break;
                    case WsEvent.Thinking thought when thought.Content.Length > 0:
                        AppendThinking(thought.Content);
                        break;
                    case WsEvent.ToolCall call:
                        AddToolCard(call.Id, call.Name, call.Args);
                        break;
                    case WsEvent.ToolResult result:
                        AddToolResult(result.Id, result.Name, result.Output);
                        break;
                    case WsEvent.ApprovalRequest approval:
                        AddApprovalCard(approval, generation);
                        break;
                    case WsEvent.Done done:
                        _turnTerminal = true;
                        var usage = done.TokensUsed is { } tokens ? $"{tokens} tokens" : "tokens unknown";
                        var cost = done.CostUsd is { } usd ? $" · ${usd:0.####} reported by the gateway" : "";
                        SetRun($"Done · {done.Model} · {usage}{cost}");
                        if (reply.Raw.Length == 0 && done.FullResponse.Length > 0)
                            AppendToLast(reply.Document, reply.Raw, done.FullResponse);
                        break;
                    case WsEvent.Aborted:
                        _turnTerminal = true;
                        SetRun("Aborted");
                        break;
                    case WsEvent.Error err:
                        _turnTerminal = true;
                        var explained = GatewayText.ExplainFrame(err.Code, err.Message);
                        AppendMessage("error", explained, copyable: false);
                        SetRun("Error: " + explained);
                        break;
                }
            }
            if (!_turnTerminal)
                SetRun(GatewayText.UnsettledTurn(false) ?? "Gateway unreachable.");
        }
        catch (OperationCanceledException)
        {
            _turnTerminal = true;
            SetRun("Cancelled");
        }
        catch (Exception ex)
        {
            _turnTerminal = true;
            var explained = GatewayText.Explain(ex);
            AppendMessage("error", explained, copyable: false);
            SetRun("Error: " + explained);
        }
        finally
        {
            _running = false;
            SendButton.IsEnabled = true;
            _streamGeneration++;
            _pending.Clear();
            UpdateNeedsYou();
            ExpireApprovalCards();
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
                _stream = null;
            }
            _runCts?.Dispose();
            _runCts = null;
            await RefreshSessionsAsync();
        }
    }

    private void AddApprovalCard(WsEvent.ApprovalRequest approval, int generation)
    {
        if (approval.RequestId.Length == 0) return;
        _pending.Add(approval.RequestId);
        UpdateNeedsYou();
        if (!IsActive) ShowNeedsYouBalloon();

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)FindResource("Brush.TextMuted"),
            FontSize = 12
        };
        var approve = new Button { Content = "Approve", Style = (Style)FindResource("AccentButton"), Margin = new Thickness(0, 8, 6, 0) };
        var deny = new Button { Content = "Deny", Margin = new Thickness(0, 8, 6, 0) };
        var always = new Button
        {
            Content = new TextBlock
            {
                Text = GatewayText.AlwaysButtonCaption(_risk?.AlwaysAsk),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        void Decide(string decision)
        {
            approve.IsEnabled = false;
            deny.IsEnabled = false;
            always.IsEnabled = false;
            var live = generation == _streamGeneration && _stream is { CanSend: true } open ? open : null;
            if (!_pending.Remove(approval.RequestId) || live is null)
            {
                note.Text = "Not sent. The connection is gone or this request is stale.";
                return;
            }
            UpdateNeedsYou();
            _ = SendDecisionOnce(live, approval.RequestId, decision, note);
        }
        approve.Click += (_, _) => Decide("approve");
        deny.Click += (_, _) => Decide("deny");
        always.Click += (_, _) => Decide("always");
        var buttons = new WrapPanel();
        buttons.Children.Add(approve);
        buttons.Children.Add(deny);
        buttons.Children.Add(always);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = approval.Tool,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Danger")
        });
        stack.Children.Add(new TextBlock { Text = approval.ArgumentsSummary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (approval.TimeoutSecs > 0)
            stack.Children.Add(new TextBlock
            {
                Text = $"The gateway auto-denies in {approval.TimeoutSecs}s if it gets no answer.",
                Foreground = (Brush)FindResource("Brush.TextMuted"),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0)
            });
        stack.Children.Add(buttons);
        stack.Children.Add(note);
        var card = new Border
        {
            Tag = "pending",
            Background = (Brush)FindResource("Brush.SurfaceRaised"),
            BorderBrush = (Brush)FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("Radius.Large"),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 4),
            Child = stack
        };
        MessagesPanel.Children.Add(card);
        ChatScroll.ScrollToEnd();
    }

    private async Task SendDecisionOnce(ChatStream stream, string requestId, string decision, TextBlock note)
    {
        try
        {
            if (!stream.CanSend)
            {
                note.Text = "Not sent. The connection is gone or this request is stale.";
                return;
            }
            await stream.SendApprovalAsync(requestId, decision, CancellationToken.None);
            note.Text = decision switch
            {
                "approve" => "Sent once: approve.",
                "always" => "Sent once: always. " + GatewayText.AlwaysWarning,
                _ => "Sent once: deny."
            };
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            note.Text = "Not sent. The connection is gone or this request is stale.";
        }
        catch (Exception ex)
        {
            note.Text = "Not sent. " + ex.Message;
        }
    }

    private void ExpireApprovalCards()
    {
        foreach (var child in MessagesPanel.Children)
        {
            if (child is not Border { Tag: "pending" } border) continue;
            border.Tag = "stale";
            if (border.Child is not StackPanel stack) continue;
            foreach (var descendant in stack.Children)
            {
                if (descendant is WrapPanel wrap)
                {
                    foreach (var button in wrap.Children)
                        if (button is Button b) b.IsEnabled = false;
                }
            }
        }
    }

    private void OnNeedsYou(object sender, RoutedEventArgs e)
    {
        ShowChat();
        foreach (var child in MessagesPanel.Children)
        {
            if (child is FrameworkElement { Tag: "pending" } element)
            {
                element.BringIntoView();
                return;
            }
        }
    }

    private void UpdateNeedsYou() =>
        NeedsYouText.Text = _pending.Count == 0 ? "Needs you" : $"Needs you ({_pending.Count})";

    private async void OnAttach(object sender, RoutedEventArgs e)
    {
        AttachNote.Text = "";
        if (Server is not { IsConfigured: true } server)
        {
            AttachNote.Text = "Connect to a gateway first (Settings…).";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (PNG, JPEG, GIF, WebP)|*.png;*.jpg;*.jpeg;*.gif;*.webp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(dialog.FileName); }
        catch (Exception ex) { AttachNote.Text = ex.Message; return; }
        if (!GatewayParse.TryPrepareImage(bytes, out _, out var rejection))
        {
            AttachNote.Text = rejection;
            return;
        }
        AttachButton.IsEnabled = false;
        try
        {
            using var client = server.CreateClient(App.Secrets);
            var uploaded = await GatewayParse.UploadImageAsync(client, server.AgentAlias, bytes, default);
            var index = Composer.CaretIndex;
            Composer.Text = Composer.Text.Insert(index, uploaded.Marker);
            Composer.CaretIndex = index + uploaded.Marker.Length;
            AttachNote.Text = "";
        }
        catch (Exception ex)
        {
            AttachNote.Text = GatewayParse.UploadErrorText(ex);
        }
        finally { AttachButton.IsEnabled = true; }
    }

    private void OnPageAgent(object sender, RoutedEventArgs e) => _ = ShowPage("This agent", FillAgent);
    private void OnPageSkills(object sender, RoutedEventArgs e) => _ = ShowPage("Skills", FillSkills);
    private void OnPageTools(object sender, RoutedEventArgs e) => _ = ShowPage("Tools", FillTools);
    private void OnPageJobs(object sender, RoutedEventArgs e) => _ = ShowPage("Jobs", FillJobs);
    private void OnPageMemory(object sender, RoutedEventArgs e) => _ = ShowPage("Memory", FillMemory);
    private void OnPageUsage(object sender, RoutedEventArgs e) => _ = ShowPage("Usage", FillUsage);
    private void OnPagePlugins(object sender, RoutedEventArgs e) => _ = ShowPage("Plugins", FillPlugins);
    private void OnPagePersonality(object sender, RoutedEventArgs e) => _ = ShowPage("Personality", FillPersonality);
    private void OnPageDevices(object sender, RoutedEventArgs e) => _ = ShowPage("Paired devices", FillDevices);

    private async Task ShowPage(string title, Func<ZeroClawClient, StackPanel, Task> fill)
    {
        if (OpenClient() is not { } client)
        {
            MessageBox.Show(this, "Connect to a gateway first (Settings…).");
            return;
        }
        using (client)
        {
            AdvancedTitle.Text = title;
            AdvancedBody.Children.Clear();
            AdvancedView.Visibility = Visibility.Visible;
            ChatView.Visibility = Visibility.Collapsed;
            _pageClient = client;
            try { await fill(client, AdvancedBody); }
            catch (Exception ex) { AddBlock(AdvancedBody, GatewayText.Explain(ex), muted: true); }
            finally { _pageClient = null; }
            if (AdvancedBody.Children.Count == 0) AddBlock(AdvancedBody, "Nothing returned.", muted: true);
        }
    }

    private async Task FillAgent(ZeroClawClient client, StackPanel body)
    {
        var alias = Server?.AgentAlias ?? "";
        var config = await ConfigApi.GetConfigAsync(client, default);
        var provider = GatewayParse.ParseProviderForAgent(config, alias);
        var status = GatewayParse.ParseStatus(await client.StatusAsync(default));
        _risk = GatewayParse.ParseRisk(config, alias);
        AddBlock(body, "Alias: " + alias, muted: false);
        AddBlock(body, GatewayText.FormatModels(provider.ProviderId, provider.Model, provider.FallbackModels, status.Provider, status.Model), muted: false);
        AddBlock(body, "The gateway process model is a different field from this agent's model. Kurobane does not overwrite either.", muted: true);
        var ask = _risk.AlwaysAsk.Count == 0 ? "none" : string.Join(", ", _risk.AlwaysAsk);
        var auto = _risk.AutoApprove.Count == 0 ? "none" : string.Join(", ", _risk.AutoApprove);
        AddBlock(body, $"Risk level: {_risk.Level ?? "unknown"} · workspace only: {_risk.WorkspaceOnly?.ToString() ?? "unknown"}", muted: false);
        AddBlock(body, "always_ask: " + ask, muted: false);
        AddBlock(body, "auto_approve: " + auto, muted: false);
        AddBlock(body, GatewayText.AlwaysWarning, muted: true);
        if (!string.IsNullOrWhiteSpace(status.Version)) AddBlock(body, "Gateway version: " + status.Version, muted: true);
    }

    private async Task FillSkills(ZeroClawClient client, StackPanel body)
    {
        var names = new List<string>();
        var alias = Server?.AgentAlias ?? "";
        await Collect(body, $"/api/agents/{Uri.EscapeDataString(alias)}/skills", node => names.AddRange(GatewayParse.ParseSkillNames(node)));
        await Collect(body, "/api/skills/bundles", node =>
        {
            foreach (var name in GatewayParse.ParseSkillNames(node))
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        });
        if (names.Count == 0) AddBlock(body, "No skill names returned.", muted: true);
        foreach (var name in names) AddBlock(body, name, muted: false);
    }

    private async Task FillTools(ZeroClawClient client, StackPanel body)
    {
        var alias = Uri.EscapeDataString(Server?.AgentAlias ?? "");
        var tools = GatewayParse.ParseTools(await client.GetAsync("/api/tools?agent=" + alias, default));
        if (tools.Count == 0) AddBlock(body, "No tools returned.", muted: true);
        foreach (var tool in tools)
            AddBlock(body, tool.Description is { Length: > 0 } description ? $"{tool.Name} — {description}" : tool.Name, muted: false);
    }

    private async Task FillJobs(ZeroClawClient client, StackPanel body)
    {
        var jobs = GatewayParse.ParseCronJobs(await client.GetAsync("/api/cron", default));
        try
        {
            var settings = GatewayParse.ParseCronSettings(await client.GetAsync("/api/cron/settings", default));
            if (settings.Enabled is not null)
                AddBlock(body, settings.Enabled.Value ? "Scheduler enabled." : "Scheduler disabled.", muted: true);
        }
        catch (Exception ex) { AddBlock(body, GatewayText.Explain(ex), muted: true); }
        AddBlock(body, "A new job is sent only after you confirm. A shell job is not pre-approved: the agent's supervision still applies.", muted: true);
        var add = new Button { Content = "New scheduled job", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        add.Click += async (_, _) =>
        {
            if (OpenClient() is not { } live) { MessageBox.Show(this, "Connect to a gateway first (Settings…)."); return; }
            using (live)
            {
                try { if (await CreateJobAsync(live)) await ShowPage("Jobs", FillJobs); }
                catch (Exception ex) { MessageBox.Show(this, GatewayText.Explain(ex), "Kurobane"); }
            }
        };
        body.Children.Add(add);
        if (jobs.Count == 0) AddBlock(body, "No jobs.", muted: true);
        foreach (var job in jobs)
        {
            var title = string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name;
            AddBlock(body, $"{title} · {job.Schedule ?? "no schedule"} · next {job.NextRun ?? "unknown"} · {(job.Enabled == false ? "paused" : "enabled")} · {job.LastStatus ?? "no last status"}", muted: false);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var id = job.Id;
            var enabled = job.Enabled != false;
            row.Children.Add(JobButton("Runs", async () => await WithClient(live => ShowRunsAsync(live, id))));
            row.Children.Add(JobButton(enabled ? "Pause" : "Resume", async () =>
            {
                if (!Confirm($"Set this job to {(enabled ? "paused" : "enabled")}?")) return;
                await WithClient(async live =>
                {
                    await live.PatchAsync("/api/cron/" + Uri.EscapeDataString(id), GatewayWrites.CronEnabled(!enabled), default);
                    await ShowPage("Jobs", FillJobs);
                });
            }));
            row.Children.Add(JobButton("Delete", async () =>
            {
                if (!Confirm("Delete this scheduled job from the gateway?")) return;
                await WithClient(async live =>
                {
                    await live.DeleteAsync("/api/cron/" + Uri.EscapeDataString(id), default);
                    await ShowPage("Jobs", FillJobs);
                });
            }));
            body.Children.Add(row);
        }
    }

    private async Task WithClient(Func<ZeroClawClient, Task> action)
    {
        if (OpenClient() is not { } live)
        {
            MessageBox.Show(this, "Connect to a gateway first (Settings…).");
            return;
        }
        using (live) await action(live);
    }

    private static Button JobButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { MessageBox.Show(GatewayText.Explain(ex), "Kurobane"); }
        };
        return button;
    }

    private async Task<bool> CreateJobAsync(ZeroClawClient client)
    {
        var agent = Server?.AgentAlias;
        if (string.IsNullOrWhiteSpace(agent)) { MessageBox.Show(this, "Choose an agent in Settings first."); return false; }
        var name = PromptDialog.Show(this, "New scheduled job", "Name (optional):", "");
        if (name is null) return false;
        var schedule = PromptDialog.Show(this, "Schedule", "Cron expression, for example 0 9 * * *:", "0 9 * * *");
        if (string.IsNullOrWhiteSpace(schedule)) return false;
        var kind = MessageBox.Show(this, "Yes: an agent prompt.\nNo: a shell command.\nCancel: stop.", "Job type", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (kind == MessageBoxResult.Cancel) return false;
        string? prompt = null;
        string? command = null;
        if (kind == MessageBoxResult.Yes)
        {
            prompt = PromptDialog.Show(this, "Agent prompt", "Prompt the job will send:", "");
            if (string.IsNullOrWhiteSpace(prompt)) return false;
        }
        else
        {
            command = PromptDialog.Show(this, "Shell command", "Command. Supervision still applies; this is not pre-approved:", "");
            if (string.IsNullOrWhiteSpace(command)) return false;
        }
        var summary = prompt is not null ? "agent prompt" : "shell command";
        if (!Confirm($"Create a {summary} job on {agent} with schedule {schedule.Trim()}?")) return false;
        await client.PostAsync("/api/cron", GatewayWrites.CronAdd(agent, schedule.Trim(), name, prompt, command), default);
        return true;
    }

    private async Task ShowRunsAsync(ZeroClawClient client, string id)
    {
        var node = await client.GetAsync("/api/cron/" + Uri.EscapeDataString(id) + "/runs", default);
        var runs = node["runs"] as JsonArray;
        if (runs is null || runs.Count == 0) { MessageBox.Show(this, "No runs recorded for this job."); return; }
        var lines = new StringBuilder();
        foreach (var run in runs.Take(12))
        {
            if (run is not JsonObject row) continue;
            lines.Append(row["started_at"]?.ToString()).Append(" · ").Append(row["status"]?.ToString());
            var output = row["output"]?.ToString();
            if (!string.IsNullOrWhiteSpace(output)) lines.Append(" · ").Append(output.Length > 180 ? output[..180] : output);
            lines.AppendLine();
        }
        MessageBox.Show(this, lines.ToString(), "Recent runs");
    }

    private bool Confirm(string text)
        => MessageBox.Show(this, text, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private async Task FillMemory(ZeroClawClient client, StackPanel body)
    {
        var entries = GatewayParse.ParseMemory(await client.GetAsync("/api/memory", default));
        if (entries.Count == 0) AddBlock(body, "No memory entries.", muted: true);
        foreach (var entry in entries)
            AddBlock(body, $"{entry.Id} · {entry.Category} · {entry.Timestamp}\n{GatewayParse.TruncateContent(entry.Content)}", muted: false);
    }

    private async Task FillUsage(ZeroClawClient client, StackPanel body)
    {
        var cost = GatewayParse.ParseCost(await client.GetAsync("/api/cost", default));
        AddBlock(body, cost.UsageLine, muted: false);
        AddBlock(body, "Token counts come from the gateway. A zero dollar amount is shown only when the gateway reported one and did not leave the tokens unpriced.", muted: true);
    }

    private async Task FillPlugins(ZeroClawClient client, StackPanel body)
    {
        var plugins = GatewayParse.ParsePlugins(await client.GetAsync("/api/plugins", default));
        AddBlock(body, plugins.PluginsEnabled switch
        {
            false => "Plugins are disabled on the gateway.",
            true => "Plugins are enabled on the gateway.",
            _ => "Plugin enablement was not reported."
        }, muted: true);
        if (plugins.Names.Count == 0) AddBlock(body, "No plugins.", muted: true);
        foreach (var name in plugins.Names) AddBlock(body, name, muted: false);
    }

    private async Task FillPersonality(ZeroClawClient client, StackPanel body)
    {
        var alias = Uri.EscapeDataString(Server?.AgentAlias ?? "");
        var names = GatewayParse.ParsePersonalityNames(await client.GetAsync("/api/personality?agent=" + alias, default));
        AddBlock(body, "Editing a file loads it, then writes it back only after you confirm. The gateway allowlist is the only names Kurobane will open.", muted: true);
        if (names.Count == 0) AddBlock(body, "No personality file names returned.", muted: true);
        foreach (var name in names)
        {
            if (!GatewayWrites.PersonalityFilenameAllowed(name))
            {
                AddBlock(body, name + " — not on the allowlist, left unread.", muted: true);
                continue;
            }
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            var edit = new Button { Content = "Edit" };
            var file = name;
            edit.Click += async (_, _) =>
            {
                try { await WithClient(live => EditPersonalityAsync(live, file)); }
                catch (Exception ex) { MessageBox.Show(this, GatewayText.Explain(ex), "Kurobane"); }
            };
            row.Children.Add(edit);
            body.Children.Add(row);
        }
    }

    private async Task EditPersonalityAsync(ZeroClawClient client, string filename)
    {
        var alias = Server?.AgentAlias ?? "";
        var node = await client.GetAsync("/api/personality/" + Uri.EscapeDataString(filename) + "?agent=" + Uri.EscapeDataString(alias), default);
        var content = node["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var text) ? text : "";
        long? mtime = null;
        if (node["mtime_ms"] is JsonValue mtimeValue)
        {
            if (mtimeValue.TryGetValue<long>(out var asLong)) mtime = asLong;
            else if (mtimeValue.TryGetValue<double>(out var asDouble)) mtime = (long)asDouble;
        }
        var edited = TextEditorDialog.Show(this, filename, content);
        if (edited is null || edited == content) return;
        if (!Confirm($"Replace {filename} on the gateway for {alias}?")) return;
        await client.PutAsync(
            "/api/personality/" + Uri.EscapeDataString(filename) + "?agent=" + Uri.EscapeDataString(alias),
            GatewayWrites.PersonalityPut(edited, mtime),
            default);
        MessageBox.Show(this, "Saved " + filename + ".");
    }

    private async Task FillDevices(ZeroClawClient client, StackPanel body)
    {
        var devices = GatewayParse.ParseDevices(await client.GetAsync("/api/devices", default));
        if (devices.Count == 0) AddBlock(body, "No paired devices returned.", muted: true);
        foreach (var device in devices)
            AddBlock(body, $"{(device.Name.Length == 0 ? "unnamed" : device.Name)} · {(device.Type.Length == 0 ? "unknown type" : device.Type)}", muted: false);
    }

    private async Task Collect(StackPanel body, string path, Action<System.Text.Json.Nodes.JsonNode> take)
    {
        if (_pageClient is null) return;
        try { take(await _pageClient.GetAsync(path, default)); }
        catch (Exception ex) { AddBlock(body, GatewayText.Explain(ex), muted: true); }
    }

    private void AddBlock(StackPanel body, string text, bool muted)
    {
        body.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)FindResource(muted ? "Brush.TextMuted" : "Brush.Text")
        });
    }

    private void RememberDraft()
    {
        if (_loadingDraft) return;
        _local.SetDraft(ServerId, _draftKey, Composer.Text);
    }

    private void LoadDraftIntoComposer()
    {
        _loadingDraft = true;
        Composer.Text = _local.GetDraft(ServerId, _draftKey);
        _loadingDraft = false;
    }

    private void ShowLabelCaption()
    {
        var label = _sessionId.Length == 0 ? null : _local.GetLabel(ServerId, _sessionId);
        PcCaption.Text = string.IsNullOrWhiteSpace(label) ? "" : "On this PC: " + label;
        if (!string.IsNullOrWhiteSpace(label) && _draftKey != DeviceLocalStore.NewChatSessionKey)
            ChatTitle.Text = label;
    }

    private void SetRun(string text) => RunStatus.Text = text;

    private Transcript AppendMessage(string role, string text, bool copyable)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = role switch { "user" => "You", "assistant" => "ZeroClaw", "tool" => "Tool", "approval" => "Approval", "error" => "Error", _ => "System" },
            FontWeight = FontWeights.SemiBold,
            Foreground = role switch
            {
                "user" => (Brush)FindResource("Brush.Text"),
                "assistant" => (Brush)FindResource("Brush.Accent"),
                "error" => (Brush)FindResource("Brush.Danger"),
                _ => (Brush)FindResource("Brush.TextMuted")
            },
            Margin = new Thickness(0, 8, 8, 0)
        });
        var raw = new StringBuilder(text);
        if (copyable)
        {
            var copy = new Button { Content = "Copy", Style = (Style)FindResource("CompactChip"), Margin = new Thickness(0, 8, 0, 0) };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(raw.ToString()); copy.Content = "Copied"; }
                catch (Exception ex) { copy.Content = ex.Message; }
            };
            header.Children.Add(copy);
        }
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        if (text.Length > 0)
            foreach (var block in MarkdownLite.Render(text))
                doc.Blocks.Add(block);
        var viewer = new FlowDocumentScrollViewer
        {
            Document = doc,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            IsSelectionEnabled = true,
            Focusable = true,
            IsTabStop = false
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(header);
        panel.Children.Add(viewer);
        MessagesPanel.Children.Add(panel);
        ChatScroll.ScrollToEnd();
        return new Transcript(doc, raw);
    }

    private void AppendToLast(FlowDocument target, StringBuilder raw, string delta)
    {
        raw.Append(delta);
        if (target.Blocks.LastBlock is Paragraph last)
            last.Inlines.Add(new Run(delta));
        else
            target.Blocks.Add(new Paragraph(new Run(delta)) { Margin = new Thickness(0, 2, 0, 2) });
        ChatScroll.ScrollToEnd();
    }

    private void AppendThinking(string content)
    {
        if (content.Length == 0) return;
        if (_turnThinking?.Content is not TextBox box)
        {
            box = ReadOnlyBox(content);
            _turnThinking = new Expander { Header = "Thinking", IsExpanded = false, Content = box, Margin = new Thickness(0, 6, 0, 2) };
            MessagesPanel.Children.Add(_turnThinking);
        }
        else box.AppendText(content);
        ChatScroll.ScrollToEnd();
    }

    private void AddToolCard(string id, string name, string args)
    {
        var box = ReadOnlyBox(args);
        var expander = new Expander { Header = name.Length == 0 ? "tool" : name, IsExpanded = false, Content = box, Margin = new Thickness(0, 4, 0, 2) };
        MessagesPanel.Children.Add(expander);
        if (id.Length > 0) _toolBodies[id] = box;
        ChatScroll.ScrollToEnd();
    }

    private void AddToolResult(string id, string name, string output)
    {
        if (id.Length > 0 && _toolBodies.TryGetValue(id, out var existing))
        {
            if (existing.Text.Length > 0) existing.AppendText("\n\n");
            existing.AppendText(output);
            return;
        }
        AddToolCard(id, name.Length == 0 ? "tool result" : name + " result", output);
    }

    private static TextBox ReadOnlyBox(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        AcceptsReturn = true
    };

    private void EnsureTray()
    {
        if (_tray is not null) return;
        try
        {
            System.Drawing.Icon? icon = null;
            if (Environment.ProcessPath is { Length: > 0 } path)
                icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            _tray = new WinForms.NotifyIcon
            {
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "Kurobane"
            };
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(Activate);
        }
        catch { _tray = null; }
    }

    private void ShowNeedsYouBalloon()
    {
        try
        {
            _tray?.ShowBalloonTip(4000, "Kurobane needs you",
                "An approval is waiting. This is a foreground notice while Kurobane is open, not background delivery.",
                WinForms.ToolTipIcon.Info);
        }
        catch { /* the in-window card still stands */ }
    }

    private readonly record struct Transcript(FlowDocument Document, StringBuilder Raw);
}

public sealed class SessionRow
{
    public SessionRow(GatewaySession session, string? localLabel)
    {
        Session = session;
        LocalLabel = localLabel ?? "";
    }

    public GatewaySession Session { get; }
    public string LocalLabel { get; }
    public string Title => LocalLabel.Length > 0 ? LocalLabel : Session.DisplayName;
    public string Detail
    {
        get
        {
            var when = Session.LastActivity == DateTimeOffset.MinValue ? "" : Session.LastActivity.ToLocalTime().ToString("g");
            var text = $"{Session.MessageCount} messages";
            if (when.Length > 0) text += " · " + when;
            if (LocalLabel.Length > 0) text += " · On this PC";
            return text;
        }
    }
}

internal static class PromptDialog
{
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var w = new Window
        {
            Title = title, Width = 420, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, ResizeMode = ResizeMode.NoResize, Background = (Brush)Application.Current.FindResource("Brush.Bg"),
        };
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(ok);
        var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted") };
        Grid.SetRow(caption, 0); Grid.SetRow(tb, 1); Grid.SetRow(panel, 2);
        grid.Children.Add(caption); grid.Children.Add(tb); grid.Children.Add(panel);
        w.Content = grid;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; w.Close(); };
        w.ShowDialog();
        return result;
    }
}

internal static class TextEditorDialog
{
    public static string? Show(Window owner, string title, string initial)
    {
        var w = new Window
        {
            Title = title, Width = 640, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, Background = (Brush)Application.Current.FindResource("Brush.Bg"),
        };
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TextBox
        {
            Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"),
        };
        var ok = new Button { Content = "Save", IsDefault = true, Width = 80, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(tb, 0); Grid.SetRow(ok, 1);
        grid.Children.Add(tb); grid.Children.Add(ok);
        w.Content = grid;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; w.DialogResult = true; w.Close(); };
        w.ShowDialog();
        return result;
    }
}
