using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace EasyClaude.Core;

/// <summary>
/// Portable layout. Everything lives under the Easy Claude root (the folder containing <see cref="RootMarker"/>):
/// bin\ = vendored executables and GeoIP data, data\ = settings, encrypted profile, OpenCode data, logs, WebView2 data, temp.
/// </summary>
public sealed class AppPaths
{
    public const string RootMarker = "easyclaude.root";

    public AppPaths(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public static AppPaths Default() => new(FindRoot(AppContext.BaseDirectory));

    /// <summary>Nearest ancestor of <paramref name="start"/> (inclusive) containing easyclaude.root; otherwise <paramref name="start"/>.</summary>
    public static string FindRoot(string start)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, RootMarker))) return Path.TrimEndingDirectorySeparator(dir.FullName);
        return full;
    }

    public string Root { get; }

    public string BinDir => Path.Combine(Root, "bin");
    public string GeoDir => Path.Combine(BinDir, "geo");
    public string PrivateMihomo => Path.Combine(BinDir, "easyclaude-mihomo.exe");
    public string PrivateOpenCode => Path.Combine(BinDir, "easyclaude-opencode.exe");

    public string DataDir => Path.Combine(Root, "data");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string EgressFile => Path.Combine(DataDir, "egress.json");
    public string ProfilesDir => Path.Combine(DataDir, "profiles");
    public string ProfileFile => Path.Combine(ProfilesDir, "profile.yml.dpapi");
    public string MihomoHome => Path.Combine(DataDir, "mihomo");
    public string MihomoConfig => Path.Combine(MihomoHome, "config.yaml");
    public string OpenCodeRoot => Path.Combine(DataDir, "opencode");
    public string XdgConfig => Path.Combine(OpenCodeRoot, "config");
    public string XdgData => Path.Combine(OpenCodeRoot, "data");
    public string XdgState => Path.Combine(OpenCodeRoot, "state");
    public string XdgCache => Path.Combine(OpenCodeRoot, "cache");
    public string OpenCodeConfigFile => Path.Combine(XdgConfig, "opencode", "opencode.jsonc");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string LogFile => Path.Combine(LogsDir, "easyclaude.log");
    public string WebView2Data => Path.Combine(DataDir, "webview2");
    public string TempDir => Path.Combine(DataDir, "tmp");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BinDir);
        LockDown(DataDir); // lock first, so everything created below inherits the private ACL
        LockDown(BinDir);
        foreach (var dir in new[]
                 {
                     ProfilesDir, MihomoHome, XdgConfig, XdgData, XdgState, XdgCache,
                     Path.GetDirectoryName(OpenCodeConfigFile)!, LogsDir, WebView2Data, TempDir,
                 })
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Restricts a directory to the current user, SYSTEM and Administrators (inheritance off). Secondary drives often grant
    /// Authenticated Users modify rights: data\ holds credentials and bin\ holds the executables Easy Claude runs.
    /// </summary>
    public static void LockDown(string directory)
    {
        var info = new DirectoryInfo(directory);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法获取当前 Windows 用户");
        var allowed = new[] { user.Value, "S-1-5-18", "S-1-5-32-544" };
        var existing = info.GetAccessControl();
        var rules = existing.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        if (existing.AreAccessRulesProtected && rules.Count == 3 && rules.All(r =>
                allowed.Contains(r.IdentityReference.Value) && r.AccessControlType == AccessControlType.Allow &&
                r.FileSystemRights == FileSystemRights.FullControl &&
                r.PropagationFlags == PropagationFlags.None &&
                r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) &&
            allowed.All(s => rules.Any(r => r.IdentityReference.Value == s))) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     user,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

/// <summary>User-editable settings stored in data\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Default model. Only the anthropic provider is permitted by policy.</summary>
    public string Model { get; set; } = "anthropic/claude-opus-5-5";

    /// <summary>ISO country codes accepted for the LLM egress (Cloudflare trace "loc").</summary>
    public List<string> AllowedEgressCountries { get; set; } = ["US", "JP"];

    /// <summary>Extra domains forced through the LLM group, in addition to the built-in Claude / OpenCode list.</summary>
    public List<string> LlmDomains { get; set; } = [];

    /// <summary>Built-in domains plus <see cref="LlmDomains"/>, normalized and de-duplicated (built-ins first).</summary>
    public IReadOnlyList<string> EffectiveLlmDomains() =>
        RuntimeConfigBuilder.DefaultLlmDomains.Concat(LlmDomains)
            .Select(d => d.Trim().TrimStart('+', '*', '.').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>CIDRs that are always rejected (by default the LAN Strata host).</summary>
    public List<string> BlockedCidrs { get; set; } = ["192.168.1.110/32"];

    public string? WorkDirectory { get; set; }
    public string MihomoLogLevel { get; set; } = "warning";
    public int MaxNodeDelayMs { get; set; } = 3000;

    public bool StrictStartupChecks { get; set; }
    public bool FirewallEnabled { get; set; }
    public string FirewallStatus { get; set; } = "未启用";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings LoadOrCreate(string file)
    {
        if (File.Exists(file))
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new AppSettings();
        var settings = new AppSettings();
        settings.Save(file);
        return settings;
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, true);
    }

    public string ResolveWorkDirectory() =>
        !string.IsNullOrWhiteSpace(WorkDirectory) && Directory.Exists(WorkDirectory)
            ? WorkDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// Runtime binaries come only from the root's bin\, populated by the component manager.
/// </summary>
public static class ToolLocator
{
    public static string FindMihomo(AppSettings settings, AppPaths paths) =>
        Resolve(paths.PrivateMihomo, @"bin\easyclaude-mihomo.exe");

    public static string FindOpenCode(AppSettings settings, AppPaths paths) =>
        Resolve(paths.PrivateOpenCode, @"bin\easyclaude-opencode.exe");

    private static string Resolve(string bundled, string display)
    {
        if (File.Exists(bundled)) return bundled;
        throw new FileNotFoundException($"缺少 {display}。请在程序中重试组件准备。");
    }
}
