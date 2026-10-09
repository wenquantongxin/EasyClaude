using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyClaude.Core;

public sealed record SelfCheckResult(
    bool Ok, string Message, string? Node, int? DelayMs, string? Country, string? Colo, int? AnthropicStatus,
    string? EgressIp = null, string? Location = null, string? Org = null);

/// <summary>
/// HTTPS probes sent through the local mihomo port exactly the way OpenCode sends its traffic: CONNECT with the
/// per-launch proxy credentials, no local DNS resolution of the target, normal certificate validation.
/// </summary>
public static class ProxyProbe
{
    private static readonly Regex Status200 = new(@"^HTTP/1\.[01] 200\b", RegexOptions.Compiled);

    public static HttpClient CreateClient(RuntimeSecrets secrets)
    {
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{secrets.ProxyUser}:{secrets.ProxyPassword}"));
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, secrets.MixedPort), ct).ConfigureAwait(false);
                    var stream = new NetworkStream(socket, ownsSocket: true);
                    var target = $"{ctx.DnsEndPoint.Host}:{ctx.DnsEndPoint.Port}";
                    var request = $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nProxy-Authorization: Basic {auth}\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);
                    var status = await ReadStatusLineAsync(stream, ct).ConfigureAwait(false);
                    if (!Status200.IsMatch(status))
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                        throw new HttpRequestException("本地代理拒绝了 CONNECT：" + status);
                    }
                    return stream;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Egress IP, country and Cloudflare colo, seen from the LLM path.</summary>
    public static async Task<(string? Ip, string? Country, string? Colo)> GetEgressAsync(HttpClient http, CancellationToken ct)
    {
        var text = await http.GetStringAsync($"https://{RuntimeConfigBuilder.TraceDomain}/cdn-cgi/trace", ct).ConfigureAwait(false);
        string? ip = null, loc = null, colo = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ip=", StringComparison.Ordinal)) ip = line[3..];
            else if (line.StartsWith("loc=", StringComparison.Ordinal)) loc = line[4..];
            else if (line.StartsWith("colo=", StringComparison.Ordinal)) colo = line[5..];
        }
        return (ip, loc, colo);
    }

    /// <summary>Best-effort region / city / network operator of the egress IP (ipinfo.io, through the LLM path).</summary>
    public static async Task<(string? Region, string? City, string? Org)> TryGetGeoAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var json = await http.GetStringAsync($"https://{RuntimeConfigBuilder.GeoDomain}/json", cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            string? Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("region"), Get("city"), Get("org"));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (null, null, null);
        }
    }

    public static async Task<int> GetAnthropicStatusAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("https://api.anthropic.com/v1/models", ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    private static async Task<string> ReadStatusLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false) == 0)
                throw new IOException("本地代理在 CONNECT 应答前关闭了连接");
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
        }
        var head = Encoding.ASCII.GetString(buffer.ToArray());
        var end = head.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? head[..end] : head;
    }
}

/// <summary>Pre-flight check: node latency, egress IP / country / location and Anthropic reachability, all through the LLM path.</summary>
public static class SelfCheck
{
    public const string DelayUrl = "https://www.gstatic.com/generate_204";


    public static async Task<SelfCheckResult> RunAsync(MihomoApi api, string group, RuntimeSecrets secrets, AppSettings settings, CancellationToken ct)
    {
        var node = await api.TryGetGroupNowAsync(group, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(node)) return new(false, "模型分组没有选中的节点", null, null, null, null, null);
        using var http = ProxyProbe.CreateClient(secrets);
        http.Timeout = TimeSpan.FromSeconds(8);
        var delayTask = api.GetDelayAsync(node, DelayUrl, 8000, ct);
        async Task<(string? Ip, string? Country, string? Colo)> Egress()
        {
            try { return await ProxyProbe.GetEgressAsync(http, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return (null, null, null); }
        }
        async Task<int?> Reachability()
        {
            try { return await ProxyProbe.GetAnthropicStatusAsync(http, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        }
        var egressTask = Egress();
        var geoTask = ProxyProbe.TryGetGeoAsync(http, ct);
        var reachTask = Reachability();
        await Task.WhenAll(delayTask, egressTask, geoTask, reachTask).ConfigureAwait(false);
        var delay = await delayTask.ConfigureAwait(false);
        var (ip, country, colo) = await egressTask.ConfigureAwait(false);
        var (region, city, org) = await geoTask.ConfigureAwait(false);
        var status = await reachTask.ConfigureAwait(false);
        var warnings = new List<string>();
        if (delay is null) warnings.Add("节点测速暂不可用");
        if (ip is null) warnings.Add("出口信息暂不可用");
        else if (!settings.AllowedEgressCountries.Contains(country ?? "", StringComparer.OrdinalIgnoreCase))
            warnings.Add($"出口地域 {GeoText.Country(country)} 不在偏好列表");
        if (status is not (200 or 401)) warnings.Add(status is null ? "Anthropic 连通性尚未确认" : $"Anthropic 返回 HTTP {status}");
        return new(!settings.StrictStartupChecks || warnings.Count == 0,
            warnings.Count == 0 ? "连接检查通过" : string.Join("；", warnings), node, delay, country, colo, status, ip,
            GeoText.Location(country, region, city), org);
    }
}
