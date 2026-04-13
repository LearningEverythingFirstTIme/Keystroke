using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Computes a 0–100 "intelligence score" per app category that reflects how
/// thoroughly the learning system understands the user's writing in that context.
///
/// Score components (100 pts total):
///   Volume      0–35 pts  — how many quality accepted completions exist
///   Quality     0–30 pts  — average behavioural quality score (latency/cycling/edits)
///   Accept rate 0–20 pts  — percentage of shown suggestions the user accepted
///   Richness    0–15 pts  — vocabulary fingerprint (+8) + style profile (+7)
///
/// After each recompute, scores are persisted to learning-scores.json so trend
/// history survives across app restarts.
///
/// The DriftDetected event fires when a category's score drops ≥ 10 points from
/// its previous snapshot — signalling that the user's writing patterns have shifted
/// away from what the learning data represents, and the system needs time to
/// catch up.
/// </summary>
public class LearningScoreService
{
    // ── Wired by App.xaml.cs after construction ───────────────────────────────
    public AcceptanceLearningService? LearningService          { get; set; }
    public StyleProfileService?       StyleProfileService       { get; set; }
    public VocabularyProfileService?  VocabularyProfileService  { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires when a category's intelligence score drops ≥ 10 points from its last
    /// snapshot. Arguments: (category, previousScore, newScore).
    /// May fire on a background thread — callers must marshal to the UI thread.
    /// </summary>
    public event Action<string, int, int>? DriftDetected;

    /// <summary>
    /// Fires after every Recompute() with each (category, newScore) pair.
    /// Used by AnalyticsAggregationService to build extended score history.
    /// </summary>
    public event Action<string, int>? ScoreComputed;

    // ── Constants ─────────────────────────────────────────────────────────────

    private const int MaxHistorySnapshots  = 3;
    private const int DriftAlertThreshold  = -10; // score must drop by ≥10 to alert
    private const int TrendImprovingDelta  =  5;
    private const int TrendDriftingDelta   = -5;

    // ── Internal state ────────────────────────────────────────────────────────

    private LearningScores _cached = new();
    private readonly string _scoresPath;
    private readonly string _logPath;
    private readonly object _lock = new();

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Score] {msg}\n"); }
        catch (IOException) { }
    }

    public LearningScoreService()
    {
        var appData   = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keystroke");
        _scoresPath = Path.Combine(appData, "learning-scores.json");
        _logPath    = Path.Combine(appData, "learning.log");

        LoadFromDisk();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the most recently computed scores without recalculating.
    /// Fast — safe to call from the UI thread.
    /// </summary>
    public LearningScores GetCachedScores()
    {
        lock (_lock) { return _cached; }
    }

    /// <summary>
    /// Recomputes intelligence scores from all three learning services,
    /// updates trend and history, checks for drift, and persists to disk.
    ///
    /// Safe to call from any thread. Fires DriftDetected on the calling thread
    /// if a significant drop is detected — callers that need UI operations must
    /// marshal to the dispatcher.
    /// </summary>
    public LearningScores Recompute()
    {
        try
        {
            // ── Gather inputs ─────────────────────────────────────────────────
            var stats       = LearningService?.GetStats();
            var styleProfile = StyleProfileService?.GetProfile();
            var vocabProfile = VocabularyProfileService?.GetProfile();

            // Determine which categories have style profile and vocab data
            var profiledCategories = styleProfile?.CategoryProfiles.Keys.ToHashSet()
                                     ?? new HashSet<string>();
            var fingerprintedCategories = vocabProfile?.Categories.Keys.ToHashSet()
                                          ?? new HashSet<string>();

            // All categories that appear in the accepted/dismissed stats
            var allCategories = new HashSet<string>();
            if (stats != null)
            {
                foreach (var k in stats.ByCategory.Keys)          allCategories.Add(k);
                foreach (var k in stats.DismissedByCategory.Keys) allCategories.Add(k);
            }

            lock (_lock)
            {
                var updatedScores = new LearningScores { LastComputed = DateTime.UtcNow };

                foreach (var cat in allCategories)
                {
                    int   accepted   = stats?.ByCategory.GetValueOrDefault(cat, 0)          ?? 0;
                    int   dismissed  = stats?.DismissedByCategory.GetValueOrDefault(cat, 0) ?? 0;
                    float avgQuality = stats?.AvgQualityByCategory.GetValueOrDefault(cat, 0.5f) ?? 0.5f;

                    bool hasStyle   = profiledCategories.Contains(cat);
                    bool hasVocab   = fingerprintedCategories.Contains(cat);

                    int newScore = ComputeScore(accepted, dismissed, avgQuality, hasStyle, hasVocab);

                    // Retrieve previous intel for this category (from old cached state)
                    _cached.Categories.TryGetValue(cat, out var previous);

                    int    prevScore = previous?.Score ?? newScore;  // first run: no delta
                    int    delta     = previous != null ? newScore - prevScore : 0;
                    string trend     = delta >= TrendImprovingDelta  ? "Improving"
                                     : delta <= TrendDriftingDelta   ? "Drifting"
                                     : "Stable";

                    // Build updated history — keep last MaxHistorySnapshots
                    var history = new List<IntelligenceSnapshot>(
                        previous?.History ?? Enumerable.Empty<IntelligenceSnapshot>());
                    history.Add(new IntelligenceSnapshot
                    {
                        Timestamp = DateTime.UtcNow,
                        Score     = newScore
                    });
                    if (history.Count > MaxHistorySnapshots)
                        history.RemoveRange(0, history.Count - MaxHistorySnapshots);

                    updatedScores.Categories[cat] = new CategoryIntelligence
                    {
                        Score          = newScore,
                        DeltaSinceLast = delta,
                        Trend          = trend,
                        ComputedAt     = DateTime.UtcNow,
                        History        = history
                    };

                    // Drift alert — only fire when we have a previous baseline
                    if (previous != null && delta <= DriftAlertThreshold)
                    {
                        Log($"Drift detected: {cat} {prevScore}→{newScore} (Δ{delta})");
                        DriftDetected?.Invoke(cat, prevScore, newScore);
                    }
                    else if (delta != 0)
                    {
                        Log($"{cat}: {prevScore}→{newScore} (Δ{delta:+0;-0}) [{trend}]");
                    }
                }

                _cached = updatedScores;
            }

            // Notify analytics of every category score so it can build extended history.
            foreach (var (cat, intel) in _cached.Categories)
                ScoreComputed?.Invoke(cat, intel.Score);

            SaveToDisk();
            return _cached;
        }
        catch (Exception ex)
        {
            Log($"Recompute error: {ex.Message}");
            lock (_lock) { return _cached; }
        }
    }

    // ── Score formula ─────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a 0–100 intelligence score for a single category.
    ///
    /// Validated score ranges (approximate):
    ///   Brand-new user (5 accepts, no profiles)          → ~32
    ///   Growing user   (50 accepts, 70% rate, no profiles) → ~55
    ///   Established    (150 accepts, 85% rate, both profiles) → ~88
    /// </summary>
    private static int ComputeScore(
        int accepted, int dismissed, float avgQuality,
        bool hasStyleProfile, bool hasVocabFingerprint)
    {
        // Volume component (0–35 pts): logarithmic, saturates around 200 entries.
        // log(n+1) / log(200) grows quickly at first, slows as data accumulates.
        double volumePts = accepted > 0
            ? Math.Min(35.0, Math.Log(accepted + 1) / Math.Log(200) * 35.0)
            : 0.0;

        // Quality component (0–30 pts): direct mapping from the 0-1 avg quality score.
        double qualityPts = avgQuality * 30.0;

        // Accept rate component (0–20 pts).
        // If no dismissals yet, partial credit (10 pts) — we simply don't know the rate.
        int    total   = accepted + dismissed;
        double ratePts = total > 0
            ? ((double)accepted / total) * 20.0
            : (accepted > 0 ? 10.0 : 0.0);

        // Profile richness component (0–15 pts): binary bonuses per profile type.
        double richPts = (hasVocabFingerprint ? 8.0 : 0.0)
                       + (hasStyleProfile     ? 7.0 : 0.0);

        return (int)Math.Round(Math.Clamp(volumePts + qualityPts + ratePts + richPts, 0.0, 100.0));
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_scoresPath)) return;
            var json = File.ReadAllText(_scoresPath);
            var loaded = JsonSerializer.Deserialize<LearningScores>(json);
            if (loaded != null)
            {
                lock (_lock) { _cached = loaded; }
                Log($"Loaded scores: {loaded.Categories.Count} categories");
            }
        }
        catch (Exception ex) { Log($"Load error: {ex.Message}"); }
    }

    private void SaveToDisk()
    {
        try
        {
            LearningScores snapshot;
            lock (_lock) { snapshot = _cached; }

            Directory.CreateDirectory(Path.GetDirectoryName(_scoresPath)!);
            var json     = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _scoresPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _scoresPath, overwrite: true);
        }
        catch (Exception ex) { Log($"Save error: {ex.Message}"); }
    }
}
