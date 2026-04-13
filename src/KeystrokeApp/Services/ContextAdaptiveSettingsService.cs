using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Computes per-context adaptive settings from the user's acceptance/dismissal history.
/// Instead of using global or hardcoded-per-category temperature and length presets,
/// this service learns what works best in each specific context:
///
///   - A user who consistently accepts short completions in Slack gets "brief" length there
///   - A user who dismisses most suggestions in a code editor gets higher temperature for variety
///   - A user who accepts everything instantly in email gets lower temperature for precision
///
/// All computation is deterministic — no LLM call. Settings are persisted to
/// context-adaptive-settings.json and recomputed every N acceptances.
/// </summary>
public class ContextAdaptiveSettingsService
{
    // ── Thresholds ────────────────────────────────────────────────────────────
    private const int MinEventsForAdaptation = 10;
    private const int MinEventsPerCategory = 15;
    private const int MaxContextsTracked = 50;

    /// <summary>Settings older than this are suppressed to prevent stale adaptation.</summary>
    private static readonly TimeSpan MaxSettingsAge = TimeSpan.FromDays(14);

    // ── File paths ────────────────────────────────────────────────────────────
    private readonly string _settingsPath;
    private readonly string _dataPath;
    private readonly string _logPath;

    // ── State ─────────────────────────────────────────────────────────────────
    private AdaptiveSettingsData? _settings;
    private int _acceptCount;
    private int _recomputeInterval;
    private bool _isGenerating;
    private CancellationTokenSource? _generateCts;
    private readonly object _lock = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public ContextAdaptiveSettingsService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _settingsPath = Path.Combine(appData, "context-adaptive-settings.json");
        _dataPath = Path.Combine(appData, "tracking.jsonl");
        _logPath = Path.Combine(appData, "context-adaptive.log");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start(int interval)
    {
        _recomputeInterval = interval;
        LoadSettings();
        Log($"Started. Interval={interval}, HasSettings={_settings != null}");
    }

    public void UpdateInterval(int interval) => _recomputeInterval = interval;

    /// <summary>
    /// Called on every acceptance. Triggers recomputation when enough events accumulate.
    /// </summary>
    public void OnAccepted()
    {
        lock (_lock)
        {
            _acceptCount++;
            if (_acceptCount >= _recomputeInterval && !_isGenerating)
            {
                _acceptCount = 0;
                _ = Task.Run(RecomputeAsync).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Log($"Unobserved error: {t.Exception.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    /// <summary>
    /// Returns the adaptive profile for a specific subcontext, falling back to category,
    /// or null if insufficient data exists for meaningful adaptation.
    /// </summary>
    public ContextAdaptiveProfile? GetSettings(string? subcontextKey, string category)
    {
        lock (_lock)
        {
            if (_settings == null) return null;
            if ((DateTime.UtcNow - _settings.LastUpdated) > MaxSettingsAge) return null;

            // Try subcontext first (most specific)
            if (!string.IsNullOrWhiteSpace(subcontextKey) &&
                _settings.Contexts.TryGetValue(subcontextKey, out var contextProfile) &&
                contextProfile.HasSufficientData)
                return contextProfile;

            // Fall back to category
            if (_settings.Categories.TryGetValue(category, out var categoryProfile) &&
                categoryProfile.HasSufficientData)
                return categoryProfile;

            return null;
        }
    }

    public AdaptiveSettingsData? GetAllSettings()
    {
        lock (_lock) return _settings;
    }

    public void CancelGeneration()
    {
        lock (_lock) { _generateCts?.Cancel(); }
    }

    public void InvalidateSettings()
    {
        lock (_lock)
        {
            _generateCts?.Cancel();
            _settings = null;
            _acceptCount = 0;
            try
            {
                if (File.Exists(_settingsPath))
                    File.Delete(_settingsPath);
            }
            catch (Exception ex) { Log($"Invalidate error: {ex.Message}"); }
        }
    }

    // ── Recomputation ─────────────────────────────────────────────────────────

    private async Task RecomputeAsync()
    {
        lock (_lock)
        {
            _isGenerating = true;
            _generateCts?.Cancel();
            _generateCts?.Dispose();
            _generateCts = new CancellationTokenSource();
        }
        var ct = _generateCts!.Token;

        try
        {
            var events = LoadEvents();
            Log($"Recomputing from {events.Count} events...");

            var newSettings = new AdaptiveSettingsData
            {
                LastUpdated = DateTime.UtcNow,
                EventsProcessed = events.Count
            };

            // ── Per-context profiles ─────────────────────────────────────
            var contextGroups = events
                .Where(e => !string.IsNullOrWhiteSpace(e.SubcontextKey))
                .GroupBy(e => e.SubcontextKey)
                .Where(g => g.Count() >= MinEventsForAdaptation)
                .OrderByDescending(g => g.Count())
                .Take(MaxContextsTracked);

            foreach (var group in contextGroups)
            {
                if (ct.IsCancellationRequested) break;
                var profile = ComputeProfile(group.ToList());
                profile.ContextKey = group.Key;
                profile.Label = group.First().ContextLabel;
                profile.Category = group.First().Category;
                newSettings.Contexts[group.Key] = profile;
            }

            // ── Per-category profiles ────────────────────────────────────
            var categoryGroups = events
                .GroupBy(e => e.Category)
                .Where(g => g.Count() >= MinEventsPerCategory);

            foreach (var group in categoryGroups)
            {
                if (ct.IsCancellationRequested) break;
                var profile = ComputeProfile(group.ToList());
                profile.Category = group.Key;
                newSettings.Categories[group.Key] = profile;
            }

            lock (_lock)
            {
                if (ct.IsCancellationRequested) return;
                _settings = newSettings;
                SaveSettings(newSettings);
            }

            Log($"Recomputation complete: {newSettings.Contexts.Count} contexts, " +
                $"{newSettings.Categories.Count} categories");
        }
        catch (OperationCanceledException) { Log("Recomputation cancelled"); }
        catch (Exception ex) { Log($"Recompute error: {ex.Message}"); }
        finally { lock (_lock) { _isGenerating = false; } }

        await Task.CompletedTask;
    }

    // ── Profile computation ───────────────────────────────────────────────────

    private static ContextAdaptiveProfile ComputeProfile(List<AdaptiveEvent> events)
    {
        var accepted = events.Where(e => e.IsAccepted).ToList();
        var dismissed = events.Where(e => e.IsDismissed).ToList();

        int acceptedCount = accepted.Count;
        int dismissedCount = dismissed.Count;
        int totalWithOutcome = acceptedCount + dismissedCount;

        double acceptRate = totalWithOutcome > 0
            ? (double)acceptedCount / totalWithOutcome
            : 0.5;

        double avgWordCount = accepted.Count > 0
            ? accepted.Average(e => CountWords(e.Completion))
            : 12; // default

        double avgLatency = accepted.Where(e => e.LatencyMs > 0).ToList() is { Count: > 0 } withLatency
            ? withLatency.Average(e => e.LatencyMs)
            : 500;

        double avgQuality = accepted.Count > 0
            ? accepted.Average(e => e.QualityScore)
            : 0.5;

        // ── Derive temperature adjustment ────────────────────────────────
        // High accept rate + fast latency → model is already accurate, nudge temp down
        // Low accept rate → model needs variety, nudge temp up
        double tempAdjust = 0;
        if (totalWithOutcome >= MinEventsForAdaptation)
        {
            if (acceptRate >= 0.80 && avgLatency < 600)
                tempAdjust = -0.08;
            else if (acceptRate >= 0.70)
                tempAdjust = -0.03;
            else if (acceptRate <= 0.35)
                tempAdjust = 0.12;
            else if (acceptRate <= 0.50)
                tempAdjust = 0.06;
        }

        // ── Derive length preset ─────────────────────────────────────────
        string suggestedPreset;
        if (avgWordCount < 5)
            suggestedPreset = "brief";
        else if (avgWordCount < 12)
            suggestedPreset = "standard";
        else if (avgWordCount < 25)
            suggestedPreset = "extended";
        else
            suggestedPreset = "unlimited";

        return new ContextAdaptiveProfile
        {
            AcceptedCount = acceptedCount,
            DismissedCount = dismissedCount,
            AcceptRate = Math.Round(acceptRate, 3),
            AvgAcceptedWordCount = Math.Round(avgWordCount, 1),
            AvgLatencyMs = Math.Round(avgLatency, 0),
            AvgQualityScore = Math.Round(avgQuality, 3),
            TemperatureAdjustment = Math.Round(tempAdjust, 3),
            SuggestedLengthPreset = suggestedPreset
        };
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private List<AdaptiveEvent> LoadEvents()
    {
        var events = new List<AdaptiveEvent>();
        if (!File.Exists(_dataPath)) return events;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            foreach (var line in File.ReadLines(_dataPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<LearningEventRecord>(line, options);
                    if (record == null) continue;

                    // Only care about events with accept/dismiss outcome.
                    bool isAccepted = record.EventType is
                        "suggestion_full_accept" or
                        "accepted_text_untouched" or
                        "suggestion_partial_accept";
                    bool isDismissed = record.EventType is
                        "suggestion_dismiss" or
                        "suggestion_typed_past";

                    if (!isAccepted && !isDismissed) continue;

                    // Skip the bonus "accepted_text_untouched" events that duplicate
                    // "suggestion_full_accept" — count each acceptance only once.
                    if (record.EventType == "accepted_text_untouched")
                        continue;

                    events.Add(new AdaptiveEvent
                    {
                        Timestamp = record.TimestampUtc,
                        Category = record.Category,
                        SubcontextKey = record.ContextKeys.SubcontextKey,
                        ContextLabel = record.ContextKeys.SubcontextLabel,
                        Completion = isAccepted
                            ? (record.AcceptedText ?? record.ShownCompletion ?? "")
                            : (record.ShownCompletion ?? ""),
                        IsAccepted = isAccepted,
                        IsDismissed = isDismissed,
                        LatencyMs = record.LatencyMs,
                        QualityScore = record.QualityScore
                    });
                }
                catch (JsonException) { /* skip malformed */ }
            }
        }
        catch (IOException ex) { Log($"Read error: {ex.Message}"); }

        return events;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var json = File.ReadAllText(_settingsPath);
            _settings = JsonSerializer.Deserialize<AdaptiveSettingsData>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Log($"Loaded: {_settings?.Contexts.Count ?? 0} contexts, {_settings?.Categories.Count ?? 0} categories");
        }
        catch (Exception ex) { Log($"Load error: {ex}"); }
    }

    private void SaveSettings(AdaptiveSettingsData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex) { Log($"Save error: {ex}"); }
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Adaptive] {msg}\n"); }
        catch (IOException) { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private sealed class AdaptiveEvent
    {
        public DateTime Timestamp { get; init; }
        public string Category { get; init; } = "";
        public string SubcontextKey { get; init; } = "";
        public string ContextLabel { get; init; } = "";
        public string Completion { get; init; } = "";
        public bool IsAccepted { get; init; }
        public bool IsDismissed { get; init; }
        public int LatencyMs { get; init; }
        public float QualityScore { get; init; }
    }
}

// ── Data models ──────────────────────────────────────────────────────────────

public class AdaptiveSettingsData
{
    public DateTime LastUpdated { get; set; }
    public int EventsProcessed { get; set; }
    public Dictionary<string, ContextAdaptiveProfile> Contexts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ContextAdaptiveProfile> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ContextAdaptiveProfile
{
    private const int MinEvents = 10;

    public string ContextKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string Category { get; set; } = "";
    public int AcceptedCount { get; set; }
    public int DismissedCount { get; set; }
    public double AcceptRate { get; set; }
    public double AvgAcceptedWordCount { get; set; }
    public double AvgLatencyMs { get; set; }
    public double AvgQualityScore { get; set; }

    /// <summary>
    /// Temperature delta to apply on top of the category default.
    /// Negative = reduce (model is accurate), Positive = increase (need variety).
    /// Typically in range [-0.08, +0.12].
    /// </summary>
    public double TemperatureAdjustment { get; set; }

    /// <summary>
    /// Suggested completion length preset derived from actual accepted completion lengths.
    /// One of: "brief", "standard", "extended", "unlimited".
    /// </summary>
    public string SuggestedLengthPreset { get; set; } = "extended";

    /// <summary>True when enough events exist for meaningful adaptation.</summary>
    public bool HasSufficientData => AcceptedCount + DismissedCount >= MinEvents;

    /// <summary>
    /// Returns the length instruction string for this context's derived preset.
    /// </summary>
    public string LengthInstruction => SuggestedLengthPreset switch
    {
        "brief" => "Write 3-5 words to complete the immediate next phrase.",
        "standard" => "Write 8-15 words to complete the sentence.",
        "extended" => "Write 15-30 words to complete the full thought.",
        "unlimited" => "Write as much as needed to complete the thought naturally.",
        _ => "Write 15-30 words to complete the full thought."
    };
}
