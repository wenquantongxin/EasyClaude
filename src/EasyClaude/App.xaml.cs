using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EasyClaude.Core;
using Microsoft.Web.WebView2.Core;

namespace EasyClaude;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private AppPaths? _paths;
    private AppSettings? _settings;
    private FileLogger? _log;
    private Orchestrator? _orchestrator;
    private DependencyManager? _dependencies;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _preparation = new(1, 1);
    private int _exiting;
    private bool _webReady;
    private bool _prepared;
    private bool _importCancelled;
    private string? _lifecycleResult;
    internal Action? AfterExit;
    public static bool IsExiting { get; private set; }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = Argument(e.Args, "--root");
        _paths = root is null ? AppPaths.Default() : new AppPaths(root);
        _lifecycleResult = Argument(e.Args, "--lifecycle-check");
        if (_lifecycleResult is not null && !Path.GetFullPath(_lifecycleResult).StartsWith(_paths.TempDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("生命周期检查结果必须保存在隔离根目录的 data\\tmp 内");
        if (Argument(e.Args, "--ui-check") is { } output)
        {
            try { await UiCheck.RunAsync(_paths, output); }
            catch (Exception ex) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
            IsExiting = true; Shutdown(); return;
        }
        if (Argument(e.Args, "--firewall") is { } operation)
        {
            RunFirewallHelper(operation, Argument(e.Args, "--result"));
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            _log?.Write("app", "unhandled: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "Easy Claude", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            if (int.TryParse(Argument(e.Args, "--restart-from"), out var previous))
            {
                try
                {
                    using var old = Process.GetProcessById(previous);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await old.WaitForExitAsync(deadline.Token);
                }
                catch (ArgumentException) { }
            }
            _mutex = new Mutex(true, FirewallGuard.MutexName(_paths.Root), out _ownsMutex);
            if (!_ownsMutex)
            {
                MessageBox.Show("Easy Claude 已在运行，请从系统托盘打开窗口。", "Easy Claude");
                Shutdown();
                return;
            }
            _paths.EnsureCreated();
            var redactor = new Redactor();
            _log = new FileLogger(_paths.LogFile, redactor);
            _settings = AppSettings.LoadOrCreate(_paths.SettingsFile);
            var firewall = FirewallGuard.Read(_paths);
            _settings.FirewallEnabled = firewall.RulesPresent;
            _settings.FirewallStatus = firewall.Message;
            _settings.Save(_paths.SettingsFile);
            _orchestrator = new Orchestrator(_paths, _settings, _log, redactor);
            _window = new MainWindow(_orchestrator, _paths, _log);
            if (_lifecycleResult is not null || Array.IndexOf(e.Args, "--background") >= 0)
            { _window.Opacity = 0; _window.ShowActivated = false; _window.ShowInTaskbar = false; }
            var progress = new Progress<DependencyProgress>(p =>
            {
                if (IsExiting) return;
                _window.ShowPreparation(p);
                _tray?.Update(new(LinkState.Starting, p.Message));
            });
            _dependencies = new DependencyManager(_paths, _log, progress);
            _tray = new TrayIcon(ShowMainWindow, RecheckAsync, RestartProxyAsync, RestartProgramAsync,
                ReimportAsync, ShowAdvanced, OpenLogs, ExitAsync);
            _window.OpenLogsRequested += OpenLogs;
            _window.ImportProfileRequested += () => _ = ReimportAsync();
            _window.ApiKeyRequested += () =>
            {
                if (_orchestrator.OpenCodeClient is not null) new ApiKeyWindow(_orchestrator) { Owner = _window }.ShowDialog();
            };
            _window.AdvancedRequested += ShowAdvanced;
            _window.RestartRequested += () => _ = RestartProgramAsync();
            _window.ExitRequested += () => _ = ExitAsync();
            _window.RecheckRequested += () => _ = RecheckAsync();
            _orchestrator.ApiKeyChanged += state => Dispatcher.InvokeAsync(() => { if (!IsExiting) _window.ShowApiKey(state); });
            _orchestrator.StatusChanged += status => Dispatcher.InvokeAsync(() => { if (!IsExiting) { _window.ShowStatus(status); _tray.Update(status); } });
            _orchestrator.OpenCodeReady += (uri, auth) => Dispatcher.InvokeAsync(() => { if (!IsExiting) _window.NavigateToOpenCode(uri, auth); });
            _orchestrator.ConflictChanged += (conflict, overridden) => Dispatcher.InvokeAsync(() => { if (!IsExiting) _window.ShowConflict(conflict, overridden); });
            _window.Show();
            if (!new ProfileStore(_paths.ProfileFile).Exists && !await ImportProfileAsync())
            {
                if (_importCancelled) await ExitAsync();
                return;
            }
            await PrepareAndStartAsync();
            if (_lifecycleResult is not null)
            {
                await LifecycleCheck.RunAsync(this, _orchestrator, _window, _paths, _lifecycleResult, Array.IndexOf(e.Args, "--check-restarted") >= 0);
                return;
            }
            if (Array.IndexOf(e.Args, "--background") >= 0)
            { _window.Hide(); _window.Opacity = 1; _window.ShowActivated = true; _window.ShowInTaskbar = true; }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log?.Write("app", "startup failed: " + ex);
            if (_lifecycleResult is not null)
            {
                Directory.CreateDirectory(_paths!.TempDir);
                File.WriteAllText(_lifecycleResult, JsonSerializer.Serialize(new { complete = true, ok = false, error = ex.Message }));
                await ExitAsync(); return;
            }
            if (_window is not null) _window.ShowPreparationError("准备失败：" + ex.Message);
            else { MessageBox.Show(ex.Message, "Easy Claude"); await ExitAsync(); }
        }
    }

    private async Task PrepareAndStartAsync()
    {
        if (_paths is null || _settings is null || _dependencies is null || _window is null || _orchestrator is null) return;
        if (!await _preparation.WaitAsync(0)) return;
        try
        {
            _window.ShowPreparation(new("正在检查运行组件…"));
            await _dependencies.EnsureAsync(new ProfileStore(_paths.ProfileFile).LoadText(), _settings, _lifetime.Token);
            await EnsureWebViewRuntimeAsync();
            if (!_webReady)
            {
                await _window.InitializeWebViewAsync();
                _webReady = true;
            }
            _prepared = true;
            if (!await _orchestrator.StartAsync(_lifetime.Token))
                _window.ShowPreparationError(_orchestrator.Status.Message);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log?.Write("prepare", ex.Message);
            _window.ShowPreparationError("准备失败：" + ex.Message + "\n可检查网络后重试，或重新导入节点配置。");
            _tray?.Update(new(LinkState.Error, "运行组件准备未完成"));
        }
        finally { _preparation.Release(); }
    }

    private static bool HasWebViewRuntime()
    {
        try { return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }

    private async Task EnsureWebViewRuntimeAsync()
    {
        if (HasWebViewRuntime()) return;
        var installer = Path.Combine(_paths!.TempDir, "webview2-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await _dependencies!.DownloadWebViewInstallerAsync(new ProfileStore(_paths.ProfileFile).LoadText(), _settings!, installer, _lifetime.Token);
            _window!.ShowPreparation(new("正在验证并安装 WebView2 运行时…"));
            WindowsSignature.VerifyMicrosoft(installer);
            var (code, _) = await ProcessRunner.RunAsync(installer, ["/silent", "/install"], _paths.TempDir,
                ChildEnvironment.ScrubOnly, TimeSpan.FromMinutes(3), _lifetime.Token);
            if (code is not (0 or 3010) || !HasWebViewRuntime()) throw new InvalidOperationException($"WebView2 安装未完成（代码 {code}），请重试。");
        }
        finally { if (File.Exists(installer)) File.Delete(installer); }
    }

    private async Task<bool> ImportProfileAsync()
    {
        if (_dependencies is null || _paths is null || _settings is null) return false;
        _importCancelled = false;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 mihomo / Clash 节点配置",
            Filter = "YAML 配置 (*.yml;*.yaml)|*.yml;*.yaml",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(_window) != true) { _importCancelled = true; return false; }
        try
        {
            var store = new ProfileStore(_paths.ProfileFile);
            var summary = await store.ImportAsync(dialog.FileName,
                (yaml, ct) => _dependencies.ValidateProfileAsync(yaml, _settings, ct), _lifetime.Token);
            _log?.Write("app", "profile imported: " + summary);
            MessageBox.Show(_window, $"配置已验证并保存：{summary}。\n保存位置：{_paths.ProfilesDir}\n原文件移动或删除后，程序仍可使用此配置。", "Easy Claude");
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            _window?.ShowPreparationError("导入未完成：" + ex.Message);
            MessageBox.Show(_window, "导入失败，原配置已保留。\n" + ex.Message, "Easy Claude", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task ReimportAsync()
    {
        if (_lifetime.IsCancellationRequested || !await _preparation.WaitAsync(0)) return;
        var imported = false;
        try { imported = await ImportProfileAsync(); }
        finally { _preparation.Release(); }
        if (!imported) return;
        if (_prepared && _orchestrator is not null) await _orchestrator.ReloadServicesAsync(_lifetime.Token);
        else await PrepareAndStartAsync();
    }
    private async Task RecheckAsync()
    {
        if (!_prepared) await PrepareAndStartAsync();
        else if (_orchestrator is not null) await _orchestrator.RecheckAsync(_lifetime.Token);
    }
    private async Task RestartProxyAsync()
    {
        if (!_prepared) await PrepareAndStartAsync();
        else if (_orchestrator is not null) await _orchestrator.RestartProxyAsync(_lifetime.Token);
    }

    internal async Task RestartProgramAsync()
    {
        if (IsExiting) return;
        var hidden = _window?.IsVisible != true;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = _paths!.Root };
        start.ArgumentList.Add("--restart-from"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--root"); start.ArgumentList.Add(_paths.Root);
        if (hidden) start.ArgumentList.Add("--background");
        if (_lifecycleResult is not null)
        {
            start.ArgumentList.Add("--lifecycle-check"); start.ArgumentList.Add(_lifecycleResult);
            start.ArgumentList.Add("--check-restarted");
        }
        Process.Start(start)?.Dispose();
        await ExitAsync();
    }

    private void ShowAdvanced()
    {
        if (_window is not null && _settings is not null && _paths is not null && _dependencies is not null)
            new AdvancedWindow(_settings, _paths, _dependencies) { Owner = _window }.ShowDialog();
    }
    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Opacity = 1; _window.ShowActivated = true; _window.ShowInTaskbar = true;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
    private void OpenLogs()
    {
        if (_paths is null) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(_paths.LogsDir); Process.Start(start)?.Dispose();
    }

    public async Task<string> UpdateFirewallAsync(bool enable)
    {
        var result = Path.Combine(_paths!.TempDir, "firewall-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = _paths.Root };
            foreach (var arg in new[] { "--firewall", enable ? "enable" : "disable", "--root", _paths.Root, "--result", result })
                start.ArgumentList.Add(arg);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动防火墙设置助手");
            await helper.WaitForExitAsync();
            if (!File.Exists(result)) return "管理员操作未完成，当前模式保持不变。";
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result));
            var message = doc.RootElement.GetProperty("message").GetString()!;
            var state = FirewallGuard.Read(_paths);
            _settings!.FirewallEnabled = state.RulesPresent;
            _settings.FirewallStatus = state.Message; _settings.Save(_paths.SettingsFile);
            return message;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "已取消管理员许可，当前防火墙设置保持不变。"; }
        finally { if (File.Exists(result)) File.Delete(result); }
    }
    private void RunFirewallHelper(string operation, string? result)
    {
        if (operation is not ("enable" or "disable")) throw new InvalidOperationException("无效的防火墙操作");
        if (result is null || !Path.GetFullPath(result).StartsWith(_paths!.TempDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("防火墙助手的结果路径无效");
        string message;
        try { FirewallGuard.Apply(_paths!, operation == "enable"); message = operation == "enable" ? FirewallGuard.Read(_paths!).Message : "防火墙加固已移除"; }
        catch (Exception ex) { message = "防火墙操作未完成：" + ex.Message; }
        File.WriteAllText(result, JsonSerializer.Serialize(new { message }));
    }

    public async Task ExitAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        IsExiting = true;
        await _lifetime.CancelAsync();
        try { _tray?.Dispose(); } catch (Exception ex) { _log?.Write("app", ex.Message); }
        try
        {
            await _preparation.WaitAsync();
            if (_orchestrator is not null) await _orchestrator.DisposeAsync();
            _window?.DisposeWebView();
        }
        catch (Exception ex) { _log?.Write("app", "shutdown: " + ex.Message); }
        finally { _preparation.Release(); }
        _log?.Write("app", "exit"); _log?.Dispose();
        if (_ownsMutex) try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        AfterExit?.Invoke();
        Shutdown();
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true; _lifetime.Cancel();
        base.OnSessionEnding(e);
    }
}
