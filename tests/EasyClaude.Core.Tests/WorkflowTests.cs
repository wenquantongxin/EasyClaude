using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using EasyClaude.Core;
using Microsoft.Extensions.Time.Testing;
using YamlDotNet.RepresentationModel;

namespace EasyClaude.Core.Tests;

public class ConfigurationWorkflowTests
{
    private const string Profile = "proxies:\n  - {name: Test, type: vless, server: 203.0.113.10, port: 443, uuid: 00000000-0000-0000-0000-000000000000}\nrules: [MATCH,DIRECT]\n";
    private static string Build(string extra) => RuntimeConfigBuilder.Build(Profile + extra,
        new(19001, "ec", "dummy-password-for-test", "test"), new(RuntimeConfigBuilder.DefaultLlmDomains, [])).Yaml;
    private static YamlMappingNode Parse(string yaml) { var s = new YamlStream(); s.Load(new StringReader(yaml)); return (YamlMappingNode)s.Documents[0].RootNode; }

    [Theory]
    [InlineData("\"tunnels\": [tcp,0.0.0.0:9090,example.invalid:80,DIRECT]\n", "tunnels")]
    [InlineData("'listeners': []\n", "listeners")]
    [InlineData("\"ntp\": {enable: true}\n", "ntp")]
    public void Quoted_top_level_owned_fields_are_removed(string extra, string field) => Assert.False(Parse(Build(extra)).Children.ContainsKey(new YamlScalarNode(field)));

    [Fact]
    public void Dns_upstreams_survive_but_listener_does_not()
    {
        var dns = (YamlMappingNode)Parse(Build("dns: {enable: true, listen: '0.0.0.0:1053', nameserver: [1.1.1.1]}\n")).Children[new YamlScalarNode("dns")];
        Assert.False(dns.Children.ContainsKey(new YamlScalarNode("listen")));
        Assert.Single(((YamlSequenceNode)dns.Children[new YamlScalarNode("nameserver")]).Children);
    }
    [Fact]
    public void Node_type_direct_cannot_enter_model_group() => Assert.Throws<InvalidDataException>(() => RuntimeConfigBuilder.Inspect(Profile.Replace("type: vless", "type: direct")));
    [Fact]
    public void Broad_hosts_wildcard_cannot_override_model_domains() => Assert.Throws<InvalidDataException>(() => Build("hosts: {'+.com': 198.51.100.10}\n"));

    [Fact]
    public async Task Import_is_independent_of_source_and_failed_validation_preserves_previous()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ec-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "original.yml");
            var destination = Path.Combine(dir, "stored.yml");
            await File.WriteAllTextAsync(source, Profile);
            var store = new ProfileStore(destination);
            var validated = false;
            await store.ImportAsync(source, (yaml, _) => { validated = yaml == Profile; return Task.CompletedTask; });
            Assert.True(validated);
            Assert.Equal(Profile, await File.ReadAllTextAsync(destination));
            File.Delete(source);
            Assert.Equal(Profile, store.LoadText());
            var relocated = Path.Combine(dir, "relocated", "profile.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
            File.Copy(destination, relocated);
            Assert.Equal(Profile, new ProfileStore(relocated).LoadText());
            var previous = await File.ReadAllBytesAsync(destination);
            await File.WriteAllTextAsync(source, Profile.Replace("Test", "Replacement"));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(source, (_, _) => throw new InvalidDataException("core rejected")));
            Assert.Equal(previous, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_validation_never_commits(bool existing)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ec-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "original.yml");
            var target = Path.Combine(dir, "stored.yml");
            await File.WriteAllTextAsync(source, Profile);
            if (existing) await File.WriteAllTextAsync(target, Profile);
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProfileStore(target).ImportAsync(source,
                (_, _) => { cts.Cancel(); return Task.CompletedTask; }, cts.Token));
            Assert.Equal(existing, File.Exists(target));
            if (existing) Assert.Equal(Profile, await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Import_normalizes_utf8_bom_and_preserves_unicode_node_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ec-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var yaml = Profile.Replace("Test", "东京节点");
            var source = Path.Combine(dir, "original.yml");
            var paths = new AppPaths(dir);
            await File.WriteAllTextAsync(source, yaml, new UTF8Encoding(true));
            var store = new ProfileStore(paths.ProfileFile);
            await store.ImportAsync(source, (_, _) => Task.CompletedTask);
            Assert.Equal("profile.yml", Path.GetFileName(paths.ProfileFile));
            Assert.Equal(yaml, store.LoadText());
            Assert.Equal(Encoding.UTF8.GetBytes(yaml), await File.ReadAllBytesAsync(paths.ProfileFile));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Invalid_yaml_never_replaces_stored_profile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ec-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "original.yml");
            var destination = Path.Combine(dir, "stored.yml");
            await File.WriteAllTextAsync(source, "proxies: [");
            await File.WriteAllTextAsync(destination, Profile);
            var store = new ProfileStore(destination);
            var validated = false;
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(source, (_, _) =>
            { validated = true; return Task.CompletedTask; }));
            Assert.False(validated);
            Assert.Equal(Profile, store.LoadText());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Default_settings_are_lightweight()
    {
        var settings = new AppSettings();
        Assert.False(settings.StrictStartupChecks);
        Assert.False(settings.FirewallEnabled);
    }
}

public class ModelTestWorkflowTests
{
    private sealed class FakeClient(params (bool Ok, int Status, string Text)[] responses) : IModelTestClient
    {
        public string? Haiku { get; set; } = "claude-haiku-test";
        public List<string> Models { get; } = [];
        public Task<string?> FindModelAsync(string providerId, string contains, CancellationToken ct) => Task.FromResult(Haiku);
        public Task<(bool Ok, int Status, string Text)> GenerateAsync(string prompt, string providerId, string modelId, CancellationToken ct)
        {
            Models.Add(modelId);
            return Task.FromResult(responses[Math.Min(Models.Count - 1, responses.Length - 1)]);
        }
    }
    [Fact]
    public async Task Haiku_success_does_not_call_default()
    {
        var client = new FakeClient((true, 200, "你好"));
        var result = await ApiKeyTester.RunAsync(client, "anthropic/claude-opus-test", true, default);
        Assert.True(result.Ok); Assert.True(result.Saved); Assert.Null(result.FallbackReason);
        Assert.Equal(new[] { "claude-haiku-test" }, client.Models);
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task Authentication_rate_limit_and_server_errors_never_fallback(int status)
    {
        var client = new FakeClient((false, status, "request failed"));
        var result = await ApiKeyTester.RunAsync(client, "anthropic/claude-opus-test", false, default);
        Assert.False(result.Ok); Assert.Single(client.Models); Assert.Null(result.FallbackReason);
    }
    [Fact]
    public async Task Model_unavailable_falls_back_exactly_once()
    {
        var client = new FakeClient((false, 404, "Model unavailable: anthropic/claude-haiku-test"), (true, 200, "你好"));
        var result = await ApiKeyTester.RunAsync(client, "anthropic/claude-opus-test", true, default);
        Assert.True(result.Ok); Assert.True(result.Saved); Assert.NotNull(result.FallbackReason);
        Assert.Equal(new[] { "claude-haiku-test", "claude-opus-test" }, client.Models);
    }
    [Fact]
    public async Task Missing_haiku_catalog_uses_default_without_paid_haiku_request()
    {
        var client = new FakeClient((true, 200, "你好")) { Haiku = null };
        var result = await ApiKeyTester.RunAsync(client, "anthropic/claude-opus-test", false, default);
        Assert.True(result.Ok); Assert.Equal("claude-opus-test", result.Model); Assert.Single(client.Models);
    }
    [Fact]
    public async Task Local_model_readiness_retries_before_fallback()
    {
        var clock = new FakeTimeProvider();
        var client = new FakeClient((false, 400, "Model unavailable"), (false, 400, "Model unavailable"), (true, 200, "你好"));
        var pending = ApiKeyTester.RunAsync(client, "anthropic/claude-opus-test", true, default, clock);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await WaitFor(() => client.Models.Count == 2);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Ok); Assert.Null(result.FallbackReason);
        Assert.All(client.Models, m => Assert.Equal("claude-haiku-test", m));
    }
    [Fact]
    public async Task Saved_key_is_reported_even_when_generation_fails()
    {
        var result = await ApiKeyTester.RunAsync(new FakeClient((false, 429, "rate limited")), "anthropic/claude-opus-test", true, default);
        Assert.True(result.Saved); Assert.False(result.Ok);
    }
    internal static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }
}

public class MonitoringTests
{
    [Fact]
    public async Task Probe_exception_does_not_end_future_latency_checks()
    {
        var clock = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var samples = new ConcurrentQueue<LatencySample>();
        var calls = 0;
        var task = LatencyMonitor.RunAsync(_ => ++calls == 1 ? throw new IOException("probe unavailable") : Task.FromResult<(string?, int?)>(("node", 120)), samples.Enqueue, clock, cts.Token);
        clock.Advance(TimeSpan.FromSeconds(60)); await ModelTestWorkflowTests.WaitFor(() => samples.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(60)); await ModelTestWorkflowTests.WaitFor(() => samples.Count == 2);
        Assert.Equal(1, samples.First().ConsecutiveFailures);
        Assert.Equal(0, samples.Last().ConsecutiveFailures);
        Assert.Equal(120, samples.Last().DelayMs);
        cts.Cancel(); await task;
    }
    [Fact]
    public async Task Latency_runs_every_sixty_seconds_and_recovers_after_failures()
    {
        var clock = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var samples = new ConcurrentQueue<LatencySample>();
        var probes = 0;
        var monitor = LatencyMonitor.RunAsync(_ => Task.FromResult<(string?, int?)>(("Node", ++probes <= 2 ? null : 100)), samples.Enqueue, clock, cts.Token);
        clock.Advance(TimeSpan.FromSeconds(59)); Assert.Empty(samples);
        clock.Advance(TimeSpan.FromSeconds(1)); await ModelTestWorkflowTests.WaitFor(() => samples.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(60)); await ModelTestWorkflowTests.WaitFor(() => samples.Count == 2);
        clock.Advance(TimeSpan.FromSeconds(60)); await ModelTestWorkflowTests.WaitFor(() => samples.Count == 3);
        Assert.Equal(new[] { 1, 2, 0 }, samples.Select(s => s.ConsecutiveFailures));
        Assert.Equal(100, samples.Last().DelayMs);
        cts.Cancel(); await monitor.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMinutes(5)); Assert.Equal(3, probes);
    }
    [Fact]
    public async Task Caller_cancellation_terminates_short_command()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var time = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"], null, ChildEnvironment.ScrubOnly, TimeSpan.FromSeconds(40), cts.Token));
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(5));
    }
}

public class DownloadTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    [Fact]
    public async Task Download_follows_https_redirect_and_finishes_exact_content()
    {
        var file = Path.Combine(Path.GetTempPath(), "ec-download-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/redirect"
            ? new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.invalid/payload") } }
            : new(HttpStatusCode.OK) { Content = new StringContent("verified payload", Encoding.UTF8) }));
        try
        {
            await DependencyManager.DownloadFileAsync(http, "https://example.invalid/redirect", file, "dummy", default);
            Assert.Equal("verified payload", await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task Truncated_download_is_rejected_and_partial_file_removed()
    {
        var file = Path.Combine(Path.GetTempPath(), "ec-truncated-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new Handler(_ =>
        {
            var content = new ByteArrayContent([1, 2, 3]); content.Headers.ContentLength = 10;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.ThrowsAsync<IOException>(() => DependencyManager.DownloadFileAsync(http, "https://example.invalid/payload", file, "dummy", default));
        Assert.False(File.Exists(file));
    }
    [Fact]
    public async Task Cancellation_leaves_no_partial_download()
    {
        var file = Path.Combine(Path.GetTempPath(), "ec-download-cancel-" + Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("dummy") }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DependencyManager.DownloadFileAsync(http, "https://example.invalid/payload", file, "dummy", cts.Token));
        Assert.False(File.Exists(file));
    }
    [Fact]
    public void Unsigned_installer_is_rejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "ec-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try { File.WriteAllBytes(file, "not an executable"u8.ToArray()); Assert.Throws<InvalidDataException>(() => WindowsSignature.VerifyMicrosoft(file)); }
        finally { File.Delete(file); }
    }
    [Fact]
    public void Microsoft_signed_dotnet_host_passes_windows_signature_verification()
    {
        var host = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(host));
        WindowsSignature.VerifyMicrosoft(host);
    }
    [Fact]
    public void Firewall_identity_is_per_installation_and_case_insensitive()
    {
        Assert.Equal(FirewallGuard.InstallationId(@"F:\EC"), FirewallGuard.InstallationId(@"f:\ec\"));
        Assert.NotEqual(FirewallGuard.InstallationId(@"F:\EC"), FirewallGuard.InstallationId(@"F:\Other"));
        Assert.DoesNotContain("127.", FirewallGuard.IPv4OutsideLoopback);
        Assert.StartsWith("::2-", FirewallGuard.IPv6OutsideLoopback);
    }
    [Fact]
    public void Firewall_com_objects_accept_and_normalize_ipv4_ipv6_ranges_without_installing_rules()
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        try
        {
            foreach (var addresses in new[] { FirewallGuard.IPv4OutsideLoopback, FirewallGuard.IPv6OutsideLoopback })
            {
                rule.Protocol = 256; rule.RemoteAddresses = addresses;
                Assert.True(FirewallGuard.SameAddressRanges((string)rule.RemoteAddresses, addresses));
            }
            Assert.True(FirewallGuard.SameAddressRanges("0:0:0:0:0:0:0:2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", FirewallGuard.IPv6OutsideLoopback));
            Assert.False(FirewallGuard.SameAddressRanges("*", FirewallGuard.IPv6OutsideLoopback));
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject((object)rule); }
    }
}
