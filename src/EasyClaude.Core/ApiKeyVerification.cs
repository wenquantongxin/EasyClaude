using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyClaude.Core;

public sealed record RememberedApiKeyTest(ApiKeyTest Test, DateTimeOffset TestedAt);

/// <summary>Local test metadata only. No key, response text or raw API error body is persisted.</summary>
public sealed class ApiKeyVerificationStore(string file)
{
    private sealed record Entry(string Identity, DateTimeOffset TestedAt, ApiKeyTest Test);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Identity(string credentialId, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentialId + "\0" + key)));

    public RememberedApiKeyTest? Load(string? identity)
    {
        if (string.IsNullOrEmpty(identity) || !File.Exists(file)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), Json);
            return entry is { Test: not null } && entry.Identity == identity
                ? new(entry.Test, entry.TestedAt) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string identity, ApiKeyTest test, DateTimeOffset testedAt)
    {
        // A history display must not claim that it just saved a key, nor retain generated replies/errors.
        var summary = new ApiKeyTest(test.Ok, test.Ok ? "模型测试成功" : "模型测试未成功",
            test.Model, LatencyMs: test.LatencyMs, FallbackReason: test.FallbackReason);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Entry(identity, testedAt, summary), Json));
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Clear() { if (File.Exists(file)) File.Delete(file); }
}
