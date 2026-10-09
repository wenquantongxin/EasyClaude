using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using EasyClaude.Core;
using Microsoft.Web.WebView2.Core;

namespace EasyClaude;

public partial class MainWindow : Window
{
    private static readonly Brush BadgeGreen = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush BadgeYellow = new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x1D));
    private static readonly Brush BadgeGray = new SolidColorBrush(Color.FromRgb(0x6E, 0x76, 0x81));
    private static readonly Brush DetailGray = new SolidColorBrush(Color.FromRgb(0xB0, 0xB8, 0xC0));

    private readonly Orchestrator _orchestrator;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private string? _origin;     // e.g. http://127.0.0.1:51234 — the only origin the WebView may talk to
    private string? _authHeader;
    private bool _webReady;

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
        var key = "API Key：" + (state.Configured ? state.Hint ?? "已配置" : "未配置");
        (string status, Brush brush) = state.LastTest switch
        {
            null when state.Configured => ("（未测试）", DetailGray),
            null => ("（点右上角“Claude API Key”填写）", BadgeYellow),
            { Ok: true } t => ($"（✓ 已连通 · {t.Model} · {t.LatencyMs} ms）", BadgeGreen),
            { } t => ("（✗ " + t.Message + "）", (Brush)Brushes.Tomato),
        };
        KeyText.Text = key + status;
        KeyText.Foreground = brush;
        KeyText.ToolTip = state.LastTest switch
        {
            { Ok: true, Reply: { } reply } => "Claude 回复：" + reply,
            { Ok: false } t => t.Message,
            _ => null,
        };
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
        PreparationPanel.Visibility = Visibility.Visible;
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
        PreparationPanel.Visibility = Visibility.Visible;
        Overlay.Text = message;
        PreparationProgress.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        SetActionsEnabled(true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        ImportButton.IsEnabled = ApiKeyButton.IsEnabled = RestartProxyButton.IsEnabled =
            RestartAllButton.IsEnabled = MoreButton.IsEnabled = RecheckButton.IsEnabled = enabled;
    }

    public void DisposeWebView() => Web.Dispose();

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
        Web.Visibility = Visibility.Visible;
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
        Web.Visibility = Visibility.Visible;
        Web.CoreWebView2.Navigate(connectUri.ToString());
    }

    public void ShowStatus(LinkStatus status)
    {
        StatusBadge.Background = status.State switch
        {
            LinkState.Ready => Brushes.SeaGreen,
            LinkState.Degraded or LinkState.Paused => Brushes.DarkGoldenrod,
            LinkState.Starting => Brushes.SteelBlue,
            LinkState.Error => Brushes.Firebrick,
            _ => Brushes.Gray,
        };
        StatusGlyph.Text = status.State switch
        {
            LinkState.Ready => "已连接", LinkState.Degraded => "提示", LinkState.Paused => "已暂停",
            LinkState.Error => "异常", LinkState.Starting => "准备中", _ => "已停止",
        };
        StatusText.Text = status.Message;
        NodeText.Text = status.NodeText;
        EgressText.Text = status.EgressText;
        EgressText.Foreground = status.Consistency == EgressConsistency.Changed ? BadgeYellow : DetailGray;
        ShowConsistency(status);
        if (Web.Visibility != Visibility.Visible) Overlay.Text = status.Message;

        var busy = status.State == LinkState.Starting;
        SetActionsEnabled(!busy);
        ConflictRecheckButton.IsEnabled = !busy;
        ConflictContinueButton.IsEnabled = !busy;
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
            EgressConsistency.Same => ("与上次一致", BadgeGreen, Brushes.White, "出口 IP 与上次运行时相同"),
            EgressConsistency.Changed => ("与上次不同", BadgeYellow, Brushes.Black, "上次：" + status.PreviousEgress),
            EgressConsistency.FirstRecord => ("首次记录", BadgeGray, Brushes.White, "第一次记录出口 IP，下次启动时将与它比对"),
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

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        if (IsLocal(uri))
        {
            if (_authHeader is not null) e.Request.Headers.SetHeader("Authorization", _authHeader);
            return;
        }
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return;
        // The UI may only talk to the local OpenCode server: no telemetry, CDNs or direct internet access.
        _log.Write("webview", "blocked non-local request to " + HostOf(uri));
        e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked by Easy Claude", "Content-Type: text/plain");
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
