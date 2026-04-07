namespace KeystrokeApp.Services;

/// <summary>
/// Centralized outbound privacy policy for all model egress.
/// Every text fragment included in prompts should pass through here first.
/// </summary>
public sealed class OutboundPrivacyService
{
    public SanitizedTypedTextResult SanitizeTypedText(string text)
    {
        var scrubbed = PiiFilter.Scrub(text) ?? "";
        var blocking = SensitiveDataDetector.ContainsBlockingSensitiveData(text);

        return new SanitizedTypedTextResult(
            blocking ? "" : scrubbed,
            blocking,
            blocking ? "Blocking sensitive data detected in active input." : null);
    }

    public string BuildSafeContextLabel(string processName, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return "";

        var category = AppCategory.GetEffectiveCategory(processName, windowTitle);
        return $"{processName} ({category})";
    }

    public string? SanitizeForPrompt(string? text) => PiiFilter.Scrub(text);

    public AcceptanceLearningService.FewShotExample SanitizeFewShotExample(AcceptanceLearningService.FewShotExample example)
    {
        return new AcceptanceLearningService.FewShotExample
        {
            Prefix = PiiFilter.Scrub(example.Prefix) ?? "",
            Completion = PiiFilter.Scrub(example.Completion) ?? "",
            Context = example.Context,
            IsNegative = example.IsNegative
        };
    }

    public sealed record SanitizedTypedTextResult(
        string Text,
        bool ShouldBlockPrediction,
        string? BlockReason);
}
