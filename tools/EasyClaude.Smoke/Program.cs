using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EasyClaude.Core;

// Always pass an isolated root for acceptance tests. No production credential database is copied.
static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
var paths = Option(args, "--root") is { } root ? new AppPaths(root) : AppPaths.Default();
using var mutex = new Mutex(true, FirewallGuard.MutexName(paths.Root), out var ownsMutex);
if (!ownsMutex) { Console.WriteLine("FAIL instance already running"); return 2; }
paths.EnsureCreated();
var redactor = new Redactor();
using var log = new FileLogger(paths.LogFile, redactor) { Echo = Console.WriteLine };
var settings = AppSettings.LoadOrCreate(paths.SettingsFile);
var store = new ProfileStore(paths.ProfileFile);
var progress = new Progress<DependencyProgress>(p => Console.WriteLine("PREPARE " + p.Message + (p.TotalBytes is > 0 ? $" {100.0 * p.Bytes / p.TotalBytes:0}%" : "")));
var dependencies = new DependencyManager(paths, log, progress);
if (Option(args, "--import") is { } importPath)
    Console.WriteLine("IMPORT " + await store.ImportAsync(importPath, (yaml, ct) => dependencies.ValidateProfileAsync(yaml, settings, ct)));
if (!store.Exists) { Console.WriteLine("FAIL missing imported profile"); return 2; }
if (!args.Contains("--skip-restore")) await dependencies.EnsureAsync(store.LoadText(), settings);
if (args.Contains("--restore-only")) { Console.WriteLine(await dependencies.VerifyAsync()); return 0; }
if (args.Contains("--test-saved-key"))
{
    await using var keyCheck = new Orchestrator(paths, settings, log, redactor);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    if (!await keyCheck.StartAsync(deadline.Token)) { Console.WriteLine("KEY_CHECK startup failed"); return 1; }
    var result = await keyCheck.TestApiKeyAsync(deadline.Token);
    Console.WriteLine("KEY_CHECK " + JsonSerializer.Serialize(result));
    return result.Ok ? 0 : 1;
}

// Fake credentials for two providers so the provider policy can be observed (process-scoped, never stored).
Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-smoke-dummy-not-a-real-key");
Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-smoke-dummy-not-a-real-key");

var results = new List<(string Name, bool Ok)>();
void Check(string name, bool ok, string detail = "")
{
    results.Add((name, ok));
    Console.WriteLine($"CHECK {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " :: " + redactor.Redact(detail) : "")}");
}

// Simulate an orphaned core from a crashed run: Easy Claude's private mihomo copy running outside any job.
var orphanHome = Path.Combine(paths.TempDir, "smoke-orphan-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(orphanHome);
var orphanConfig = Path.Combine(orphanHome, "config.yaml");
File.WriteAllText(orphanConfig,
    $"mixed-port: {PortUtil.GetFreeTcpPort()}\nallow-lan: false\nbind-address: \"127.0.0.1\"\nexternal-controller: \"\"\nlog-level: silent\n");
var orphanStart = new ProcessStartInfo(ToolLocator.FindMihomo(settings, paths)) { UseShellExecute = false, CreateNoWindow = true };
foreach (var arg in new[] { "-d", orphanHome, "-f", orphanConfig }) orphanStart.ArgumentList.Add(arg);
using var orphan = Process.Start(orphanStart)!;
await Task.Delay(1500);
var orphanWasAlive = !orphan.HasExited;

var children = new List<int>();
var orchestrator = new Orchestrator(paths, settings, log, redactor);
try
{
    Uri? connect = null;
    orchestrator.OpenCodeReady += (uri, _) => connect = uri;
    var started = await orchestrator.StartAsync();
    if (orchestrator.Status.State == LinkState.Paused)
    {
        Console.WriteLine("INFO paused by route conflict: " + orchestrator.Conflict?.Message);
        started = await orchestrator.ContinueDespiteConflictAsync();
    }
    if (started) await orchestrator.RecheckAsync();
    var s0 = orchestrator.Status;
    Check("pipeline start (cleanup -> route check -> mihomo -> self-check -> opencode -> pairing)", started, $"{s0.State} {s0.Message} {s0.Detail}");
    Check("orphaned core from a previous run cleaned up (matched by image path)", orphanWasAlive && orphan.HasExited, $"alive before start: {orphanWasAlive}");
    Check("route check: node route not captured by another client's TUN", orchestrator.Conflict is null,
        orchestrator.Conflict?.Message ?? "node route uses a physical adapter");

    if (started && orchestrator.OpenCodeClient is { } client && connect is not null)
    {
        children.AddRange(orchestrator.ChildProcessIds);
        var images = orchestrator.ChildProcessIds.Select(ImageOf).ToList();
        Check("children run from Easy Claude's private copies",
            images.Contains(paths.PrivateMihomo, StringComparer.OrdinalIgnoreCase) && images.Contains(paths.PrivateOpenCode, StringComparer.OrdinalIgnoreCase),
            string.Join(", ", images.Select(x => Path.GetFileName(x))));
        Check("egress IP and location resolved", s0.EgressIp is not null && !string.IsNullOrEmpty(s0.Location),
            $"{s0.EgressText}; consistency={s0.Consistency}");
        Check("plaintext runtime config deleted after mihomo start", !File.Exists(paths.MihomoConfig));

        // api/model is a snapshot that "may precede initial plugin settlement": wait until it is populated.
        var modelMap = new Dictionary<string, (int Enabled, int Disabled)>();
        var modelBody = string.Empty;
        var waitModels = Stopwatch.StartNew();
        while (waitModels.Elapsed < TimeSpan.FromSeconds(45))
        {
            int modelStatus;
            (modelStatus, modelBody) = await client.GetAsync("api/model", default);
            modelMap = modelStatus == 200 ? ModelProviders(modelBody) : new Dictionary<string, (int Enabled, int Disabled)>();
            if (modelMap.Count > 0) break;
            await Task.Delay(1000);
        }
        var anthropicEnabled = modelMap.TryGetValue("anthropic", out var anthropic) ? anthropic.Enabled : 0;
        var othersEnabled = modelMap.Where(kv => kv.Key != "anthropic").Sum(kv => kv.Value.Enabled);
        Check("provider policy: only anthropic models enabled (openai key present but blocked)", anthropicEnabled > 0 && othersEnabled == 0,
            $"after {waitModels.Elapsed.TotalSeconds:0}s; " +
            string.Join(", ", modelMap.Select(kv => $"{kv.Key}={kv.Value.Enabled} enabled/{kv.Value.Disabled} disabled")));

        // API key flow: a key Anthropic rejects must not be stored, and must never reach the log.
        const string invalidKey = "sk-ant-api03-smoke-invalid-key-000000000000000000";
        var credentialsBefore = await client.ListKeyCredentialsAsync("anthropic", default);
        var invalid = await orchestrator.ApplyApiKeyAsync(invalidKey);
        var credentialsAfter = await client.ListKeyCredentialsAsync("anthropic", default);
        Check("invalid API key rejected by Anthropic and not stored",
            !invalid.Ok && invalid.Message.Contains("401", StringComparison.Ordinal) && credentialsBefore.Count == credentialsAfter.Count,
            invalid.Message);
        var keyState = orchestrator.ApiKey;
        Check("API key state exposes at most a short prefix",
            keyState.Hint is null || (keyState.Hint.EndsWith('…') && keyState.Hint.Length <= 18),
            $"configured={keyState.Configured} hint-length={keyState.Hint?.Length ?? 0}");
        Check("rejected key never written to the log", !ReadShared(paths.LogFile).Contains("smoke-invalid-key", StringComparison.Ordinal));

        var (agentStatus, agentBody) = await client.GetAsync("api/agent", default);
        IReadOnlyList<string> agents = agentStatus == 200 ? OpenCodeClient.ExtractIds(agentBody) : Array.Empty<string>();
        Check("isolated config: no strata / orchestrator agents",
            agentStatus == 200 && agents.Count > 0 &&
            !agents.Any(a => a.Contains("strata", StringComparison.OrdinalIgnoreCase) || a.Contains("orchestrator", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", agents));

        var (rootStatus, rootBody) = await client.GetAsync("", default);
        Check("web UI served by the private OpenCode copy", rootStatus == 200 && rootBody.Contains("<html", StringComparison.OrdinalIgnoreCase),
            "http " + rootStatus);

        var connectStatus = await OpenCodeClient.ProbeAsync(connect, default);
        Check("pairing page served", connectStatus == 200, "http " + connectStatus);

        // Simulated proxy crash: status must turn Error (fail closed), then a proxy-only restart recovers.
        using (var mihomo = Process.GetProcessById(orchestrator.MihomoProcessId!.Value)) mihomo.Kill();
        var sw = Stopwatch.StartNew();
        while (orchestrator.Status.State != LinkState.Error && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(250);
        Check("proxy crash detected", orchestrator.Status.State == LinkState.Error, orchestrator.Status.Message);

        using (var destination = new TcpListener(IPAddress.Loopback, 0))
        using (var proxy = ProxyProbe.CreateClient(new RuntimeSecrets(PortUtil.GetFreeTcpPort(), "unused", "unused", "unused")))
        {
            destination.Start();
            var port = ((IPEndPoint)destination.LocalEndpoint).Port;
            try { await proxy.GetAsync($"http://127.0.0.1:{port}/"); Check("unavailable local proxy fails without direct fallback", false); }
            catch (HttpRequestException) { Check("unavailable local proxy fails without direct fallback", !destination.Pending()); }
        }

        var restarted = await orchestrator.RestartProxyAsync();
        if (restarted) await orchestrator.RecheckAsync();
        var s1 = orchestrator.Status;
        Check("proxy-only restart recovers (same port and credentials)", restarted && s1.State is LinkState.Ready or LinkState.Degraded,
            $"{s1.State} {s1.Message} {s1.Detail}");
        Check("egress consistent with the previous check after restart", s1.Consistency == EgressConsistency.Same, $"{s1.Consistency}; {s1.EgressText}");
        Check("opencode still healthy after proxy restart", await client.PingAsync(default));
        children.AddRange(orchestrator.ChildProcessIds);
    }
}
finally
{
    await orchestrator.DisposeAsync();
    if (!orphan.HasExited)
    {
        try { orphan.Kill(); }
        catch (InvalidOperationException) { }
    }
    try { Directory.Delete(orphanHome, recursive: true); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
}

await Task.Delay(1000);
var leftovers = children.Distinct().Where(Alive).ToList();
Check("no leftover child processes after shutdown", leftovers.Count == 0, string.Join(",", leftovers));
Check("plaintext runtime config absent after shutdown", !File.Exists(paths.MihomoConfig));

var failed = results.Count(r => !r.Ok);
Console.WriteLine($"SUMMARY {results.Count - failed}/{results.Count} passed");
return failed == 0 ? 0 : 1;

// Counts enabled / disabled models per provider in a model.list response ({ location, data: Model.Info[] }).
static Dictionary<string, (int Enabled, int Disabled)> ModelProviders(string json)
{
    var map = new Dictionary<string, (int Enabled, int Disabled)>(StringComparer.Ordinal);
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return map;
    foreach (var model in data.EnumerateArray())
    {
        var provider = model.TryGetProperty("providerID", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "?";
        var enabled = !model.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
        var current = map.GetValueOrDefault(provider);
        map[provider] = enabled ? (current.Enabled + 1, current.Disabled) : (current.Enabled, current.Disabled + 1);
    }
    return map;
}

// Reads a file that another handle keeps open for writing (the log).
static string ReadShared(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static string ImageOf(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return process.MainModule?.FileName ?? "?";
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
    {
        return "?";
    }
}

static bool Alive(int pid)
{
    try
    {
        using var p = Process.GetProcessById(pid);
        return !p.HasExited;
    }
    catch (ArgumentException) { return false; }
    catch (InvalidOperationException) { return false; }
}
