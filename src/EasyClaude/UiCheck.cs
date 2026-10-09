using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        main.ShowApiKey(orchestrator.ApiKey);
        main.ShowStatus(new(LinkState.Ready, "连接服务正常", "示例节点", 142, "US", "LAX", "198.51.100.1", "美国 · Los Angeles", "示例网络"));
        Check("restart wording", ((Button)main.FindName("RestartAllButton")).Content.ToString() == "重启程序");
        Check("application icon", main.Icon is not null);
        foreach (var scale in new[] { 1.0, 1.5, 2.0 }) Render(main, output, "main-" + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), 1280, 760, scale);
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
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
    }
}
