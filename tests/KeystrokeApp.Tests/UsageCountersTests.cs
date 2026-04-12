using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class UsageCountersTests : IDisposable
{
    private readonly string _tempDir;

    public UsageCountersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "keystroke-usage-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void RecordAcceptedSuggestion_IncrementsCounts_AndDedupesBySuggestionId()
    {
        var today = new DateOnly(2026, 4, 12);
        var counters = CreateCounters(today);

        var first = counters.RecordAcceptedSuggestion("sugg-1");
        var duplicate = counters.RecordAcceptedSuggestion("sugg-1");
        var second = counters.RecordAcceptedSuggestion("sugg-2");

        Assert.True(first.Counted);
        Assert.False(duplicate.Counted);
        Assert.True(second.Counted);
        Assert.Equal(2, second.Snapshot.TotalAcceptedSuggestions);
        Assert.Equal(2, second.Snapshot.DailyAcceptedSuggestions);
    }

    [Fact]
    public void GetSnapshot_RollsDailyCount_WhenLocalDateChanges()
    {
        var today = new DateOnly(2026, 4, 12);
        var currentDay = today;
        var counters = CreateCounters(() => currentDay);

        counters.RecordAcceptedSuggestion("sugg-1");
        currentDay = today.AddDays(1);

        var snapshot = counters.GetSnapshot();

        Assert.Equal(1, snapshot.TotalAcceptedSuggestions);
        Assert.Equal(0, snapshot.DailyAcceptedSuggestions);
        Assert.Equal(today.AddDays(1), snapshot.DailyAcceptedDateLocal);
    }

    [Fact]
    public void MarkLearningNudgeShown_PersistsAcrossReload()
    {
        var today = new DateOnly(2026, 4, 12);
        var path = Path.Combine(_tempDir, "usage.json");
        var counters = new UsageCounters(path, () => today);

        Assert.True(counters.MarkLearningNudgeShown());

        var reloaded = new UsageCounters(path, () => today);
        var snapshot = reloaded.GetSnapshot();

        Assert.True(snapshot.LearningNudgeShown);
        Assert.False(reloaded.MarkLearningNudgeShown());
    }

    [Fact]
    public void CanRequestPrediction_ReturnsFalse_WhenFreeTierReachedLimit()
    {
        var today = new DateOnly(2026, 4, 12);
        var counters = CreateCounters(today);

        for (var i = 0; i < UsageCounters.DailyFreeLimit; i++)
            counters.RecordAcceptedSuggestion($"sugg-{i}");

        Assert.False(counters.CanRequestPrediction(limitEnabled: true, personalizedAiEnabled: false));
        Assert.True(counters.CanRequestPrediction(limitEnabled: false, personalizedAiEnabled: false));
        Assert.True(counters.CanRequestPrediction(limitEnabled: true, personalizedAiEnabled: true));
    }

    [Fact]
    public void Reset_ClearsPersistedCounts()
    {
        var today = new DateOnly(2026, 4, 12);
        var path = Path.Combine(_tempDir, "usage.json");
        var counters = new UsageCounters(path, () => today);
        counters.RecordAcceptedSuggestion("sugg-1");
        counters.MarkLearningNudgeShown();

        counters.Reset();

        var reloaded = new UsageCounters(path, () => today).GetSnapshot();
        Assert.Equal(0, reloaded.TotalAcceptedSuggestions);
        Assert.Equal(0, reloaded.DailyAcceptedSuggestions);
        Assert.False(reloaded.LearningNudgeShown);
    }

    [Fact]
    public void DailyRollover_ClearsDeduplicationSet()
    {
        var today = new DateOnly(2026, 4, 12);
        var currentDay = today;
        var counters = CreateCounters(() => currentDay);

        // Accept a suggestion on day 1
        var first = counters.RecordAcceptedSuggestion("sugg-1");
        Assert.True(first.Counted);

        // Same ID is deduplicated within the same day
        var dupe = counters.RecordAcceptedSuggestion("sugg-1");
        Assert.False(dupe.Counted);

        // Roll to next day — dedup set should clear
        currentDay = today.AddDays(1);

        // Same ID can now be counted on the new day
        var nextDay = counters.RecordAcceptedSuggestion("sugg-1");
        Assert.True(nextDay.Counted);
        Assert.Equal(1, nextDay.Snapshot.DailyAcceptedSuggestions);
    }

    private UsageCounters CreateCounters(DateOnly day)
        => CreateCounters(() => day);

    private UsageCounters CreateCounters(Func<DateOnly> todayProvider)
        => new(Path.Combine(_tempDir, $"usage-{Guid.NewGuid():n}.json"), todayProvider);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
