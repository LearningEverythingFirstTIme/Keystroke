using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class PredictionEngineBaseTests
{
    [Fact]
    public void RejectDuplicate_AllowsLegitimateUserPhrase()
    {
        var result = TestPredictionEngine.InvokeRejectDuplicate(
            "In the docs,",
            " the user interface should stay responsive.");

        Assert.Equal(" the user interface should stay responsive.", result);
    }

    [Fact]
    public void RejectDuplicate_RejectsPromptMarkupLeakage()
    {
        var result = TestPredictionEngine.InvokeRejectDuplicate(
            "Please finish this sentence",
            " <complete_this> leaked back into the answer.");

        Assert.Null(result);
    }

    private sealed class TestPredictionEngine : PredictionEngineBase
    {
        public TestPredictionEngine() : base("prediction-engine-tests.log")
        {
        }

        public static string? InvokeRejectDuplicate(string typedText, string completion) =>
            RejectDuplicate(typedText, completion);
    }
}
