using System.Text.Json;

namespace EasyClaude.Core.Tests;

public class ModelCatalogVisibilityTests
{
    [Fact]
    public void Web_catalog_keeps_three_families_and_their_metadata_without_enabling_disabled_models()
    {
        const string backend = """
            {"location":{"directory":"C:/project"},"extra":"preserved","data":[
              {"providerID":"anthropic","id":"claude-fable-5-5","enabled":true,"limit":{"context":200000},"variants":[{"id":"high"}]},
              {"providerID":"anthropic","id":"claude-opus-5-5","enabled":true},
              {"providerID":"anthropic","id":"claude-sonnet-5-5","enabled":true},
              {"providerID":"anthropic","id":"claude-opus-4-6","enabled":false},
              {"providerID":"anthropic","id":"claude-haiku-5-5","enabled":true},
              {"providerID":"anthropic","id":"future-other-family","enabled":true},
              {"providerID":"openai","id":"claude-opus-alias","enabled":true}]}
            """;
        using var view = JsonDocument.Parse(ModelCatalogVisibility.Filter(backend));
        var models = view.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(new[] { "claude-fable-5-5", "claude-opus-5-5", "claude-sonnet-5-5", "claude-opus-4-6" }, models.Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("C:/project", view.RootElement.GetProperty("location").GetProperty("directory").GetString());
        Assert.Equal("preserved", view.RootElement.GetProperty("extra").GetString());
        Assert.Equal(200000, models[0].GetProperty("limit").GetProperty("context").GetInt32());
        Assert.Equal("high", models[0].GetProperty("variants")[0].GetProperty("id").GetString());
        Assert.False(models[3].GetProperty("enabled").GetBoolean());
        using var original = JsonDocument.Parse(backend);
        Assert.Equal(7, original.RootElement.GetProperty("data").GetArrayLength());
        Assert.True(original.RootElement.GetProperty("data")[4].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Missing_families_are_not_fabricated_and_invalid_entries_are_not_visible()
    {
        using var view = JsonDocument.Parse(ModelCatalogVisibility.Filter("{\"data\":[null,3,{\"id\":\"claude-opus-5\"},{\"providerID\":\"anthropic\",\"id\":42},{\"providerID\":\"anthropic\",\"id\":\"claude-haiku-5\"}]}"));
        Assert.Empty(view.RootElement.GetProperty("data").EnumerateArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("[]")]
    public void Invalid_catalog_is_rejected_instead_of_displaying_an_unfiltered_list(string json) =>
        Assert.Throws<InvalidDataException>(() => ModelCatalogVisibility.Filter(json));
}
