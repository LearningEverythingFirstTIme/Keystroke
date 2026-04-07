namespace KeystrokeApp.Services;

public sealed class LearningRetrievalService
{
    private readonly LearningReranker _reranker;

    public LearningRetrievalService(LearningReranker reranker)
    {
        _reranker = reranker;
    }

    public List<RankedLearningEvidence> GetCandidates(LearningCorpusSnapshot snapshot, ContextSnapshot context, bool negatives, int maxCount)
    {
        if (!string.IsNullOrWhiteSpace(context.SubcontextKey) &&
            snapshot.DisabledContextKeys.Contains(context.SubcontextKey))
        {
            return [];
        }

        var source = negatives ? snapshot.NegativeEvidence : snapshot.PositiveEvidence;

        var ranked = source
            .Where(e => !snapshot.DisabledContextKeys.Contains(e.SubcontextKey))
            .Where(e => IsRelevant(e, context))
            .Select(e => _reranker.Score(e, context, negatives))
            .Where(r => r.Score > 0.2)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Evidence.TimestampUtc)
            .ToList();

        var selected = new List<RankedLearningEvidence>();
        foreach (var item in ranked)
        {
            bool tooSimilar = selected.Any(existing =>
                JaccardSimilarity(existing.Evidence.Completion, item.Evidence.Completion) > 0.72);
            if (tooSimilar)
                continue;

            selected.Add(item);
            if (selected.Count >= maxCount)
                break;
        }

        return selected;
    }

    private static bool IsRelevant(LearningEvidence evidence, ContextSnapshot context)
    {
        if (string.IsNullOrWhiteSpace(evidence.Completion))
            return false;

        if (evidence.Prefix.Length > 120)
            return false;

        if (string.Equals(evidence.Category, context.Category, StringComparison.OrdinalIgnoreCase))
            return true;

        return (evidence.Category, context.Category) switch
        {
            ("Chat", "Email") => true,
            ("Email", "Chat") => true,
            ("Code", "Terminal") => true,
            ("Terminal", "Code") => true,
            _ => false
        };
    }

    private static double JaccardSimilarity(string left, string right)
    {
        var leftWords = new HashSet<string>(
            left.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var rightWords = new HashSet<string>(
            right.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (leftWords.Count == 0 || rightWords.Count == 0)
            return 0;

        int intersection = leftWords.Count(w => rightWords.Contains(w));
        int union = leftWords.Count + rightWords.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
