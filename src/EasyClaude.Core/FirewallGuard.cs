using System.Reflection;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EasyClaude.Core;

public sealed record FirewallState(bool RulesEnabled, bool Effective, string Message, bool RulesPresent = false);

/// <summary>Optional Windows Firewall rules scoped only to this installation's OpenCode executable.</summary>
public static class FirewallGuard
{
    public const string IPv4OutsideLoopback = "0.0.0.0-126.255.255.255,128.0.0.0-255.255.255.255";
    public const string IPv6OutsideLoopback = "::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";
    public static string InstallationId(string root) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())))[..16];
    public static string MutexName(string root) => @"Local\EasyClaude." + InstallationId(root);
    private static string Name(AppPaths paths, string family) => "EasyClaude." + InstallationId(paths.Root) + "." + family;

    public static FirewallState Read(AppPaths paths)
    {
        object? policy = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic p = policy!;
            dynamic rules = p.Rules;
            var present = 0;
            var valid = 0;
            foreach (var family in new[] { "IPv4", "IPv6" })
            {
                object? rule = null;
                try
                {
                    rule = rules.Item(Name(paths, family));
                    present++;
                    dynamic r = rule;
                    if (!(bool)r.Enabled || (int)r.Direction != 2 || (int)r.Action != 0 ||
                        (int)r.Protocol != 256 || (int)r.Profiles != int.MaxValue ||
                        !string.Equals((string)r.ApplicationName, paths.PrivateOpenCode, StringComparison.OrdinalIgnoreCase) ||
                        !SameAddressRanges((string)r.RemoteAddresses, family == "IPv4" ? IPv4OutsideLoopback : IPv6OutsideLoopback))
                        continue;
                    valid++;
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                finally { Release(rule); }
            }
            if (valid != 2) return new(false, false, present == 0 ? "未启用" : "规则未完整启用，请修复或移除", present > 0);
            var active = (int)p.CurrentProfileTypes;
            foreach (var profile in new[] { 1, 2, 4 })
            {
                if ((active & profile) == 0) continue;
                var enabled = (bool)policy!.GetType().InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, policy, [profile])!;
                if (!enabled) return new(true, false, "规则已安装，但当前网络的 Windows 防火墙未启用", true);
            }
            return new(true, true, "已生效：OpenCode 仅可连接本机代理", true);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return new(false, false, "未启用"); }
        catch (COMException) { return new(false, false, "未启用（或系统策略不可访问）"); }
        catch (Exception ex) { return new(false, false, "无法确认防火墙状态：" + ex.Message); }
        finally { Release(policy); }
    }

    public static bool SameAddressRanges(string actual, string expected)
    {
        static string Normalize(string text) => string.Join(",", text.Split(',', StringSplitOptions.TrimEntries)
            .Select(token => string.Join("-", token.Split('-', StringSplitOptions.TrimEntries)
                .Select(address => IPAddress.TryParse(address, out var parsed) ? parsed.ToString() : address)))
            .Order(StringComparer.Ordinal));
        return Normalize(actual).Equals(Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(AppPaths paths, bool enable)
    {
        if (!File.Exists(Path.Combine(paths.Root, AppPaths.RootMarker))) throw new InvalidOperationException("无效的程序根目录");
        if (enable && !File.Exists(paths.PrivateOpenCode)) throw new FileNotFoundException("请先恢复 OpenCode 组件");
        object? policy = null;
        var created = new List<string>();
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!);
            dynamic rules = ((dynamic)policy!).Rules;
            foreach (var family in new[] { "IPv4", "IPv6" })
            {
                var name = Name(paths, family);
                try { rules.Remove(name); } catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                if (!enable) continue;
                object? rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!);
                try
                {
                    dynamic r = rule!;
                    r.Name = name; r.Grouping = "Easy Claude";
                    r.Description = "Restrict this Easy Claude OpenCode instance to loopback; mihomo is unaffected.";
                    r.ApplicationName = paths.PrivateOpenCode;
                    r.Direction = 2; r.Action = 0; r.Protocol = 256; r.Profiles = int.MaxValue;
                    r.RemoteAddresses = family == "IPv4" ? IPv4OutsideLoopback : IPv6OutsideLoopback;
                    r.Enabled = true;
                    rules.Add(r);
                    created.Add(name);
                }
                finally { Release(rule); }
            }
        }
        catch
        {
            if (policy is not null)
                foreach (var name in created) try { ((dynamic)policy).Rules.Remove(name); } catch (Exception) { }
            throw;
        }
        finally { Release(policy); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
