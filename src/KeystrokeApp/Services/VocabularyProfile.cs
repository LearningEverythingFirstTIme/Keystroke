namespace KeystrokeApp.Services;

/// <summary>
/// Persisted output of VocabularyProfileService — one CategoryVocabulary per app category.
/// Stored at %AppData%/Keystroke/vocabulary-profile.json.
/// </summary>
public class VocabularyProfile
{
    public DateTime LastUpdated      { get; set; }
    public int      EntriesProcessed { get; set; }

    /// <summary>Per-category extracted fingerprints. Key = category name (e.g. "Email").</summary>
    public Dictionary<string, CategoryVocabulary> Categories { get; set; } = new();
    public Dictionary<string, CategoryVocabulary> Contexts { get; set; } = new();
    public Dictionary<string, string> ContextLabels { get; set; } = new();
}

/// <summary>
/// Deterministic writing fingerprint extracted from a user's accepted completions
/// within a single app category. Every field is computed without any LLM call.
/// </summary>
public class CategoryVocabulary
{
    /// <summary>
    /// High-frequency personal words that appear more often in this user's
    /// completions than in baseline English — their "voice words".
    /// </summary>
    public List<string> TopWords { get; set; } = new();

    /// <summary>
    /// N-gram phrases that frequently appear at the START of accepted completions.
    /// e.g. ["Thanks for your", "Happy to", "Following up on"]
    /// </summary>
    public List<string> OpeningPhrases { get; set; } = new();

    /// <summary>
    /// N-gram phrases that frequently appear at the END of accepted completions.
    /// e.g. ["let me know", "appreciate it"]
    /// </summary>
    public List<string> ClosingPhrases { get; set; } = new();

    /// <summary>Mean word count per sentence across accepted completions.</summary>
    public double AvgSentenceWords { get; set; }

    /// <summary>True when contractions (don't, I'll, we've, ...) appear frequently.</summary>
    public bool UsesContractions { get; set; }

    /// <summary>True when the Oxford comma pattern ", and " appears consistently.</summary>
    public bool OxfordComma { get; set; }

    /// <summary>"formal" | "casual-professional" | "casual"</summary>
    public string Formality { get; set; } = "casual-professional";

    /// <summary>"frequent" | "rare"</summary>
    public string EmDash { get; set; } = "rare";

    /// <summary>"frequent" | "occasional" | "rare"</summary>
    public string Exclamation { get; set; } = "rare";

    /// <summary>True when ellipsis (...) appears in multiple completions.</summary>
    public bool UsesEllipsis { get; set; }
}
