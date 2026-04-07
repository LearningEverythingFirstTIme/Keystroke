using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class SuggestionAcceptanceTests
{
    [Fact]
    public void GetRemainingCompletion_UsesDirectPrefixMatch_WhenAvailable()
    {
        var remaining = SuggestionAcceptance.GetRemainingCompletion("I think", "I think so");

        Assert.Equal(" so", remaining);
    }

    [Fact]
    public void GetRemainingCompletion_TrimsClippedPrefixOverlap()
    {
        var remaining = SuggestionAcceptance.GetRemainingCompletion("I think", "think so");

        Assert.Equal(" so", remaining);
    }

    [Fact]
    public void GetRemainingCompletion_PreservesLeadingSpace_WhenNothingOverlaps()
    {
        var remaining = SuggestionAcceptance.GetRemainingCompletion("hello", " world");

        Assert.Equal(" world", remaining);
    }

    [Fact]
    public void GetRemainingCompletion_ReturnsEmpty_WhenSuggestionAlreadyFullyTyped()
    {
        var remaining = SuggestionAcceptance.GetRemainingCompletion("hello world", "world");

        Assert.Equal(string.Empty, remaining);
    }
}
