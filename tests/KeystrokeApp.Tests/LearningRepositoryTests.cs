using System.Text.Json;
using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class LearningRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public LearningRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "keystroke-learning-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void GetSnapshot_DedupesLegacyRecordsWhenDualWrittenV2EventExists()
    {
        var timestamp = DateTime.UtcNow;
        var prefs = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        var repo = new LearningRepository(
            preferences: prefs,
            legacyPath: Path.Combine(_tempDir, "completions.jsonl"),
            eventPath: Path.Combine(_tempDir, "learning-events.v2.jsonl"));

        File.WriteAllLines(Path.Combine(_tempDir, "completions.jsonl"),
        [
            JsonSerializer.Serialize(new
            {
                timestamp = timestamp,
                action = "accepted",
                prefix = "Thanks",
                completion = " for the update",
                app = "chrome",
                window = "Gmail",
                category = "Email",
                editedAfter = false,
                qualityScore = 0.8f
            })
        ]);

        File.WriteAllLines(Path.Combine(_tempDir, "learning-events.v2.jsonl"),
        [
            JsonSerializer.Serialize(new LearningEventRecord
            {
                TimestampUtc = timestamp.AddSeconds(1),
                EventType = "accepted_text_untouched",
                ProcessName = "chrome",
                Category = "Email",
                TypedPrefix = "Thanks",
                AcceptedText = " for the update",
                ContextKeys = new LearningEventContextKeys
                {
                    ProcessKey = "proc",
                    WindowKey = "win",
                    SubcontextKey = "ctx-email",
                    SubcontextLabel = "Project Inbox"
                },
                SourceWeight = 0.7f,
                QualityScore = 0.9f
            })
        ]);

        var snapshot = repo.GetSnapshot(forceRefresh: true);

        Assert.Single(snapshot.PositiveEvidence);
        Assert.Equal(1, snapshot.DedupedLegacyCount);
        Assert.Equal(LearningSourceType.AssistAcceptedUntouched, snapshot.PositiveEvidence[0].SourceType);
    }

    [Fact]
    public void GetSnapshot_PrefersAcceptedTextUntouchedOverMatchingFullAccept()
    {
        var timestamp = DateTime.UtcNow;
        var repo = new LearningRepository(
            legacyPath: Path.Combine(_tempDir, "completions.jsonl"),
            eventPath: Path.Combine(_tempDir, "learning-events.v2.jsonl"));

        File.WriteAllText(Path.Combine(_tempDir, "completions.jsonl"), string.Empty);
        File.WriteAllLines(Path.Combine(_tempDir, "learning-events.v2.jsonl"),
        [
            JsonSerializer.Serialize(new LearningEventRecord
            {
                SuggestionId = "s-1",
                RequestId = 42,
                TimestampUtc = timestamp,
                EventType = "suggestion_full_accept",
                ProcessName = "chrome",
                Category = "Email",
                TypedPrefix = "Thanks",
                AcceptedText = " for the update",
                ContextKeys = new LearningEventContextKeys { SubcontextKey = "ctx-email", SubcontextLabel = "Project Inbox" },
                SourceWeight = 0.55f,
                QualityScore = 0.75f
            }),
            JsonSerializer.Serialize(new LearningEventRecord
            {
                SuggestionId = "s-1",
                RequestId = 42,
                TimestampUtc = timestamp.AddMilliseconds(200),
                EventType = "accepted_text_untouched",
                ProcessName = "chrome",
                Category = "Email",
                TypedPrefix = "Thanks",
                AcceptedText = " for the update",
                ContextKeys = new LearningEventContextKeys { SubcontextKey = "ctx-email", SubcontextLabel = "Project Inbox" },
                SourceWeight = 0.7f,
                QualityScore = 0.9f
            })
        ]);

        var snapshot = repo.GetSnapshot(forceRefresh: true);

        Assert.Single(snapshot.PositiveEvidence);
        Assert.Equal(LearningSourceType.AssistAcceptedUntouched, snapshot.PositiveEvidence[0].SourceType);
    }

    [Fact]
    public void GetSnapshot_FiltersDisabledContextFromActiveEvidenceButKeepsSummary()
    {
        var prefs = new LearningContextPreferencesService(Path.Combine(_tempDir, "prefs.json"));
        prefs.SetDisabled("ctx-disabled", "Alex thread", "Chat", true);

        var repo = new LearningRepository(
            preferences: prefs,
            legacyPath: Path.Combine(_tempDir, "completions.jsonl"),
            eventPath: Path.Combine(_tempDir, "learning-events.v2.jsonl"));

        File.WriteAllText(Path.Combine(_tempDir, "completions.jsonl"), string.Empty);
        File.WriteAllLines(Path.Combine(_tempDir, "learning-events.v2.jsonl"),
        [
            JsonSerializer.Serialize(new LearningEventRecord
            {
                TimestampUtc = DateTime.UtcNow.AddMinutes(-2),
                EventType = "manual_continuation_committed",
                ProcessName = "slack",
                Category = "Chat",
                UserWrittenText = "let's ship it",
                ContextKeys = new LearningEventContextKeys
                {
                    SubcontextKey = "ctx-disabled",
                    SubcontextLabel = "Alex thread"
                },
                SourceWeight = 1.0f,
                QualityScore = 1.0f
            }),
            JsonSerializer.Serialize(new LearningEventRecord
            {
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
                EventType = "manual_continuation_committed",
                ProcessName = "slack",
                Category = "Chat",
                UserWrittenText = "sounds good to me",
                ContextKeys = new LearningEventContextKeys
                {
                    SubcontextKey = "ctx-active",
                    SubcontextLabel = "Jordan thread"
                },
                SourceWeight = 1.0f,
                QualityScore = 1.0f
            })
        ]);

        var snapshot = repo.GetSnapshot(forceRefresh: true);

        Assert.Single(snapshot.PositiveEvidence);
        Assert.Equal("ctx-active", snapshot.PositiveEvidence[0].SubcontextKey);
        Assert.True(snapshot.Contexts["ctx-disabled"].IsDisabled);
        Assert.Equal(1, snapshot.Contexts["ctx-disabled"].NativeCount);
    }

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
