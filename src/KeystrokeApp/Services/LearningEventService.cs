using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

public sealed class LearningEventService
{
    private readonly string _dataPath;
    private readonly LearningContextPreferencesService _preferences;
    internal readonly object WriteLock = new();

    public LearningEventService(
        string? dataPath = null,
        LearningContextPreferencesService? preferences = null)
    {
        _dataPath = dataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke",
            "tracking.jsonl");
        _preferences = preferences ?? new LearningContextPreferencesService();
    }

    public string DataPath => _dataPath;

    public void Append(LearningEventRecord record)
    {
        try
        {
            if (_preferences.IsDisabled(record.ContextKeys.SubcontextKey))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);

            var sanitized = record with
            {
                TypedPrefix = PiiFilter.Scrub(record.TypedPrefix) ?? "",
                ShownCompletion = PiiFilter.Scrub(record.ShownCompletion) ?? "",
                AcceptedText = PiiFilter.Scrub(record.AcceptedText) ?? "",
                UserWrittenText = PiiFilter.Scrub(record.UserWrittenText) ?? ""
            };

            var json = JsonSerializer.Serialize(sanitized);
            lock (WriteLock)
            {
                File.AppendAllText(_dataPath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            // Learning events must never interrupt typing or prediction.
            Debug.WriteLine($"[LearningEvent] Append failed: {ex.Message}");
        }
    }

    public void PruneIfNeeded(int maxLines = 4000)
    {
        try
        {
            // Hold WriteLock for the entire read-modify-write so that
            // concurrent Append() calls can't insert lines between the
            // read and the atomic rename (which would silently lose them).
            lock (WriteLock)
            {
                if (!File.Exists(_dataPath))
                    return;

                var lines = File.ReadAllLines(_dataPath);
                if (lines.Length <= maxLines)
                    return;

                var tempPath = _dataPath + ".tmp";
                File.WriteAllLines(tempPath, lines[^maxLines..]);
                File.Move(tempPath, _dataPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LearningEvent] Prune failed: {ex.Message}");
        }
    }
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
