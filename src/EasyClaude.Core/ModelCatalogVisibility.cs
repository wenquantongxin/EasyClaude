using System.Text;
using System.Text.Json;

namespace EasyClaude.Core;

/// <summary>Filters the web UI catalog only; backend model availability and Key tests remain unchanged.</summary>
public static class ModelCatalogVisibility
{
    public static string Filter(string json)
    {
        using var catalog = JsonDocument.Parse(json);
        if (catalog.RootElement.ValueKind != JsonValueKind.Object ||
            !catalog.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenCode 模型目录格式不正确");

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in catalog.RootElement.EnumerateObject())
            {
                if (property.Name != "data") { property.WriteTo(writer); continue; }
                writer.WritePropertyName("data");
                writer.WriteStartArray();
                foreach (var model in models.EnumerateArray())
                    if (Visible(model)) model.WriteTo(writer);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool Visible(JsonElement model) =>
        model.ValueKind == JsonValueKind.Object &&
        model.TryGetProperty("providerID", out var provider) && provider.ValueKind == JsonValueKind.String && provider.GetString() == "anthropic" &&
        model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } name &&
        (name.StartsWith("claude-fable-", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("claude-opus-", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("claude-sonnet-", StringComparison.OrdinalIgnoreCase));
}
