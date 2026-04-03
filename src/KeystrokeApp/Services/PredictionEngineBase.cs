using System;
using System.IO;
using System.Net.Http;
using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Shared base for all prediction engines. Provides: logging, rate-limit guard,
/// prompt construction (system instruction + user prompt), dynamic temperature,
/// adaptive token sizing, and completion post-processing (TrimToWholeWords,
/// RejectDuplicate, StripThinkTags).
///
/// Each concrete engine implements only the API-specific payload format and
/// response parsing — BuildMessages()/BuildContents(), the HTTP call, and the
/// response DTO types.
/// </summary>
public abstract class PredictionEngineBase
{
    private readonly string _logPath;
    private DateTime _lastRateLimitError = DateTime.MinValue;
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(10);

    // ── Configurable from settings (set by App.xaml.cs after construction) ───

    public string SystemPrompt    { get; set; } = AppConfig.DefaultSystemPrompt;
    public string LengthInstruction { get; set; } = "Write 15-30 words to complete the full thought. Never repeat words or phrases you've already used.";
    public double Temperature      { get; set; } = 0.3;
    public int    MaxOutputTokens  { get; set; } = 100;

    // ── Anti-repetition: track recently generated completions ─────────────────
    private readonly Queue<string> _recentCompletions = new();
    private readonly List<string> _recentTrailingPhrases = new();
    private const int MaxRecentCompletions = 8;

    /// <summary>
    /// Record a completion that was generated (regardless of whether the user accepted it).
    /// Called by engines after successful generation to feed the anti-repetition system.
    /// Tracks both full completions and trailing phrases to catch "different sentence, same ending" patterns.
    /// </summary>
    public void RecordRecentCompletion(string completion)
    {
        var trimmed = completion.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;
        lock (_recentCompletions)
        {
            // Avoid recording exact duplicates back-to-back
            if (_recentCompletions.Count > 0 && _recentCompletions.Last() == trimmed)
                return;
            _recentCompletions.Enqueue(trimmed);
            while (_recentCompletions.Count > MaxRecentCompletions)
                _recentCompletions.Dequeue();

            // Also track the trailing 2-word phrase — this catches the pattern where
            // different completions all end with the same filler ("all day", "right now", etc.)
            var trailing = GetTrailingPhrase(trimmed, 2);
            if (trailing != null && trailing.Length > 3)
            {
                _recentTrailingPhrases.Add(trailing);
                // Keep bounded — only track endings from the last N completions
                while (_recentTrailingPhrases.Count > MaxRecentCompletions * 2)
                    _recentTrailingPhrases.Remove(_recentTrailingPhrases.First());
            }
        }
    }

    /// <summary>
    /// Get the list of recent completions for anti-repetition injection.
    /// </summary>
    protected List<string> GetRecentCompletions()
    {
        lock (_recentCompletions)
            return _recentCompletions.ToList();
    }

    /// <summary>
    /// Get overused trailing phrases (appearing 2+ times in recent completions).
    /// These get injected alongside full completions in the anti-repetition block.
    /// </summary>
    protected List<string> GetOverusedEndings()
    {
        lock (_recentCompletions)
        {
            return _recentTrailingPhrases
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .Select(g => g.Key)
                .ToList();
        }
    }

    private static string? GetTrailingPhrase(string text, int wordCount)
    {
        var words = text.TrimEnd('.', ',', '!', '?', ';', ':')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < wordCount) return null;
        return string.Join(" ", words[^wordCount..]).ToLowerInvariant();
    }

    /// <summary>
    /// Injected by the caller (App.xaml.cs) after construction.
    /// Not initialised here to avoid a wasted file read on every engine creation.
    /// </summary>
    public AcceptanceLearningService  LearningService          { get; set; } = null!;
    public StyleProfileService?       StyleProfileService       { get; set; }
    public VocabularyProfileService?  VocabularyProfileService  { get; set; }

    // ── Context window limits (engines can override for their token budgets) ──

    /// <summary>Max chars of rolling context to include in the user prompt.</summary>
    protected virtual int RollingContextLimit => 1500;
    /// <summary>Max chars of OCR screen text to include in the user prompt.</summary>
    protected virtual int ScreenContextLimit => 4000;

    // ── Constructor ───────────────────────────────────────────────────────────

    protected PredictionEngineBase(string logFileName)
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", logFileName);
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    protected void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch (IOException) { }
    }

    // ── Rate limiting ─────────────────────────────────────────────────────────

    protected bool IsRateLimited()
    {
        var elapsed = DateTime.UtcNow - _lastRateLimitError;
        if (elapsed < RateLimitCooldown)
        {
            Log($"Rate-limit cooldown active ({elapsed.TotalSeconds:F1}s remaining), skipping");
            return true;
        }
        return false;
    }

    protected void CheckRateLimitResponse(HttpResponseMessage response, string errorBody)
    {
        if ((int)response.StatusCode == 429 ||
            errorBody.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
        {
            _lastRateLimitError = DateTime.UtcNow;
            Log($"Rate limit hit — cooldown {RateLimitCooldown.TotalSeconds}s");
        }
    }

    // ── Prompt construction ───────────────────────────────────────────────────

    /// <summary>
    /// Build the system instruction: behavioral rules + tone hint + anti-repetition.
    /// Learning signals (style, vocab, session) are now in the USER prompt so the model
    /// treats them as contextual data rather than instructions to follow blindly.
    /// Ollama uses its own shorter instruction (BuildOllamaSystemInstruction) instead.
    /// </summary>
    protected string BuildSystemInstruction(ContextSnapshot context)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SystemPrompt);

        if (context.HasAppContext)
        {
            var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
            var toneHint = AppCategory.GetToneHint(category);
            sb.AppendLine();
            sb.AppendLine($"Application context: {toneHint}");
        }

        // Anti-repetition: recently generated completions (regardless of accept/dismiss).
        // This is the one learning signal that stays in the system prompt because it's
        // a hard behavioral constraint ("do not repeat"), not a soft contextual hint.
        var recentCompletions = GetRecentCompletions();
        var negativeExamples = LearningService?.GetNegativeExamples(context, 2) ?? [];
        var overusedEndings = GetOverusedEndings();
        if (recentCompletions.Count > 0 || negativeExamples.Count > 0 || overusedEndings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("AVOID REPEATING (hard constraint — never output these or close rephrasings):");
            foreach (var rc in recentCompletions)
                sb.AppendLine($"  - \"{rc}\"");
            foreach (var ex in negativeExamples)
                sb.AppendLine($"  - \"{ex.Completion.Trim()}\"");

            if (overusedEndings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("OVERUSED ENDINGS — do NOT end completions with any of these phrases:");
                foreach (var ending in overusedEndings)
                    sb.AppendLine($"  - \"{ending}\"");
            }
        }

        sb.AppendLine();
        sb.AppendLine(LengthInstruction);
        return sb.ToString();
    }

    /// <summary>
    /// Build the user-facing prompt: app context + rolling context + screen context
    /// + the typed text to complete. Context window sizes are controlled by
    /// RollingContextLimit and ScreenContextLimit.
    /// </summary>
    protected string BuildUserPrompt(ContextSnapshot context)
    {
        var sb = new StringBuilder();

        if (context.HasAppContext)
        {
            sb.AppendLine($"[Application: {context.ProcessName} — \"{context.WindowTitle}\"]");
            sb.AppendLine();
        }

        if (context.HasScreenContext)
        {
            var screenText = context.ScreenText!;
            if (screenText.Length > ScreenContextLimit)
                screenText = "..." + screenText[^ScreenContextLimit..];
            sb.AppendLine("<screen_context>");
            sb.AppendLine(screenText);
            sb.AppendLine("</screen_context>");
            sb.AppendLine();
        }

        if (context.HasRollingContext)
        {
            var rollingText = context.RollingContext!;
            if (rollingText.Length > RollingContextLimit)
                rollingText = "..." + rollingText[^RollingContextLimit..];
            sb.AppendLine("<recently_written>");
            sb.AppendLine(rollingText);
            sb.AppendLine("</recently_written>");
            sb.AppendLine();
        }

        // Learning signals — injected as soft contextual hints the model can weigh
        // alongside screen/rolling context, NOT as hard instructions in the system prompt.
        var learningHints = BuildLearningHints(context);
        if (!string.IsNullOrEmpty(learningHints))
        {
            sb.AppendLine("<user_style_hints>");
            sb.AppendLine(learningHints);
            sb.AppendLine("</user_style_hints>");
            sb.AppendLine();
        }

        sb.AppendLine("<complete_this>");
        sb.Append(context.TypedText);
        sb.AppendLine();
        sb.AppendLine("</complete_this>");
        return sb.ToString();
    }

    /// <summary>
    /// Minimum number of clean (non-contaminated) accepted entries required before
    /// style and vocabulary hints are injected. Below this threshold, the learning
    /// system stays silent and lets the model work from screen context alone.
    /// </summary>
    private const int MinEntriesForHints = 30;

    /// <summary>
    /// Minimum average quality score (0–1) required before style/vocab hints are
    /// injected. Quality below this indicates the model's completions are frequently
    /// being dismissed or edited, so the learned patterns are unreliable.
    /// </summary>
    private const float MinQualityForHints = 0.55f;

    /// <summary>
    /// Builds the optional learning-derived hints block. These are soft signals about
    /// the user's writing style, vocabulary, and recent session activity. Placed in the
    /// user prompt so the model treats them as context to weigh, not rules to obey.
    ///
    /// Style and vocabulary hints are ONLY injected when there is enough high-quality
    /// training data. Session hints (recent acceptances) are always injected when
    /// available since they're real-time and don't depend on historical quality.
    /// </summary>
    private string? BuildLearningHints(ContextSnapshot context)
    {
        var parts = new List<string>();

        if (context.HasAppContext)
        {
            var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
            var categoryStr = category.ToString();

            // Check if we have enough high-quality data to trust style/vocab hints.
            var stats = LearningService?.GetStats();
            bool hasEnoughData = stats != null
                && stats.TotalAccepted >= MinEntriesForHints
                && stats.OverallAvgQuality >= MinQualityForHints;

            // Style profile (LLM-generated summary) — only when data is trustworthy
            if (hasEnoughData && StyleProfileService != null)
            {
                var styleHint = StyleProfileService.GetStyleHint(categoryStr);
                if (!string.IsNullOrEmpty(styleHint))
                    parts.Add($"Writing style: {styleHint}");
            }

            // Vocabulary fingerprint (deterministic) — only when data is trustworthy
            if (hasEnoughData && VocabularyProfileService != null)
            {
                var vocabHint = VocabularyProfileService.GetVocabularyHint(categoryStr);
                if (!string.IsNullOrEmpty(vocabHint))
                    parts.Add(vocabHint);
            }

            // Session hint — always available since it's real-time signal, not historical.
            if (LearningService != null)
            {
                var sessionHint = LearningService.GetSessionModeHint(categoryStr);
                if (!string.IsNullOrEmpty(sessionHint))
                    parts.Add(sessionHint);
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    // ── Temperature and token sizing ──────────────────────────────────────────

    /// <summary>
    /// Selects temperature based on app context category. Code/terminal → low (precision),
    /// chat/browser → higher (variety to break out of repetitive patterns).
    /// Falls back to the configured Temperature if no context.
    /// </summary>
    protected double GetDynamicTemperature(ContextSnapshot context)
    {
        if (!context.HasAppContext) return Temperature;
        var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
        return category switch
        {
            AppCategory.Category.Code     => 0.15,
            AppCategory.Category.Terminal  => 0.15,
            AppCategory.Category.Email     => 0.35,
            AppCategory.Category.Document  => 0.35,
            AppCategory.Category.Browser   => 0.45,
            AppCategory.Category.Chat      => 0.5,
            _                              => Temperature
        };
    }

    /// <summary>
    /// Caps token budget based on prefix length. Short prefixes are highly ambiguous —
    /// generating 100 tokens produces vague output and wastes latency.
    /// Override in engines that need special token budgets (e.g. reasoning-first models).
    /// </summary>
    protected virtual int GetAdaptiveMaxTokens(string prefix)
    {
        var wordCount = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 4) return Math.Min(25, MaxOutputTokens);
        if (wordCount < 8) return Math.Min(60, MaxOutputTokens);
        return MaxOutputTokens;
    }

    // ── Post-processing ───────────────────────────────────────────────────────

    /// <summary>
    /// Strips &lt;think&gt;...&lt;/think&gt; blocks emitted by reasoning models
    /// (DeepSeek, Qwen3, etc.).
    /// </summary>
    protected static string StripThinkTags(string text)
    {
        if (!text.Contains("<think>", StringComparison.OrdinalIgnoreCase)) return text;
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf("<think>", i, StringComparison.OrdinalIgnoreCase);
            if (start < 0) { sb.Append(text[i..]); break; }
            sb.Append(text[i..start]);
            int end = text.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            i = end < 0 ? text.Length : end + "</think>".Length;
        }
        return sb.ToString().TrimStart('\n', ' ');
    }

    /// <summary>
    /// Rejects completions that repeat text already in the buffer. Only checks the last
    /// 100 characters of typed text — the buffer can grow very long, making false positives
    /// likely on older text. Uses a 3-word sliding window: any 3 consecutive completion
    /// words that appear in recent typed text disqualifies the completion.
    /// </summary>
    protected static string? RejectDuplicate(string typedText, string completion)
    {
        if (string.IsNullOrWhiteSpace(completion)) return completion;

        // Check for prompt leakage first — reject completions that contain
        // system-prompt framing phrases no real person would type.
        if (RejectPromptLeakage(completion) == null) return null;

        var clean = completion.Trim();
        if (clean.Length < 8) return completion;

        var words      = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var recentTyped = typedText.Length > 100 ? typedText[^100..] : typedText;
        var recentLower = recentTyped.ToLowerInvariant();

        if (words.Length >= 3)
        {
            for (int i = 0; i <= words.Length - 3; i++)
            {
                var phrase = string.Join(" ", words[i..(i + 3)]).ToLowerInvariant();
                if (recentLower.Contains(phrase)) return null;
            }
        }
        return completion;
    }

    /// <summary>
    /// Rejects completions containing phrases that leak from system prompt framing
    /// (e.g. "the user") which should never appear in first-person typed text.
    /// </summary>
    protected static string? RejectPromptLeakage(string completion)
    {
        if (string.IsNullOrWhiteSpace(completion)) return completion;
        var lower = completion.ToLowerInvariant();

        foreach (var phrase in PromptLeakagePhrases)
            if (lower.Contains(phrase)) return null;

        return completion;
    }

    /// <summary>
    /// Phrases that indicate the model is narrating about its own prompt context
    /// rather than producing text the person would actually type.
    /// </summary>
    private static readonly string[] PromptLeakagePhrases =
    [
        "the user",
        "the person",
        "screen context",
        "complete_this",
        "recently_written",
        "style_hints",
    ];

    /// <summary>
    /// Trims trailing partial words so completions always end on a word boundary.
    /// A "complete" ending is: trailing punctuation, trailing whitespace, or a full
    /// word with no trailing character (even without punctuation).
    /// </summary>
    protected static string TrimToWholeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return trimmed;
        char last = trimmed[^1];
        if (char.IsPunctuation(last) || char.IsWhiteSpace(last)) return trimmed;
        int lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace < 0) return trimmed;
        string lastWord = trimmed[(lastSpace + 1)..];
        if (lastWord.Length == 1 && lastWord != "I" && lastWord != "a" && lastWord != "A")
            return trimmed[..lastSpace].TrimEnd();
        return trimmed;
    }
}
