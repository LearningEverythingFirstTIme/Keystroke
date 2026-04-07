namespace KeystrokeApp.Services;

public sealed class LearningHintBundle
{
    public double Confidence { get; init; }
    public bool IsContextDisabled { get; init; }
    public string? StyleHint { get; init; }
    public string? VocabularyHint { get; init; }
    public string? SessionHint { get; init; }
    public string? PreferredClosings { get; init; }
    public string? AvoidPatterns { get; init; }
}

public static class LearningHintBundleBuilder
{
    public static LearningHintBundle Build(
        AcceptanceLearningService? learningService,
        StyleProfileService? styleProfileService,
        VocabularyProfileService? vocabularyProfileService,
        ContextSnapshot context)
    {
        if (learningService == null)
            return new LearningHintBundle();

        var signal = learningService.GetContextSignal(context);
        if (signal.IsDisabled)
        {
            return new LearningHintBundle
            {
                Confidence = 0,
                IsContextDisabled = true
            };
        }

        var examples = learningService.GetExamples(context, signal.Confidence >= 0.75 ? 3 : 1);
        var negative = learningService.GetNegativeExamples(context, 2);

        string? styleHint = null;
        string? vocabHint = null;
        string? preferredClosings = null;

        if (signal.Confidence >= 0.45)
        {
            styleHint = styleProfileService?.GetStyleHint(context.Category, context.SubcontextKey);
            vocabHint = vocabularyProfileService?.GetVocabularyHint(context.Category, context.SubcontextKey);
        }

        if (signal.Confidence >= 0.75 && examples.Count > 0)
        {
            var endings = examples
                .Where(e => e.WasUntouched || e.SourceType == LearningSourceType.NativeWriting.ToString())
                .Select(e => GetTrailingWords(e.Completion, 3))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            if (endings.Count > 0)
                preferredClosings = $"Preferred endings in similar contexts: {string.Join("; ", endings.Select(e => $"\"{e}\""))}";
        }

        string? avoidPatterns = negative.Count > 0
            ? $"Avoid recently rejected patterns in this context: {string.Join("; ", negative.Take(2).Select(n => $"\"{n.Completion.Trim()}\""))}"
            : null;

        return new LearningHintBundle
        {
            Confidence = signal.Confidence,
            IsContextDisabled = false,
            StyleHint = styleHint,
            VocabularyHint = vocabHint,
            SessionHint = learningService.GetSessionModeHint(context.SubcontextKey, context.Category),
            PreferredClosings = preferredClosings,
            AvoidPatterns = avoidPatterns
        };
    }

    private static string GetTrailingWords(string text, int count)
    {
        var words = text.Trim().TrimEnd('.', ',', '!', '?', ';', ':')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";
        return string.Join(" ", words.TakeLast(Math.Min(count, words.Length)));
    }
}
