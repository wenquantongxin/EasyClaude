using EasyClaude.Core;

namespace EasyClaude.Core.Tests;

public class ApiKeyMaskTests
{
    [Fact]
    public void Hint_shows_only_a_short_prefix_of_real_keys()
    {
        var key = "sk-ant-api03-" + new string('A', 95);
        var hint = ApiKeyMask.Hint(key);
        Assert.Equal("sk-ant-api03-AAAA…", hint);
        Assert.True(hint.Length <= 18);
    }

    [Fact]
    public void Hint_never_reveals_more_than_40_percent_of_short_keys()
    {
        var hint = ApiKeyMask.Hint("abcdefghijklmnopqrst"); // 20 chars -> 8 visible
        Assert.Equal("abcdefgh…", hint);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData("short-key", false)]
    [InlineData("sk-ant-api03-abc defghijklmnopqrstuvwxyz", false)]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz中", false)]
    public void Key_format_check(string key, bool expected) => Assert.Equal(expected, ApiKeyMask.LooksValid(key));
}

public class DomainListTests
{
    [Theory]
    [InlineData("anthropic.com")]
    [InlineData("claude.ai")]
    [InlineData("claude.com")]
    [InlineData("clau.de")]
    [InlineData("claudemcpclient.com")]
    [InlineData("claudemcpcontent.com")]
    [InlineData("claudeusercontent.com")]
    [InlineData("cdn.anthropic.com")]
    [InlineData("anthropic.com.cdn.cloudflare.net")]
    [InlineData("servd-anthropic-website.b-cdn.net")]
    [InlineData("anthropic.auth0.com")]
    [InlineData("anthropic-com.ghost.io")]
    [InlineData("opencode.ai")]
    [InlineData("models.dev")]
    public void Built_in_domains_are_routed_through_the_llm_group(string domain)
    {
        var settings = new AppSettings();
        var secrets = new RuntimeSecrets(17890, "ec", "0123456789abcdef0123456789abcdef", "pipe");
        const string yaml = "proxies:\n  - {name: Node-A, type: vless, server: 203.0.113.10, port: 443}\nrules:\n  - MATCH,DIRECT\n";
        var result = RuntimeConfigBuilder.Build(yaml, secrets, new BuildOptions(settings.EffectiveLlmDomains(), []));
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        stream.Load(new StringReader(result.Yaml));
        var map = (YamlDotNet.RepresentationModel.YamlMappingNode)stream.Documents[0].RootNode;
        var rules = (YamlDotNet.RepresentationModel.YamlSequenceNode)map.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("rules")];
        Assert.Contains(rules.Children.Cast<YamlDotNet.RepresentationModel.YamlScalarNode>(), rule => rule.Value == $"DOMAIN-SUFFIX,{domain},LLM");
    }

    [Fact]
    public void Extra_settings_domains_are_merged_and_deduplicated()
    {
        var settings = new AppSettings { LlmDomains = ["Example.COM", ".anthropic.com", "+.claude.ai", " "] };
        var domains = settings.EffectiveLlmDomains();
        Assert.Contains("example.com", domains);
        Assert.Single(domains, d => d == "anthropic.com");
        Assert.Single(domains, d => d == "claude.ai");
        Assert.DoesNotContain(domains, d => d.Length == 0);
        Assert.Equal("anthropic.com", domains[0]); // built-ins first
    }
}
