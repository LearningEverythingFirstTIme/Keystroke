using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class PromptPreviewBuilderTests
{
    [Fact]
    public void Build_HidesBlockedTypedInputAndSuppressesSend()
    {
        var config = new AppConfig
        {
            MinBufferLength = 3,
            OcrEnabled = true,
            RollingContextEnabled = true
        };

        var snapshot = PromptPreviewBuilder.Build(
            config,
            "gemini (model)",
            "password = hunter2",
            "code",
            "Secrets",
            appEnabled: true,
            new OutboundPrivacyService(),
            learningService: null,
            styleProfileService: null,
            vocabularyProfileService: null,
            screenText: "Visible text",
            rollingContext: "Recent text");

        Assert.True(snapshot.TypedInputBlocked);
        Assert.False(snapshot.WouldSendPrediction);
        Assert.Contains("blocked", snapshot.TypedTextPreview, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<complete_this>", snapshot.UserPromptPreview);
        Assert.Contains("(hidden or empty)", snapshot.UserPromptPreview);
    }

    [Fact]
    public void Build_ShowsPromptSectionsForEligibleInput()
    {
        var config = new AppConfig
        {
            MinBufferLength = 3,
            OcrEnabled = true,
            RollingContextEnabled = true,
            LearningEnabled = false
        };

        var snapshot = PromptPreviewBuilder.Build(
            config,
            "gpt5 (model)",
            "Please send",
            "olk",
            "Draft",
            appEnabled: true,
            new OutboundPrivacyService(),
            learningService: null,
            styleProfileService: null,
            vocabularyProfileService: null,
            screenText: "Email thread",
            rollingContext: "Yesterday we said hi.");

        Assert.True(snapshot.WouldSendPrediction);
        Assert.Contains("<screen_context>", snapshot.UserPromptPreview);
        Assert.Contains("<recently_written>", snapshot.UserPromptPreview);
        Assert.Contains("<complete_this>", snapshot.UserPromptPreview);
        Assert.Contains("Please send", snapshot.UserPromptPreview);
    }
}
