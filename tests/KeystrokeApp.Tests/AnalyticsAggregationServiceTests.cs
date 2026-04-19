using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class AnalyticsAggregationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storePath;
    private readonly string _dbPath;
    private readonly LearningDatabase _database;

    public AnalyticsAggregationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"keystroke_analytics_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "analytics-daily.json");
        _dbPath = Path.Combine(_tempDir, "learning.db");
        _database = new LearningDatabase(_dbPath);
        _database.EnsureCreated();
    }

    public void Dispose()
    {
        _database.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* cleanup best-effort */ }
    }

    private AnalyticsAggregationService CreateService() =>
        new(_database, _storePath);

    private void WriteTrackingEvent(string eventType, string category = "Email",
        DateTime? timestamp = null, string acceptedText = "hello world",
        string correctionType = "", string contextKey = "", string contextLabel = "",
        float qualityScore = 0.7f)
    {
        var record = new LearningEventRecord
        {
            EventId = Guid.NewGuid().ToString("n"),
            EventType = eventType,
            Category = category,
            TimestampUtc = timestamp ?? DateTime.UtcNow,
            AcceptedText = acceptedText,
            UserWrittenText = eventType == "manual_continuation_committed" ? acceptedText : "",
            QualityScore = qualityScore,
            LatencyMs = 400,
            CorrectionType = correctionType,
            ContextKeys = new LearningEventContextKeys
            {
                SubcontextKey = contextKey,
                SubcontextLabel = contextLabel
            }
        };
        _database.InsertEvent(record);
    }

    // ── Rollup accuracy ───────────────────────────────────────────────────

    [Fact]
    public void Empty_event_file_produces_empty_rollups()
    {
        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Empty(store.Rollups);
        Assert.Equal(0, store.CurrentStreak);
        Assert.Empty(store.AchievedMilestones);
    }

    [Fact]
    public void Single_day_events_produce_one_rollup()
    {
        var now = DateTime.UtcNow;
        WriteTrackingEvent("suggestion_full_accept", timestamp: now, acceptedText: "hello world");
        WriteTrackingEvent("suggestion_full_accept", timestamp: now.AddMinutes(1), acceptedText: "good morning");
        WriteTrackingEvent("suggestion_dismiss", timestamp: now.AddMinutes(2));
        WriteTrackingEvent("manual_continuation_committed", timestamp: now.AddMinutes(3), acceptedText: "native text");

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Single(store.Rollups);
        var rollup = store.Rollups[0];
        Assert.Equal(2, rollup.TotalAccepted);
        Assert.Equal(1, rollup.TotalDismissed);
        Assert.Equal(1, rollup.TotalNativeCommits);
        Assert.True(rollup.WordsAssisted >= 4); // "hello world" + "good morning"
    }

    [Fact]
    public void Multi_day_events_produce_separate_rollups()
    {
        var day1 = DateTime.UtcNow.AddDays(-2);
        var day2 = DateTime.UtcNow.AddDays(-1);

        WriteTrackingEvent("suggestion_full_accept", timestamp: day1, acceptedText: "day one");
        WriteTrackingEvent("suggestion_full_accept", timestamp: day2, acceptedText: "day two");
        WriteTrackingEvent("suggestion_dismiss", timestamp: day2);

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.True(store.Rollups.Count >= 2);
    }

    [Fact]
    public void Category_breakdown_is_populated()
    {
        WriteTrackingEvent("suggestion_full_accept", category: "Email", acceptedText: "email text");
        WriteTrackingEvent("suggestion_full_accept", category: "Chat", acceptedText: "chat text");
        WriteTrackingEvent("suggestion_dismiss", category: "Email");

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        var rollup = store.Rollups[0];
        Assert.True(rollup.CategoryBreakdown.ContainsKey("Email"));
        Assert.True(rollup.CategoryBreakdown.ContainsKey("Chat"));
        Assert.Equal(1, rollup.CategoryBreakdown["Email"].Accepted);
        Assert.Equal(1, rollup.CategoryBreakdown["Email"].Dismissed);
        Assert.Equal(1, rollup.CategoryBreakdown["Chat"].Accepted);
    }

    [Fact]
    public void Corrections_are_counted()
    {
        WriteTrackingEvent("suggestion_full_accept", correctionType: "truncated");
        WriteTrackingEvent("suggestion_full_accept", correctionType: "replaced_ending");
        WriteTrackingEvent("suggestion_full_accept", correctionType: "none");

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Equal(2, store.Rollups[0].TotalCorrections);
    }

    [Fact]
    public void Partial_accept_quality_is_not_double_counted()
    {
        WriteTrackingEvent(
            "suggestion_partial_accept",
            qualityScore: 0.7f,
            acceptedText: "partial accept");

        var svc = CreateService();
        svc.Refresh();

        Assert.Single(svc.GetStore().Rollups);
        Assert.Equal(0.7f, svc.GetStore().Rollups[0].AvgQualityScore, 3);
    }

    [Fact]
    public void Mixed_accepts_produce_correct_daily_and_weekly_quality()
    {
        var now = DateTime.UtcNow;
        WriteTrackingEvent(
            "suggestion_full_accept",
            timestamp: now,
            qualityScore: 0.9f,
            acceptedText: "full accept");
        WriteTrackingEvent(
            "suggestion_partial_accept",
            timestamp: now.AddMinutes(1),
            qualityScore: 0.6f,
            acceptedText: "partial accept");

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Single(store.Rollups);
        Assert.Equal(0.75f, store.Rollups[0].AvgQualityScore, 3);
        Assert.NotEmpty(store.WeeklySummaries);
        Assert.Equal(0.75f, store.WeeklySummaries[0].AvgQuality, 3);
    }

    // ── Incremental refresh ───────────────────────────────────────────────

    [Fact]
    public void Incremental_refresh_only_adds_new_events()
    {
        WriteTrackingEvent("suggestion_full_accept", acceptedText: "first");

        var svc = CreateService();
        svc.Refresh();
        Assert.Equal(1, svc.GetStore().CumulativeAccepted);

        // Add more events and refresh again
        WriteTrackingEvent("suggestion_full_accept",
            timestamp: DateTime.UtcNow.AddSeconds(2), acceptedText: "second");
        svc.Refresh();
        Assert.Equal(2, svc.GetStore().CumulativeAccepted);
    }

    // ── Streaks ───────────────────────────────────────────────────────────

    [Fact]
    public void Consecutive_days_produce_correct_streak()
    {
        for (int i = 0; i < 5; i++)
        {
            WriteTrackingEvent("suggestion_full_accept",
                timestamp: DateTime.UtcNow.AddDays(-i), acceptedText: $"day {i}");
        }

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Equal(5, store.CurrentStreak);
        Assert.Equal(5, store.LongestStreak);
    }

    [Fact]
    public void Gap_in_days_resets_current_streak()
    {
        // Days 0, 1, 2 (consecutive), then gap, then day 5, 6
        WriteTrackingEvent("suggestion_full_accept",
            timestamp: DateTime.UtcNow, acceptedText: "today");
        WriteTrackingEvent("suggestion_full_accept",
            timestamp: DateTime.UtcNow.AddDays(-1), acceptedText: "yesterday");
        WriteTrackingEvent("suggestion_full_accept",
            timestamp: DateTime.UtcNow.AddDays(-2), acceptedText: "two days ago");
        // gap on day -3, -4
        WriteTrackingEvent("suggestion_full_accept",
            timestamp: DateTime.UtcNow.AddDays(-5), acceptedText: "five days ago");

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Equal(3, store.CurrentStreak);
        Assert.True(store.LongestStreak >= 3);
    }

    // ── Milestones ────────────────────────────────────────────────────────

    [Fact]
    public void Milestone_triggered_at_threshold()
    {
        for (int i = 0; i < 10; i++)
        {
            WriteTrackingEvent("suggestion_full_accept",
                timestamp: DateTime.UtcNow.AddSeconds(i), acceptedText: $"text {i}");
        }

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Contains(store.AchievedMilestones, m => m.Id == "accept_10");
    }

    [Fact]
    public void Milestone_not_triggered_below_threshold()
    {
        for (int i = 0; i < 5; i++)
        {
            WriteTrackingEvent("suggestion_full_accept",
                timestamp: DateTime.UtcNow.AddSeconds(i), acceptedText: $"text {i}");
        }

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.DoesNotContain(store.AchievedMilestones, m => m.Id == "accept_10");
    }

    [Fact]
    public void Native_milestone_uses_native_count()
    {
        for (int i = 0; i < 10; i++)
        {
            WriteTrackingEvent("manual_continuation_committed",
                timestamp: DateTime.UtcNow.AddSeconds(i), acceptedText: $"native {i}");
        }

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.Contains(store.AchievedMilestones, m => m.Id == "native_10");
    }

    // ── Weekly summaries ──────────────────────────────────────────────────

    [Fact]
    public void Weekly_summaries_are_generated()
    {
        for (int i = 0; i < 7; i++)
        {
            WriteTrackingEvent("suggestion_full_accept",
                timestamp: DateTime.UtcNow.AddDays(-i), acceptedText: $"text {i}");
        }

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        Assert.True(store.WeeklySummaries.Count >= 1);
        var week = store.WeeklySummaries[0];
        Assert.True(week.TotalAccepted >= 1);
        Assert.True(week.AcceptanceRate > 0);
    }

    [Fact]
    public void Current_and_previous_week_queries_work()
    {
        WriteTrackingEvent("suggestion_full_accept", acceptedText: "this week");
        WriteTrackingEvent("suggestion_full_accept",
            timestamp: DateTime.UtcNow.AddDays(-8), acceptedText: "last week");

        var svc = CreateService();
        svc.Refresh();

        var current = svc.GetCurrentWeekSummary();
        Assert.NotNull(current);
        Assert.True(current!.TotalAccepted >= 1);

        // Previous week may or may not exist depending on exact day alignment
        // Just verify the method doesn't throw
        _ = svc.GetPreviousWeekSummary();
    }

    // ── Score snapshots ───────────────────────────────────────────────────

    [Fact]
    public void Score_snapshots_are_recorded()
    {
        var svc = CreateService();
        svc.RecordScoreSnapshot("Email", 72);
        svc.RecordScoreSnapshot("Chat", 55);

        var emailHistory = svc.GetScoreHistory("Email");
        var chatHistory = svc.GetScoreHistory("Chat");

        Assert.Single(emailHistory);
        Assert.Equal(72, emailHistory[0].Score);
        Assert.Single(chatHistory);
        Assert.Equal(55, chatHistory[0].Score);
    }

    [Fact]
    public void Score_snapshots_update_same_day()
    {
        var svc = CreateService();
        svc.RecordScoreSnapshot("Email", 70);
        svc.RecordScoreSnapshot("Email", 75);

        var history = svc.GetScoreHistory("Email");
        Assert.Single(history);
        Assert.Equal(75, history[0].Score); // updated, not appended
    }

    [Fact]
    public void Weighted_average_score_is_correct()
    {
        var svc = CreateService();
        svc.RecordScoreSnapshot("Email", 80);
        svc.RecordScoreSnapshot("Chat", 60);

        Assert.Equal(70, svc.GetWeightedAverageScore());
    }

    // ── Pruning ───────────────────────────────────────────────────────────

    [Fact]
    public void Old_rollups_are_pruned()
    {
        var svc = CreateService();
        // Generate 100 days of data
        for (int i = 0; i < 100; i++)
        {
            WriteTrackingEvent("suggestion_full_accept",
                timestamp: DateTime.UtcNow.AddDays(-i).AddSeconds(i), // ensure distinct timestamps
                acceptedText: $"day {i}");
        }

        svc.Refresh();
        var store = svc.GetStore();

        Assert.True(store.Rollups.Count <= 90);
    }

    // ── Persistence ───────────────────────────────────────────────────────

    [Fact]
    public void Store_survives_reload()
    {
        WriteTrackingEvent("suggestion_full_accept", acceptedText: "persisted text");

        var svc1 = CreateService();
        svc1.Refresh();
        Assert.Equal(1, svc1.GetStore().CumulativeAccepted);

        // Create new service pointing to same file — should load persisted data
        var svc2 = CreateService();
        Assert.Equal(1, svc2.GetStore().CumulativeAccepted);
    }

    // ── Next milestone ────────────────────────────────────────────────────

    [Fact]
    public void Next_milestone_returns_correct_progress()
    {
        for (int i = 0; i < 5; i++)
        {
            WriteTrackingEvent("suggestion_full_accept",
                timestamp: DateTime.UtcNow.AddSeconds(i), acceptedText: $"text {i}");
        }

        var svc = CreateService();
        svc.Refresh();
        var next = svc.GetNextMilestone();

        Assert.NotNull(next);
        Assert.Equal("accept_10", next!.Value.Id);
        Assert.Equal(5, next.Value.Current);
        Assert.Equal(10, next.Value.Threshold);
    }

    // ── Hour distribution ─────────────────────────────────────────────────

    [Fact]
    public void Hour_distribution_is_populated()
    {
        WriteTrackingEvent("suggestion_full_accept", acceptedText: "morning text");
        WriteTrackingEvent("suggestion_dismiss");

        var svc = CreateService();
        svc.Refresh();
        var store = svc.GetStore();

        var rollup = store.Rollups[0];
        Assert.True(rollup.HourAcceptDistribution.Sum() >= 1);
        Assert.True(rollup.HourDismissDistribution.Sum() >= 1);
    }
}
