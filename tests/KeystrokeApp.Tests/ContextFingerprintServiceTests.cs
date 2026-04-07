using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class ContextFingerprintServiceTests
{
    private readonly ContextFingerprintService _service = new();

    [Fact]
    public void Create_DerivesEmailContextWithoutLeakingWindowTitleToSafeLabel()
    {
        var result = _service.Create("chrome", "Project Phoenix - Gmail");

        Assert.Equal("Email", result.Category);
        Assert.Equal("chrome (Email)", result.SafeContextLabel);
        Assert.DoesNotContain("Project Phoenix", result.SafeContextLabel);
        Assert.False(string.IsNullOrWhiteSpace(result.SubcontextKey));
        Assert.Equal("Project Phoenix", result.SubcontextLabel);
    }

    [Fact]
    public void Create_ReturnsStableKeysForSameContext()
    {
        var first = _service.Create("code", "Keystroke - Visual Studio");
        var second = _service.Create("code", "Keystroke - Visual Studio");

        Assert.Equal(first.ProcessKey, second.ProcessKey);
        Assert.Equal(first.WindowKey, second.WindowKey);
        Assert.Equal(first.SubcontextKey, second.SubcontextKey);
    }

    [Fact]
    public void Create_ChangesSubcontextKeyWhenThreadChanges()
    {
        var first = _service.Create("slack", "Alex - Slack");
        var second = _service.Create("slack", "Jordan - Slack");

        Assert.NotEqual(first.SubcontextKey, second.SubcontextKey);
        Assert.Equal("Chat", first.Category);
        Assert.Equal("Chat", second.Category);
    }
}
