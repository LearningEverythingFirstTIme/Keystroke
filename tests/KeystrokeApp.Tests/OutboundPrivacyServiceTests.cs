using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class OutboundPrivacyServiceTests
{
    private readonly OutboundPrivacyService _service = new();

    [Fact]
    public void SanitizeTypedText_ScrubsSafeToSendPiiWithoutBlocking()
    {
        var result = _service.SanitizeTypedText("Contact me at nick@example.com");

        Assert.Equal("Contact me at [EMAIL]", result.Text);
        Assert.False(result.ShouldBlockPrediction);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public void SanitizeTypedText_BlocksWhenSecretIsDetected()
    {
        var result = _service.SanitizeTypedText("api_key: sk-abcdefghijklmnopqrstuvwxyz123456");

        Assert.Equal(string.Empty, result.Text);
        Assert.True(result.ShouldBlockPrediction);
        Assert.Equal("Blocking sensitive data detected in active input.", result.BlockReason);
    }

    [Fact]
    public void BuildSafeContextLabel_UsesCategoryInsteadOfWindowTitle()
    {
        var label = _service.BuildSafeContextLabel("chrome", "Inbox - Gmail");

        Assert.Equal("chrome (Email)", label);
        Assert.DoesNotContain("Inbox - Gmail", label);
    }
}
