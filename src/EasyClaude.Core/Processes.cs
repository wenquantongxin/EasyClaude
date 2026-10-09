using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace EasyClaude.Core;

public static class SecretGen
{
    public static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}

public static class PortUtil
{
    public static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// Child-process environments: strip inherited proxy / OpenCode / XDG / TLS-weakening variables, then pin ours.
/// The user's global HTTP(S)_PROXY (e.g. Clash Verge on 7897) must never leak into our children.
/// </summary>
public static class ChildEnvironment
{
    private static readonly string[] RemoveExact =
    [
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "FTP_PROXY",
        "NODE_TLS_REJECT_UNAUTHORIZED", "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR",
    ];

    private static readonly string[] RemovePrefixes = ["OPENCODE", "XDG_"];

    public static bool ShouldRemove(string key) =>
        RemoveExact.Any(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
        RemovePrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static void Apply(IDictionary<string, string?> env, IReadOnlyDictionary<string, string> set)
    {
        foreach (var key in env.Keys.ToList())
            if (ShouldRemove(key)) env.Remove(key);
        foreach (var (key, value) in set) env[key] = value;
    }

    public static void ScrubOnly(IDictionary<string, string?> env) => Apply(env, new Dictionary<string, string>());

    public static void ForMihomo(IDictionary<string, string?> env)
    {
        ScrubOnly(env);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        env["LISTEN_NAMEDPIPE_SDDL"] = $"D:P(A;;GRGW;;;{sid})(A;;GRGW;;;SY)(A;;GRGW;;;BA)";
    }

    public static Dictionary<string, string> ForOpenCode(AppPaths paths, RuntimeSecrets proxy, string serverPassword) => new()
    {
        ["XDG_CONFIG_HOME"] = paths.XdgConfig,
        ["XDG_DATA_HOME"] = paths.XdgData,
        ["XDG_STATE_HOME"] = paths.XdgState,
        ["XDG_CACHE_HOME"] = paths.XdgCache,
        ["TEMP"] = paths.TempDir, // OpenCode's temp files (tool output etc.) stay inside data\tmp
        ["TMP"] = paths.TempDir,
        ["HTTP_PROXY"] = proxy.ProxyUrl,
        ["HTTPS_PROXY"] = proxy.ProxyUrl,
        ["NO_PROXY"] = "localhost,127.0.0.1,::1",
        ["OPENCODE_SERVER_PASSWORD"] = serverPassword,
    };
}

public static class ProcessRunner
{
    /// <summary>
    /// Start a long-running child: no window, stdout/stderr streamed line by line, stdin kept open
    /// (the child sees EOF only when we die), and assigned to the job so it can never outlive Easy Claude.
    /// </summary>
    public static Process Start(string exe, IEnumerable<string> args, string? workDir,
        Action<IDictionary<string, string?>>? configureEnv, Action<string> onLine, JobObject? job)
    {
        var process = new Process { StartInfo = CreateStartInfo(exe, args, workDir, configureEnv), EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("无法启动进程：" + exe);
        try
        {
            job?.Assign(process);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Run a short command to completion (stdin closed) and return its exit code and combined output.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, IEnumerable<string> args, string? workDir,
        Action<IDictionary<string, string?>>? configureEnv, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(exe, args, workDir, configureEnv) };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} 运行超时");
        }
        process.WaitForExit(); // drain asynchronous stdout/stderr before reading the result
        lock (output) return (process.ExitCode, output.ToString());
    }

    private static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args, string? workDir,
        Action<IDictionary<string, string?>>? configureEnv)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        configureEnv?.Invoke(psi.Environment);
        return psi;
    }
}
