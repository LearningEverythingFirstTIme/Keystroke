namespace KeystrokeApp.Services;

/// <summary>
/// Single source of truth for detecting prompt-leakage and known contamination
/// patterns in completions. Used by AcceptanceLearningService, StyleProfileService,
/// and VocabularyProfileService to filter poisoned entries from training data.
/// </summary>
public static class ContaminationFilter
{
    /// <summary>
    /// Returns true if the completion contains phrases that indicate prompt leakage
    /// or known contamination patterns. Contaminated completions should be excluded
    /// from few-shot examples, style profiles, and vocabulary analysis.
    /// </summary>
    public static bool IsContaminated(string completion)
    {
        var lower = completion.ToLowerInvariant();
        foreach (var phrase in ContaminationPhrases)
            if (lower.Contains(phrase))
                return true;
        return false;
    }

    /// <summary>
    /// Phrases that indicate a completion was generated from system-prompt leakage
    /// or is a known repetitive pattern that poisons the learning system.
    /// </summary>
    private static readonly string[] ContaminationPhrases =
    [
        "the user",
        "the person",
        "screen context",
        "complete_this",
        "recently_written",
        "style_hints",
        "all day",
    ];
}
