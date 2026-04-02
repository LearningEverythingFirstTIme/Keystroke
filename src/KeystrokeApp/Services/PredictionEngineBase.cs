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

    /// <summary>
    /// Injected by the caller (App.xaml.cs) after construction.
    /// Not initialised here to avoid a wasted file read on every engine creation.
    /// </summary>
    public AcceptanceLearningService  LearningService          { get; set; } = null!;
    public StyleProfileService?       StyleProfileService       { get; set; }
    public VocabularyProfileService?  VocabularyProfileService  { get; set; }

    // ── Context window limits (engines can override for their token budgets) ──

    /// <summary>Max chars of rolling context to include in the user prompt.</summary>
    protected virtual int RollingContextLimit => 400;
    /// <summary>Max chars of OCR screen text to include in the user prompt.</summary>
    protected virtual int ScreenContextLimit => 1200;

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
    /// Build the system instruction: base behavioral rules + app-specific tone hint
    /// + learned style profile + recently-rejected patterns to avoid + length guidance.
    /// Used by all cloud engines unchanged.
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

            if (StyleProfileService != null)
            {
                var styleHint = StyleProfileService.GetStyleHint(category.ToString());
                if (!string.IsNullOrEmpty(styleHint))
                {
                    sb.AppendLine();
                    sb.AppendLine($"User's writing style: {styleHint}");
                }
            }

            // Vocabulary fingerprint — deterministically extracted, no LLM cost.
            // Provides specific, reliable signals the model can act on immediately.
            if (VocabularyProfileService != null)
            {
                var vocabHint = VocabularyProfileService.GetVocabularyHint(category.ToString());
                if (!string.IsNullOrEmpty(vocabHint))
                {
                    sb.AppendLine();
                    sb.AppendLine(vocabHint);
                }
            }
        }

        // Sub-Phase C: session writing mode hint — verbatim recent completions the user
        // accepted this session, injected before negative examples so the model can
        // mirror the current voice and topic. Only shown when there is enough session
        // data to be meaningful (≥2 items in the rolling 15-minute window).
        if (LearningService != null && context.HasAppContext)
        {
            var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
            var sessionHint = LearningService.GetSessionModeHint(category.ToString());
            if (!string.IsNullOrEmpty(sessionHint))
            {
                sb.AppendLine();
                sb.AppendLine(sessionHint);
            }
        }

        // Inject recently-rejected completions as negative guidance so the model
        // avoids repeating patterns the user has already dismissed.
        var negativeExamples = LearningService?.GetNegativeExamples(context, 2) ?? [];
        if (negativeExamples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("The user recently dismissed these completions for similar text — avoid this style:");
            foreach (var ex in negativeExamples)
                sb.AppendLine($"  - \"{ex.Completion.Trim()}\"");
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

        if (context.HasRollingContext)
        {
            var rollingText = context.RollingContext!;
            if (rollingText.Length > RollingContextLimit)
                rollingText = "..." + rollingText[^RollingContextLimit..];
            sb.AppendLine("Recently written text (the user's previous sentences in this document/conversation):");
            sb.AppendLine("---");
            sb.AppendLine(rollingText);
            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (context.HasScreenContext)
        {
            var screenText = context.ScreenText!;
            if (screenText.Length > ScreenContextLimit)
                screenText = "..." + screenText[^ScreenContextLimit..];
            sb.AppendLine("Text visible on screen (the conversation/document the user is participating in):");
            sb.AppendLine("---");
            sb.AppendLine(screenText);
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("The user is currently typing the following text. Predict what comes next:");
        sb.AppendLine();
        sb.Append(context.TypedText);
        return sb.ToString();
    }

    // ── Temperature and token sizing ──────────────────────────────────────────

    /// <summary>
    /// Selects temperature based on app context category. Code/terminal → low (precision),
    /// chat → higher (variety). Falls back to the configured Temperature if no context.
    /// </summary>
    protected double GetDynamicTemperature(ContextSnapshot context)
    {
        if (!context.HasAppContext) return Temperature;
        var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
        return category switch
        {
            AppCategory.Category.Code     => 0.1,
            AppCategory.Category.Terminal => 0.1,
            AppCategory.Category.Email    => 0.2,
            AppCategory.Category.Document => 0.25,
            AppCategory.Category.Browser  => 0.3,
            AppCategory.Category.Chat     => 0.35,
            _                             => Temperature
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
