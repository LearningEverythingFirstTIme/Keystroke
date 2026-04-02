namespace KeystrokeApp.Services;

/// <summary>
/// Persisted learning intelligence scores — one entry per app category.
/// Stored in %AppData%/Keystroke/learning-scores.json.
///
/// Sub-Phase D: each time the style profile regenerates, LearningScoreService
/// recomputes scores from all learning signals, pushes a snapshot, and checks
/// for drift. The SettingsWindow reads this model to render Intelligence Cards.
/// </summary>
public class LearningScores
{
    public DateTime LastComputed { get; set; }
    public Dictionary<string, CategoryIntelligence> Categories { get; set; } = new();
}

public class CategoryIntelligence
{
    /// <summary>Intelligence score 0–100 for this category.</summary>
    public int Score { get; set; }

    /// <summary>
    /// How the score has moved since the previous snapshot.
    /// Positive = improving, negative = drifting, 0 = stable or first run.
    /// </summary>
    public int DeltaSinceLast { get; set; }

    /// <summary>"Improving", "Stable", or "Drifting"</summary>
    public string Trend { get; set; } = "Stable";

    public DateTime ComputedAt { get; set; }

    /// <summary>
    /// Up to 3 score snapshots, oldest first.
    /// Used to render a micro-trend and detect sustained drift.
    /// </summary>
    public List<IntelligenceSnapshot> History { get; set; } = new();
}

public class IntelligenceSnapshot
{
    public DateTime Timestamp { get; set; }
    public int      Score     { get; set; }
}
