using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class OnboardingStateServiceTests
{
    private readonly OnboardingStateService _service = new();

    [Fact]
    public void ApplyRecommendedGeminiDefaults_SetsExpectedDefaults()
    {
        var config = new AppConfig
        {
            PredictionEngine = "claude",
            ClaudeModel = "claude-haiku-4-5",
            OcrEnabled = false,
            RollingContextEnabled = false,
            LearningEnabled = true
        };

        _service.ApplyRecommendedGeminiDefaults(config);

        Assert.Equal("gemini", config.PredictionEngine);
        Assert.Equal(AppConfig.DefaultGeminiModel, config.GeminiModel);
        Assert.True(config.OcrEnabled);
        Assert.True(config.RollingContextEnabled);
        Assert.False(config.LearningEnabled);
    }

    [Fact]
    public void HasUsableProviderSetup_RecognizesGeminiKey()
    {
        var config = new AppConfig
        {
            PredictionEngine = "gemini",
            GeminiApiKey = "test-key-that-is-long-enough"
        };

        Assert.True(_service.HasUsableProviderSetup(config));
    }

    [Fact]
    public void TryCompleteOnboardingFromExistingSetup_MarksCompleted()
    {
        var config = new AppConfig
        {
            ConsentAccepted = true,
            OnboardingCompleted = false,
            PredictionEngine = "gpt5",
            OpenAiApiKey = "test-openai-key-that-is-long-enough"
        };

        var changed = _service.TryCompleteOnboardingFromExistingSetup(config);

        Assert.True(changed);
        Assert.True(config.OnboardingCompleted);
    }
}
