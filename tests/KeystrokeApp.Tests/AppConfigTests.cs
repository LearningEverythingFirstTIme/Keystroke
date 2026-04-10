using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class AppConfigTests
{
    [Fact]
    public void NormalizeModelSelections_MigratesLegacyModelIds()
    {
        var config = new AppConfig
        {
            GeminiModel = "gemini-2.5-flash",
            Gpt5Model = "gpt-5-mini",
            ClaudeModel = "claude-sonnet-4-20250514",
            OllamaModel = "qwen2.5:7b"
        };

        config.NormalizeModelSelections();

        Assert.Equal("gemini-3-flash-preview", config.GeminiModel);
        Assert.Equal("gpt-5.4-mini", config.Gpt5Model);
        Assert.Equal("claude-sonnet-4-6", config.ClaudeModel);
        Assert.Equal("qwen3:8b", config.OllamaModel);
    }

    [Fact]
    public void NormalizeModelSelections_FallsBackToCuratedDefaultsForUnknownModels()
    {
        var config = new AppConfig
        {
            GeminiModel = "gemini-experimental",
            Gpt5Model = "gpt-5-legacy",
            ClaudeModel = "claude-preview",
            OllamaModel = "tiny-random-model"
        };

        config.NormalizeModelSelections();

        Assert.Equal(AppConfig.DefaultGeminiModel, config.GeminiModel);
        Assert.Equal(AppConfig.DefaultGpt5Model, config.Gpt5Model);
        Assert.Equal(AppConfig.DefaultClaudeModel, config.ClaudeModel);
        Assert.Equal(AppConfig.DefaultOllamaModel, config.OllamaModel);
    }
}
