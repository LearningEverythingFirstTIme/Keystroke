using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

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
    private static readonly OutboundPrivacyService OutboundPrivacy = new();

    // ── Shared HTTP connection pool ─────────────────────────────────────────
    //
    // All prediction engines share a single SocketsHttpHandler so TCP connections
    // are reused across engine switches.  Each engine still creates its own
    // HttpClient (with its own headers / timeout) but passes this shared handler
    // with disposeHandler:false so Disposing the HttpClient does NOT destroy the
    // connection pool.  This avoids socket exhaustion (a well-known .NET pitfall)
    // and eliminates the cold TCP/TLS handshake on every engine switch.
    //
    private static readonly SocketsHttpHandler SharedHttpHandler = new()
    {
        MaxConnectionsPerServer  = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    };

    /// <summary>
    /// Create an HttpClient that shares the pooled connections with all other engines.
    /// Callers MUST pass disposeHandler: false (done internally here) so that
    /// disposing the returned HttpClient does not destroy the shared pool.
    /// </summary>
    protected static HttpClient CreatePooledHttpClient(TimeSpan timeout)
    {
        return new HttpClient(SharedHttpHandler, disposeHandler: false)
        {
            Timeout = timeout
        };
    }

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
    /// Hard ceiling (in characters) for the entire AVOID REPEATING block injected
    /// into the system prompt. Prevents anti-repetition from crowding out actual
    /// context when many completions accumulate.
    /// </summary>
    private const int MaxAntiRepetitionChars = 600;
    private const int MaxOverusedEndingsReturned = 3;

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
                .OrderByDescending(g => g.Count())
                .Take(MaxOverusedEndingsReturned)
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

    // ── Streaming degeneration detection ────────────────────────────────────

    /// <summary>
    /// Creates a new degeneration detector for use in a streaming prediction loop.
    /// Call <see cref="StreamingDegenerationDetector.IsDegenerate"/> on each chunk;
    /// if it returns true, abort the stream — the model is stuck in a repetition loop.
    /// </summary>
    protected static StreamingDegenerationDetector CreateDegenerationDetector()
        => new();

    /// <summary>
    /// Detects when a streaming model degenerates into repeating the same character
    /// or short pattern (e.g. ".........", "!!!!!", "aaaa"). Tracks the trailing
    /// characters of the accumulated output and fires when a single character repeats
    /// 5+ times consecutively, or when a short pattern (2-4 chars) repeats 3+ times.
    ///
    /// Usage: create one per streaming call, feed every chunk via IsDegenerate().
    /// </summary>
    protected class StreamingDegenerationDetector
    {
        private readonly StringBuilder _tail = new();
        private const int TailSize = 40; // Only need the trailing window

        /// <summary>
        /// Feed a new streaming chunk. Returns true if the accumulated output
        /// shows degeneration and the stream should be aborted.
        /// </summary>
        public bool IsDegenerate(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return false;

            _tail.Append(chunk);

            // Keep only the trailing window to avoid unbounded growth
            if (_tail.Length > TailSize)
                _tail.Remove(0, _tail.Length - TailSize);

            if (_tail.Length < 5) return false;

            // Check 1: single character repeated 5+ times at the tail
            char last = _tail[^1];
            int sameCount = 0;
            for (int i = _tail.Length - 1; i >= 0 && _tail[i] == last; i--)
                sameCount++;

            if (sameCount >= 5)
                return true;

            // Check 2: short pattern (2-4 chars) repeated 3+ times at the tail
            // e.g. ". ." repeating, or "ha" repeating
            for (int patLen = 2; patLen <= 4 && patLen * 3 <= _tail.Length; patLen++)
            {
                var pattern = _tail.ToString(_tail.Length - patLen, patLen);
                int repeats = 1;
                int pos = _tail.Length - patLen * 2;
                while (pos >= 0)
                {
                    var segment = _tail.ToString(pos, patLen);
                    if (segment != pattern) break;
                    repeats++;
                    pos -= patLen;
                }
                if (repeats >= 3)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the clean portion of the accumulated text (everything before the
        /// degenerate trailing run). Call after IsDegenerate() returns true.
        /// </summary>
        public string GetCleanTail()
        {
            var text = _tail.ToString();

            // Walk backwards to find where the repetition started
            if (text.Length < 2) return text;

            char last = text[^1];
            int runStart = text.Length - 1;
            while (runStart > 0 && text[runStart - 1] == last)
                runStart--;

            // If the run is ≥5 chars, return everything before it
            if (text.Length - runStart >= 5)
                return text[..runStart];

            return text;
        }
    }

    /// <summary>
    /// Injected by the caller (App.xaml.cs) after construction.
    /// Nullable — engines must guard access. Not a constructor parameter to avoid
    /// a wasted file read on every engine creation.
    /// </summary>
    public AcceptanceLearningService?  LearningService          { get; set; }
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
        // The entire block is capped at MaxAntiRepetitionChars to prevent crowding out
        // real context when many completions accumulate.
        var recentCompletions = GetRecentCompletions();
        var negativeExamples = LearningService?.GetNegativeExamples(context, 2) ?? [];
        var overusedEndings = GetOverusedEndings();
        if (recentCompletions.Count > 0 || negativeExamples.Count > 0 || overusedEndings.Count > 0)
        {
            var antiRep = new StringBuilder();
            antiRep.AppendLine("AVOID REPEATING (hard constraint — never output these or close rephrasings):");

            // Overused endings are short and high-signal — add first.
            if (overusedEndings.Count > 0)
            {
                foreach (var ending in overusedEndings)
                    antiRep.AppendLine($"  - ending: \"{ending}\"");
            }

            // Negative examples (dismissed completions) — add next, most relevant.
            foreach (var ex in negativeExamples)
            {
                var line = $"  - \"{TruncateForPrompt(ex.Completion.Trim(), 80)}\"";
                if (antiRep.Length + line.Length > MaxAntiRepetitionChars) break;
                antiRep.AppendLine(line);
            }

            // Recent completions — oldest are least relevant, so add newest first
            // and stop when budget is exhausted.
            for (int i = recentCompletions.Count - 1; i >= 0; i--)
            {
                var line = $"  - \"{TruncateForPrompt(recentCompletions[i], 80)}\"";
                if (antiRep.Length + line.Length > MaxAntiRepetitionChars) break;
                antiRep.AppendLine(line);
            }

            sb.AppendLine();
            sb.Append(antiRep);
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
            sb.AppendLine($"[Application: {context.SafeContextLabel}]");
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
        var bundle = LearningHintBundleBuilder.Build(LearningService, StyleProfileService, VocabularyProfileService, context);

        if (bundle.IsContextDisabled)
            return null;

        if (bundle.Confidence > 0)
        {
            if (bundle.Confidence >= 0.45 && !string.IsNullOrWhiteSpace(bundle.StyleHint))
                parts.Add($"Writing style ({bundle.Confidence:P0} confidence): {OutboundPrivacy.SanitizeForPrompt(bundle.StyleHint)}");

            if (bundle.Confidence >= 0.45 && !string.IsNullOrWhiteSpace(bundle.VocabularyHint))
                parts.Add(OutboundPrivacy.SanitizeForPrompt(bundle.VocabularyHint) ?? "");

            if (!string.IsNullOrWhiteSpace(bundle.PreferredClosings))
                parts.Add(OutboundPrivacy.SanitizeForPrompt(bundle.PreferredClosings) ?? "");

            if (!string.IsNullOrWhiteSpace(bundle.AvoidPatterns))
                parts.Add(OutboundPrivacy.SanitizeForPrompt(bundle.AvoidPatterns) ?? "");

            if (!string.IsNullOrWhiteSpace(bundle.SessionHint))
                parts.Add(OutboundPrivacy.SanitizeForPrompt(bundle.SessionHint) ?? "");

            parts = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (parts.Count > 0)
                return string.Join("\n", parts);
        }
        return null;
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

    // ── Shared SSE stream parsing ────────────────────────────────────────────

    /// <summary>
    /// Shared SSE stream parser for all cloud prediction engines. Reads "data: "
    /// prefixed lines, delegates text extraction to the engine-specific callback,
    /// handles first-chunk normalization, degeneration detection, and final
    /// post-processing (trim, dedup, record).
    /// </summary>
    protected async Task<string?> ParseSseStreamAsync(
        HttpResponseMessage response,
        string prefix,
        Func<string, string?> extractText,
        Action<string> onChunk,
        CancellationToken ct)
    {
        var fullCompletion = new StringBuilder();
        bool isFirstChunk = true;
        var degenDetector = CreateDegenerationDetector();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            var dataJson = line[6..];
            if (dataJson == "[DONE]") break;

            try
            {
                var text = extractText(dataJson);
                if (!string.IsNullOrEmpty(text))
                {
                    if (isFirstChunk)
                    {
                        text = text.TrimStart('"');
                        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            text = text[prefix.Length..];
                        if (text.Length > 0 && !prefix.EndsWith(" ") && !text.StartsWith(" "))
                            text = " " + text;
                        isFirstChunk = false;
                    }

                    if (degenDetector.IsDegenerate(text))
                    {
                        Log($"Stream aborted: degeneration detected after {fullCompletion.Length} chars");
                        break;
                    }

                    fullCompletion.Append(text);
                    onChunk(text);
                }
            }
            catch (JsonException) { /* malformed chunk — skip */ }
        }

        var raw = fullCompletion.ToString().TrimEnd('"').Trim();
        var result = TrimToWholeWords(StripThinkTags(raw));
        result = RejectDuplicate(prefix, result) ?? "";
        Log($"Stream complete: {result.Length} chars{(string.IsNullOrWhiteSpace(result) ? " (rejected as duplicate)" : "")}");
        if (!string.IsNullOrWhiteSpace(result))
            RecordRecentCompletion(result);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    /// Shared post-processing for non-streaming completions and alternatives:
    /// strip think tags, trim quotes, strip prefix echo, ensure leading space,
    /// trim to whole words, and reject duplicates.
    /// </summary>
    protected string? PostProcessCompletion(string prefix, string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion)) return null;

        completion = StripThinkTags(completion);
        completion = completion.Trim('"').Trim();
        if (string.IsNullOrWhiteSpace(completion)) return null;

        if (completion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            completion = completion[prefix.Length..].TrimStart();
        if (completion.Length > 0 && !prefix.EndsWith(" ") && !completion.StartsWith(" "))
            completion = " " + completion;

        completion = TrimToWholeWords(completion);
        return RejectDuplicate(prefix, completion!);
    }

    /// <summary>
    /// Truncates a string to maxLen characters, appending "…" if shortened.
    /// Used to keep individual anti-repetition entries from consuming the budget.
    /// </summary>
    private static string TruncateForPrompt(string text, int maxLen)
    {
        if (text.Length <= maxLen) return text;
        return text[..(maxLen - 1)] + "…";
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
    /// Rejects completions containing literal prompt markers from the model input.
    /// This stays intentionally narrow so normal prose like "the user" is allowed.
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
    /// Literal prompt markers that should never appear in a completion.
    /// </summary>
    private static readonly string[] PromptLeakagePhrases =
    [
        "[application:",
        "<screen_context>",
        "</screen_context>",
        "<recently_written>",
        "</recently_written>",
        "<complete_this>",
        "</complete_this>",
        "<user_style_hints>",
        "</user_style_hints>",
    ];

    /// <summary>
    /// Trims trailing partial words so completions always end on a word boundary.
    /// A "complete" ending is: trailing punctuation, trailing whitespace, or a full
    /// word with no trailing character (even without punctuation).
    /// Also strips degenerate repeated punctuation (e.g. "........." or "!!!!!!!")
    /// that models sometimes produce when they fail to stop cleanly.
    /// </summary>
    protected static string TrimToWholeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return trimmed;

        // Strip trailing runs of repeated punctuation (3+ of the same character).
        // Keep up to the natural count: "..." stays as "...", "?!" stays, but
        // ".........." becomes "..." and "!!!!!" becomes "!".
        trimmed = TrimRepeatedTrailingPunctuation(trimmed);
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

    /// <summary>
    /// Collapses trailing runs of the same punctuation character to a sensible length.
    /// "........" → "..." (ellipsis is the natural form for periods)
    /// "!!!!!!"  → "!"   (single is natural for exclamation/question)
    /// "???"     → "?"   (single is natural)
    /// Mixed punctuation like "?!" is left alone.
    /// </summary>
    private static string TrimRepeatedTrailingPunctuation(string text)
    {
        if (text.Length < 3) return text;

        // Find where the trailing run of the same character starts
        char last = text[^1];
        if (!char.IsPunctuation(last)) return text;

        int runStart = text.Length - 1;
        while (runStart > 0 && text[runStart - 1] == last)
            runStart--;

        int runLength = text.Length - runStart;
        if (runLength < 3) return text; // "." or ".." — leave as-is

        // Periods get collapsed to "..." (ellipsis); everything else to a single char
        int keepCount = last == '.' ? 3 : 1;
        return text[..runStart] + new string(last, keepCount);
    }
}
