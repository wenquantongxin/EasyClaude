using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using EasyClaude.Core;
using Microsoft.Web.WebView2.Core;

namespace EasyClaude;

public partial class MainWindow : Window
{
    private static readonly Brush BadgeGreen = new SolidColorBrush(Color.FromRgb(0x18, 0x7D, 0x58));
    private static readonly Brush BadgeYellow = new SolidColorBrush(Color.FromRgb(0x9A, 0x65, 0x11));
    private static readonly Brush BadgeGray = new SolidColorBrush(Color.FromRgb(0x66, 0x76, 0x8B));
    private static readonly Brush DetailGray = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush ErrorRose = new SolidColorBrush(Color.FromRgb(0xBE, 0x38, 0x50));

    private readonly Orchestrator _orchestrator;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private string? _origin;     // e.g. http://127.0.0.1:51234 — the only origin the WebView may talk to
    private string? _authHeader;
    private bool _webReady;
    private bool _showingHelp;
    private HelpView? _help;
    private readonly HttpClient _catalogHttp = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource _webLifetime = new();
    private bool _webDisposed;

    public MainWindow(Orchestrator orchestrator, AppPaths paths, FileLogger log)
    {
        _orchestrator = orchestrator;
        _paths = paths;
        _log = log;
        InitializeComponent();
    }

    public event Action? OpenLogsRequested;

    public event Action? ImportProfileRequested;

    public event Action? ApiKeyRequested;
    public event Action? AdvancedRequested;
    public event Action? RestartRequested;
    public event Action? ExitRequested;
    public event Action? RecheckRequested;
    private bool _closingPrompt;

    /// <summary>"API Key：sk-ant-api03-AbCd…（✓ 已连通 · model · N ms）" — only the first few characters are ever shown.</summary>
    public void ShowApiKey(ApiKeyState state)
    {
        var key = state.Configured ? state.Hint ?? "已配置" : "未配置";
        (string status, Brush brush) = state.LastTest switch
        {
            null when state.Configured => (" · 未测试", DetailGray),
            null => (" · 点击右上角填写", BadgeYellow),
            { Ok: true } when state.IsHistorical => (" · 上次测试成功", BadgeGreen),
            { Ok: false } when state.IsHistorical => (" · 上次测试未成功", ErrorRose),
            { Ok: true } => (" · 模型已连通", BadgeGreen),
            _ => (" · 测试未成功", ErrorRose),
        };
        KeyText.Text = key + status;
        KeyText.Foreground = brush;
        var result = state.LastTest switch
        {
            { Ok: true } t => $"实际模型：{t.Model}\n耗时：{t.LatencyMs} ms\n{t.Message}" + (t.Reply is null ? "" : "\nClaude 回复：" + t.Reply),
            { Ok: false } t => t.Message,
            _ => null,
        };
        var time = state.TestedAt is { } at ? $"测试时间：{at.ToLocalTime():yyyy-MM-dd HH:mm}\n" : string.Empty;
        KeyText.ToolTip = result is null ? null : time + (state.IsHistorical ? "这是上次测试记录，本次启动未重新测试。\n" : "") + result;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            if (!_closingPrompt)
            {
                _closingPrompt = true;
                try
                {
                    var dialog = new CloseWindow { Owner = this };
                    if (dialog.ShowDialog() == true)
                    {
                        if (dialog.ExitProgram) ExitRequested?.Invoke();
                        else Hide();
                    }
                }
                finally { _closingPrompt = false; }
            }
        }
        base.OnClosing(e);
    }

    public void ShowPreparation(DependencyProgress progress)
    {
        PreparationPanel.Visibility = _showingHelp ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        Overlay.Text = progress.TotalBytes is > 0
            ? $"{progress.Message} · {progress.Bytes / 1048576.0:F1} / {progress.TotalBytes / 1048576.0:F1} MB"
            : progress.Message;
        PreparationProgress.Visibility = Visibility.Visible;
        PreparationProgress.IsIndeterminate = progress.TotalBytes is not > 0;
        if (progress.TotalBytes is > 0) PreparationProgress.Value = 100.0 * progress.Bytes / progress.TotalBytes.Value;
        SetActionsEnabled(false);
    }

    public void ShowPreparationError(string message)
    {
        PreparationPanel.Visibility = _showingHelp ? Visibility.Collapsed : Visibility.Visible;
        Overlay.Text = message;
        PreparationProgress.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        SetActionsEnabled(true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        ImportButton.IsEnabled = ApiKeyButton.IsEnabled = RestartProxyButton.IsEnabled =
            RestartAllButton.IsEnabled = RecheckButton.IsEnabled = AdvancedMenuItem.IsEnabled = enabled;
    }

    public void DisposeWebView()
    {
        if (_webDisposed) return;
        _webDisposed = true;
        _webLifetime.Cancel();
        _catalogHttp.Dispose();
        Web.Dispose();
        _webLifetime.Dispose();
    }

    internal async Task<bool> CheckLocalBrowserAsync(CancellationToken ct)
    {
        while (_origin is null || !Web.CoreWebView2.Source.StartsWith(_origin + "/", StringComparison.OrdinalIgnoreCase) ||
            (await Web.CoreWebView2.CookieManager.GetCookiesAsync(_origin)).Count == 0)
            await Task.Delay(200, ct);
        // All real resources and API calls still go through the production WebView2 filter.
        await Web.CoreWebView2.ExecuteScriptAsync("window.__easyClaudeCheck = null; fetch('/api/info').then(r => window.__easyClaudeCheck = r.status).catch(() => window.__easyClaudeCheck = -1);").WaitAsync(ct);
        while (true)
        {
            var status = await Web.CoreWebView2.ExecuteScriptAsync("window.__easyClaudeCheck").WaitAsync(ct);
            if (status == "200") return true;
            if (status == "-1") return false;
            await Task.Delay(200, ct);
        }
    }

    public async Task InitializeWebViewAsync()
    {
        Web.Visibility = _showingHelp ? Visibility.Collapsed : Visibility.Visible;
        var options = new CoreWebView2EnvironmentOptions("--no-proxy-server");
        var environment = await CoreWebView2Environment.CreateAsync(null, _paths.WebView2Data, options);
        await Web.EnsureCoreWebView2Async(environment);
        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (IsLocal(e.Uri) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (_origin is null) return;
            try
            {
                var cookies = await core.CookieManager.GetCookiesAsync(_origin);
                _log.Write("webview", $"navigation {(e.IsSuccess ? "ok" : "failed: " + e.WebErrorStatus)}; cookies for local origin: {cookies.Count}");
            }
            catch (Exception ex)
            {
                _log.Write("webview", "cookie check failed: " + ex.Message);
            }
        };
        _webReady = true;
        if (_origin is null) Web.Visibility = Visibility.Collapsed;
    }

    public void NavigateToOpenCode(Uri connectUri, string authHeader)
    {
        _origin = connectUri.GetLeftPart(UriPartial.Authority);
        _authHeader = authHeader;
        if (!_webReady) return;
        PreparationPanel.Visibility = Visibility.Collapsed;
        Web.Visibility = _showingHelp ? Visibility.Collapsed : Visibility.Visible;
        Web.CoreWebView2.Navigate(connectUri.ToString());
    }

    public void ShowStatus(LinkStatus status)
    {
        (string background, string foreground) = status.State switch
        {
            LinkState.Ready => ("#E8F6EF", "#187D58"),
            LinkState.Degraded or LinkState.Paused => ("#FFF3D7", "#9A6511"),
            LinkState.Starting => ("#EAF2FE", "#2563EB"),
            LinkState.Error => ("#FDECEF", "#BE3850"),
            _ => ("#EDF1F6", "#66768B"),
        };
        StatusBadge.Background = BrushOf(background);
        StatusGlyph.Foreground = StatusDot.Fill = BrushOf(foreground);
        StatusGlyph.Text = status.State switch
        {
            LinkState.Ready => "已连接", LinkState.Degraded => "提示", LinkState.Paused => "已暂停",
            LinkState.Error => "异常", LinkState.Starting => "准备中", _ => "已停止",
        };
        StatusText.Text = status.Message;
        StatusText.ToolTip = status.Message;
        NodeText.Text = status.Node is null ? "节点待确认" : "节点 " + status.Node;
        NodeText.ToolTip = NodeText.Text;
        ShowWifi(status);
        EgressText.Text = string.IsNullOrEmpty(status.EgressText) ? "等待检测出口…" : status.EgressText;
        EgressText.ToolTip = EgressText.Text;
        EgressText.Foreground = status.Consistency == EgressConsistency.Changed ? BadgeYellow : DetailGray;
        ShowConsistency(status);
        if (Web.Visibility != Visibility.Visible) Overlay.Text = status.Message;

        var busy = status.State == LinkState.Starting;
        SetActionsEnabled(!busy);
        ConflictRecheckButton.IsEnabled = !busy;
        ConflictContinueButton.IsEnabled = !busy;
    }

    private void ShowWifi(LinkStatus status)
    {
        var pending = status.State == LinkState.Starting;
        var offline = !pending && (status.NodeReachable == false || status.State == LinkState.Stopped);
        var delay = offline || pending ? null : status.DelayMs;
        var color = pending ? "#3B82F6" : offline || delay is null ? "#94A3B8" : delay <= 250 ? "#3B82F6" : delay <= 800 ? "#BA7C14" : "#D9546C";
        WifiGlyph.Stroke = WifiDot.Fill = BrushOf(color);
        LatencyText.Foreground = offline ? ErrorRose : !pending && delay is null ? DetailGray : BrushOf(color);
        LatencyBadge.Background = BrushOf(offline || delay > 800 ? "#FFF1F2" : delay > 250 ? "#FFF8E8" : !pending && delay is null ? "#EDF1F6" : "#EFF6FF");
        WifiOfflineBadge.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        LatencyText.Text = pending ? "检测中…" : offline ? "未连通" : delay is null ? "待检测" : delay + " ms";
        var quality = delay is null ? null : delay <= 250 ? "延迟较低" : delay <= 800 ? "延迟中等" : "延迟较高";
        var detail = pending ? "正在检测节点与连接，请稍候。" : offline ? "上次节点探测未成功，可点击重新检测。" : delay is null ? "尚未取得节点延迟。" : $"节点延迟 {delay} ms · {quality}。";
        RecheckButton.ToolTip = "重新检测连接\n" + detail + "\n自动刷新：每 60 秒。蓝色 ≤ 250 ms；琥珀色 251–800 ms；玫红色 > 800 ms。\n延迟表示响应时间，不代表下载带宽或模型生成速度。";
        System.Windows.Automation.AutomationProperties.SetItemStatus(RecheckButton, pending ? "检测中" : offline ? "未连通" : delay is null ? "待检测" : $"{delay} 毫秒，{quality}");
    }

    private static Brush BrushOf(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    internal void ShowHelp()
    {
        if (_help is null)
        {
            _help = new HelpView();
            _help.CloseRequested += HideHelp;
            HelpPanel.Content = _help;
        }
        _showingHelp = true;
        Web.Visibility = PreparationPanel.Visibility = Visibility.Collapsed;
        HelpPanel.Visibility = Visibility.Visible;
        _help.FocusReturnButton();
    }

    internal void HideHelp()
    {
        _showingHelp = false;
        HelpPanel.Visibility = Visibility.Collapsed;
        var hasChat = _webReady && _origin is not null;
        Web.Visibility = hasChat ? Visibility.Visible : Visibility.Collapsed;
        PreparationPanel.Visibility = hasChat ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Focus();
    }

    /// <summary>Shows the TUN banner while a conflict exists and the user has not chosen "仍然继续".</summary>
    public void ShowConflict(RouteConflict? conflict, bool overridden)
    {
        ConflictBanner.Visibility = conflict is not null && !overridden ? Visibility.Visible : Visibility.Collapsed;
        ConflictText.Text = conflict?.Message ?? string.Empty;
    }

    private void ShowConsistency(LinkStatus status)
    {
        (string? text, Brush? background, Brush? foreground, string? tip) = status.Consistency switch
        {
            EgressConsistency.Same => ("与上次一致", BrushOf("#E8F6EF"), BadgeGreen, "出口 IP 与上次运行时相同"),
            EgressConsistency.Changed => ("与上次不同", BrushOf("#FFF3D7"), BadgeYellow, "上次：" + status.PreviousEgress),
            EgressConsistency.FirstRecord => ("首次记录", BrushOf("#EDF1F6"), BadgeGray, "第一次记录出口 IP，下次启动时将与它比对"),
            _ => (null, null, null, null),
        };
        if (text is null)
        {
            ConsistencyBadge.Visibility = Visibility.Collapsed;
            return;
        }
        ConsistencyText.Text = text;
        ConsistencyText.Foreground = foreground;
        ConsistencyBadge.Background = background;
        ConsistencyBadge.ToolTip = tip;
        ConsistencyBadge.Visibility = Visibility.Visible;
    }

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        if (IsLocal(uri))
        {
            if (_authHeader is not null) e.Request.Headers.SetHeader("Authorization", _authHeader);
            if (e.Request.Method == "GET" && new Uri(uri).AbsolutePath == "/api/model")
            {
                var deferral = e.GetDeferral();
                try { await ShowModelCatalogAsync(e); }
                finally
                {
                    try { deferral.Complete(); }
                    catch (Exception) when (_webDisposed) { }
                }
            }
            return;
        }
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return;
        // The UI may only talk to the local OpenCode server: no telemetry, CDNs or direct internet access.
        _log.Write("webview", "blocked non-local request to " + HostOf(uri));
        e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by Easy Claude", "Content-Type: text/plain");
    }

    private async Task ShowModelCatalogAsync(CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            // Replace just this local GET: the backend receives one request with its original project context.
            // Key tests use OpenCodeClient directly and retain access to Haiku when available.
            using var request = new HttpRequestMessage(HttpMethod.Get, e.Request.Uri);
            foreach (var header in e.Request.Headers)
                if (!header.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _catalogHttp.SendAsync(request, _webLifetime.Token);
            var body = await response.Content.ReadAsStringAsync(_webLifetime.Token);
            if (response.IsSuccessStatusCode) body = ModelCatalogVisibility.Filter(body);
            if (_webDisposed) return;
            e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes(body)), (int)response.StatusCode, response.ReasonPhrase ?? "Model catalog",
                "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception) when (_webDisposed) { }
        catch (Exception ex)
        {
            _log.Write("webview", "model catalog unavailable: " + ex.GetType().Name);
            e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes("{\"message\":\"模型列表暂不可用，请稍后重试。\"}")),
                502, "Model catalog unavailable", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
        }
    }

    private bool IsLocal(string uri) =>
        _origin is not null &&
        (uri.Equals(_origin, StringComparison.OrdinalIgnoreCase) || uri.StartsWith(_origin + "/", StringComparison.OrdinalIgnoreCase));

    private static string HostOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Scheme + "://" + u.Host : "(invalid uri)";

    private void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        _log.Write("webview", "opening in system browser: " + HostOf(uri));
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _log.Write("webview", "open external failed: " + ex.Message); }
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => RecheckRequested?.Invoke();

    private async void RestartProxy_Click(object sender, RoutedEventArgs e) => await _orchestrator.RestartProxyAsync();

    private void RestartAll_Click(object sender, RoutedEventArgs e) => RestartRequested?.Invoke();

    private void Advanced_Click(object sender, RoutedEventArgs e) => AdvancedRequested?.Invoke();
    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();
    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.IsOpen = true;
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();

    private void Import_Click(object sender, RoutedEventArgs e) => ImportProfileRequested?.Invoke();

    private void ApiKey_Click(object sender, RoutedEventArgs e) => ApiKeyRequested?.Invoke();

    private async void ConflictRecheck_Click(object sender, RoutedEventArgs e) => await _orchestrator.RecheckAsync();

    private async void ConflictContinue_Click(object sender, RoutedEventArgs e) => await _orchestrator.ContinueDespiteConflictAsync();
}
