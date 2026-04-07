using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class LearningRetrievalServiceTests
{
    private readonly LearningRetrievalService _service = new(new LearningReranker());

    [Fact]
    public void GetCandidates_PrefersNativeWritingInSameSubcontext()
    {
        var context = new ContextSnapshot
        {
            TypedText = "Thanks for",
            Category = "Email",
            ProcessKey = "proc-a",
            WindowKey = "win-a",
            SubcontextKey = "ctx-a"
        };

        var snapshot = new LearningCorpusSnapshot
        {
            PositiveEvidence =
            [
                new LearningEvidence
                {
                    Prefix = "Thanks for",
                    Completion = " sending that over.",
                    Category = "Email",
                    ProcessKey = "proc-a",
                    WindowKey = "win-a",
                    SubcontextKey = "ctx-a",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
                    QualityScore = 0.92f,
                    SourceType = LearningSourceType.NativeWriting,
                    WasUntouched = true,
                    ContextConfidence = 0.9,
                    SourceWeight = 1.0f
                },
                new LearningEvidence
                {
                    Prefix = "Thanks for",
                    Completion = " the quick response.",
                    Category = "Email",
                    ProcessKey = "proc-b",
                    WindowKey = "win-b",
                    SubcontextKey = "ctx-b",
                    TimestampUtc = DateTime.UtcNow.AddMinutes(-3),
                    QualityScore = 0.95f,
                    SourceType = LearningSourceType.AssistPartial,
                    WasUntouched = false,
                    ContextConfidence = 0.4,
                    SourceWeight = 0.45f
                }
            ]
        };

        var results = _service.GetCandidates(snapshot, context, negatives: false, maxCount: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal(" sending that over.", results[0].Evidence.Completion);
        Assert.Equal("subcontext", results[0].ContextMatchLevel);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void GetCandidates_DeduplicatesVerySimilarCompletions()
    {
        var now = DateTime.UtcNow;
        var context = new ContextSnapshot
        {
            TypedText = "Thanks for",
            Category = "Email",
            ProcessKey = "proc-a",
            WindowKey = "win-a",
            SubcontextKey = "ctx-a"
        };

        var snapshot = new LearningCorpusSnapshot
        {
            PositiveEvidence =
            [
                new LearningEvidence
                {
                    Prefix = "Thanks for",
                    Completion = " the update today",
                    Category = "Email",
                    ProcessKey = "proc-a",
                    WindowKey = "win-a",
                    SubcontextKey = "ctx-a",
                    TimestampUtc = now.AddMinutes(-2),
                    QualityScore = 0.9f,
                    SourceType = LearningSourceType.NativeWriting,
                    WasUntouched = true,
                    ContextConfidence = 0.85,
                    SourceWeight = 1.0f
                },
                new LearningEvidence
                {
                    Prefix = "Thanks for",
                    Completion = " the update today",
                    Category = "Email",
                    ProcessKey = "proc-a",
                    WindowKey = "win-a",
                    SubcontextKey = "ctx-a",
                    TimestampUtc = now.AddMinutes(-1),
                    QualityScore = 0.88f,
                    SourceType = LearningSourceType.AssistAcceptedUntouched,
                    WasUntouched = true,
                    ContextConfidence = 0.85,
                    SourceWeight = 0.85f
                },
                new LearningEvidence
                {
                    Prefix = "Thanks for",
                    Completion = " getting this done so quickly.",
                    Category = "Email",
                    ProcessKey = "proc-a",
                    WindowKey = "win-a",
                    SubcontextKey = "ctx-a",
                    TimestampUtc = now.AddMinutes(-4),
                    QualityScore = 0.84f,
                    SourceType = LearningSourceType.NativeWriting,
                    WasUntouched = true,
                    ContextConfidence = 0.85,
                    SourceWeight = 1.0f
                }
            ]
        };

        var results = _service.GetCandidates(snapshot, context, negatives: false, maxCount: 3);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.Evidence.Completion == " the update today");
        Assert.Contains(results, item => item.Evidence.Completion == " getting this done so quickly.");
    }
}
