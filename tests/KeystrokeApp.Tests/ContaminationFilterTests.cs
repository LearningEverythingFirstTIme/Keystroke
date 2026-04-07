using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class ContaminationFilterTests
{
    [Theory]
    [InlineData("The user is asking for help.")]
    [InlineData("Use the SCREEN CONTEXT and continue.")]
    [InlineData("recently_written examples are attached.")]
    public void IsContaminated_ReturnsTrueForKnownLeakagePatterns(string completion)
    {
        Assert.True(ContaminationFilter.IsContaminated(completion));
    }

    [Fact]
    public void IsContaminated_ReturnsFalseForNormalCompletion()
    {
        Assert.False(ContaminationFilter.IsContaminated("Thanks, I can take a look at that this afternoon."));
    }
}
