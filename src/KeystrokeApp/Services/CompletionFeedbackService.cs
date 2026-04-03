using System;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Tracks prediction acceptance/dismissal for the learning system.
/// Writes structured JSONL to %AppData%/Keystroke/completions.jsonl.
///
/// Sub-Phase A enrichment: accepted entries now carry latencyMs, cycleDepth,
/// editedAfter, and a derived qualityScore so the learning service can weight
/// past evidence by how well it actually matched the user's intent.
/// </summary>
public class CompletionFeedbackService
{
    private readonly string _dataPath;
    private readonly object _writeLock = new();

    public CompletionFeedbackService()
    {
        _dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "completions.jsonl");
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
        WriteEntry("ignored", prefix, completion, processName, windowTitle,
                   latencyMs: -1, cycleDepth: 0, editedAfter: false, qualityScore: 0f);
    }

    /// <summary>
    /// If the completions file exceeds maxLines, rewrites it keeping only the most recent entries.
    /// Call once at startup — keeps the file from growing unbounded over months of use.
    /// </summary>
    public void PruneIfNeeded(int maxLines = 2000)
    {
        try
        {
            if (!File.Exists(_dataPath))
                return;

            var lines = File.ReadAllLines(_dataPath);
            if (lines.Length <= maxLines)
                return;

            var trimmed  = lines[^maxLines..];
            var tempPath = _dataPath + ".tmp";
            File.WriteAllLines(tempPath, trimmed);
            File.Move(tempPath, _dataPath, overwrite: true);
        }
        catch (Exception) { /* Pruning failure is non-fatal */ }
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
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);

            var entry = new
            {
                timestamp    = DateTime.UtcNow.ToString("o"),
                action,
                prefix       = PiiFilter.Scrub(prefix),
                completion   = PiiFilter.Scrub(completion),
                app          = processName,
                window       = StripWindowDetail(windowTitle),
                category     = AppCategory.GetEffectiveCategory(processName, windowTitle).ToString(),
                // Sub-Phase A signal fields (always written; -1/0/false/0.5 for non-accepted entries)
                latencyMs,
                cycleDepth,
                editedAfter,
                qualityScore = MathF.Round(qualityScore, 3)
            };

            var json = JsonSerializer.Serialize(entry);

            lock (_writeLock)
            {
                File.AppendAllText(_dataPath, json + "\n");
            }
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
