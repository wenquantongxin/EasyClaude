using System.Net;
using System.Text;

namespace EasyClaude.Core.Tests;

public class CredentialUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    [Fact]
    public async Task Successful_save_is_reported_even_when_old_credential_cleanup_fails()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Post ? Response(HttpStatusCode.OK, "{\"data\":{\"id\":\"current\"}}") :
            request.Method == HttpMethod.Delete ? Response(HttpStatusCode.Forbidden, "{}") :
            Response(HttpStatusCode.OK, "{\"data\":[{\"id\":\"old\",\"integrationID\":\"anthropic\",\"active\":false},{\"id\":\"current\",\"integrationID\":\"anthropic\",\"active\":true}]}"));
        using var client = new OpenCodeClient(new Uri("http://127.0.0.1:1"), "dummy", handler);
        var saved = false;
        var warning = await client.ReplaceAnthropicKeyAsync("test-only-not-a-real-key", default, () => saved = true);
        Assert.True(saved); Assert.Contains("403", warning);
    }
    [Fact]
    public async Task Model_catalog_authentication_error_is_not_treated_as_missing_haiku()
    {
        using var handler = new Handler(_ => Response(HttpStatusCode.Unauthorized, "{}"));
        using var client = new OpenCodeClient(new Uri("http://127.0.0.1:1"), "dummy", handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.FindModelAsync("anthropic", "haiku", default));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }
}
