using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasyClaude.Core;

/// <summary>Client for the isolated <c>opencode serve</c> instance (HTTP Basic auth, user "opencode").</summary>
public sealed class OpenCodeClient : IModelTestClient, IDisposable
{
    private readonly HttpClient _http;

    public OpenCodeClient(Uri baseUri, string password, HttpMessageHandler? handler = null)
    {
        BaseUri = baseUri;
        BasicAuthHeaderValue = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(40),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", BasicAuthHeaderValue);
    }

    public Uri BaseUri { get; }

    /// <summary>Authorization header value; injected by the WebView2 host for requests to the local origin.</summary>
    public string BasicAuthHeaderValue { get; }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("api/info", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<string> CreatePairingCodeAsync(CancellationToken ct)
    {
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("api/pair", body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            return code.GetString()!;
        throw new InvalidOperationException("OpenCode 配对接口没有返回 code");
    }

    public Uri GetConnectUri(string code) => new(BaseUri, "auth/connect/" + Uri.EscapeDataString(code));

    public async Task<(int Status, string Body)> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Stores <paramref name="key"/> as the active anthropic credential, then removes older anthropic credentials so no
    /// stale key stays on disk. Request and response bodies contain the secret and are never logged.
    /// </summary>
    public async Task<string?> ReplaceAnthropicKeyAsync(string key, CancellationToken ct, Action? onSaved = null)
    {
        var body = new JsonObject
        {
            ["integrationID"] = "anthropic",
            ["label"] = "Easy Claude",
            ["activate"] = true,
            ["value"] = new JsonObject { ["type"] = "key", ["key"] = key },
        };
        string newId;
        using (var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"))
        using (var response = await _http.PostAsync("api/credential", content, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenCode 未能保存凭据（HTTP {(int)response.StatusCode}）");
            onSaved?.Invoke();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            newId = doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        }
        var warnings = new List<string>();
        foreach (var credential in await ListKeyCredentialsAsync("anthropic", ct).ConfigureAwait(false))
        {
            if (credential.Id == newId) continue;
            using var deleted = await _http.DeleteAsync("api/credential/" + Uri.EscapeDataString(credential.Id), ct).ConfigureAwait(false);
            if (!deleted.IsSuccessStatusCode) warnings.Add($"旧凭据清理失败（HTTP {(int)deleted.StatusCode}）");
        }
        return warnings.Count == 0 ? null : string.Join("；", warnings);
    }

    /// <summary>Stored credentials of an integration. The API returns secret values: only masked hints leave this method.</summary>
    public async Task<IReadOnlyList<ApiKeyCredential>> ListKeyCredentialsAsync(string integrationId, CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/credential", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var result = new List<ApiKeyCredential>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("integrationID", out var integration) || integration.GetString() != integrationId) continue;
            var id = entry.GetProperty("id").GetString()!;
            var active = entry.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
            string? hint = null;
            string? identity = null;
            if (entry.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("key", out var secret) && secret.ValueKind == JsonValueKind.String)
            {
                var key = secret.GetString()!;
                hint = ApiKeyMask.Hint(key);
                identity = ApiKeyVerificationStore.Identity(id, key);
            }
            result.Add(new(id, active, hint, identity));
        }
        return result;
    }

    /// <summary>Newest enabled, non-deprecated model of a provider whose id contains <paramref name="contains"/>.</summary>
    public async Task<string?> FindModelAsync(string providerId, string contains, CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/model", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var candidates = new List<(string Id, string Release)>();
        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("providerID", out var p) || p.GetString() != providerId) continue;
            if (!model.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id) continue;
            if (!id.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
            if (model.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
            if (model.TryGetProperty("status", out var status) && status.GetString() == "deprecated") continue;
            var release = string.Empty;
            if (model.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object)
                foreach (var property in time.EnumerateObject().OrderBy(p => p.Name is "release" or "created" ? 0 : 1))
                {
                    release = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => property.Value.GetDouble().ToString("000000000000000000000", CultureInfo.InvariantCulture),
                        _ => string.Empty,
                    };
                    if (release.Length > 0) break;
                }
            candidates.Add((id, release));
        }
        return candidates.OrderByDescending(c => c.Release, StringComparer.Ordinal).Select(c => c.Id).FirstOrDefault();
    }

    /// <summary>One stateless generation through OpenCode (its credential, policy and proxy path).</summary>
    public async Task<(bool Ok, int Status, string Text)> GenerateAsync(string prompt, string providerId, string modelId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["model"] = new JsonObject { ["id"] = modelId, ["providerID"] = providerId },
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("api/experimental/generate", content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (false, (int)response.StatusCode, text);
        using var doc = JsonDocument.Parse(text);
        return (true, 200, doc.RootElement.GetProperty("data").GetProperty("text").GetString() ?? string.Empty);
    }

    /// <summary>Unauthenticated GET without redirects (used to verify one-time pairing links).</summary>
    public static async Task<int> ProbeAsync(Uri uri, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    /// <summary>Best-effort extraction of entity IDs from list endpoints such as api/provider and api/agent.</summary>
    public static IReadOnlyList<string> ExtractIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement;
        if (list.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "data", "items", "providers", "agents" })
                if (list.TryGetProperty(name, out var inner))
                {
                    list = inner;
                    break;
                }

        var ids = new List<string>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    ids.Add(element.GetString()!);
                    continue;
                }
                if (element.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "id", "providerID", "name" })
                    if (element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        ids.Add(v.GetString()!);
                        break;
                    }
            }
        }
        else if (list.ValueKind == JsonValueKind.Object)
        {
            ids.AddRange(list.EnumerateObject().Select(p => p.Name));
        }
        return ids;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Runs a dedicated <c>opencode serve</c> with an isolated XDG tree (own config, credentials, sessions DB) and a
/// pinned environment: its only route to the internet is the local mihomo port.
/// </summary>
public sealed class OpenCodeHost
{
    private readonly string _exe;
    private readonly AppPaths _paths;
    private readonly FileLogger _log;
    private readonly JobObject _job;
    private Process? _process;

    public OpenCodeHost(string exe, AppPaths paths, FileLogger log, JobObject job)
    {
        _exe = exe;
        _paths = paths;
        _log = log;
        _job = job;
    }

    public event Action<int>? Exited;

    public Uri? BaseUri { get; private set; }

    public OpenCodeClient? Client { get; private set; }

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

    public async Task StartAsync(RuntimeSecrets proxy, string workDir, Redactor redactor, CancellationToken ct)
    {
        var port = PortUtil.GetFreeTcpPort();
        var password = SecretGen.Hex(16);
        redactor.AddSecret(password);
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        var env = ChildEnvironment.ForOpenCode(_paths, proxy, password);
        var process = ProcessRunner.Start(_exe,
            ["serve", "--hostname", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture)],
            workDir, e => ChildEnvironment.Apply(e, env), line => _log.Write("opencode", line), _job);
        _process = process;
        process.Exited += (_, _) =>
        {
            var code = -1;
            try { code = process.ExitCode; }
            catch (InvalidOperationException) { }
            Exited?.Invoke(code);
        };
        Client = new OpenCodeClient(BaseUri, password);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"OpenCode 服务启动后立即退出（退出码 {process.ExitCode}），详见日志");
            if (await Client.PingAsync(ct).ConfigureAwait(false)) return;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("OpenCode 服务启动超时");
    }

    public async Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        Client?.Dispose();
        Client = null;
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
}

/// <summary>Writes the isolated global OpenCode config. Global policies override project configs.</summary>
public static class OpenCodeConfigWriter
{
    public static void Write(string file, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var config = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = settings.Model,
            ["default_agent"] = "build",
            ["update"] = "disable",
            ["experimental"] = new JsonObject
            {
                ["policies"] = new JsonArray(
                    new JsonObject { ["action"] = "provider.use", ["resource"] = "*", ["effect"] = "deny" },
                    new JsonObject { ["action"] = "provider.use", ["resource"] = "anthropic", ["effect"] = "allow" }),
            },
        };
        var text = "// Managed by Easy Claude: rewritten on every start, edits here are discarded.\n" +
                   "// Only the anthropic provider may be used (global policies also override project configs).\n" +
                   config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(file, text, new UTF8Encoding(false));
    }
}
