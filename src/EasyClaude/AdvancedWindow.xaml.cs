using System;
using System.Threading;
using System.Windows;
using EasyClaude.Core;

namespace EasyClaude;
public partial class AdvancedWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly DependencyManager _dependencies;
    private readonly CancellationTokenSource _cts = new();
    private bool _busy;
    public AdvancedWindow(AppSettings settings, AppPaths paths, DependencyManager dependencies)
    {
        _settings = settings; _paths = paths; _dependencies = dependencies;
        InitializeComponent();
        StrictBox.IsChecked = settings.StrictStartupChecks;
        var state = FirewallGuard.Read(paths);
        FirewallBox.IsChecked = state.RulesPresent;
        RepairButton.IsEnabled = state.RulesPresent;
        FirewallText.Text = state.Message;
        Closing += (_, _) => _cts.Cancel();
    }
    private void Busy(bool value)
    {
        _busy = value;
        SaveButton.IsEnabled = VerifyButton.IsEnabled = !value;
        RepairButton.IsEnabled = !value && _settings.FirewallEnabled;
        StrictBox.IsEnabled = FirewallBox.IsEnabled = !value;
    }
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        Busy(true); ResultText.Text = "正在校验组件…";
        try { ResultText.Text = await _dependencies.VerifyAsync(_cts.Token); }
        catch (OperationCanceledException) { ResultText.Text = "校验已取消"; }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { Busy(false); }
    }
    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        try { ResultText.Text = await ((App)Application.Current).UpdateFirewallAsync(true); RefreshFirewall(); }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { Busy(false); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Busy(true);
        try
        {
            _settings.StrictStartupChecks = StrictBox.IsChecked == true;
            var requested = FirewallBox.IsChecked == true;
            var actual = FirewallGuard.Read(_paths);
            if (requested != actual.RulesPresent || (requested && !actual.RulesEnabled))
                ResultText.Text = await ((App)Application.Current).UpdateFirewallAsync(requested);
            else ResultText.Text = "设置已保存，严格启动检查将在下次重新连接或重启时应用。";
            RefreshFirewall();
            _settings.Save(_paths.SettingsFile);
        }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { Busy(false); }
    }
    private void RefreshFirewall()
    {
        var state = FirewallGuard.Read(_paths);
        FirewallBox.IsChecked = _settings.FirewallEnabled = state.RulesPresent;
        FirewallText.Text = _settings.FirewallStatus = state.Message;
    }
}
