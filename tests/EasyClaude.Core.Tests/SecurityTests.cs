using EasyClaude.Core;

namespace EasyClaude.Core.Tests;

public class RedactorTests
{
    [Fact]
    public void Public_ips_are_redacted_but_loopback_and_lan_are_kept()
    {
        var r = new Redactor();
        Assert.Equal("dial tcp [ip]:443 via 127.0.0.1:7890 lan 192.168.1.110",
            r.Redact("dial tcp 203.0.113.10:443 via 127.0.0.1:7890 lan 192.168.1.110"));
    }

    [Fact]
    public void Secrets_keys_and_credentials_are_redacted()
    {
        var r = new Redactor();
        r.AddSecret("hunter2-secret-value");
        var s = r.Redact("pw=hunter2-secret-value key=sk-ant-api03-abcDEF_123 url=http://ec:pa55word@127.0.0.1:1234/x Authorization: Basic ZWM6cGFzcw==");
        Assert.DoesNotContain("hunter2-secret-value", s);
        Assert.DoesNotContain("sk-ant-api03", s);
        Assert.DoesNotContain("pa55word", s);
        Assert.DoesNotContain("ZWM6cGFzcw==", s);
    }

    [Fact]
    public void Ansi_sequences_are_stripped()
    {
        Assert.Equal("Error: invalid x-api-key", new Redactor().Redact("\u001b[91m\u001b[1mError: \u001b[0minvalid x-api-key"));
    }
}

public class ChildEnvironmentTests
{
    [Fact]
    public void OpenCode_environment_is_scrubbed_and_pinned_to_local_proxy()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows",
            ["HTTP_PROXY"] = "http://127.0.0.1:7897",
            ["https_proxy"] = "http://127.0.0.1:7897",
            ["ALL_PROXY"] = "socks5://127.0.0.1:7897",
            ["OPENCODE_SESSION_ID"] = "abc",
            ["OPENCODE"] = "1",
            ["XDG_DATA_HOME"] = @"C:\elsewhere",
            ["NODE_TLS_REJECT_UNAUTHORIZED"] = "0",
            ["NODE_EXTRA_CA_CERTS"] = @"C:\evil.pem",
        };
        var paths = new AppPaths(@"C:\EC");
        var secrets = new RuntimeSecrets(17890, "ec", "pw0123456789", "pipe");

        ChildEnvironment.Apply(env, ChildEnvironment.ForOpenCode(paths, secrets, "serverpw"));

        Assert.Equal(@"C:\Windows", env["PATH"]);
        Assert.Equal("http://ec:pw0123456789@127.0.0.1:17890", env["HTTPS_PROXY"]);
        Assert.Equal("http://ec:pw0123456789@127.0.0.1:17890", env["HTTP_PROXY"]);
        Assert.Equal("localhost,127.0.0.1,::1", env["NO_PROXY"]);
        Assert.False(env.ContainsKey("ALL_PROXY"));
        Assert.False(env.ContainsKey("OPENCODE_SESSION_ID"));
        Assert.False(env.ContainsKey("OPENCODE"));
        Assert.False(env.ContainsKey("NODE_TLS_REJECT_UNAUTHORIZED"));
        Assert.False(env.ContainsKey("NODE_EXTRA_CA_CERTS"));
        Assert.Equal("serverpw", env["OPENCODE_SERVER_PASSWORD"]);
        Assert.Equal(Path.Combine(@"C:\EC", "data", "opencode", "data"), env["XDG_DATA_HOME"]);
        Assert.Equal(Path.Combine(@"C:\EC", "data", "tmp"), env["TEMP"]);
        Assert.Equal(Path.Combine(@"C:\EC", "data", "tmp"), env["TMP"]);
        Assert.DoesNotContain("192.168.1.110", env["NO_PROXY"]); // the Strata host is never bypassed
    }
}
