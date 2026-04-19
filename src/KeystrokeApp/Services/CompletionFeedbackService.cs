using System;
using System.IO;

namespace KeystrokeApp.Services;

/// <summary>
/// Tracks prediction acceptance/dismissal for the learning system.
/// Writes structured events to the SQLite learning database.
///
/// Sub-Phase A enrichment: accepted entries carry latencyMs, cycleDepth,
/// editedAfter, and a derived qualityScore so the learning service can weight
/// past evidence by how well it actually matched the user's intent.
/// </summary>
public class CompletionFeedbackService
{
    private readonly LearningDatabase? _database;
    private readonly LearningContextPreferencesService _preferences;
    private readonly ContextFingerprintService _fingerprints;

    public CompletionFeedbackService(
        LearningContextPreferencesService preferences,
        LearningDatabase? database = null,
        ContextFingerprintService? fingerprints = null)
    {
        _database = database;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _fingerprints = fingerprints ?? new ContextFingerprintService();
    }

    // ── Public logging API ────────────────────────────────────────────────────

    /// <summary>
    /// Log a full suggestion acceptance (Tab key).
    /// </summary>
    /// <param name="latencyMs">
    /// Milliseconds between the suggestion becoming visible and Tab being pressed.
    /// Pass -1 if the timestamp was not captured (e.g. legacy code path).
    /// </param>
    /// <param name="cycleDepth">
    /// How many alternatives the user scrolled through before accepting.
    /// 0 = first suggestion taken immediately (strong positive signal).
    /// </param>
    /// <param name="editedAfter">
    /// True if the user pressed Backspace within 1500ms of the injection,
    /// indicating the completion was accepted but immediately corrected.
    /// </param>
    public void LogAccepted(
        string prefix, string completion,
        string processName, string windowTitle,
        int latencyMs = -1, int cycleDepth = 0, bool editedAfter = false)
    {
        var quality = ComputeQualityScore(latencyMs, cycleDepth, editedAfter);
        WriteEntry("accepted", prefix, completion, processName, windowTitle,
                   latencyMs, cycleDepth, editedAfter, quality);
    }

    public void LogDismissed(string prefix, string completion, string processName, string windowTitle)
    {
        // Dismissed entries have no timing/cycle signals — they were never accepted.
        WriteEntry("dismissed", prefix, completion, processName, windowTitle,
                   latencyMs: -1, cycleDepth: 0, editedAfter: false, qualityScore: 0f);
    }

    public void LogIgnored(string prefix, string completion, string processName, string windowTitle)
    {
        // Ignored entries have no learning value — skip them.
    }

    // ── Quality score ─────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a 0.0–1.0 quality score from the three behavioural signals.
    ///
    /// Weights:
    ///   Latency  → up to 0.50  (how instantly the user accepted)
    ///   Cycling  → up to 0.30  (did they take the first suggestion?)
    ///   No edit  → up to 0.20  (did they keep what was injected?)
    /// </summary>
    public static float ComputeQualityScore(int latencyMs, int cycleDepth, bool editedAfter)
    {
        // Latency component — unknown (-1) gets a neutral 0.5
        float latencyScore = latencyMs < 0    ? 0.5f
            : latencyMs < 300  ? 1.0f   // instant → excellent
            : latencyMs < 700  ? 0.7f   // quick   → good
            : latencyMs < 1500 ? 0.4f   // hesitant → marginal
            : 0.1f;                      // slow    → poor signal

        // Cycle depth component
        float cycleScore = cycleDepth switch
        {
            0 => 1.00f,   // first suggestion: perfect
            1 => 0.67f,
            2 => 0.33f,
            _ => 0.00f    // ≥3 cycles: the top suggestion was clearly wrong
        };

        // Post-edit component
        float editScore = editedAfter ? 0.0f : 1.0f;

        return (latencyScore * 0.50f) + (cycleScore * 0.30f) + (editScore * 0.20f);
    }

    // ── Internal write ────────────────────────────────────────────────────────

    private void WriteEntry(
        string action, string prefix, string completion,
        string processName, string windowTitle,
        int latencyMs, int cycleDepth, bool editedAfter, float qualityScore)
    {
        try
        {
            var fingerprint = _fingerprints.Create(processName, windowTitle);
            if (_preferences.IsDisabled(fingerprint.SubcontextKey))
                return;

            var eventType = action switch
            {
                "accepted" => "suggestion_full_accept",
                "dismissed" => "suggestion_dismiss",
                _ => ""
            };
            if (string.IsNullOrEmpty(eventType)) return;

            var record = new LearningEventRecord
            {
                TimestampUtc = DateTime.UtcNow,
                EventType = eventType,
                ProcessName = processName,
                Category = fingerprint.Category,
                SafeContextLabel = fingerprint.SafeContextLabel,
                ContextKeys = new LearningEventContextKeys
                {
                    ProcessKey = fingerprint.ProcessKey,
                    WindowKey = fingerprint.WindowKey,
                    SubcontextKey = fingerprint.SubcontextKey,
                    ProcessLabel = fingerprint.ProcessLabel,
                    WindowLabel = fingerprint.WindowLabel,
                    SubcontextLabel = fingerprint.SubcontextLabel
                },
                TypedPrefix = PiiFilter.Scrub(prefix) ?? "",
                ShownCompletion = PiiFilter.Scrub(completion) ?? "",
                AcceptedText = action == "accepted" ? (PiiFilter.Scrub(completion) ?? "") : "",
                LatencyMs = latencyMs,
                CycleDepth = cycleDepth,
                EditedAfterAccept = editedAfter,
                QualityScore = MathF.Round(qualityScore, 3),
                SourceWeight = action == "accepted"
                    ? (editedAfter ? 0.35f : 0.5f)
                    : 1.0f
            };

            _database?.InsertEvent(record);
        }
        catch (Exception) { /* Write failure is non-fatal */ }
    }

    /// <summary>
    /// Strips document-specific details from window titles to reduce privacy exposure.
    /// e.g., "Budget 2026.xlsx - Excel" → "Excel"
    /// </summary>
    private static string StripWindowDetail(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
            return windowTitle;

        var lastDash = windowTitle.LastIndexOf(" - ", StringComparison.Ordinal);
        if (lastDash >= 0 && lastDash + 3 < windowTitle.Length)
            return windowTitle[(lastDash + 3)..];

        return windowTitle;
    }
}
