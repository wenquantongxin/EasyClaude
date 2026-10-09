using System.Net;
using System.Text;

namespace EasyClaude.Core.Tests;

public sealed class ApiKeyVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "easyclaude-key-test-" + Guid.NewGuid().ToString("N"));
    private string FileName => Path.Combine(_root, "api-key-test.json");
    private const string Key = "test-only-secret-key-with-shared-prefix-AAAA";
    private static readonly DateTimeOffset At = new(2026, 10, 10, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Restart_restores_result_and_time_without_storing_key_reply_or_raw_error()
    {
        var identity = ApiKeyVerificationStore.Identity("credential-a", Key);
        new ApiKeyVerificationStore(FileName).Save(identity, new(true, Key, "claude-haiku-test", "private test reply", 142, true, "Haiku 回退"), At);
        var restored = new ApiKeyVerificationStore(FileName).Load(identity)!;
        Assert.True(restored.Test.Ok);
        Assert.Equal(At, restored.TestedAt);
        Assert.Equal("claude-haiku-test", restored.Test.Model);
        Assert.Equal(142, restored.Test.LatencyMs);
        Assert.Equal("Haiku 回退", restored.Test.FallbackReason);
        Assert.Null(restored.Test.Reply);
        Assert.False(restored.Test.Saved);
        var json = File.ReadAllText(FileName);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain("private test reply", json);
    }

    [Theory]
    [InlineData("credential-b", Key)]
    [InlineData("credential-a", "test-only-secret-key-with-shared-prefix-BBBB")]
    public void A_different_credential_or_key_never_reuses_the_previous_result(string id, string key)
    {
        new ApiKeyVerificationStore(FileName).Save(ApiKeyVerificationStore.Identity("credential-a", Key), new(true, "ok"), At);
        Assert.Equal(ApiKeyMask.Hint(Key), ApiKeyMask.Hint(key));
        Assert.Null(new ApiKeyVerificationStore(FileName).Load(ApiKeyVerificationStore.Identity(id, key)));
    }

    [Fact]
    public void Missing_credential_and_corrupt_metadata_are_ignored()
    {
        Assert.Null(new ApiKeyVerificationStore(FileName).Load("unknown"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(FileName, "{invalid json");
        Assert.Null(new ApiKeyVerificationStore(FileName).Load("unknown"));
        Assert.Null(new ApiKeyVerificationStore(FileName).Load(null));
    }

    [Fact]
    public void A_completed_failed_test_replaces_the_old_success()
    {
        var store = new ApiKeyVerificationStore(FileName);
        var identity = ApiKeyVerificationStore.Identity("credential-a", Key);
        store.Save(identity, new(true, "ok"), At);
        store.Save(identity, new(false, "raw API error: " + Key), At.AddMinutes(1));
        var restored = store.Load(identity)!;
        Assert.False(restored.Test.Ok);
        Assert.Equal(At.AddMinutes(1), restored.TestedAt);
        Assert.DoesNotContain(Key, File.ReadAllText(FileName));
    }

    [Fact]
    public void Clearing_for_a_new_key_removes_the_old_test()
    {
        var store = new ApiKeyVerificationStore(FileName);
        var identity = ApiKeyVerificationStore.Identity("credential-a", Key);
        store.Save(identity, new(true, "ok"), At);
        store.Clear();
        Assert.Null(store.Load(identity));
        Assert.False(File.Exists(FileName));
    }

    private sealed class Handler(string key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"credential-a\",\"integrationID\":\"anthropic\",\"active\":true,\"value\":{\"key\":\"" + key + "\"}}]}", Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task Credential_refresh_detects_a_replaced_key_with_the_same_id_and_hint()
    {
        using var first = new OpenCodeClient(new Uri("http://127.0.0.1:1"), "dummy", new Handler(Key));
        using var second = new OpenCodeClient(new Uri("http://127.0.0.1:1"), "dummy", new Handler("test-only-secret-key-with-shared-prefix-BBBB"));
        var a = Assert.Single(await first.ListKeyCredentialsAsync("anthropic", default));
        var b = Assert.Single(await second.ListKeyCredentialsAsync("anthropic", default));
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Hint, b.Hint);
        Assert.NotEqual(a.Identity, b.Identity);
        Assert.DoesNotContain(Key, a.ToString());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
