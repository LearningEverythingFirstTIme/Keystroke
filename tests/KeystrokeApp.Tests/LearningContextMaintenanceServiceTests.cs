using System.Text.Json;
using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class LearningContextMaintenanceServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _eventPath;
    private readonly string _legacyPath;
    private readonly object _eventLock = new();
    private readonly object _legacyLock = new();

    public LearningContextMaintenanceServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "keystroke-maintenance-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
        _eventPath = Path.Combine(_tempDir, "tracking.jsonl");
        _legacyPath = Path.Combine(_tempDir, "completions.jsonl");
    }

    // ── ClearAssistData tests ────────────────────────────────────────────────

    [Fact]
    public void ClearAssistData_RemovesOnlyAcceptanceEventsForMatchingContext()
    {
        var targetContext = "ctx-slack-team";

        // Write mix of assist and native events for target context
        var events = new[]
        {
            MakeRecord("suggestion_full_accept", targetContext, "accepted completion"),
            MakeRecord("manual_continuation_committed", targetContext, userText: "typed this myself"),
            MakeRecord("suggestion_dismiss", targetContext, "dismissed completion"),
            MakeRecord("accepted_text_untouched", targetContext, "untouched accept"),
            MakeRecord("suggestion_partial_accept", targetContext, "partial accept"),
            MakeRecord("suggestion_typed_past", targetContext, "typed past it"),
        };
        WriteEvents(events);

        var service = CreateService();
        service.ClearAssistData(targetContext);

        var remaining = ReadRemainingEvents();

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

        var events = new[]
        {
            MakeRecord("suggestion_full_accept", targetContext, "target accept"),
            MakeRecord("suggestion_full_accept", otherContext, "other accept"),
            MakeRecord("manual_continuation_committed", targetContext, userText: "target native"),
            MakeRecord("manual_continuation_committed", otherContext, userText: "other native"),
        };
        WriteEvents(events);

        var service = CreateService();
        service.ClearAssistData(targetContext);

        var remaining = ReadRemainingEvents();

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
        var events = new[]
        {
            MakeRecord("suggestion_full_accept", "ctx-something", "accept"),
        };
        WriteEvents(events);

        var service = CreateService();
        service.ClearAssistData("");

        var remaining = ReadRemainingEvents();
        Assert.Single(remaining);
    }

    [Fact]
    public void ClearAssistData_NonexistentFile_DoesNotThrow()
    {
        var service = new LearningContextMaintenanceService(
            legacyPath: Path.Combine(_tempDir, "nonexistent-legacy.jsonl"),
            eventPath: Path.Combine(_tempDir, "nonexistent-events.jsonl"),
            appDataPath: _tempDir);

        // Should not throw
        service.ClearAssistData("ctx-anything");
    }

    [Fact]
    public void ClearAssistData_CaseInsensitiveContextMatching()
    {
        var events = new[]
        {
            MakeRecord("suggestion_full_accept", "CTX-SLACK-Team", "accept 1"),
            MakeRecord("suggestion_full_accept", "ctx-slack-team", "accept 2"),
        };
        WriteEvents(events);

        var service = CreateService();
        service.ClearAssistData("ctx-slack-team");

        var remaining = ReadRemainingEvents();
        Assert.Empty(remaining);
    }

    // ── ClearContext tests ───────────────────────────────────────────────────

    [Fact]
    public void ClearContext_RemovesAllEventsForMatchingContext()
    {
        var targetContext = "ctx-slack-team";

        var events = new[]
        {
            MakeRecord("suggestion_full_accept", targetContext, "accept"),
            MakeRecord("manual_continuation_committed", targetContext, userText: "native"),
            MakeRecord("suggestion_dismiss", targetContext, "dismiss"),
            MakeRecord("suggestion_full_accept", "ctx-other", "other accept"),
        };
        WriteEvents(events);

        var service = CreateService();
        service.ClearContext(targetContext);

        var remaining = ReadRemainingEvents();
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
            legacyPath: _legacyPath,
            eventPath: _eventPath,
            appDataPath: _tempDir,
            eventWriteLock: _eventLock,
            legacyWriteLock: _legacyLock);

    private static LearningEventRecord MakeRecord(
        string eventType,
        string subcontextKey,
        string? acceptedText = null,
        string? userText = null) =>
        new()
        {
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

    private void WriteEvents(LearningEventRecord[] records)
    {
        var lines = records.Select(r => JsonSerializer.Serialize(r));
        File.WriteAllLines(_eventPath, lines);
    }

    private List<LearningEventRecord> ReadRemainingEvents()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadAllLines(_eventPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<LearningEventRecord>(l, options)!)
            .ToList();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
