using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace EasyClaude.Core;

/// <summary>Per-launch secrets of the local mihomo instance. Only ever written to the short-lived runtime config.</summary>
public sealed record RuntimeSecrets(int MixedPort, string ProxyUser, string ProxyPassword, string PipeName)
{
    public string ProxyUrl => $"http://{ProxyUser}:{ProxyPassword}@127.0.0.1:{MixedPort}";
    public string PipePath => @"\\.\pipe\" + PipeName;
    public override string ToString() => $"RuntimeSecrets {{ MixedPort = {MixedPort}, PipeName = {PipeName} }}";
}

public sealed record BuildOptions(IReadOnlyList<string> LlmDomains, IReadOnlyList<string> BlockedCidrs, string LogLevel = "warning");

public sealed record ProfileSummary(IReadOnlyList<string> Nodes, int GroupCount, int RuleCount)
{
    public override string ToString() => $"{Nodes.Count} 个节点、{GroupCount} 个分组、{RuleCount} 条规则";
}

public sealed record BuildResult(
    string Yaml, string LlmGroup, IReadOnlyList<string> Nodes, IReadOnlyList<string> PrependedRules,
    int NoResolveAdded, bool MatchAppended, IReadOnlyList<string> KeptBlocks, IReadOnlyList<string> DroppedBlocks,
    IReadOnlyList<string> NodeServers)
{
    // Yaml (credentials) and NodeServers (node addresses) must never appear in ToString().
    public override string ToString() =>
        $"group={LlmGroup} nodes={Nodes.Count} prepended={PrependedRules.Count} no-resolve-added={NoResolveAdded} " +
        $"match-appended={MatchAppended} kept=[{string.Join(",", KeptBlocks)}] dropped=[{string.Join(",", DroppedBlocks)}]";
}

/// <summary>
/// Builds the hardened runtime mihomo config. Original scalar values and styles are preserved by the YAML representation tree; Easy Claude owns inbound, controller, groups and rule order.
/// </summary>
public static class RuntimeConfigBuilder
{
    /// <summary>Claude / Anthropic domains, each a DOMAIN-SUFFIX rule into the LLM group (subdomains included).</summary>
    public static readonly IReadOnlyList<string> ClaudeDomains =
    [
        // core
        "anthropic.com",                      // main site, API (api.anthropic.com), statsig.anthropic.com …
        "claude.ai",                          // web app
        "claude.com",                         // alternate domain
        "clau.de",                            // short links (redirects)
        "claudemcpclient.com",                // MCP client traffic
        "claudemcpcontent.com",               // MCP content delivery
        "claudeusercontent.com",              // user content / attachments
        "anthropic-api.com",
        // CDN and infrastructure
        "cdn.anthropic.com",                  // covered by anthropic.com, listed for completeness
        "anthropic.com.cdn.cloudflare.net",   // Cloudflare CDN (CNAME target)
        "servd-anthropic-website.b-cdn.net",  // BunnyCDN static assets
        // authentication and content
        "anthropic.auth0.com",                // OAuth login
        "anthropic-com.ghost.io",             // blog / CMS
    ];

    /// <summary>OpenCode's own catalog and services.</summary>
    public static readonly IReadOnlyList<string> OpenCodeDomains = ["opencode.ai", "models.dev"];

    public static readonly IReadOnlyList<string> DefaultLlmDomains = [.. ClaudeDomains, .. OpenCodeDomains];

    /// <summary>Egress IP / country (Cloudflare trace).</summary>
    public const string TraceDomain = "www.cloudflare.com";

    /// <summary>Egress region / city / operator (best effort).</summary>
    public const string GeoDomain = "ipinfo.io";

    /// <summary>Self-check endpoints; routed through the LLM group exactly like the API itself.</summary>
    public static readonly IReadOnlyList<string> SelfCheckDomains = [TraceDomain, GeoDomain];


    private static readonly HashSet<string> IpRuleTypes = new(StringComparer.OrdinalIgnoreCase)
        { "IP-CIDR", "IP-CIDR6", "IP-SUFFIX", "IP-ASN", "GEOIP" };
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "port", "socks-port", "redir-port", "tproxy-port", "mixed-port", "allow-lan", "bind-address",
        "authentication", "skip-auth-prefixes", "lan-allowed-ips", "lan-disallowed-ips",
        "external-controller", "external-controller-tls", "external-controller-unix", "external-controller-pipe",
        "external-controller-cors", "external-doh-server", "secret", "external-ui", "external-ui-url", "external-ui-name",
        "tun", "listeners", "tunnels", "ebpf", "iptables", "interface-name", "routing-mark", "tls", "ntp",
        "mode", "log-level", "ipv6", "geo-auto-update", "profile", "unified-delay", "proxy-groups", "rules",
        "proxy-providers", "rule-providers", "geox-url",
    };

    public static ProfileSummary Inspect(string yaml)
    {
        var root = Parse(yaml);
        var names = ReadNodeNames(root);
        ValidateHosts(root, DefaultLlmDomains);
        if (Get(root, "proxy-providers") is not null || Get(root, "rule-providers") is not null)
            throw new InvalidDataException("请导入包含内联节点和规则的配置；目前不支持 proxy-providers / rule-providers 订阅。");
        return new(names, Seq(root, "proxy-groups")?.Children.Count ?? 0, Seq(root, "rules")?.Children.Count ?? 0);
    }

    public static BuildResult Build(string yaml, RuntimeSecrets secrets, BuildOptions options, bool downloadsOnly = false)
    {
        var summary = Inspect(yaml);
        var root = Parse(yaml);
        ValidateHosts(root, options.LlmDomains);
        var originalKeys = root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ToList();
        var nodes = summary.Nodes;
        var servers = Seq(root, "proxies")!.Children.OfType<YamlMappingNode>()
            .Select(p => Scalar(p, "server")).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().Trim('[', ']')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var groups = Seq(root, "proxy-groups") ?? new YamlSequenceNode();
        var taken = nodes.Concat(groups.Children.OfType<YamlMappingNode>().Select(g => Scalar(g, "name") ?? ""))
            .ToHashSet(StringComparer.Ordinal);
        var llm = "LLM";
        while (taken.Contains(llm)) llm += "-Egress";
        var prepended = new List<string>();
        foreach (var cidr in options.BlockedCidrs)
            prepended.Add($"{(cidr.Contains(':') ? "IP-CIDR6" : "IP-CIDR")},{cidr},REJECT,no-resolve");
        foreach (var domain in options.LlmDomains) prepended.Add($"DOMAIN-SUFFIX,{domain},{llm}");
        foreach (var domain in SelfCheckDomains) prepended.Add($"DOMAIN,{domain},{llm}");
        var rules = new List<string>();
        var noResolveAdded = 0;
        var hasMatch = false;
        if (!downloadsOnly)
        {
            foreach (var item in Seq(root, "rules")?.Children ?? [])
            {
                if (item is not YamlScalarNode { Value: { } value }) throw new InvalidDataException("rules 中存在非字符串条目");
                var rule = value.Trim();
                var parts = rule.Split(',').Select(p => p.Trim()).ToArray();
                if (parts[0].Equals("MATCH", StringComparison.OrdinalIgnoreCase)) hasMatch = true;
                if (parts.Length >= 3 && IpRuleTypes.Contains(parts[0]) && !parts.Skip(3).Contains("no-resolve", StringComparer.OrdinalIgnoreCase))
                {
                    rule += ",no-resolve";
                    noResolveAdded++;
                }
                rules.Add(rule);
            }
        }
        if (!hasMatch) rules.Add($"MATCH,{llm}");
        foreach (var key in Dropped) root.Children.Remove(new YamlScalarNode(key));
        if (Get(root, "dns") is YamlMappingNode dns) dns.Children.Remove(new YamlScalarNode("listen"));
        if (downloadsOnly)
        {
            // Bootstrap uses no GeoIP rules; keep groups needed by a node's dialer-proxy.
            var keep = new HashSet<string> { "proxies", "hosts" };
            foreach (var key in root.Children.Keys.OfType<YamlScalarNode>().ToList())
                if (!keep.Contains(key.Value!)) root.Children.Remove(key);
        }
        void Set(string key, string value, ScalarStyle style = ScalarStyle.Plain) =>
            root.Children[new YamlScalarNode(key)] = new YamlScalarNode(value) { Style = style };
        Set("mixed-port", secrets.MixedPort.ToString(CultureInfo.InvariantCulture));
        Set("allow-lan", "false"); Set("bind-address", "127.0.0.1", ScalarStyle.DoubleQuoted);
        root.Children[new YamlScalarNode("authentication")] = Strings([$"{secrets.ProxyUser}:{secrets.ProxyPassword}"]);
        root.Children[new YamlScalarNode("skip-auth-prefixes")] = new YamlSequenceNode();
        Set("external-controller", "", ScalarStyle.DoubleQuoted);
        Set("external-controller-pipe", secrets.PipePath, ScalarStyle.DoubleQuoted);
        Set("mode", "rule"); Set("log-level", options.LogLevel); Set("ipv6", "false");
        Set("geo-auto-update", "false"); Set("unified-delay", "true");
        root.Children[new YamlScalarNode("profile")] = new YamlMappingNode
        {
            { "store-selected", "false" }, { "store-fake-ip", "false" },
        };
        var llmGroup = new YamlMappingNode
        {
            { "name", new YamlScalarNode(llm) { Style = ScalarStyle.DoubleQuoted } },
            { "type", "select" }, { "proxies", Strings(nodes) },
        };
        groups.Children.Insert(0, llmGroup);
        root.Children[new YamlScalarNode("proxy-groups")] = groups;
        root.Children[new YamlScalarNode("rules")] = Strings(prepended.Concat(rules));
        NormalizeCollections(root, new HashSet<YamlNode>(ReferenceEqualityComparer.Instance));
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        var output = writer.ToString().Replace("\r\n", "\n");
        var parsed = Parse(output);
        if (Scalar(parsed, "allow-lan") != "false" || Scalar(parsed, "bind-address") != "127.0.0.1" ||
            Scalar(parsed, "external-controller") != "" || Get(parsed, "tunnels") is not null)
            throw new InvalidDataException("生成配置的本地入口不正确");
        return new(output, llm, nodes, prepended, noResolveAdded, !hasMatch,
            originalKeys.Where(k => !Dropped.Contains(k)).ToList(), originalKeys.Where(Dropped.Contains).ToList(), servers);
    }

    private static YamlSequenceNode Strings(IEnumerable<string> values) =>
        new(values.Select(v => (YamlNode)new YamlScalarNode(v) { Style = ScalarStyle.DoubleQuoted }));

    private static void NormalizeCollections(YamlNode node, HashSet<YamlNode> visited)
    {
        if (!visited.Add(node)) return;
        if (node is YamlMappingNode map)
        {
            map.Style = MappingStyle.Block;
            foreach (var child in map.Children) { NormalizeCollections(child.Key, visited); NormalizeCollections(child.Value, visited); }
        }
        else if (node is YamlSequenceNode sequence)
        {
            sequence.Style = SequenceStyle.Block;
            foreach (var child in sequence.Children) NormalizeCollections(child, visited);
        }
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml.TrimStart('\uFEFF'))); }
        catch (YamlException ex) { throw new InvalidDataException($"YAML 解析失败（第 {ex.Start.Line} 行）", ex); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("配置文件必须是单个 YAML 映射文档");
        if (root.Children.Keys.Any(k => k is not YamlScalarNode)) throw new InvalidDataException("顶层配置键必须是字符串");
        return root;
    }
    private static YamlNode? Get(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static YamlSequenceNode? Seq(YamlMappingNode map, string key) => Get(map, key) as YamlSequenceNode;
    private static string? Scalar(YamlMappingNode map, string key) => (Get(map, key) as YamlScalarNode)?.Value;

    private static List<string> ReadNodeNames(YamlMappingNode root)
    {
        var proxies = Seq(root, "proxies");
        if (proxies is null || proxies.Children.Count == 0)
            throw new InvalidDataException("需要包含内联 proxies 节点；目前不支持只有 proxy-providers 的配置。");
        var names = new List<string>();
        foreach (var item in proxies.Children)
        {
            if (item is not YamlMappingNode proxy || string.IsNullOrWhiteSpace(Scalar(proxy, "name")) || string.IsNullOrWhiteSpace(Scalar(proxy, "type")))
                throw new InvalidDataException("每个节点必须包含 name 和 type");
            var name = Scalar(proxy, "name")!;
            var type = Scalar(proxy, "type")!;
            if (type.Equals("direct", StringComparison.OrdinalIgnoreCase) || type.StartsWith("reject", StringComparison.OrdinalIgnoreCase) ||
                name.ToUpperInvariant() is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE")
                throw new InvalidDataException("模型节点必须是代理类型，不能使用 DIRECT / REJECT");
            if (names.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("节点名称重复");
            names.Add(name);
        }
        return names;
    }

    private static void ValidateHosts(YamlMappingNode root, IReadOnlyList<string> domains)
    {
        if (Get(root, "hosts") is not YamlMappingNode hosts) return;
        foreach (var key in hosts.Children.Keys.OfType<YamlScalarNode>())
        {
            var host = (key.Value ?? "").Trim().TrimStart('+', '*', '.').ToLowerInvariant();
            if (domains.Concat(SelfCheckDomains).Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal) ||
                    d.EndsWith("." + host, StringComparison.Ordinal)))
                throw new InvalidDataException("hosts 不能覆盖模型或连通性检测域名");
        }
    }
}
