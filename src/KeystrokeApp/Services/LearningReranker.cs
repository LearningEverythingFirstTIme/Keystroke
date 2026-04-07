namespace KeystrokeApp.Services;

public sealed class LearningReranker
{
    public RankedLearningEvidence Score(LearningEvidence evidence, ContextSnapshot context, bool isNegative)
    {
        double score = 0;
        string matchLevel = "global";

        var currentWords = SplitWords(context.TypedText);
        var evidenceWords = SplitWords(evidence.Prefix);

        double lexical = ComputeLexicalSimilarity(currentWords, evidenceWords);
        score += lexical * 0.15;

        if (!string.IsNullOrWhiteSpace(context.SubcontextKey) &&
            string.Equals(context.SubcontextKey, evidence.SubcontextKey, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.30;
            matchLevel = "subcontext";
        }
        else if (!string.IsNullOrWhiteSpace(context.WindowKey) &&
                 string.Equals(context.WindowKey, evidence.WindowKey, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.20;
            matchLevel = "window";
        }
        else if (!string.IsNullOrWhiteSpace(context.ProcessKey) &&
                 string.Equals(context.ProcessKey, evidence.ProcessKey, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.12;
            matchLevel = "process";
        }
        else if (string.Equals(context.Category, evidence.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.08;
            matchLevel = "category";
        }

        double sourceTrust = evidence.SourceType switch
        {
            LearningSourceType.NativeWriting => 1.0,
            LearningSourceType.AssistAcceptedUntouched => 0.85,
            LearningSourceType.AssistAccepted => 0.65,
            LearningSourceType.AssistPartial => 0.45,
            LearningSourceType.LegacyAccepted => 0.5,
            LearningSourceType.Dismissed => 0.2,
            LearningSourceType.TypedPast => 0.15,
            LearningSourceType.LegacyDismissed => 0.15,
            _ => 0.4
        };
        score += sourceTrust * 0.20;

        if (evidence.WasUntouched)
            score += 0.15;

        score += evidence.QualityScore * 0.10;

        var age = DateTime.UtcNow - evidence.TimestampUtc;
        double recency = age.TotalMinutes < 15 ? 1.0
            : age.TotalHours < 1 ? 0.8
            : age.TotalDays < 1 ? 0.55
            : age.TotalDays < 7 ? 0.3
            : 0.1;
        score += recency * 0.10;

        score += evidence.ContextConfidence * 0.10;

        if (isNegative)
            score += 0.10;

        return new RankedLearningEvidence
        {
            Evidence = evidence,
            Score = Math.Round(Math.Clamp(score, 0, 1), 3),
            ContextMatchLevel = matchLevel,
            Confidence = Math.Round(Math.Clamp((score * 0.7) + (evidence.ContextConfidence * 0.3), 0, 1), 3)
        };
    }

    private static double ComputeLexicalSimilarity(string[] currentWords, string[] evidenceWords)
    {
        if (currentWords.Length == 0 || evidenceWords.Length == 0)
            return 0;

        var currentNgrams = BuildNgrams(currentWords);
        var evidenceNgrams = BuildNgrams(evidenceWords);
        int intersection = currentNgrams.Count(n => evidenceNgrams.Contains(n));
        int union = currentNgrams.Count + evidenceNgrams.Count - intersection;
        if (union == 0)
            return 0;

        double score = (double)intersection / union;
        if (currentWords[0] == evidenceWords[0])
            score += 0.2;
        return Math.Min(1.0, score);
    }

    private static HashSet<string> BuildNgrams(string[] words)
    {
        var ngrams = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < words.Length - 1; i++)
            ngrams.Add($"{words[i]} {words[i + 1]}");
        return ngrams;
    }

    private static string[] SplitWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public sealed class RankedLearningEvidence
{
    public LearningEvidence Evidence { get; init; } = new();
    public double Score { get; init; }
    public double Confidence { get; init; }
    public string ContextMatchLevel { get; init; } = "global";
}
