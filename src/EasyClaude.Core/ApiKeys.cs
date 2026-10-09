namespace EasyClaude.Core;

/// <summary>Result of an API key validation / "你好" test. Never contains the key itself.</summary>
public sealed record ApiKeyTest(bool Ok, string Message, string? Model = null, string? Reply = null, int? LatencyMs = null,
    bool Saved = false, string? FallbackReason = null);

/// <summary>What the UI may know about the stored key: whether one exists and its first few characters.</summary>
public sealed record ApiKeyState(bool Configured, string? Hint, ApiKeyTest? LastTest)
{
    public static readonly ApiKeyState Unknown = new(false, null, null);
}

public static class ApiKeyMask
{
    /// <summary>Only the first few characters, e.g. "sk-ant-api03-AbCd…" (at most 17, and at most 40% of the key).</summary>
    public static string Hint(string key)
    {
        key = key.Trim();
        if (key.Length == 0) return "…";
        var visible = Math.Clamp(key.Length * 2 / 5, 1, 17);
        return key[..visible] + "…";
    }

    /// <summary>Plausible key: at least 20 printable ASCII characters, no whitespace.</summary>
    public static bool LooksValid(string key) =>
        key.Length >= 20 && key.All(c => c > ' ' && c < 127);
}

/// <summary>Validates a key with Anthropic through the LLM path (free endpoint), without storing anything.</summary>
public static class AnthropicProbe
{
    public static async Task<int> ValidateKeyAsync(HttpClient http, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1");
        request.Headers.TryAddWithoutValidation("x-api-key", key);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return (int)response.StatusCode;
    }
}

public interface IModelTestClient
{
    Task<string?> FindModelAsync(string providerId, string contains, CancellationToken ct);
    Task<(bool Ok, int Status, string Text)> GenerateAsync(string prompt, string providerId, string modelId, CancellationToken ct);
}

/// <summary>Bounded local readiness retries; only model-unavailable errors can trigger one default-model fallback.</summary>
public static class ApiKeyTester
{
    public static bool IsModelUnavailable(int status, string text) =>
        status is 400 or 404 && (text.Contains("Model unavailable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ModelSelectionError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not_found_error", StringComparison.OrdinalIgnoreCase));

    public static async Task<ApiKeyTest> RunAsync(IModelTestClient client, string configuredModel, bool saved,
        CancellationToken ct, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var fallback = configuredModel.Contains('/') ? configuredModel[(configuredModel.IndexOf('/') + 1)..] : configuredModel;
        var start = time.GetTimestamp();
        string? haiku = null;
        for (var i = 0; i < (saved ? 3 : 1); i++)
        {
            haiku = await client.FindModelAsync("anthropic", "haiku", ct).ConfigureAwait(false);
            if (haiku is not null || !saved) break;
            if (i < 2) await Task.Delay(TimeSpan.FromMilliseconds(500), time, ct).ConfigureAwait(false);
        }
        string? reason = haiku is null ? "Haiku 当前不可用，已改用默认模型" : null;
        var model = haiku ?? fallback;
        (bool Ok, int Status, string Text) result = (false, 0, string.Empty);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = await client.GenerateAsync("请只回复：你好", "anthropic", model, ct).ConfigureAwait(false);
            if (result.Ok || !IsModelUnavailable(result.Status, result.Text) || result.Status != 400 || attempt == 2) break;
            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 500 : 1500), time, ct).ConfigureAwait(false);
            var refreshed = await client.FindModelAsync("anthropic", "haiku", ct).ConfigureAwait(false);
            if (haiku is not null && refreshed is not null) model = haiku = refreshed;
        }
        if (!result.Ok && haiku is not null && model != fallback && IsModelUnavailable(result.Status, result.Text))
        {
            reason = "Haiku 暂不可调用，已改用默认模型";
            model = fallback;
            result = await client.GenerateAsync("请只回复：你好", "anthropic", model, ct).ConfigureAwait(false);
        }
        var latency = (int)time.GetElapsedTime(start).TotalMilliseconds;
        return result.Ok
            ? new(true, "模型测试成功", model, Shorten(result.Text, 80), latency, saved, reason)
            : new(false, $"模型测试失败（HTTP {result.Status}）：{Shorten(result.Text, 180)}", model, null, latency, saved, reason);
    }

    private static string Shorten(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
