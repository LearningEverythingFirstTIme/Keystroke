using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class LearningContextMaintenanceServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LearningDatabase _database;

    public LearningContextMaintenanceServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "keystroke-maintenance-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
        _database = new LearningDatabase(Path.Combine(_tempDir, "learning.db"));
        _database.EnsureCreated();
    }

    // ── ClearAssistData tests ────────────────────────────────────────────────

    [Fact]
    public void ClearAssistData_RemovesOnlyAcceptanceEventsForMatchingContext()
    {
        var targetContext = "ctx-slack-team";

        // Write mix of assist and native events for target context
        WriteEvents(
            MakeRecord("suggestion_full_accept", targetContext, "accepted completion"),
            MakeRecord("manual_continuation_committed", targetContext, userText: "typed this myself"),
            MakeRecord("suggestion_dismiss", targetContext, "dismissed completion"),
            MakeRecord("accepted_text_untouched", targetContext, "untouched accept"),
            MakeRecord("suggestion_partial_accept", targetContext, "partial accept"),
            MakeRecord("suggestion_typed_past", targetContext, "typed past it"));

        var service = CreateService();
        service.ClearAssistData(targetContext);

        var remaining = _database.GetAllEvents();

        // Kept: manual_continuation_committed, suggestion_dismiss, suggestion_typed_past
        // Removed: suggestion_full_accept, accepted_text_untouched, suggestion_partial_accept
        Assert.Equal(3, remaining.Count);
        Assert.Contains(remaining, r => r.EventType == "manual_continuation_committed");
        Assert.Contains(remaining, r => r.EventType == "suggestion_dismiss");
        Assert.Contains(remaining, r => r.EventType == "suggestion_typed_past");
    }

    [Fact]
    public void ClearAssistData_PreservesEventsFromOtherContexts()
    {
        var targetContext = "ctx-slack-team";
        var otherContext = "ctx-email-inbox";

        WriteEvents(
            MakeRecord("suggestion_full_accept", targetContext, "target accept"),
            MakeRecord("suggestion_full_accept", otherContext, "other accept"),
            MakeRecord("manual_continuation_committed", targetContext, userText: "target native"),
            MakeRecord("manual_continuation_committed", otherContext, userText: "other native"));

        var service = CreateService();
        service.ClearAssistData(targetContext);

        var remaining = _database.GetAllEvents();

        // Target accept removed, everything else kept
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining,
            r => r.EventType == "suggestion_full_accept" &&
                 r.ContextKeys.SubcontextKey == targetContext);
        Assert.Contains(remaining,
            r => r.EventType == "suggestion_full_accept" &&
                 r.ContextKeys.SubcontextKey == otherContext);
    }

    [Fact]
    public void ClearAssistData_EmptyContextKey_NoOp()
    {
        WriteEvents(MakeRecord("suggestion_full_accept", "ctx-something", "accept"));

        var service = CreateService();
        service.ClearAssistData("");

        var remaining = _database.GetAllEvents();
        Assert.Single(remaining);
    }

    [Fact]
    public void ClearAssistData_NullDatabase_DoesNotThrow()
    {
        var service = new LearningContextMaintenanceService(
            database: null,
            appDataPath: _tempDir);

        // Should not throw
        service.ClearAssistData("ctx-anything");
    }

    [Fact]
    public void ClearAssistData_ExactContextMatching()
    {
        // SubcontextKey matching is exact (SQL WHERE = binary collation), so only
        // the exact-cased key is removed.
        WriteEvents(
            MakeRecord("suggestion_full_accept", "CTX-SLACK-Team", "accept 1"),
            MakeRecord("suggestion_full_accept", "ctx-slack-team", "accept 2"));

        var service = CreateService();
        service.ClearAssistData("ctx-slack-team");

        var remaining = _database.GetAllEvents();
        Assert.Single(remaining);
        Assert.Equal("CTX-SLACK-Team", remaining[0].ContextKeys.SubcontextKey);
    }

    // ── ClearContext tests ───────────────────────────────────────────────────

    [Fact]
    public void ClearContext_RemovesAllEventsForMatchingContext()
    {
        var targetContext = "ctx-slack-team";

        WriteEvents(
            MakeRecord("suggestion_full_accept", targetContext, "accept"),
            MakeRecord("manual_continuation_committed", targetContext, userText: "native"),
            MakeRecord("suggestion_dismiss", targetContext, "dismiss"),
            MakeRecord("suggestion_full_accept", "ctx-other", "other accept"));

        var service = CreateService();
        service.ClearContext(targetContext);

        var remaining = _database.GetAllEvents();
        Assert.Single(remaining);
        Assert.Equal("ctx-other", remaining[0].ContextKeys.SubcontextKey);
    }

    // ── InvalidateDerivedArtifacts tests ─────────────────────────────────────

    [Fact]
    public void InvalidateDerivedArtifacts_DeletesAllDerivedFiles()
    {
        var artifacts = new[]
        {
            "style-profile.json",
            "vocabulary-profile.json",
            "learning-scores.json",
            "correction-patterns.json",
            "context-adaptive-settings.json"
        };

        foreach (var artifact in artifacts)
            File.WriteAllText(Path.Combine(_tempDir, artifact), "{}");

        var service = CreateService();
        service.InvalidateDerivedArtifacts();

        foreach (var artifact in artifacts)
            Assert.False(File.Exists(Path.Combine(_tempDir, artifact)),
                $"Expected {artifact} to be deleted");
    }

    [Fact]
    public void InvalidateDerivedArtifacts_DoesNotThrow_WhenFilesDoNotExist()
    {
        var service = CreateService();
        // Should not throw even though none of the artifact files exist
        service.InvalidateDerivedArtifacts();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private LearningContextMaintenanceService CreateService() =>
        new(
            database: _database,
            appDataPath: _tempDir);

    private static LearningEventRecord MakeRecord(
        string eventType,
        string subcontextKey,
        string? acceptedText = null,
        string? userText = null) =>
        new()
        {
            EventId = Guid.NewGuid().ToString("n"),
            TimestampUtc = DateTime.UtcNow,
            EventType = eventType,
            ProcessName = "testapp",
            Category = "Chat",
            AcceptedText = acceptedText ?? "",
            UserWrittenText = userText ?? "",
            ContextKeys = new LearningEventContextKeys
            {
                SubcontextKey = subcontextKey,
                SubcontextLabel = subcontextKey
            }
        };

    private void WriteEvents(params LearningEventRecord[] records)
    {
        foreach (var record in records)
            _database.InsertEvent(record);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
