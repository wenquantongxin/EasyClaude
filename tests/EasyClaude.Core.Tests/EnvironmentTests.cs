using System.Net.NetworkInformation;
using EasyClaude.Core;

namespace EasyClaude.Core.Tests;

public class RouteInspectorTests
{
    [Theory]
    [InlineData("Mihomo", "Mihomo Tunnel", NetworkInterfaceType.Unknown)]                 // Clash Verge TUN
    [InlineData("Meta", "Meta Tunnel", NetworkInterfaceType.Unknown)]                     // Clash Meta TUN
    [InlineData("wg0", "WireGuard Tunnel", NetworkInterfaceType.Unknown)]
    [InlineData("以太网 2", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet)]       // OpenVPN
    [InlineData("singbox_tun", "sing-box TUN", NetworkInterfaceType.Unknown)]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown)]
    [InlineData("Teredo", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel)]
    public void Tunnel_like_adapters_are_flagged(string name, string description, NetworkInterfaceType type) =>
        Assert.True(RouteInspector.IsTunnelLike(name, description, type));

    [Theory]
    [InlineData("WLAN", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211)]
    [InlineData("以太网", "Realtek PCIe GbE Family Controller", NetworkInterfaceType.Ethernet)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("宽带连接", "WAN Miniport (PPPOE)", NetworkInterfaceType.Ppp)]                // PPPoE dial-up is a real uplink
    public void Physical_and_dialup_adapters_are_not_flagged(string name, string description, NetworkInterfaceType type) =>
        Assert.False(RouteInspector.IsTunnelLike(name, description, type));

    [Fact]
    public void Conflict_message_names_adapter_and_tells_what_to_switch_off()
    {
        var message = new RouteConflict("Mihomo", "Mihomo Tunnel", ["Clash Verge"]).Message;
        Assert.Contains("Mihomo", message);
        Assert.Contains("Clash Verge", message);
        Assert.Contains("系统代理可以保持开启", message);
    }
}

public class EgressHistoryTests
{
    private static readonly EgressRecord Previous =
        new("JP-US-Reality", "198.51.100.1", "US", "美国 California Los Angeles", new DateTimeOffset(2026, 10, 9, 20, 0, 0, TimeSpan.FromHours(8)));

    [Fact]
    public void First_run_is_recorded() =>
        Assert.Equal(EgressConsistency.FirstRecord, EgressHistory.Compare(null, "198.51.100.1").Consistency);

    [Fact]
    public void Same_ip_is_consistent() =>
        Assert.Equal(EgressConsistency.Same, EgressHistory.Compare(Previous, "198.51.100.1").Consistency);

    [Fact]
    public void Different_ip_is_flagged_with_previous_details()
    {
        var comparison = EgressHistory.Compare(Previous, "198.51.100.2");
        Assert.Equal(EgressConsistency.Changed, comparison.Consistency);
        Assert.Contains("198.51.100.1", comparison.Previous);
        Assert.Contains("JP-US-Reality", comparison.Previous);
    }

    [Fact]
    public void Unknown_current_ip_is_not_judged() =>
        Assert.Equal(EgressConsistency.Unknown, EgressHistory.Compare(Previous, null).Consistency);

    [Fact]
    public void History_round_trips_through_disk()
    {
        var file = Path.Combine(Path.GetTempPath(), "easyclaude-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new EgressHistory(file);
            Assert.Null(history.Load());
            history.Save(Previous);
            Assert.Equal(Previous, history.Load());
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class AppPathsTests
{
    [Fact]
    public void Root_is_the_nearest_ancestor_containing_the_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "ec-root-" + Guid.NewGuid().ToString("N"));
        var deep = Path.Combine(root, "src", "EasyClaude", "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(root, AppPaths.RootMarker), string.Empty);
        try
        {
            Assert.Equal(root, AppPaths.FindRoot(deep + Path.DirectorySeparatorChar));
            var paths = new AppPaths(root);
            Assert.Equal(Path.Combine(root, "bin", "easyclaude-mihomo.exe"), paths.PrivateMihomo);
            Assert.Equal(Path.Combine(root, "bin", "geo"), paths.GeoDir);
            Assert.Equal(Path.Combine(root, "data", "opencode", "data"), paths.XdgData);
            Assert.Equal(Path.Combine(root, "data", "logs", "easyclaude.log"), paths.LogFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Without_marker_the_start_directory_is_the_root()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ec-nomarker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.Equal(dir, AppPaths.FindRoot(dir)); }
        finally { Directory.Delete(dir); }
    }
}

public class GeoTextTests
{
    [Fact]
    public void Location_combines_country_region_and_city() =>
        Assert.Equal("美国 California Los Angeles", GeoText.Location("US", "California", "Los Angeles"));

    [Fact]
    public void Location_falls_back_to_country() => Assert.Equal("日本", GeoText.Location("JP", null, null));

    [Fact]
    public void Unknown_country_codes_are_shown_as_is() => Assert.Equal("ZZ", GeoText.Country("ZZ"));
}
