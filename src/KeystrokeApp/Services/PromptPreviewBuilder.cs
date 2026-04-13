using System.Text;

namespace KeystrokeApp.Services;

public static class PromptPreviewBuilder
{
    private const int RollingContextLimit = 1500;
    private const int ScreenContextLimit = 4000;

    public static PromptPreviewSnapshot Build(
        AppConfig config,
        string providerLabel,
        string typedBuffer,
        string processName,
        string windowTitle,
        bool appEnabled,
        OutboundPrivacyService outboundPrivacy,
        AcceptanceLearningService? learningService,
        StyleProfileService? styleProfileService,
        VocabularyProfileService? vocabularyProfileService,
        CorrectionPatternService? correctionPatternService,
        string? screenText,
        string? rollingContext)
    {
        var sanitizedTyped = outboundPrivacy.SanitizeTypedText(typedBuffer);
        var sanitizedScreen = outboundPrivacy.SanitizeForPrompt(screenText) ?? "";
        var sanitizedRolling = config.RollingContextEnabled
            ? outboundPrivacy.SanitizeForPrompt(rollingContext) ?? ""
            : "";

        var category = AppCategory.GetEffectiveCategory(processName, windowTitle);
        var safeContextLabel = string.IsNullOrWhiteSpace(processName)
            ? "No active app detected"
            : $"{PerAppSettings.NormalizeProcessName(processName)} ({category})";

        var context = new ContextSnapshot
        {
            TypedText = sanitizedTyped.ShouldBlockPrediction ? "" : sanitizedTyped.Text,
            ProcessName = processName,
            WindowTitle = windowTitle,
            SafeContextLabel = safeContextLabel,
            Category = category.ToString(),
            ScreenText = string.IsNullOrWhiteSpace(sanitizedScreen) ? null : sanitizedScreen,
            RollingContext = string.IsNullOrWhiteSpace(sanitizedRolling) ? null : sanitizedRolling
        };

        var learningHints = BuildLearningHints(
            learningService,
            styleProfileService,
            vocabularyProfileService,
            correctionPatternService,
            context,
            outboundPrivacy);
        var promptPreview = BuildUserPromptPreview(context, learningHints);

        var blockedByLength = typedBuffer.Length < config.MinBufferLength;
        var wouldSendPrediction = appEnabled &&
            !blockedByLength &&
            !sanitizedTyped.ShouldBlockPrediction;

        return new PromptPreviewSnapshot
        {
            ProviderLabel = providerLabel,
            AppFilteringModeLabel = PerAppSettings.NormalizeMode(config.AppFilteringMode) == PerAppSettings.AllowListedOnly
                ? "Only listed apps"
                : "All apps except blocked ones",
            ActiveAppLabel = string.IsNullOrWhiteSpace(processName)
                ? "No active app"
                : $"{processName} - {windowTitle}".Trim().TrimEnd('-').Trim(),
            AppAvailabilityLabel = appEnabled ? "Suggestions allowed here" : "Suggestions paused here",
            AppAvailabilityReason = appEnabled
                ? "The current app is eligible for predictions."
                : "The current app is blocked or not on the allow list.",
            WouldSendPrediction = wouldSendPrediction,
            TypedInputBlocked = sanitizedTyped.ShouldBlockPrediction,
            TypedInputStatus = sanitizedTyped.ShouldBlockPrediction
                ? sanitizedTyped.BlockReason ?? "Prediction is blocked for the active text."
                : blockedByLength
                    ? $"Waiting for at least {config.MinBufferLength} characters before sending."
                    : "The current text is eligible to send when prediction fires.",
            TypedTextPreview = sanitizedTyped.ShouldBlockPrediction
                ? "(hidden because sensitive input is blocked)"
                : string.IsNullOrWhiteSpace(sanitizedTyped.Text)
                    ? "(type in another app to preview the outbound prefix)"
                    : sanitizedTyped.Text,
            ScreenContextPreview = config.OcrEnabled
                ? PreviewText(sanitizedScreen, ScreenContextLimit, "(OCR is on, but no readable screen text is cached yet)")
                : "(OCR is off)",
            RollingContextPreview = config.RollingContextEnabled
                ? PreviewText(sanitizedRolling, RollingContextLimit, "(recently written context is empty)")
                : "(rolling context is off)",
            LearningHintsIncluded = !string.IsNullOrWhiteSpace(learningHints),
            LearningHintsPreview = string.IsNullOrWhiteSpace(learningHints)
                ? config.LearningEnabled
                    ? "(no stable learning hints available for this context yet)"
                    : "(learning is off)"
                : learningHints,
            UserPromptPreview = promptPreview
        };
    }

    private static string BuildUserPromptPreview(ContextSnapshot context, string? learningHints)
    {
        var sb = new StringBuilder();

        if (context.HasAppContext)
        {
            sb.AppendLine($"[Application: {context.SafeContextLabel}]");
            sb.AppendLine();
        }

        if (context.HasScreenContext)
        {
            sb.AppendLine("<screen_context>");
            sb.AppendLine(PreviewText(context.ScreenText!, ScreenContextLimit, ""));
            sb.AppendLine("</screen_context>");
            sb.AppendLine();
        }

        if (context.HasRollingContext)
        {
            sb.AppendLine("<recently_written>");
            sb.AppendLine(PreviewText(context.RollingContext!, RollingContextLimit, ""));
            sb.AppendLine("</recently_written>");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(learningHints))
        {
            sb.AppendLine("<user_style_hints>");
            sb.AppendLine(learningHints);
            sb.AppendLine("</user_style_hints>");
            sb.AppendLine();
        }

        sb.AppendLine("<complete_this>");
        sb.AppendLine(string.IsNullOrWhiteSpace(context.TypedText)
            ? "(hidden or empty)"
            : context.TypedText);
        sb.AppendLine("</complete_this>");
        return sb.ToString().TrimEnd();
    }

    private static string? BuildLearningHints(
        AcceptanceLearningService? learningService,
        StyleProfileService? styleProfileService,
        VocabularyProfileService? vocabularyProfileService,
        CorrectionPatternService? correctionPatternService,
        ContextSnapshot context,
        OutboundPrivacyService outboundPrivacy)
    {
        var parts = new List<string>();
        var bundle = LearningHintBundleBuilder.Build(
            learningService, styleProfileService, vocabularyProfileService, context,
            correctionPatternService);

        if (bundle.IsContextDisabled || bundle.Confidence <= 0)
            return null;

        if (bundle.Confidence >= 0.45 && !string.IsNullOrWhiteSpace(bundle.StyleHint))
            parts.Add($"Writing style ({bundle.Confidence:P0} confidence): {outboundPrivacy.SanitizeForPrompt(bundle.StyleHint)}");

        if (bundle.Confidence >= 0.45 && !string.IsNullOrWhiteSpace(bundle.VocabularyHint))
            parts.Add(outboundPrivacy.SanitizeForPrompt(bundle.VocabularyHint) ?? "");

        if (bundle.Confidence >= 0.45 && !string.IsNullOrWhiteSpace(bundle.CorrectionHint))
            parts.Add(outboundPrivacy.SanitizeForPrompt(bundle.CorrectionHint) ?? "");

        if (!string.IsNullOrWhiteSpace(bundle.PreferredClosings))
            parts.Add(outboundPrivacy.SanitizeForPrompt(bundle.PreferredClosings) ?? "");

        if (!string.IsNullOrWhiteSpace(bundle.AvoidPatterns))
            parts.Add(outboundPrivacy.SanitizeForPrompt(bundle.AvoidPatterns) ?? "");

        if (!string.IsNullOrWhiteSpace(bundle.SessionHint))
            parts.Add(outboundPrivacy.SanitizeForPrompt(bundle.SessionHint) ?? "");

        parts = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string PreviewText(string? value, int maxChars, string emptyFallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return emptyFallback;

        var trimmed = value.Trim();
        return trimmed.Length <= maxChars
            ? trimmed
            : "..." + trimmed[^maxChars..];
    }
}
