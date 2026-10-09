using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyClaude.Core;

/// <summary>Another client's TUN / virtual adapter carries the route to the proxy node (never contains the node address).</summary>
public sealed record RouteConflict(string AdapterName, string AdapterDescription, IReadOnlyList<string> SuspectedClients)
{
    public string Message
    {
        get
        {
            var who = SuspectedClients.Count > 0 ? string.Join("、", SuspectedClients) : "某个代理或 VPN 客户端";
            return $"通往代理节点的流量正被虚拟网卡「{AdapterName}」接管，疑似 {who} 开启了 TUN／虚拟网卡模式。" +
                   "Easy Claude 的流量会被再代理一次，速度和稳定性都会变差。" +
                   "请在该客户端中关闭 TUN／虚拟网卡模式（系统代理可以保持开启），然后点“我已关闭，重新检测”。";
        }
    }
}

/// <summary>Finds out which network adapter Windows would use to reach the proxy node.</summary>
public static class RouteInspector
{
    private static readonly Regex VirtualAdapter = new(
        @"(?i)wintun|wireguard|tap-windows|\btap\b|openvpn|\bmeta\b|mihomo|clash|sing-?box|\btun\d*\b|tailscale|zerotier|" +
        @"hiddify|nekoray|nekobox|v2ray|xray|vpn|anyconnect|globalprotect|pangp|forti|nordlynx|astrill|letsvpn|\bwg\d+\b",
        RegexOptions.Compiled);

    /// <summary>True for TUN / VPN style adapters. Physical NICs, Hyper-V switches and PPPoE dial-up are not flagged.</summary>
    public static bool IsTunnelLike(string name, string description, NetworkInterfaceType type) =>
        type == NetworkInterfaceType.Tunnel || VirtualAdapter.IsMatch(name) || VirtualAdapter.IsMatch(description);

    public static async Task<RouteConflict?> CheckAsync(IEnumerable<string> nodeServers, CancellationToken ct)
    {
        foreach (var server in nodeServers)
        {
            var addresses = IPAddress.TryParse(server, out var literal) ? [literal] : await ResolveAsync(server, ct).ConfigureAwait(false);
            foreach (var address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                var nic = BestInterfaceFor(address);
                if (nic is null || !IsTunnelLike(nic.Name, nic.Description, nic.NetworkInterfaceType)) continue;
                return new RouteConflict(nic.Name, nic.Description, KnownClients.Running(nic.Name + " " + nic.Description));
            }
        }
        return null;
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        try { return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false); }
        catch (SocketException) { return []; }
    }

    private static NetworkInterface? BestInterfaceFor(IPAddress address)
    {
        if (GetBestInterface(BitConverter.ToUInt32(address.GetAddressBytes(), 0), out var index) != 0) return null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.GetIPProperties().GetIPv4Properties()?.Index == (int)index) return nic;
            }
            catch (NetworkInformationException)
            {
            }
        }
        return null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}

/// <summary>Known proxy / VPN clients, only used to make messages specific (never as the conflict criterion).</summary>
public static class KnownClients
{
    private static readonly (string Process, string Display)[] Clients =
    [
        ("clash-verge", "Clash Verge"), ("verge-mihomo", "Clash Verge"), ("clash-nyanpasu", "Clash Nyanpasu"),
        ("mihomo-party", "Mihomo Party"), ("FlClash", "FlClash"), ("Clash for Windows", "Clash for Windows"),
        ("clash-ninja", "Clash V-Ninja"), ("ninja-mihomo", "Clash V-Ninja"), ("v2rayN", "v2rayN"), ("nekoray", "NekoRay"),
        ("nekobox", "NekoBox"), ("Hiddify", "Hiddify"), ("sing-box", "sing-box"), ("Shadowsocks", "Shadowsocks"),
        ("wireguard", "WireGuard"), ("openvpn-gui", "OpenVPN"), ("openvpn", "OpenVPN"), ("tailscale-ipn", "Tailscale"),
        ("zerotier_desktop_ui", "ZeroTier"), ("vpnui", "Cisco AnyConnect"), ("PanGPA", "GlobalProtect"),
        ("FortiClient", "FortiClient"), ("ExpressVPN", "ExpressVPN"), ("NordVPN", "NordVPN"), ("Surfshark", "Surfshark"),
        ("LetsPRO", "快连 LetsVPN"), ("Astrill", "Astrill VPN"),
    ];

    /// <summary>Display names of running known clients; if none is running, a guess from the adapter name.</summary>
    public static IReadOnlyList<string> Running(string? adapterHint = null)
    {
        var found = new List<string>();
        foreach (var (process, display) in Clients)
        {
            var matches = Process.GetProcessesByName(process);
            foreach (var p in matches) p.Dispose();
            if (matches.Length > 0 && !found.Contains(display)) found.Add(display);
        }
        if (found.Count > 0 || adapterHint is null) return found;
        var guess = adapterHint switch
        {
            _ when Regex.IsMatch(adapterHint, "(?i)wintun|mihomo|meta|clash") => "Clash 系客户端",
            _ when Regex.IsMatch(adapterHint, "(?i)wireguard") => "WireGuard",
            _ when Regex.IsMatch(adapterHint, "(?i)tap|openvpn") => "OpenVPN 类客户端",
            _ when Regex.IsMatch(adapterHint, "(?i)tailscale") => "Tailscale",
            _ when Regex.IsMatch(adapterHint, "(?i)zerotier") => "ZeroTier",
            _ => null,
        };
        if (guess is not null) found.Add(guess);
        return found;
    }
}

/// <summary>
/// Kills leftovers of Easy Claude's own private binaries, matched by full image path (never by process name alone).
/// Safe because the single-instance mutex guarantees no other live Easy Claude owns such a process.
/// </summary>
public static class OrphanCleaner
{
    public static int KillByImagePath(string imagePath)
    {
        var expected = Path.GetFullPath(imagePath);
        var killed = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }
        return killed;
    }
}

public enum EgressConsistency
{
    Unknown,
    FirstRecord,
    Same,
    Changed,
}

public sealed record EgressRecord(string Node, string Ip, string? Country, string? Location, DateTimeOffset CheckedAt);

public sealed record EgressComparison(EgressConsistency Consistency, string? Previous);

/// <summary>Remembers the last observed egress so a change of the IP path is visible.</summary>
public sealed class EgressHistory(string file)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public EgressRecord? Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<EgressRecord>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(EgressRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(record, Json));
    }

    public static EgressComparison Compare(EgressRecord? previous, string? currentIp)
    {
        if (string.IsNullOrEmpty(currentIp)) return new(EgressConsistency.Unknown, null);
        if (previous is null) return new(EgressConsistency.FirstRecord, null);
        if (previous.Ip == currentIp) return new(EgressConsistency.Same, null);
        return new(EgressConsistency.Changed,
            $"{previous.Ip} · {previous.Location ?? GeoText.Country(previous.Country)}（{previous.CheckedAt.LocalDateTime:MM-dd HH:mm}，节点 {previous.Node}）");
    }
}

public static class GeoText
{
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "美国", ["JP"] = "日本", ["SG"] = "新加坡", ["HK"] = "中国香港", ["TW"] = "中国台湾", ["MO"] = "中国澳门",
        ["CN"] = "中国大陆", ["KR"] = "韩国", ["GB"] = "英国", ["DE"] = "德国", ["FR"] = "法国", ["NL"] = "荷兰",
        ["CA"] = "加拿大", ["AU"] = "澳大利亚", ["IN"] = "印度", ["RU"] = "俄罗斯", ["MY"] = "马来西亚", ["TH"] = "泰国",
        ["VN"] = "越南", ["PH"] = "菲律宾", ["ID"] = "印度尼西亚", ["TR"] = "土耳其", ["BR"] = "巴西", ["IT"] = "意大利",
        ["ES"] = "西班牙", ["CH"] = "瑞士", ["SE"] = "瑞典", ["FI"] = "芬兰", ["IE"] = "爱尔兰", ["AE"] = "阿联酋",
    };

    public static string Country(string? code) =>
        string.IsNullOrWhiteSpace(code) ? "未知" : Countries.TryGetValue(code, out var name) ? name : code;

    public static string Location(string? country, string? region, string? city)
    {
        var parts = new List<string> { Country(country) };
        if (!string.IsNullOrWhiteSpace(region)) parts.Add(region);
        if (!string.IsNullOrWhiteSpace(city) && !string.Equals(city, region, StringComparison.OrdinalIgnoreCase)) parts.Add(city);
        return string.Join(" ", parts);
    }
}
