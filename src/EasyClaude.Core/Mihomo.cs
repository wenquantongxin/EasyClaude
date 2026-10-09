using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace EasyClaude.Core;

/// <summary>REST client for the mihomo controller over its Windows named pipe (no TCP control port is opened).</summary>
public sealed class MihomoApi : IDisposable
{
    private readonly HttpClient _http;

    public MihomoApi(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(3000, ct).ConfigureAwait(false);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://mihomo.pipe/"), Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<string?> TryGetVersionAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var doc = await GetJsonAsync("version", deadline.Token).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<string?> GetGroupNowAsync(string group, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("proxies/" + Uri.EscapeDataString(group), ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("now", out var now) ? now.GetString() : null;
    }

    public async Task<string?> TryGetGroupNowAsync(string group, CancellationToken ct)
    {
        try
        {
            return await GetGroupNowAsync(group, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<int?> GetDelayAsync(string proxy, string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var path = $"proxies/{Uri.EscapeDataString(proxy)}/delay?timeout={timeoutMs}&url={Uri.EscapeDataString(url)}";
            using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("delay", out var d) && d.TryGetInt32(out var ms) ? ms : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Runs mihomo with the hardened runtime config inside the Easy Claude job object.</summary>
public sealed class MihomoHost
{
    private static readonly string[] GeoFiles = ["Country.mmdb", "geoip.dat", "geosite.dat", "ASN.mmdb"];
    private readonly string _exe;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly JobObject _job;
    private readonly bool _useGeoData;
    private Process? _process;

    public MihomoHost(string exe, AppPaths paths, FileLogger log, JobObject job, bool useGeoData = true)
    {
        _exe = exe;
        _paths = paths;
        _log = log;
        _job = job;
        _useGeoData = useGeoData;
    }

    public event Action<int>? Exited;

    public MihomoApi? Api { get; private set; }

    public string? Version { get; private set; }

    public int? ProcessId
    {
        get
        {
            try { return _process?.Id; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task WriteConfigAndTestAsync(string yaml, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.MihomoHome);
        if (_useGeoData) CopyGeoData();
        await File.WriteAllTextAsync(_paths.MihomoConfig, yaml, new UTF8Encoding(false), ct).ConfigureAwait(false);
        var (code, output) = await ProcessRunner.RunAsync(_exe, ["-t", "-d", _paths.MihomoHome, "-f", _paths.MihomoConfig],
            _paths.MihomoHome, ChildEnvironment.ForMihomo, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(4))
            _log.Write("mihomo-t", line);
        if (code != 0) throw new InvalidOperationException($"mihomo 配置校验失败（退出码 {code}），详见日志");
    }

    /// <summary>
    /// Start mihomo and wait until <paramref name="readyGroup"/> is visible through the controller. mihomo serves the
    /// controller before it has applied the config, so /version alone is not a readiness signal.
    /// </summary>
    public async Task StartAsync(string pipeName, string readyGroup, CancellationToken ct)
    {
        var process = ProcessRunner.Start(_exe, ["-d", _paths.MihomoHome, "-f", _paths.MihomoConfig], _paths.MihomoHome,
            ChildEnvironment.ForMihomo, line => _log.Write("mihomo", line), _job);
        _process = process;
        process.Exited += (_, _) =>
        {
            var code = -1;
            try { code = process.ExitCode; }
            catch (InvalidOperationException) { }
            Exited?.Invoke(code);
        };
        Api = new MihomoApi(pipeName);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"mihomo 启动后立即退出（退出码 {process.ExitCode}），详见日志");
            if (await Api.TryGetGroupNowAsync(readyGroup, ct).ConfigureAwait(false) is not null)
            {
                Version = await Api.TryGetVersionAsync(ct).ConfigureAwait(false);
                return;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"mihomo 启动超时：{readyGroup} 分组未就绪");
    }

    public async Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        Api?.Dispose();
        Api = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public void DeleteConfigFile()
    {
        try
        {
            if (File.Exists(_paths.MihomoConfig)) File.Delete(_paths.MihomoConfig);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("core", "WARNING: could not delete runtime config: " + ex.Message);
        }
    }

    private void CopyGeoData()
    {
        // Geo data is maintained in bin\geo by the component manager.
        foreach (var name in GeoFiles)
        {
            var target = Path.Combine(_paths.MihomoHome, name);
            var source = Path.Combine(_paths.GeoDir, name);
            if (!File.Exists(source)) continue;
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length &&
                File.GetLastWriteTimeUtc(target) == File.GetLastWriteTimeUtc(source)) continue;
            File.Copy(source, target, true);
            _log.Write("core", $"copied {name} from bin\\geo");
        }
    }
}
