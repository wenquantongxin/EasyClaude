using System.Diagnostics;
using System.Net;

namespace EasyClaude.Core;

public enum LinkState
{
    Stopped,
    Starting,
    Paused,
    Ready,
    Degraded,
    Error,
}

public sealed record LinkStatus(
    LinkState State, string Message, string? Node = null, int? DelayMs = null, string? Country = null, string? Colo = null,
    string? EgressIp = null, string? Location = null, string? Org = null,
    EgressConsistency Consistency = EgressConsistency.Unknown, string? PreviousEgress = null, bool? NodeReachable = null)
{
    /// <summary>"节点 X · 延迟 N ms".</summary>
    public string NodeText => string.Join(" · ", new[]
    {
        Node is null ? null : "节点 " + Node,
        DelayMs is null ? null : $"延迟 {DelayMs} ms",
    }.Where(x => x is not null));

    /// <summary>"出口 IP a.b.c.d · 美国 California Los Angeles · AS… operator".</summary>
    public string EgressText => EgressIp is null
        ? string.Empty
        : string.Join(" · ", new[] { "出口 IP " + EgressIp, Location ?? GeoText.Country(Country), Org }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>Compact line for logs (the logger redacts IP addresses).</summary>
    public string Detail => string.Join(" · ", new[]
    {
        NodeText,
        Country is null ? null : "出口 " + Country + (Colo is null ? string.Empty : "/" + Colo),
        Consistency == EgressConsistency.Unknown ? null : "一致性 " + Consistency,
    }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>
/// UI-agnostic pipeline: orphan cleanup → decrypt profile → hardened config → route (TUN) check → mihomo → self-check with
/// egress consistency → isolated opencode serve → pairing, plus a watchdog. Fail-closed by construction.
/// </summary>
public sealed class Orchestrator : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly FileLogger _log;
    private readonly Redactor _redactor;
    private readonly EgressHistory _egressHistory;
    private readonly ApiKeyVerificationStore _keyVerification;
    private string? _keyIdentity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JobObject? _job;
    private MihomoHost? _mihomo;
    private OpenCodeHost? _openCode;
    private RuntimeSecrets? _secrets;
    private BuildResult? _build;
    private SelfCheckResult? _lastCheck;
    private EgressComparison? _egress;
    private RouteConflict? _conflict;
    private bool _conflictOverridden;
    private CancellationTokenSource? _watchCts;
    private volatile bool _stopping;
    private Task? _watchTask;
    private readonly TimeProvider _time;
    private int _delayFailures;
    private bool _diagnosticsComplete;

    public Orchestrator(AppPaths paths, AppSettings settings, FileLogger log, Redactor redactor, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _paths = paths;
        _settings = settings;
        _log = log;
        _redactor = redactor;
        _egressHistory = new EgressHistory(paths.EgressFile);
        _keyVerification = new(paths.ApiKeyTestFile);
    }

    public event Action<LinkStatus>? StatusChanged;

    /// <summary>Raised with the one-time pairing URL and the Basic auth header for the local origin.</summary>
    public event Action<Uri, string>? OpenCodeReady;

    /// <summary>Raised with the current route conflict (null = none) and whether the user chose to continue anyway.</summary>
    public event Action<RouteConflict?, bool>? ConflictChanged;

    /// <summary>Raised when the stored key (masked hint) or the last "你好" test changes.</summary>
    public event Action<ApiKeyState>? ApiKeyChanged;

    public LinkStatus Status { get; private set; } = new(LinkState.Stopped, "未启动");

    public ApiKeyState ApiKey { get; private set; } = ApiKeyState.Unknown;

    public RouteConflict? Conflict => _conflict;

    public OpenCodeClient? OpenCodeClient => _openCode?.Client;

    public int? MihomoProcessId => _mihomo?.ProcessId;

    public IReadOnlyList<int> ChildProcessIds =>
        new[] { _mihomo?.ProcessId, _openCode?.ProcessId }.Where(p => p is not null).Select(p => p!.Value).ToList();

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await StartCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>"重新检测" / "我已关闭，重新检测": re-run every check and continue a paused startup.</summary>
    public async Task<bool> RecheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_build is null && !await StartCoreAsync(ct).ConfigureAwait(false)) return false;
            return await ResumeAsync(ct, awaitDiagnostics: true).ConfigureAwait(false);
        }
        catch (Exception ex) { return Fail("重新检测失败", ex); }
        finally { _gate.Release(); }
    }

    /// <summary>"仍然继续": accept the TUN conflict for this session (the link stays fail-closed and TLS-protected).</summary>
    public async Task<bool> ContinueDespiteConflictAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _conflictOverridden = true;
            _log.Write("core", "user chose to continue despite the route conflict");
            ConflictChanged?.Invoke(_conflict, true);
            return _build is null ? await StartCoreAsync(ct).ConfigureAwait(false) : await ResumeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return Fail("继续启动失败", ex); }
        finally { _gate.Release(); }
    }

    /// <summary>Restart only mihomo with the same port, credentials and pipe, so the running OpenCode keeps working.</summary>
    public async Task<bool> RestartProxyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_build is null || _secrets is null || _job is null) return await StartCoreAsync(ct).ConfigureAwait(false);
            await StopWatchdogAsync().ConfigureAwait(false);
            await StopMihomoAsync().ConfigureAwait(false);
            return await ResumeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return Fail("重启代理失败", ex); }
        finally { _gate.Release(); }
    }

    public async Task<bool> ReloadServicesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync("正在重启…").ConfigureAwait(false);
            return await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync("已停止").ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<bool> StartCoreAsync(CancellationToken ct)
    {
        if (_job is not null) await StopCoreAsync("正在重新连接…").ConfigureAwait(false);
        _stopping = false;
        _diagnosticsComplete = false;
        _delayFailures = 0;
        try
        {
            Set(new(LinkState.Starting, "正在准备连接服务…"));
            CleanupOrphans();
            var yaml = new ProfileStore(_paths.ProfileFile).LoadText();
            _secrets = new RuntimeSecrets(PortUtil.GetFreeTcpPort(), "ec", SecretGen.Hex(16), "easyclaude-mihomo-" + SecretGen.Hex(6));
            _redactor.AddSecret(_secrets.ProxyPassword);
            _build = RuntimeConfigBuilder.Build(yaml, _secrets,
                new BuildOptions(_settings.EffectiveLlmDomains(), _settings.BlockedCidrs, _settings.MihomoLogLevel));
            foreach (var server in _build.NodeServers)
                if (!IPAddress.TryParse(server, out _)) _redactor.AddSecret(server); // node domains never reach logs; IPs are redacted anyway
            _log.Write("core", "runtime config built: " + _build);
            _job = new JobObject();
            return await ResumeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopCoreAsync("启动未完成").ConfigureAwait(false);
            return Fail("启动失败", ex);
        }
    }

    /// <summary>Continue from wherever the pipeline stands: route check → mihomo → self-check → OpenCode → watchdog.</summary>

    private async Task<bool> ResumeAsync(CancellationToken ct, bool awaitDiagnostics = false)
    {
        await StopWatchdogAsync().ConfigureAwait(false);
        if (_settings.StrictStartupChecks || awaitDiagnostics)
        {
            Set(Status with { State = LinkState.Starting, Message = _settings.StrictStartupChecks ? "正在执行严格启动检查…" : "正在重新检测连接…" });
            SetConflict(await RouteInspector.CheckAsync(_build!.NodeServers, ct).ConfigureAwait(false));
            if (_settings.StrictStartupChecks && _conflict is not null && !_conflictOverridden && _openCode is not { IsRunning: true })
            {
                Set(new(LinkState.Paused, "严格检查已暂停启动：节点流量被其他 TUN 接管"));
                return false;
            }
        }
        if (_mihomo is not { IsRunning: true })
        {
            await StopMihomoAsync().ConfigureAwait(false);
            await StartMihomoCoreAsync(ct).ConfigureAwait(false);
        }
        if (_settings.StrictStartupChecks || awaitDiagnostics)
        {
            var check = await RunSelfCheckAsync(ct).ConfigureAwait(false);
            if (!check.Ok)
            {
                // A manual failed probe must not strand a running client without periodic recovery.
                if (_openCode is { IsRunning: true } && _mihomo is { IsRunning: true })
                {
                    PublishReady();
                    StartWatchdog(diagnose: false);
                }
                return false;
            }
        }
        else
        {
            var node = await _mihomo!.Api!.TryGetGroupNowAsync(_build!.LlmGroup, ct).ConfigureAwait(false);
            _lastCheck ??= new(true, "正在后台检查连接", node, null, null, null, null);
        }
        if (_openCode is not { IsRunning: true }) await StartOpenCodeCoreAsync(ct).ConfigureAwait(false);
        else PublishReady();
        StartWatchdog(diagnose: !awaitDiagnostics);
        return true;
    }

    private void CleanupOrphans()
    {
        foreach (var image in new[] { _paths.PrivateMihomo, _paths.PrivateOpenCode })
        {
            var killed = OrphanCleaner.KillByImagePath(image);
            if (killed > 0) _log.Write("core", $"cleaned up {killed} orphaned process(es) of {Path.GetFileName(image)}");
        }
    }

    private async Task StartMihomoCoreAsync(CancellationToken ct)
    {
        Set(new(LinkState.Starting, "正在启动 mihomo…"));
        var host = new MihomoHost(ToolLocator.FindMihomo(_settings, _paths), _paths, _log, _job!);
        try
        {
            await host.WriteConfigAndTestAsync(_build!.Yaml, ct).ConfigureAwait(false);
            await host.StartAsync(_secrets!.PipeName, _build.LlmGroup, ct).ConfigureAwait(false);
        }
        catch
        {
            await host.StopAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            host.DeleteConfigFile(); // plaintext credentials exist on disk only during validation and startup
        }
        host.Exited += OnMihomoExited;
        _mihomo = host;
        _log.Write("core", $"mihomo {host.Version} running (pid {host.ProcessId}), mixed port 127.0.0.1:{_secrets!.MixedPort}");
    }

    private async Task StopMihomoAsync()
    {
        if (_mihomo is null) return;
        _mihomo.Exited -= OnMihomoExited;
        await _mihomo.StopAsync().ConfigureAwait(false);
        _mihomo = null;
    }

    private async Task<SelfCheckResult> RunSelfCheckAsync(CancellationToken ct, bool showProgress = true)
    {
        if (showProgress) Set(Status with { State = LinkState.Starting, Message = "正在检测节点、出口与连接…" });
        var result = await SelfCheck.RunAsync(_mihomo!.Api!, _build!.LlmGroup, _secrets!, _settings, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _lastCheck = result;
        _diagnosticsComplete = true;
        _delayFailures = result.DelayMs is null ? 1 : 0;
        _log.Write("core", "self-check: " + result);
        if (!result.Ok)
        {
            Set(new(LinkState.Error, result.Message, result.Node, result.DelayMs, result.Country, result.Colo,
                result.EgressIp, result.Location, result.Org, NodeReachable: result.DelayMs is not null));
            return result;
        }
        if (result.EgressIp is not null)
        {
            _egress = EgressHistory.Compare(_egressHistory.Load(), result.EgressIp);
            _egressHistory.Save(new EgressRecord(result.Node ?? "?", result.EgressIp, result.Country, result.Location, DateTimeOffset.Now));
            _log.Write("core", "egress consistency with last run: " + _egress.Consistency);
        }
        return result;
    }

    private async Task StartOpenCodeCoreAsync(CancellationToken ct)
    {
        var check = _lastCheck;
        Set(new(LinkState.Starting, "正在启动 OpenCode…", check?.Node, check?.DelayMs, check?.Country, check?.Colo,
            check?.EgressIp, check?.Location, check?.Org, _egress?.Consistency ?? EgressConsistency.Unknown, _egress?.Previous));
        if (_openCode is not null)
        {
            _openCode.Exited -= OnOpenCodeExited;
            await _openCode.StopAsync().ConfigureAwait(false);
            _openCode = null;
        }
        OpenCodeConfigWriter.Write(_paths.OpenCodeConfigFile, _settings);
        var host = new OpenCodeHost(ToolLocator.FindOpenCode(_settings, _paths), _paths, _log, _job!);
        try
        {
            await host.StartAsync(_secrets!, _settings.ResolveWorkDirectory(), _redactor, ct).ConfigureAwait(false);
        }
        catch
        {
            await host.StopAsync().ConfigureAwait(false);
            throw;
        }
        host.Exited += OnOpenCodeExited;
        _openCode = host;
        _log.Write("core", $"opencode serve running (pid {host.ProcessId}) at {host.BaseUri}");
        var code = await host.Client!.CreatePairingCodeAsync(ct).ConfigureAwait(false);
        _redactor.AddSecret(code);
        PublishReady();
        OpenCodeReady?.Invoke(host.Client.GetConnectUri(code), host.Client.BasicAuthHeaderValue);
        await RefreshApiKeyStateAsync(host.Client, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validate with Anthropic (free endpoint, through the LLM path) → store in the isolated OpenCode, replacing older keys →
    /// "你好" test through OpenCode. A key Anthropic rejects is never stored. The key is registered with the redactor first.
    /// </summary>
    public async Task<ApiKeyTest> ApplyApiKeyAsync(string key, CancellationToken ct = default)
    {
        key = key.Trim();
        _redactor.AddSecret(key);
        if (!ApiKeyMask.LooksValid(key))
            return new(false, "Key 格式不对：应为一串不含空格的字符（官方 Key 通常以 sk-ant-api03- 开头）。未保存。");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var saved = false;
        try
        {
            if (_openCode?.Client is not { } client || _secrets is null || _mihomo is not { IsRunning: true })
                return new(false, "连接服务尚未就绪，请稍后再试。未保存。");
            int status;
            using (var http = ProxyProbe.CreateClient(_secrets))
            {
                try { status = await AnthropicProbe.ValidateKeyAsync(http, key, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    return new(false, "无法经代理连接 Anthropic：" + _redactor.Redact(ex.Message) + "。未保存。");
                }
            }
            _log.Write("core", $"api key validation with Anthropic: HTTP {status}");
            if (status is 401 or 403) return new(false, $"Anthropic 拒绝了这个 Key（HTTP {status}）。未保存。");
            if (status != 200) return new(false, $"验证 Key 时 Anthropic 返回 HTTP {status}。未保存。");
            var warning = await client.ReplaceAnthropicKeyAsync(key, ct, () =>
            {
                saved = true;
                _keyIdentity = null;
                try { _keyVerification.Clear(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("core", "clear key test metadata: " + ex.Message); }
                ApiKey = new(true, ApiKeyMask.Hint(key), null);
                ApiKeyChanged?.Invoke(ApiKey);
            }).ConfigureAwait(false);
            _log.Write("core", "api key applied to the isolated OpenCode (older anthropic credentials removed)");
            var test = await TestApiKeyCoreAsync(client, ct, saved: true).ConfigureAwait(false);
            return warning is null ? test : test with { Message = test.Message + "；" + warning };
        }
        catch (OperationCanceledException)
        {
            return new(false, saved ? "Key 已保存，模型测试已取消" : "操作已取消，未保存", Saved: saved);
        }
        catch (Exception ex)
        {
            _log.Write("core", "apply api key failed: " + ex.Message);
            return new(false, (saved ? "Key 已保存，后续操作失败：" : "应用 Key 失败：") + _redactor.Redact(ex.Message), Saved: saved);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sends "你好" to the newest haiku model through OpenCode with the stored key.</summary>
    public async Task<ApiKeyTest> TestApiKeyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_openCode?.Client is not { } client) return new(false, "OpenCode 尚未就绪，请稍后再试。");
            return await TestApiKeyCoreAsync(client, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }


    private async Task<ApiKeyTest> TestApiKeyCoreAsync(OpenCodeClient client, CancellationToken ct, bool saved = false)
    {
        ApiKeyTest test;
        var remember = true;
        try
        {
            if (!await RefreshApiKeyStateAsync(client, ct).ConfigureAwait(false))
                return new(false, "无法读取已保存的 Key，请稍后重试。", Saved: saved);
            if (!ApiKey.Configured) return new(false, "还没有保存 Key。", Saved: saved);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            test = await ApiKeyTester.RunAsync(client, _settings.Model, saved, deadline.Token, _time).ConfigureAwait(false);
            test = test with { Message = _redactor.Redact(test.Message) };
        }
        catch (OperationCanceledException)
        {
            remember = !ct.IsCancellationRequested;
            test = new(false, ct.IsCancellationRequested ? "模型测试已取消" : "模型测试超时，请稍后重试", Saved: saved);
        }
        catch (Exception ex)
        {
            test = new(false, "模型测试失败：" + _redactor.Redact(ex.Message), Saved: saved);
        }
        _log.Write("core", $"api key test: ok={test.Ok} model={test.Model} latency={test.LatencyMs} ms");
        if (remember)
        {
            var testedAt = _time.GetUtcNow();
            if (_keyIdentity is { } identity)
                try { _keyVerification.Save(identity, test, testedAt); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Write("core", "save key test metadata: " + ex.Message);
                    test = test with { Message = test.Message + "；本地测试记录未能保存，重启后需重新测试" };
                }
            ApiKey = ApiKey with { LastTest = test, TestedAt = testedAt, IsHistorical = false };
        }
        ApiKeyChanged?.Invoke(ApiKey);
        return test;
    }

    private async Task<bool> RefreshApiKeyStateAsync(OpenCodeClient client, CancellationToken ct)
    {
        try
        {
            var credentials = await client.ListKeyCredentialsAsync("anthropic", ct).ConfigureAwait(false);
            var current = credentials.FirstOrDefault(c => c.Active);
            current ??= credentials.FirstOrDefault();
            if (current is null || current.Identity != _keyIdentity)
            {
                _keyIdentity = current?.Identity;
                var remembered = _keyVerification.Load(_keyIdentity);
                ApiKey = new(current is not null, current?.Hint, remembered?.Test, remembered?.TestedAt, remembered is not null);
            }
            else ApiKey = ApiKey with { Configured = true, Hint = current.Hint };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.Write("core", "api key state refresh failed: " + ex.Message);
            return false;
        }
        ApiKeyChanged?.Invoke(ApiKey);
        return true;
    }

    private static string Shorten(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Green only when latency is fine, the egress matches the last run and no TUN captures the node route.</summary>
    private void PublishReady()
    {
        var check = _lastCheck;
        if (check is null) return;
        var reasons = new List<string>();
        if (check.DelayMs is { } d && d > _settings.MaxNodeDelayMs) reasons.Add("节点延迟偏高");
        if (_egress?.Consistency == EgressConsistency.Changed) reasons.Add("出口 IP 与上次不同");
        if (_conflict is not null) reasons.Add("其他客户端的 TUN 仍在接管节点流量");
        if (_delayFailures >= 2) reasons.Add("连续两次测速未成功，将继续每 60 秒检测");
        if (_diagnosticsComplete && check.EgressIp is null) reasons.Add("出口信息暂不可用");
        if (_diagnosticsComplete && check.Country is not null && !_settings.AllowedEgressCountries.Contains(check.Country, StringComparer.OrdinalIgnoreCase)) reasons.Add("出口地域与偏好不同");
        if (_diagnosticsComplete && check.AnthropicStatus is not (200 or 401)) reasons.Add("Anthropic 连通性尚未确认");
        var message = reasons.Count == 0 ? (_diagnosticsComplete ? "连接服务正常" : "服务就绪，正在后台检测连接") : "服务运行中（" + string.Join("；", reasons) + "）";
        Set(new(reasons.Count == 0 ? LinkState.Ready : LinkState.Degraded, message, check.Node, check.DelayMs, check.Country,
            check.Colo, check.EgressIp, check.Location, check.Org, _egress?.Consistency ?? EgressConsistency.Unknown, _egress?.Previous,
            NodeReachable: check.DelayMs is not null ? true : _diagnosticsComplete || _delayFailures > 0 ? false : null));
    }

    private void SetConflict(RouteConflict? conflict)
    {
        var changed = (conflict is null) != (_conflict is null) || conflict?.AdapterName != _conflict?.AdapterName;
        _conflict = conflict;
        if (conflict is null) _conflictOverridden = false; // a new conflict later in the session asks again
        if (changed) ConflictChanged?.Invoke(conflict, _conflictOverridden);
    }

    private async Task StopCoreAsync(string message)
    {
        _stopping = true;
        await StopWatchdogAsync().ConfigureAwait(false);
        if (_openCode is not null)
        {
            _openCode.Exited -= OnOpenCodeExited;
            await _openCode.StopAsync().ConfigureAwait(false);
            _openCode = null;
        }
        await StopMihomoAsync().ConfigureAwait(false);
        _job?.Dispose(); // kills anything still alive in the job
        _job = null;
        try
        {
            if (File.Exists(_paths.MihomoConfig)) File.Delete(_paths.MihomoConfig);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _secrets = null;
        _build = null;
        _lastCheck = null;
        _egress = null;
        SetConflict(null);
        Set(new(LinkState.Stopped, message));
    }

    private void OnMihomoExited(int code)
    {
        if (_stopping) return;
        _log.Write("core", $"mihomo exited unexpectedly (code {code})");
        Set(Status with { State = LinkState.Error, Message = "代理进程已退出，请点“重启代理”恢复连接。", DelayMs = null, NodeReachable = false });
    }

    private void OnOpenCodeExited(int code)
    {
        if (_stopping) return;
        _log.Write("core", $"opencode exited unexpectedly (code {code})");
        Set(Status with { State = LinkState.Error, Message = "OpenCode 服务已退出，请点“重启程序”。" });
    }


    private void StartWatchdog(bool diagnose = true)
    {
        var cts = _watchCts = new CancellationTokenSource();
        _watchTask = Task.WhenAll(WatchHealthAsync(cts.Token), WatchLatencyAsync(cts.Token), diagnose ? DiagnoseAsync(cts.Token) : Task.CompletedTask);
    }

    private async Task StopWatchdogAsync()
    {
        var cts = Interlocked.Exchange(ref _watchCts, null);
        var task = Interlocked.Exchange(ref _watchTask, null);
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        try { if (task is not null) await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("watch", ex.Message); }
        finally { cts.Dispose(); }
    }

    private async Task DiagnoseAsync(CancellationToken ct)
    {
        try
        {
            if (!_settings.StrictStartupChecks)
            {
                SetConflict(await RouteInspector.CheckAsync(_build!.NodeServers, ct).ConfigureAwait(false));
                await RunSelfCheckAsync(ct, showProgress: false).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && _openCode is { IsRunning: true } && _mihomo is { IsRunning: true }) PublishReady();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("diagnostics", ex.Message); }
    }

    private async Task WatchHealthAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), _time);
        var failures = 0;
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_stopping || _mihomo?.Api is not { } api) continue;
                if (!_mihomo.IsRunning) { OnMihomoExited(-1); continue; }
                if (_openCode is not { IsRunning: true }) { OnOpenCodeExited(-1); continue; }
                if (await api.TryGetVersionAsync(ct).ConfigureAwait(false) is null)
                {
                    if (++failures >= 2) Set(Status with { State = LinkState.Error, Message = "代理控制接口无响应，请重启代理" });
                    continue;
                }
                if (failures >= 2) PublishReady();
                failures = 0;
                if (++tick % 12 == 0)
                {
                    SetConflict(await RouteInspector.CheckAsync(_build!.NodeServers, ct).ConfigureAwait(false));
                    if (Status.State is LinkState.Ready or LinkState.Degraded) PublishReady();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write("watch", ex.Message); }
    }

    private Task WatchLatencyAsync(CancellationToken ct) => LatencyMonitor.RunAsync(async token =>
    {
        var api = _mihomo?.Api;
        if (api is null || _mihomo is not { IsRunning: true }) return (_lastCheck?.Node, (int?)null);
        var node = await api.TryGetGroupNowAsync(_build!.LlmGroup, token).ConfigureAwait(false) ?? _lastCheck?.Node;
        var delay = node is null ? null : await api.GetDelayAsync(node, SelfCheck.DelayUrl, 8000, token).ConfigureAwait(false);
        return (node, delay);
    }, sample =>
    {
        if (_stopping || _lastCheck is null || _openCode is not { IsRunning: true } || _mihomo is not { IsRunning: true }) return;
        _delayFailures = sample.DelayMs is null ? _delayFailures + 1 : 0;
        _lastCheck = _lastCheck with { Node = sample.Node ?? _lastCheck.Node, DelayMs = sample.DelayMs };
        if (Status.State is LinkState.Ready or LinkState.Degraded) PublishReady();
    }, _time, ct);

    private bool Fail(string what, Exception ex)
    {
        _log.Write("core", $"{what}: {ex}");
        Set(new(LinkState.Error, $"{what}：{ex.Message}"));
        return false;
    }

    private void Set(LinkStatus status)
    {
        status = status with { Message = _redactor.Redact(status.Message) };
        Status = status;
        _log.Write("status", $"{status.State}: {status.Message} {status.Detail}");
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { _log.Write("core", "status handler error: " + ex.Message); }
    }
}
