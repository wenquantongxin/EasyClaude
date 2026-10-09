using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyClaude.Core;

namespace EasyClaude;

/// <summary>Deterministic WPF acceptance harness; no real keys, model calls or changes to system DPI.</summary>
internal static class UiCheck
{
    internal static async Task RunAsync(AppPaths paths, string output)
    {
        Directory.CreateDirectory(output);
        paths.EnsureCreated();
        var results = new List<object>();
        void Check(string name, bool ok) { results.Add(new { name, ok }); if (!ok) throw new InvalidOperationException(name); }
        using var log = new FileLogger(paths.LogFile, new Redactor());
        await using var orchestrator = new Orchestrator(paths, new(), log, new());
        typeof(Orchestrator).GetProperty(nameof(Orchestrator.ApiKey))!.SetValue(orchestrator, new ApiKeyState(true, "sk-ant-demo…", null));
        var main = new MainWindow(orchestrator, paths, log);
        var historical = new ApiKeyState(true, "sk-ant-demo…", new(true, "模型测试成功", "claude-haiku-test", LatencyMs: 142), DateTimeOffset.UtcNow.AddHours(-1), true);
        main.ShowApiKey(historical);
        Check("remembered key test is labeled as history", ((TextBlock)main.FindName("KeyText")).Text.Contains("上次测试成功"));
        Check("remembered key test tooltip shows time and no new request", ((TextBlock)main.FindName("KeyText")).ToolTip.ToString()!.Contains("测试时间：") && ((TextBlock)main.FindName("KeyText")).ToolTip.ToString()!.Contains("未重新测试"));
        main.ShowStatus(new(LinkState.Ready, "连接服务正常", "示例节点", 142, "US", "LAX", "198.51.100.123", "美国 · California · Los Angeles", "AS13335 Cloudflare, Inc."));
        Check("restart wording", AutomationProperties.GetName((Button)main.FindName("RestartAllButton")) == "重启程序");
        Check("application icon", main.Icon is not null);
        var wifi = (Button)main.FindName("RecheckButton");
        var cross = (Border)main.FindName("WifiOfflineBadge");
        var latency = (TextBlock)main.FindName("LatencyText");
        string WifiColor() => ((SolidColorBrush)((System.Windows.Shapes.Path)main.FindName("WifiGlyph")).Stroke).Color.ToString();
        var clicks = 0;
        main.RecheckRequested += () => clicks++;
        wifi.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("wifi invokes recheck directly", clicks == 1 && wifi.IsEnabled);
        Check("recheck removed from more menu", !Items((ContextMenu)((Button)main.FindName("MoreButton")).ContextMenu).Exists(item => item.Header?.ToString() == "重新检测连接"));
        Check("low latency blue", WifiColor() == "#FF3B82F6" && latency.Text == "142 ms" && cross.Visibility == Visibility.Collapsed);
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) Render(main, output, "main-" + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), 1280, 760, scale);
        var header = (FrameworkElement)((DockPanel)main.Content).Children[0];
        Capture(header, output, "header", header.ActualWidth, header.ActualHeight, 1);
        Render(main, output, "main-minimum", 1020, 500, 1);
        var connection = (Border)main.FindName("ConnectionCard");
        var egress = (Border)main.FindName("EgressCard");
        Check("connection area stays compact", connection.ActualWidth <= 300);
        Check("egress area gets substantially more width", egress.ActualWidth >= connection.ActualWidth * 1.5);
        var wifiPath = (System.Windows.Shapes.Path)main.FindName("WifiGlyph");
        var wifiDot = (System.Windows.Shapes.Ellipse)main.FindName("WifiDot");
        var wifiBounds = wifiPath.Data.GetRenderBounds(new Pen(wifiPath.Stroke, wifiPath.StrokeThickness));
        wifiBounds.Union(new Rect(wifiDot.TranslatePoint(new Point(), wifiPath), new Size(wifiDot.ActualWidth, wifiDot.ActualHeight)));
        var iconRight = wifiPath.TranslatePoint(new Point(wifiBounds.Right, 0), (FrameworkElement)main.Content).X;
        var nodeLeft = ((TextBlock)main.FindName("NodeText")).TranslatePoint(new Point(), (FrameworkElement)main.Content).X;
        Check("wifi grouped next to node with compact spacing", nodeLeft - iconRight is >= 6 and <= 14);
        var iconMiddle = wifiPath.TranslatePoint(new Point(0, wifiBounds.Y + wifiBounds.Height / 2), (FrameworkElement)main.Content).Y;
        var nodeBlock = (TextBlock)main.FindName("NodeText");
        var nodeMiddle = nodeBlock.TranslatePoint(new Point(0, nodeBlock.ActualHeight / 2), (FrameworkElement)main.Content).Y;
        Check("wifi vertically centered with node text", Math.Abs(iconMiddle - nodeMiddle) <= 1.5);
        Check("wifi visual height matches detail text", Math.Abs(wifiBounds.Height - ((TextBlock)main.FindName("NodeText")).ActualHeight) <= 3);
        main.ShowStatus(new(LinkState.Ready, "连接服务正常", "示例节点", 600));
        Check("medium latency amber", WifiColor() == "#FFBA7C14" && latency.Text == "600 ms");
        main.ShowStatus(new(LinkState.Degraded, "节点延迟偏高", "示例节点", 1200));
        Check("high latency rose without offline cross", WifiColor() == "#FFD9546C" && cross.Visibility == Visibility.Collapsed);
        main.ShowStatus(new(LinkState.Ready, "正在后台检测连接", "示例节点"));
        Check("unknown sample stays neutral", WifiColor() == "#FF94A3B8" && latency.Text == "待检测" && cross.Visibility == Visibility.Collapsed);
        main.ShowStatus(new(LinkState.Degraded, "节点探测未成功，仍将继续检测", "示例节点", NodeReachable: false));
        Check("failed probe shows lower left red cross", cross.Visibility == Visibility.Visible && cross.HorizontalAlignment == HorizontalAlignment.Left && cross.VerticalAlignment == VerticalAlignment.Bottom && latency.Text == "未连通");
        Render(main, output, "main-offline", 1280, 760, 1.5);
        main.ShowStatus(new(LinkState.Starting, "正在重新检测连接…", "示例节点", NodeReachable: false));
        Check("rechecking disables duplicate clicks and clears stale cross", !wifi.IsEnabled && latency.Text == "检测中…" && cross.Visibility == Visibility.Collapsed);
        main.ShowStatus(new(LinkState.Error, "OpenCode 服务退出", "示例节点", 142, NodeReachable: true));
        Check("backend failure does not mark reachable node offline", cross.Visibility == Visibility.Collapsed && latency.Text == "142 ms");
        main.ShowStatus(new(LinkState.Ready, "连接服务正常", "示例节点", 142));
        Check("recovered probe restores wifi action and color", wifi.IsEnabled && WifiColor() == "#FF3B82F6" && cross.Visibility == Visibility.Collapsed);
        var helpPanel = (ContentControl)main.FindName("HelpPanel");
        ((MenuItem)main.FindName("HelpMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("more help opens embedded page", helpPanel.Visibility == Visibility.Visible && helpPanel.Content is HelpView && ((FrameworkElement)main.FindName("PreparationPanel")).Visibility == Visibility.Collapsed);
        var help = (HelpView)helpPanel.Content;
        Render(main, output, "help", 1280, 820, 1.5);
        Render(main, output, "help-minimum", 1020, 500, 1);
        main.ShowStatus(new(LinkState.Starting, "正在检测连接…", "示例节点"));
        Check("help remains visible while status changes and is available during startup", helpPanel.Visibility == Visibility.Visible && ((Button)main.FindName("MoreButton")).IsEnabled);
        Buttons(help).Find(b => b.Tag?.ToString() == "ConnectionStatus")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        help.UpdateLayout();
        Check("help navigation scrolls to section", ((ScrollViewer)help.FindName("HelpScroll")).VerticalOffset > 0);
        ((Button)help.FindName("ReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("help return restores preparation without a chat", helpPanel.Visibility == Visibility.Collapsed && ((FrameworkElement)main.FindName("PreparationPanel")).Visibility == Visibility.Visible);
        main.ShowStatus(new(LinkState.Ready, "连接服务正常", "示例节点", 142));
        var api = new ApiKeyWindow(orchestrator);
        var action = (Button)api.FindName("ActionButton");
        var keyBox = (PasswordBox)api.FindName("KeyBox");
        Check("one action tests stored key", action.IsEnabled && action.Content.ToString() == "测试已保存的 Key");
        keyBox.Password = "sk-ant-demo-for-ui-check-only";
        Check("one action saves entered key", action.Content.ToString() == "保存新 Key 并测试");
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        Check("failed save restores action and keeps input for retry", action.IsEnabled && keyBox.IsEnabled && keyBox.Password.Length > 0);
        keyBox.Clear();
        Render(api, output, "api-key", 580, 340, 1.5);
        typeof(Orchestrator).GetProperty(nameof(Orchestrator.ApiKey))!.SetValue(orchestrator, historical);
        var previous = new ApiKeyWindow(orchestrator);
        Check("key dialog distinguishes historical result", ((TextBlock)previous.FindName("ResultText")).Text.Contains("上次测试记录") && ((TextBlock)previous.FindName("ResultText")).Text.Contains("未重新发送"));
        Render(previous, output, "api-key-history", 580, 460, 1.5);
        var close = new CloseWindow();
        Render(close, output, "close-window", 530, 225, 1.5);
        Check("close choices", Buttons((DependencyObject)close.Content).Count == 3);
        Check("background is default", Buttons((DependencyObject)close.Content).Find(b => b.IsDefault)?.Content.ToString() == "后台运行");
        foreach (var choice in new[] { "后台运行", "退出程序", "取消" })
        {
            var dialog = new CloseWindow { Opacity = 0, ShowActivated = false };
            dialog.Loaded += (_, _) =>
            {
                if (choice == "取消") dialog.Close();
                else Buttons((DependencyObject)dialog.Content).Find(b => b.Content.ToString() == choice)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            var accepted = dialog.ShowDialog() == true;
            Check("close action " + choice, choice == "取消" ? !accepted : accepted && dialog.ExitProgram == (choice == "退出程序"));
        }
        var dependencies = new DependencyManager(paths, log);
        var advanced = new AdvancedWindow(new(), paths, dependencies);
        Render(advanced, output, "advanced", 570, 445, 1.5);
        Check("optional strict checks default off", ((CheckBox)advanced.FindName("StrictBox")).IsChecked != true);
        Check("optional firewall default off", ((CheckBox)advanced.FindName("FirewallBox")).IsChecked != true);
        Check("missing firewall rules show ordinary mode", ((TextBlock)advanced.FindName("FirewallText")).Text == "未启用");
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        main.DisposeWebView();
    }
    private static List<MenuItem> Items(ContextMenu menu)
    {
        var result = new List<MenuItem>();
        foreach (var item in menu.Items) if (item is MenuItem entry) result.Add(entry);
        return result;
    }
    private static List<Button> Buttons(DependencyObject parent)
    {
        var result = new List<Button>();
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is Button button) result.Add(button);
            if (child is DependencyObject dependency) result.AddRange(Buttons(dependency));
        }
        return result;
    }
    private static void Render(Window window, string output, string name, double width, double height, double scale)
    {
        var visual = (FrameworkElement)window.Content;
        if (visual is Panel panel) panel.Background = window.Background;
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        Capture(visual, output, name, width, height, scale);
    }
    private static void Capture(FrameworkElement visual, string output, string name, double width, double height, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
    }
}
