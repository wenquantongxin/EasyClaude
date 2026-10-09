using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;

namespace EasyClaude.Core;

public sealed record DependencyProgress(string Message, long Bytes = 0, long? TotalBytes = null);
public sealed record InstalledArtifact(string Sha256, long Length, DateTime LastWriteUtc, string Source, string Version);
public sealed class InstallationManifest
{
    public Dictionary<string, InstalledArtifact> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One installer for startup, profile imports and explicit integrity checks. No network on a healthy warm start.</summary>
public sealed class DependencyManager(AppPaths paths, FileLogger log, IProgress<DependencyProgress>? progress = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly JsonDocument Catalog = JsonDocument.Parse(
        Assembly.GetExecutingAssembly().GetManifestResourceStream("EasyClaude.dependencies.json")!);
    private static readonly (string Local, string Remote)[] GeoFiles =
        [("Country.mmdb", "country.mmdb"), ("geoip.dat", "geoip.dat"), ("geosite.dat", "geosite.dat"), ("ASN.mmdb", "GeoLite2-ASN.mmdb")];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string ManifestPath => Path.Combine(paths.BinDir, "manifest.json");
    private static string Version(string key) => Catalog.RootElement.GetProperty(key).GetString()!;
    public static string WebViewInstallerUrl => Version("webViewInstallerUrl");

    public async Task EnsureAsync(string profile, AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            paths.EnsureCreated();
            var manifest = LoadManifest();
            using var system = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
            system.DefaultRequestHeaders.UserAgent.ParseAdd("EasyClaude/0.2");
            if (!await InstalledAsync("easyclaude-mihomo.exe", manifest, ct).ConfigureAwait(false))
                await InstallMihomoAsync(system, manifest, ct).ConfigureAwait(false);
            var missingOpenCode = !await InstalledAsync("easyclaude-opencode.exe", manifest, ct).ConfigureAwait(false);
            var missingGeo = new List<(string Local, string Remote)>();
            foreach (var file in GeoFiles)
                if (!await InstalledAsync("geo/" + file.Local, manifest, ct).ConfigureAwait(false)) missingGeo.Add(file);
            if (!missingOpenCode && missingGeo.Count == 0) return;
            await WithNodeProxyAsync(profile, settings, async http =>
            {
                if (missingOpenCode) await InstallOpenCodeAsync(http, manifest, ct).ConfigureAwait(false);
                if (missingGeo.Count > 0)
                {
                    using var release = await GetJsonAsync(http,
                        $"https://api.github.com/repos/{Version("geoRepository")}/releases/tags/latest", ct).ConfigureAwait(false);
                    foreach (var geo in missingGeo)
                    {
                        var asset = FindAsset(release, geo.Remote);
                        var checksum = FindAsset(release, geo.Remote + ".sha256sum");
                        using var sumResponse = await GetAsync(http, checksum.GetProperty("browser_download_url").GetString()!, ct).ConfigureAwait(false);
                        sumResponse.EnsureSuccessStatusCode();
                        var sum = (await sumResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                        await DownloadAndInstallAsync(http, asset.GetProperty("browser_download_url").GetString()!,
                            "geo/" + geo.Local, "sha256", sum, null, "latest", manifest, ct).ConfigureAwait(false);
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ValidateProfileAsync(string yaml, AppSettings settings, CancellationToken ct = default)
    {
        await EnsureAsync(yaml, settings, ct).ConfigureAwait(false);
        var secrets = new RuntimeSecrets(PortUtil.GetFreeTcpPort(), "ec", SecretGen.Hex(16), "ec-validate-" + SecretGen.Hex(6));
        var build = RuntimeConfigBuilder.Build(yaml, secrets, new(settings.EffectiveLlmDomains(), settings.BlockedCidrs, settings.MihomoLogLevel));
        var temp = Path.Combine(paths.TempDir, "validate-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            progress?.Report(new("正在验证节点配置…"));
            await File.WriteAllTextAsync(temp, build.Yaml, ct).ConfigureAwait(false);
            var (code, output) = await ProcessRunner.RunAsync(paths.PrivateMihomo, ["-t", "-d", paths.GeoDir, "-f", temp],
                paths.GeoDir, ChildEnvironment.ForMihomo, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            foreach (var line in output.Split('\n').TakeLast(4)) log.Write("profile-check", line.Trim());
            if (code != 0) throw new InvalidDataException("mihomo 配置校验失败，原配置已保留。详见日志。");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<string> VerifyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = LoadManifest();
            var failed = new List<string>();
            foreach (var name in new[] { "easyclaude-mihomo.exe", "easyclaude-opencode.exe" }.Concat(GeoFiles.Select(g => "geo/" + g.Local)))
            {
                progress?.Report(new("正在校验 " + name));
                if (!manifest.Files.TryGetValue(name, out var artifact) || !File.Exists(Target(name)) ||
                    !string.Equals(await HashFileAsync(Target(name), "sha256", ct).ConfigureAwait(false), artifact.Sha256, StringComparison.OrdinalIgnoreCase)) failed.Add(name);
            }
            return failed.Count == 0 ? "全部组件完整性校验通过" : "缺失或变更：" + string.Join("、", failed) + "。请重启程序以恢复组件。";
        }
        finally { _gate.Release(); }
    }

    public async Task DownloadWebViewInstallerAsync(string profile, AppSettings settings, string target, CancellationToken ct)
    {
        await EnsureAsync(profile, settings, ct).ConfigureAwait(false);
        await WithNodeProxyAsync(profile, settings, async http =>
        {
            await DownloadFileAsync(http, WebViewInstallerUrl, target, "WebView2 运行时", ct, progress).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task WithNodeProxyAsync(string profile, AppSettings settings, Func<HttpClient, Task> action, CancellationToken ct)
    {
        var scratch = new AppPaths(Path.Combine(paths.TempDir, "bootstrap-" + Guid.NewGuid().ToString("N")));
        scratch.EnsureCreated();
        using var job = new JobObject();
        var secrets = new RuntimeSecrets(PortUtil.GetFreeTcpPort(), "ec", SecretGen.Hex(16), "ec-bootstrap-" + SecretGen.Hex(6));
        var host = new MihomoHost(paths.PrivateMihomo, scratch, log, job, useGeoData: false);
        try
        {
            progress?.Report(new("正在通过导入节点建立下载连接…"));
            var build = RuntimeConfigBuilder.Build(profile, secrets, new(settings.EffectiveLlmDomains(), settings.BlockedCidrs), downloadsOnly: true);
            await host.WriteConfigAndTestAsync(build.Yaml, ct).ConfigureAwait(false);
            await host.StartAsync(secrets.PipeName, build.LlmGroup, ct).ConfigureAwait(false);
            host.DeleteConfigFile();
            using var http = ProxyProbe.CreateClient(secrets);
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EasyClaude/0.2");
            await action(http).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
            host.DeleteConfigFile();
            DeleteScratch(scratch.Root, paths.TempDir);
        }
    }

    private async Task InstallMihomoAsync(HttpClient http, InstallationManifest manifest, CancellationToken ct)
    {
        var version = Version("mihomoVersion");
        using var release = await GetJsonAsync(http, $"https://api.github.com/repos/MetaCubeX/mihomo/releases/tags/v{version}", ct).ConfigureAwait(false);
        var cpu = Avx2.IsSupported ? "v3" : "v1";
        var asset = FindAsset(release, $"mihomo-windows-amd64-{cpu}-v{version}.zip");
        var digest = asset.GetProperty("digest").GetString() ?? throw new InvalidDataException("官方 mihomo 发布缺少摘要");
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal)) throw new InvalidDataException("不支持的 mihomo 官方摘要");
        await DownloadAndInstallAsync(http, asset.GetProperty("browser_download_url").GetString()!, "easyclaude-mihomo.exe",
            "sha256", digest[7..], "zip", version, manifest, ct).ConfigureAwait(false);
    }

    private async Task InstallOpenCodeAsync(HttpClient http, InstallationManifest manifest, CancellationToken ct)
    {
        var version = Version("openCodeVersion");
        var package = Avx2.IsSupported ? "cli-windows-x64" : "cli-windows-x64-baseline";
        using var metadata = await GetJsonAsync(http, $"https://registry.npmjs.org/@opencode%2f{package}/{version}", ct).ConfigureAwait(false);
        var dist = metadata.RootElement.GetProperty("dist");
        var integrity = dist.GetProperty("integrity").GetString()!;
        if (!integrity.StartsWith("sha512-", StringComparison.Ordinal)) throw new InvalidDataException("OpenCode 包缺少 SHA512 完整性信息");
        var hash = Convert.ToHexString(Convert.FromBase64String(integrity[7..]));
        await DownloadAndInstallAsync(http, dist.GetProperty("tarball").GetString()!, "easyclaude-opencode.exe", "sha512", hash,
            "tgz", version, manifest, ct).ConfigureAwait(false);
    }

    private async Task DownloadAndInstallAsync(HttpClient http, string url, string name, string algorithm, string expected,
        string? archiveType, string version, InstallationManifest manifest, CancellationToken ct)
    {
        var stage = Path.Combine(paths.BinDir, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var download = Path.Combine(stage, "download");
            progress?.Report(new("正在下载 " + name));
            await DownloadFileAsync(http, url, download, name, ct, progress).ConfigureAwait(false);
            if (!string.Equals(await HashFileAsync(download, algorithm, ct).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(name + " 的官方校验值不匹配，下载文件已拒绝。");
            var extracted = Path.Combine(stage, "component");
            if (archiveType is null) File.Move(download, extracted);
            else if (archiveType == "zip")
            {
                using var zip = ZipFile.OpenRead(download);
                var entry = zip.Entries.Single(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                await using var input = entry.Open();
                await using var output = File.Create(extracted);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            else
            {
                await using var compressed = File.OpenRead(download);
                await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                TarEntry? entry;
                var found = false;
                while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false)) is not null)
                {
                    if (!entry.Name.StartsWith("package/bin/", StringComparison.Ordinal) || !entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (found || entry.DataStream is null) throw new InvalidDataException("OpenCode 包中的可执行文件不唯一");
                    await using var output = File.Create(extracted);
                    await entry.DataStream.CopyToAsync(output, ct).ConfigureAwait(false);
                    found = true;
                }
                if (!found) throw new InvalidDataException("OpenCode 包缺少可执行文件");
            }
            ct.ThrowIfCancellationRequested();
            var target = Target(name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var hash = await HashFileAsync(extracted, "sha256", ct).ConfigureAwait(false);
            File.Move(extracted, target, true);
            var info = new FileInfo(target);
            manifest.Files[name] = new(hash, info.Length, info.LastWriteTimeUtc, url, version);
            SaveManifest(manifest);
            log.Write("components", "installed and verified " + name + " version " + version);
        }
        finally { DeleteScratch(stage, paths.BinDir); }
    }

    public static async Task DownloadFileAsync(HttpClient http, string url, string target, string label, CancellationToken ct,
        IProgress<DependencyProgress>? progress = null)
    {
        try
        {
            using var response = await GetAsync(http, url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > 800L * 1024 * 1024) throw new InvalidDataException("组件包过大");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(target);
            var buffer = new byte[65536];
            long total = 0;
            var last = Environment.TickCount64;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > 800L * 1024 * 1024) throw new InvalidDataException("组件包过大");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (Environment.TickCount64 - last >= 200)
                {
                    progress?.Report(new("正在下载 " + label, total, length));
                    last = Environment.TickCount64;
                }
            }
            if (length is { } expected && total != expected) throw new IOException("下载不完整");
        }
        catch { if (File.Exists(target)) File.Delete(target); throw; }
    }

    private async Task<bool> InstalledAsync(string name, InstallationManifest manifest, CancellationToken ct)
    {
        if (!manifest.Files.TryGetValue(name, out var artifact) || !File.Exists(Target(name))) return false;
        var expectedVersion = name switch { "easyclaude-mihomo.exe" => Version("mihomoVersion"), "easyclaude-opencode.exe" => Version("openCodeVersion"), _ => null };
        if (expectedVersion is not null && artifact.Version != expectedVersion) return false;
        var info = new FileInfo(Target(name));
        if (info.Length == artifact.Length && info.LastWriteTimeUtc == artifact.LastWriteUtc) return true;
        if (!string.Equals(await HashFileAsync(info.FullName, "sha256", ct).ConfigureAwait(false), artifact.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        manifest.Files[name] = artifact with { Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        SaveManifest(manifest);
        return true;
    }

    public static async Task<string> HashFileAsync(string path, string algorithm, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(algorithm == "sha512" ? await SHA512.HashDataAsync(input, ct).ConfigureAwait(false) : await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }
    private string Target(string name) => Path.Combine(paths.BinDir, name.Replace('/', Path.DirectorySeparatorChar));
    private InstallationManifest LoadManifest()
    {
        try { return JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(ManifestPath), Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    private void SaveManifest(InstallationManifest manifest)
    {
        File.WriteAllText(ManifestPath + ".tmp", JsonSerializer.Serialize(manifest, Json));
        File.Move(ManifestPath + ".tmp", ManifestPath, true);
    }
    private static JsonElement FindAsset(JsonDocument release, string name) =>
        release.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await GetAsync(http, url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }
    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            var uri = new Uri(url);
            if (uri.Scheme != "https") throw new InvalidDataException("组件必须通过 HTTPS 下载");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var next = response.Headers.Location ?? throw new IOException("下载重定向缺少目标地址");
            url = new Uri(uri, next).AbsoluteUri;
            response.Dispose();
        }
        throw new IOException("组件下载重定向过多");
    }
    private static void DeleteScratch(string directory, string parent)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("临时目录超出程序范围");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
