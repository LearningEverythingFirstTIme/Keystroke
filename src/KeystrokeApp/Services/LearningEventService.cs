using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KeystrokeApp.Services;

public sealed class LearningEventService
{
    private readonly LearningDatabase? _database;
    private readonly LearningContextPreferencesService _preferences;
    private readonly ReliabilityTraceService? _reliabilityTrace;

    // Throttles how often we trace repeated failures. Without this, a locked database
    // or full disk would flood the trace log on every keystroke worth recording.
    private int _consecutiveFailures;
    private DateTime _lastFailureTracedUtc = DateTime.MinValue;

    public LearningEventService(
        LearningContextPreferencesService preferences,
        LearningDatabase? database = null,
        ReliabilityTraceService? reliabilityTrace = null)
    {
        _database = database;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _reliabilityTrace = reliabilityTrace;
    }

    public void Append(LearningEventRecord record)
    {
        try
        {
            if (_preferences.IsDisabled(record.ContextKeys.SubcontextKey))
                return;

            var sanitized = record with
            {
                TypedPrefix = PiiFilter.Scrub(record.TypedPrefix) ?? "",
                ShownCompletion = PiiFilter.Scrub(record.ShownCompletion) ?? "",
                AcceptedText = PiiFilter.Scrub(record.AcceptedText) ?? "",
                UserWrittenText = PiiFilter.Scrub(record.UserWrittenText) ?? ""
            };

            _database?.InsertEvent(sanitized);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            // Learning events must never interrupt typing or prediction — but silently
            // dropping every write means the user has no signal that their learning
            // corpus has stopped growing. Trace the first failure + every 10th after
            // (throttled to once per minute) so the reliability log tells the story
            // even if nobody's watching Debug.
            var count = Interlocked.Increment(ref _consecutiveFailures);
            Debug.WriteLine($"[LearningEvent] Append failed (#{count}): {ex.Message}");

            var shouldTrace = count == 1 || count % 10 == 0;
            var now = DateTime.UtcNow;
            if (shouldTrace && (now - _lastFailureTracedUtc) > TimeSpan.FromSeconds(30))
            {
                _lastFailureTracedUtc = now;
                var data = new Dictionary<string, string>
                {
                    ["event_type"] = record.EventType,
                    ["consecutive_failures"] = count.ToString(),
                    ["exception"] = ex.GetType().Name,
                    ["message"] = Truncate(ex.Message, 200)
                };
                if (ex is SqliteException sqlEx)
                    data["sqlite_code"] = sqlEx.SqliteErrorCode.ToString();

                _reliabilityTrace?.Trace(
                    area: "learning",
                    eventName: "event_write_failed",
                    message: $"Learning event write failed: {ex.Message}",
                    data: data);
            }
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}

public sealed record LearningEventRecord
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; init; } = "";
    public string SuggestionId { get; init; } = "";
    public long RequestId { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string EventType { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string Category { get; init; } = "";
    public string SafeContextLabel { get; init; } = "";
    public LearningEventContextKeys ContextKeys { get; init; } = new();
    public string TypedPrefix { get; init; } = "";
    public string ShownCompletion { get; init; } = "";
    public string AcceptedText { get; init; } = "";
    public string UserWrittenText { get; init; } = "";
    public string CommitReason { get; init; } = "";
    public int LatencyMs { get; init; } = -1;
    public int CycleDepth { get; init; }
    public bool EditedAfterAccept { get; init; }
    public int UntouchedForMs { get; init; }
    public float QualityScore { get; init; } = 0.5f;
    public float SourceWeight { get; init; } = 0.5f;
    public double Confidence { get; init; } = 0.5;

    // ── Correction fields (Phase 1: Correction Learning) ─────────────────────
    // Populated when the user edits a suggestion immediately after accepting it.
    // BackspaceCount > 0 means the user deleted from the end of the completion;
    // CorrectedText contains the replacement they typed. Together these form a
    // (deleted → replaced) pair that CorrectionPatternService uses to learn
    // systematic editing patterns (word preferences, length, formality).

    /// <summary>What the user deleted from the end of the accepted completion.</summary>
    public string DeletedSuffix { get; init; } = "";

    /// <summary>What the user typed as replacement after deleting.</summary>
    public string CorrectedText { get; init; } = "";

    /// <summary>Net backspace count into the original completion.</summary>
    public int CorrectionBackspaces { get; init; }

    /// <summary>
    /// Classification: "none", "truncated" (deleted only),
    /// "replaced_ending" (deleted and retyped), or "minor" (1-2 chars, likely typo fix).
    /// </summary>
    public string CorrectionType { get; init; } = "";
}

public sealed record LearningEventContextKeys
{
    public string ProcessKey { get; init; } = "";
    public string WindowKey { get; init; } = "";
    public string SubcontextKey { get; init; } = "";
    public string ProcessLabel { get; init; } = "";
    public string WindowLabel { get; init; } = "";
    public string SubcontextLabel { get; init; } = "";
}
