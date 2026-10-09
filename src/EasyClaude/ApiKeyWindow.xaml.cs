using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using EasyClaude.Core;

namespace EasyClaude;
public partial class ApiKeyWindow : Window
{
    private readonly Orchestrator _orchestrator;
    private CancellationTokenSource? _operation;
    private bool _busy;
    public ApiKeyWindow(Orchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        InitializeComponent();
        ShowCurrent();
        if (orchestrator.ApiKey.LastTest is { } last) ShowResult(last, orchestrator.ApiKey.IsHistorical, orchestrator.ApiKey.TestedAt);
        Loaded += (_, _) => KeyBox.Focus();
        Closing += (_, _) => _operation?.Cancel();
    }
    private void KeyChanged(object sender, RoutedEventArgs e) => UpdateAction();
    private void UpdateAction()
    {
        if (ActionButton is null) return;
        if (_busy) { ActionButton.Content = "处理中…"; ActionButton.IsEnabled = false; return; }
        var entered = !string.IsNullOrWhiteSpace(KeyBox.Password);
        ActionButton.Content = entered ? "保存新 Key 并测试" : "测试已保存的 Key";
        ActionButton.IsEnabled = !_busy && (entered || _orchestrator.ApiKey.Configured);
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var key = KeyBox.Password.Trim();
        _operation = new CancellationTokenSource();
        _busy = true;
        KeyBox.IsEnabled = false;
        CloseButton.Content = "取消测试";
        UpdateAction();
        ResultText.Foreground = Brushes.SlateGray;
        ResultText.Text = key.Length == 0 ? "正在测试已保存的 Key…" : "正在验证新 Key，通过后保存并测试模型…";
        try
        {
            var result = key.Length == 0 ? await _orchestrator.TestApiKeyAsync(_operation.Token) : await _orchestrator.ApplyApiKeyAsync(key, _operation.Token);
            if (result.Saved) KeyBox.Clear();
            ShowResult(result);
        }
        catch (OperationCanceledException) { ResultText.Text = "操作已取消"; }
        catch (Exception ex) { ResultText.Text = "操作未完成：" + ex.Message; }
        finally
        {
            key = string.Empty;
            _operation.Dispose(); _operation = null;
            _busy = false; KeyBox.IsEnabled = true; CloseButton.Content = "关闭";
            ShowCurrent();
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _operation?.Cancel(); return; }
        Close();
    }
    private void ShowCurrent()
    {
        CurrentText.Text = "已保存的 Key：" + (_orchestrator.ApiKey.Configured ? _orchestrator.ApiKey.Hint ?? "已配置" : "未配置");
        UpdateAction();
    }
    private void ShowResult(ApiKeyTest result, bool historical = false, DateTimeOffset? testedAt = null)
    {
        ResultText.Foreground = result.Ok ? Brushes.SeaGreen : Brushes.Firebrick;
        var saved = result.Saved ? "新 Key 已保存。\n" : string.Empty;
        var history = historical ? "上次测试记录" + (testedAt is { } at ? $" · {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "") + "\n本次启动未重新发送测试请求。\n" : string.Empty;
        ResultText.Text = history + saved + (result.Ok ? $"模型测试成功 · {result.Model} · {result.LatencyMs} ms" + (result.Reply is null ? "" : "\nClaude 回复：" + result.Reply) :
                result.Message + (result.Model is null ? "" : $"\n实际模型：{result.Model} · {result.LatencyMs} ms"))
            + (result.FallbackReason is null ? "" : "\n" + result.FallbackReason);
    }
}
