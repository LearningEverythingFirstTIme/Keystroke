using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

/// <summary>
/// Covers PR 3 P1-7: LearningEventService surfaces SQLite errors to
/// ReliabilityTraceService with throttling, instead of silently dropping writes.
/// </summary>
public class LearningEventServiceTests : IDisposable
{
    private readonly string _tempDir;

    public LearningEventServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "keystroke-learning-event-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Append_SurfacesFirstFailureToReliabilityTrace()
    {
        // No EnsureCreated → events table missing → InsertEvent throws SqliteException.
        using var database = new LearningDatabase(Path.Combine(_tempDir, "unmigrated.db"));
        var preferences = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        var trace = new ReliabilityTraceService();

        var captured = new List<ReliabilityTraceEvent>();
        trace.EventRecorded += captured.Add;

        var service = new LearningEventService(preferences, database, trace);
        service.Append(NewRecord("suggestion_shown"));

        var learningFailures = captured.Where(e => e.Area == "learning" && e.EventName == "event_write_failed").ToList();
        Assert.Single(learningFailures);

        var evt = learningFailures[0];
        Assert.NotNull(evt.Data);
        Assert.Equal("suggestion_shown", evt.Data!["event_type"]);
        Assert.Equal("1", evt.Data["consecutive_failures"]);
        Assert.Contains("sqlite_code", evt.Data.Keys);
    }

    [Fact]
    public void Append_ThrottlesRepeatFailuresWithin30Seconds()
    {
        using var database = new LearningDatabase(Path.Combine(_tempDir, "unmigrated.db"));
        var preferences = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        var trace = new ReliabilityTraceService();

        var captured = new List<ReliabilityTraceEvent>();
        trace.EventRecorded += captured.Add;

        var service = new LearningEventService(preferences, database, trace);

        // First failure always traces; second/third within 30s dampener should not.
        for (var i = 0; i < 3; i++)
            service.Append(NewRecord("suggestion_shown"));

        var learningFailures = captured.Where(e => e.Area == "learning" && e.EventName == "event_write_failed").ToList();
        Assert.Single(learningFailures);
    }

    [Fact]
    public void Append_DoesNotTraceWhenWriteSucceeds()
    {
        using var database = new LearningDatabase(Path.Combine(_tempDir, "migrated.db"));
        database.EnsureCreated();
        var preferences = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        var trace = new ReliabilityTraceService();

        var captured = new List<ReliabilityTraceEvent>();
        trace.EventRecorded += captured.Add;

        var service = new LearningEventService(preferences, database, trace);
        service.Append(NewRecord("suggestion_shown"));

        Assert.Empty(captured.Where(e => e.Area == "learning"));
    }

    [Fact]
    public void Append_DoesNotThrowWhenDatabaseIsUnavailable()
    {
        using var database = new LearningDatabase(Path.Combine(_tempDir, "unmigrated.db"));
        var preferences = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        var service = new LearningEventService(preferences, database, reliabilityTrace: null);

        // Must never throw — typing must not be interrupted.
        var ex = Record.Exception(() => service.Append(NewRecord("suggestion_shown")));
        Assert.Null(ex);
    }

    [Fact]
    public void Append_SkipsDisabledContext()
    {
        using var database = new LearningDatabase(Path.Combine(_tempDir, "unmigrated.db"));
        var preferences = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        preferences.SetDisabled("ctx-off", "Private doc", "Writing", true);

        var trace = new ReliabilityTraceService();
        var captured = new List<ReliabilityTraceEvent>();
        trace.EventRecorded += captured.Add;

        var service = new LearningEventService(preferences, database, trace);

        // Database is unmigrated so any insert would throw; disabled context means
        // we must short-circuit before the insert attempt, so no failure is traced.
        service.Append(NewRecord("suggestion_shown", subcontextKey: "ctx-off"));

        Assert.Empty(captured.Where(e => e.Area == "learning"));
    }

    [Fact]
    public void Constructor_RejectsNullPreferences()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new LearningEventService(preferences: null!));
    }

    private static LearningEventRecord NewRecord(string eventType, string subcontextKey = "ctx-default")
        => new()
        {
            EventId = Guid.NewGuid().ToString("n"),
            EventType = eventType,
            TimestampUtc = DateTime.UtcNow,
            ContextKeys = new LearningEventContextKeys
            {
                SubcontextKey = subcontextKey,
                SubcontextLabel = "Test context"
            },
            TypedPrefix = "hello",
            ShownCompletion = " world"
        };

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
