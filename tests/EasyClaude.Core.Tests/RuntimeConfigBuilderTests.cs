using EasyClaude.Core;
using YamlDotNet.RepresentationModel;

namespace EasyClaude.Core.Tests;

public class RuntimeConfigBuilderTests
{
    // Fake profile only (documentation IP range, zero UUID). Never put real node data in tests.
    private const string SampleRaw = """
mode: Rule
log-level: info
port: 7890
allow-lan: true
external-controller: 0.0.0.0:9090
secret: abc
tun:
  enable: true
dns:
  enable: true
  nameserver:
    - 223.5.5.5
proxies:
  - name: "Node-A"
    type: vless
    server: 203.0.113.10
    port: 443
    uuid: 00000000-0000-0000-0000-000000000000
    short-id: "0123"
    password: on
proxy-groups:
  - name: Proxy
    type: select
    proxies:
      - Node-A
      - DIRECT
rules:
  - DOMAIN-SUFFIX,anthropic.com,Proxy
  - IP-CIDR,192.168.0.0/16,DIRECT
  - GEOIP,CN,DIRECT,no-resolve
  - MATCH,Proxy

""";

    private const string GroupsBlock =
        "proxy-groups:\n  - name: Proxy\n    type: select\n    proxies:\n      - Node-A\n      - DIRECT\n";

    private static readonly string Sample = SampleRaw.Replace("\r\n", "\n");
    private static readonly RuntimeSecrets Secrets = new(17890, "ec", "0123456789abcdef0123456789abcdef", "easyclaude-mihomo-test");
    private static readonly BuildOptions Options = new(RuntimeConfigBuilder.DefaultLlmDomains, ["192.168.1.110/32"], "warning");

    private static YamlMappingNode Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? S(YamlMappingNode map, string key) => (Get(map, key) as YamlScalarNode)?.Value;

    private static List<string> Rules(string yaml) =>
        ((YamlSequenceNode)Get(Load(yaml), "rules")!).Children.Cast<YamlScalarNode>().Select(x => x.Value!).ToList();

    [Fact]
    public void Sample_contains_expected_groups_block() => Assert.Contains(GroupsBlock, Sample);

    [Fact]
    public void Node_scalar_values_and_quoting_are_preserved()
    {
        var yaml = RuntimeConfigBuilder.Build(Sample, Secrets, Options).Yaml;
        var root = Load(yaml);
        var proxy = Assert.IsType<YamlMappingNode>(Assert.IsType<YamlSequenceNode>(Get(root, "proxies")).Children.Single());
        Assert.Equal("Node-A", S(proxy, "name"));
        Assert.Equal("0123", S(proxy, "short-id"));
        Assert.Equal("on", S(proxy, "password"));
        Assert.Equal(YamlDotNet.Core.ScalarStyle.DoubleQuoted, Assert.IsType<YamlScalarNode>(Get(proxy, "short-id")).Style);
    }

    [Fact]
    public void Inbound_and_controller_are_locked_down()
    {
        var root = Load(RuntimeConfigBuilder.Build(Sample, Secrets, Options).Yaml);
        Assert.Equal("17890", S(root, "mixed-port"));
        Assert.Equal("false", S(root, "allow-lan"));
        Assert.Equal("127.0.0.1", S(root, "bind-address"));
        Assert.Equal("", S(root, "external-controller"));
        Assert.Equal(@"\\.\pipe\easyclaude-mihomo-test", S(root, "external-controller-pipe"));
        Assert.Equal("rule", S(root, "mode"));
        Assert.Equal("true", S(root, "unified-delay"));
        Assert.Null(Get(root, "port"));
        Assert.Null(Get(root, "tun"));
        Assert.Null(Get(root, "secret"));
        var auth = Assert.IsType<YamlSequenceNode>(Get(root, "authentication"));
        Assert.Equal("ec:0123456789abcdef0123456789abcdef", ((YamlScalarNode)auth.Children.Single()).Value);
        Assert.Empty(Assert.IsType<YamlSequenceNode>(Get(root, "skip-auth-prefixes")).Children);
    }

    [Fact]
    public void Llm_group_is_first_and_never_contains_direct()
    {
        var root = Load(RuntimeConfigBuilder.Build(Sample, Secrets, Options).Yaml);
        var groups = (YamlSequenceNode)Get(root, "proxy-groups")!;
        var first = (YamlMappingNode)groups.Children[0];
        Assert.Equal("LLM", S(first, "name"));
        Assert.Equal("select", S(first, "type"));
        var members = ((YamlSequenceNode)Get(first, "proxies")!).Children.Cast<YamlScalarNode>().Select(x => x.Value).ToList();
        Assert.Equal(new[] { "Node-A" }, members);
        Assert.Equal("Proxy", S((YamlMappingNode)groups.Children[1], "name"));
    }

    [Fact]
    public void Rules_are_prepended_and_ip_rules_get_no_resolve()
    {
        var result = RuntimeConfigBuilder.Build(Sample, Secrets, Options);
        var rules = Rules(result.Yaml);
        Assert.Equal("IP-CIDR,192.168.1.110/32,REJECT,no-resolve", rules[0]);
        Assert.Equal("DOMAIN-SUFFIX,anthropic.com,LLM", rules[1]);
        Assert.Contains("DOMAIN-SUFFIX,models.dev,LLM", rules);
        Assert.Contains("DOMAIN,www.cloudflare.com,LLM", rules);
        Assert.Contains("IP-CIDR,192.168.0.0/16,DIRECT,no-resolve", rules);
        Assert.Contains("GEOIP,CN,DIRECT,no-resolve", rules);
        Assert.DoesNotContain(rules, r => r.EndsWith("no-resolve,no-resolve", StringComparison.Ordinal));
        Assert.Equal("MATCH,Proxy", rules[^1]);
        Assert.Equal(1, result.NoResolveAdded);
        Assert.False(result.MatchAppended);
    }

    [Fact]
    public void Column_zero_group_items_keep_consistent_indentation()
    {
        var yaml = Sample.Replace(GroupsBlock, "proxy-groups:\n- name: Proxy\n  type: select\n  proxies:\n  - Node-A\n  - DIRECT\n");
        Assert.Contains("\n- name: Proxy\n", yaml);
        var groups = (YamlSequenceNode)Get(Load(RuntimeConfigBuilder.Build(yaml, Secrets, Options).Yaml), "proxy-groups")!;
        Assert.Equal(2, groups.Children.Count);
    }

    [Fact]
    public void Flow_style_groups_are_reserialized_with_types()
    {
        var yaml = Sample.Replace(GroupsBlock,
            "proxy-groups: [{name: Auto, type: url-test, proxies: [Node-A], interval: 300, lazy: true, url: 'https://www.gstatic.com/generate_204'}]\n");
        var result = RuntimeConfigBuilder.Build(yaml.Replace(",Proxy", ",Auto"), Secrets, Options);
        Assert.Contains("interval: 300", result.Yaml);
        Assert.Contains("lazy: true", result.Yaml);
        var groups = (YamlSequenceNode)Get(Load(result.Yaml), "proxy-groups")!;
        Assert.Equal("Auto", S((YamlMappingNode)groups.Children[1], "name"));
    }

    [Fact]
    public void Llm_group_name_avoids_collisions()
    {
        var yaml = Sample.Replace("  - name: Proxy\n", "  - name: LLM\n").Replace(",Proxy", ",LLM");
        Assert.Equal("LLM-Egress", RuntimeConfigBuilder.Build(yaml, Secrets, Options).LlmGroup);
    }

    [Fact]
    public void Missing_match_rule_is_appended_to_llm_group()
    {
        var result = RuntimeConfigBuilder.Build(Sample.Replace("  - MATCH,Proxy\n", ""), Secrets, Options);
        Assert.True(result.MatchAppended);
        Assert.Equal("MATCH,LLM", Rules(result.Yaml)[^1]);
    }

    [Fact]
    public void Hosts_overriding_llm_domains_are_rejected()
    {
        var yaml = Sample + "hosts:\n  api.anthropic.com: 198.51.100.7\n";
        Assert.Throws<InvalidDataException>(() => RuntimeConfigBuilder.Build(yaml, Secrets, Options));
    }

    [Fact]
    public void Duplicate_top_level_keys_are_rejected()
    {
        Assert.ThrowsAny<Exception>(() => RuntimeConfigBuilder.Build(Sample + "mode: global\n", Secrets, Options));
    }

    [Fact]
    public void Profile_without_inline_proxies_is_rejected()
    {
        const string yaml = "proxy-providers:\n  p1:\n    type: http\n    url: https://example.invalid/sub\nrules:\n  - MATCH,DIRECT\n";
        var ex = Assert.Throws<InvalidDataException>(() => RuntimeConfigBuilder.Inspect(yaml));
        Assert.Contains("proxy-providers", ex.Message);
    }

    [Fact]
    public void Inspect_summarizes_profile()
    {
        var summary = RuntimeConfigBuilder.Inspect(Sample);
        Assert.Equal(new[] { "Node-A" }, summary.Nodes);
        Assert.Equal(1, summary.GroupCount);
        Assert.Equal(4, summary.RuleCount);
    }

    [Fact]
    public void Self_check_domains_are_routed_and_node_servers_exposed_only_as_data()
    {
        var result = RuntimeConfigBuilder.Build(Sample, Secrets, Options);
        var rules = Rules(result.Yaml);
        Assert.Contains("DOMAIN,www.cloudflare.com,LLM", rules);
        Assert.Contains("DOMAIN,ipinfo.io,LLM", rules);
        Assert.Equal(new[] { "203.0.113.10" }, result.NodeServers);
        Assert.DoesNotContain("203.0.113.10", result.ToString());
    }

    [Fact]
    public void ToString_never_exposes_credentials()
    {
        var result = RuntimeConfigBuilder.Build(Sample, Secrets, Options);
        Assert.Contains(Secrets.ProxyPassword, result.Yaml);
        Assert.DoesNotContain(Secrets.ProxyPassword, result.ToString());
        Assert.DoesNotContain(Secrets.ProxyPassword, Secrets.ToString());
    }
}
