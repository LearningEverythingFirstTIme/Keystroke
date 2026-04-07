using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class PiiFilterTests
{
    [Fact]
    public void Scrub_ReplacesAllRecognizedSensitiveValues()
    {
        const string input = "Reach me at nick@example.com or call 555-123-4567.";

        var scrubbed = PiiFilter.Scrub(input);

        Assert.Equal("Reach me at [EMAIL] or call [PHONE].", scrubbed);
    }

    [Fact]
    public void Scrub_ReturnsNullAndEmptyInputsUnchanged()
    {
        Assert.Null(PiiFilter.Scrub(null));
        Assert.Equal(string.Empty, PiiFilter.Scrub(string.Empty));
    }

    [Fact]
    public void Scrub_HandlesOverlappingSensitiveMatchesWithoutThrowing()
    {
        var scrubbed = PiiFilter.Scrub("api_key: sk-abcdefghijklmnopqrstuvwxyz123456");

        Assert.Equal("[PASSWORD_FIELD]", scrubbed);
    }
}
