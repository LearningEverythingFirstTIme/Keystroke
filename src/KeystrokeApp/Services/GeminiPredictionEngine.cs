using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

public class GeminiPredictionEngine : IPredictionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _streamEndpoint;
    private readonly string _logPath;

    // Rate limiting protection
    private static DateTime _lastRateLimitError = DateTime.MinValue;
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(10);

    // These can be updated from settings without recreating the engine
    public string SystemPrompt { get; set; } = AppConfig.DefaultSystemPrompt;
    public string LengthInstruction { get; set; } = "Write 15-30 words to complete the full thought.";
    public double Temperature { get; set; } = 0.3;
    public int MaxOutputTokens { get; set; } = 100;
    public AcceptanceLearningService LearningService { get; set; } = new();

    public GeminiPredictionEngine(string apiKey, string model = "gemini-3.1-flash-lite-preview")
    {
        _apiKey = apiKey;
        _endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        _streamEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse";
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "gemini.log");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    private bool IsRateLimited()
    {
        var timeSinceLastError = DateTime.UtcNow - _lastRateLimitError;
        if (timeSinceLastError < RateLimitCooldown)
        {
            Log($"Rate limit cooldown active ({timeSinceLastError.TotalSeconds:F1}s remaining), skipping request");
            return true;
        }
        return false;
    }

    private void CheckRateLimitResponse(HttpResponseMessage response, string errorBody)
    {
        if ((int)response.StatusCode == 429 || errorBody.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
        {
            _lastRateLimitError = DateTime.UtcNow;
            Log($"Rate limit hit! Cooldown: {RateLimitCooldown.TotalSeconds}s");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>
    /// Gets the appropriate temperature for the context.
    /// Code needs precision (low temp), chat benefits from flexibility (higher temp).
    /// Falls back to the configured Temperature if no app context.
    /// </summary>
    private double GetDynamicTemperature(ContextSnapshot context)
    {
        if (!context.HasAppContext)
            return Temperature;

        var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
        
        return category switch
        {
            AppCategory.Category.Code => 0.1,      // Strict, deterministic for code
            AppCategory.Category.Terminal => 0.1,  // Precise for commands
            AppCategory.Category.Email => 0.2,     // Professional, predictable
            AppCategory.Category.Document => 0.25, // Structured prose
            AppCategory.Category.Browser => 0.3,   // Balanced for web forms
            AppCategory.Category.Chat => 0.35,     // Slightly more creative for conversation
            _ => Temperature                       // Default from settings
        };
    }

    public async Task<string?> PredictAsync(ContextSnapshot context, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3)
            return null;

        if (IsRateLimited()) return null;

        try
        {
            var systemText = BuildSystemInstruction(context);
            var dynamicTemp = GetDynamicTemperature(context);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            var body = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = systemText } }
                },
                contents = BuildContents(context),
                generationConfig = new
                {
                    maxOutputTokens = adaptiveTokens,
                    temperature = dynamicTemp,
                    topP = 0.9,
                    thinkingConfig = new { thinkingBudget = 0 }
                }
            };

            var json = JsonSerializer.Serialize(body);

            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            var rollingCtxLen = context.RollingContext?.Length ?? 0;
            Log($"=== Request for: \"{prefix}\" [app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, ocr={context.HasScreenContext}, rolling={rollingCtxLen}, tokens={adaptiveTokens}] ===");

            var response = await _httpClient.PostAsync(
                _endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                Log($"Error {response.StatusCode}: {err}");
                CheckRateLimitResponse(response, err);
                return null;
            }

            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GeminiResponse>(respBody);
            var completion = result?.Candidates?[0]?.Content?.Parts?[0]?.Text?.Trim();
            Log($"Completion: {completion ?? "(null)"}");

            if (string.IsNullOrWhiteSpace(completion))
                return null;

            // Strip quotes Gemini adds despite instructions —
            // both full wrapping ("...") and lone leading/trailing marks
            completion = completion.Trim('"');
            completion = completion.Trim();

            if (completion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                completion = completion[prefix.Length..].TrimStart();

            completion = TrimToWholeWords(completion);
            completion = RejectDuplicate(prefix, completion!);
            return string.IsNullOrWhiteSpace(completion) ? null : completion;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"Exception: {ex.Message}"); return null; }
    }

    public async Task<string?> PredictStreamingAsync(ContextSnapshot context, Action<string> onChunk, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3)
            return null;

        if (IsRateLimited()) return null;

        try
        {
            var systemText = BuildSystemInstruction(context);
            var dynamicTemp = GetDynamicTemperature(context);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            var body = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = systemText } }
                },
                contents = BuildContents(context),
                generationConfig = new
                {
                    maxOutputTokens = adaptiveTokens,
                    temperature = dynamicTemp,
                    topP = 0.9,
                    thinkingConfig = new { thinkingBudget = 0 }
                }
            };

            var json = JsonSerializer.Serialize(body);

            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            var rollingCtxLen = context.RollingContext?.Length ?? 0;
            Log($"=== Stream for: \"{prefix}\" [app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, rolling={rollingCtxLen}, tokens={adaptiveTokens}] ===");

            var request = new HttpRequestMessage(HttpMethod.Post, _streamEndpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                Log($"Stream error {response.StatusCode}: {err}");
                CheckRateLimitResponse(response, err);
                return null;
            }

            var fullCompletion = new StringBuilder();
            bool isFirstChunk = true;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                // SSE format: "data: {json}" lines, with blank lines between events
                if (!line.StartsWith("data: ")) continue;

                var dataJson = line[6..]; // Strip "data: " prefix
                try
                {
                    var chunk = JsonSerializer.Deserialize<GeminiResponse>(dataJson);
                    var text = chunk?.Candidates?[0]?.Content?.Parts?[0]?.Text;

                    if (!string.IsNullOrEmpty(text))
                    {
                        // First chunk: handle space prefix and quote stripping
                        if (isFirstChunk)
                        {
                            text = text.TrimStart('"');
                            // Handle prefix duplication
                            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                text = text[prefix.Length..];
                            // Add leading space if needed
                            if (text.Length > 0 && !prefix.EndsWith(" ") && !text.StartsWith(" "))
                                text = " " + text;
                            isFirstChunk = false;
                        }

                        fullCompletion.Append(text);
                        onChunk(text);
                    }
                }
                catch (JsonException)
                {
                    // Malformed chunk — skip it
                }
            }

            var result = TrimToWholeWords(fullCompletion.ToString().TrimEnd('"').Trim());
            result = RejectDuplicate(prefix, result) ?? "";
            Log($"Stream complete: {result.Length} chars{(string.IsNullOrWhiteSpace(result) ? " (rejected as duplicate)" : "")}");

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"Stream exception: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Fetch multiple alternative completions using candidateCount.
    /// Uses the non-streaming endpoint with slightly higher temperature for variety.
    /// Returns a list of completions (may be empty).
    /// </summary>
    public async Task<List<string>> FetchAlternativesAsync(ContextSnapshot context, int count = 3, CancellationToken ct = default)
    {
        var results = new List<string>();
        var prefix = context.TypedText;

        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3)
            return results;

        if (IsRateLimited()) return results;

        try
        {
            var systemText = BuildSystemInstruction(context);
            var dynamicTemp = GetDynamicTemperature(context);
            // For alternatives, add variety on top of the base dynamic temperature
            var altTemp = Math.Min(dynamicTemp + 0.3, 1.5);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            var body = new
            {
                systemInstruction = new
                {
                    parts = new object[] { new { text = systemText } }
                },
                contents = BuildContents(context),
                generationConfig = new
                {
                    maxOutputTokens = adaptiveTokens,
                    candidateCount = count,
                    temperature = altTemp,
                    topP = 0.95,
                    thinkingConfig = new { thinkingBudget = 0 }
                }
            };

            var json = JsonSerializer.Serialize(body);
            Log($"=== Alternatives for: \"{prefix}\" (count={count}, temp={altTemp:F1}, tokens={adaptiveTokens}) ===");

            var response = await _httpClient.PostAsync(
                _endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
                return results;

            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GeminiResponse>(respBody);

            if (result?.Candidates == null)
                return results;

            foreach (var candidate in result.Candidates)
            {
                var text = candidate?.Content?.Parts?[0]?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                text = text.Trim('"');
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    text = text[prefix.Length..];

                // Add leading space if needed
                if (text.Length > 0 && !prefix.EndsWith(" ") && !text.StartsWith(" "))
                    text = " " + text;

                text = TrimToWholeWords(text.Trim('"'));
                text = RejectDuplicate(prefix, text!);

                if (!string.IsNullOrWhiteSpace(text))
                    results.Add(text);
            }

            Log($"Got {results.Count} alternatives");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Alternatives error: {ex.Message}"); }

        return results;
    }

    /// <summary>
    /// Build the system instruction — stable behavioral rules + app-specific tone + few-shot examples.
    /// This goes into Gemini's systemInstruction field (separate from user content).
    /// </summary>
    private string BuildSystemInstruction(ContextSnapshot context)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SystemPrompt);

        // Add app-specific tone hint
        if (context.HasAppContext)
        {
            var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
            var toneHint = AppCategory.GetToneHint(category);
            sb.AppendLine();
            sb.AppendLine($"Application context: {toneHint}");
        }

        sb.AppendLine();
        sb.AppendLine(LengthInstruction);

        return sb.ToString();
    }

    /// <summary>
    /// Build the user-facing prompt — rolling context + screen context + the text to complete.
    /// Kept separate from system instruction so Gemini treats it as the "input".
    /// </summary>
    private string BuildUserPrompt(ContextSnapshot context)
    {
        var sb = new StringBuilder();

        // App identification
        if (context.HasAppContext)
        {
            sb.AppendLine($"[Application: {context.ProcessName} — \"{context.WindowTitle}\"]");
            sb.AppendLine();
        }

        // Rolling context: recently accepted text from this editing session
        // This provides continuity across multiple completion/tab cycles
        if (context.HasRollingContext)
        {
            var rollingText = context.RollingContext!;
            // Limit rolling context to avoid overwhelming the prompt
            if (rollingText.Length > 400)
                rollingText = "..." + rollingText[^400..];

            sb.AppendLine("Recently written text (the user's previous sentences in this document/conversation):");
            sb.AppendLine("---");
            sb.AppendLine(rollingText);
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // Screen context from OCR — this is the most valuable signal for external context
        if (context.HasScreenContext)
        {
            var screenText = context.ScreenText!;
            if (screenText.Length > 1200)
                screenText = "..." + screenText[^1200..];

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

    /// <summary>
    /// Builds the contents array with few-shot conversation turns followed by the real request.
    /// Gemini uses "model" for the assistant role in multi-turn contents.
    /// </summary>
    private object[] BuildContents(ContextSnapshot context)
    {
        var examples = LearningService.GetExamples(context, 3);
        var contents = new List<object>();

        foreach (var ex in examples)
        {
            var fewShotUser = $"[Application: {ex.Context}]\n\nThe user is currently typing the following text. Predict what comes next:\n\n{ex.Prefix}";
            contents.Add(new { role = "user", parts = new[] { new { text = fewShotUser } } });
            contents.Add(new { role = "model", parts = new[] { new { text = ex.Completion } } });
        }

        contents.Add(new { role = "user", parts = new[] { new { text = BuildUserPrompt(context) } } });

        if (examples.Count > 0)
            Log($"Included {examples.Count} few-shot conversation turns for {context.ProcessName}");

        return contents.ToArray();
    }

    /// <summary>
    /// Caps max tokens based on prefix length. Short prefixes are highly ambiguous —
    /// generating 100 tokens just produces vague output and wastes latency.
    /// </summary>
    private int GetAdaptiveMaxTokens(string prefix)
    {
        var wordCount = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 4) return Math.Min(25, MaxOutputTokens);
        if (wordCount < 8) return Math.Min(60, MaxOutputTokens);
        return MaxOutputTokens;
    }

    /// <summary>
    /// Detect if the completion is just repeating text the user already typed.
    /// Checks if a significant chunk of the completion already appears in the typed text.
    /// Returns null if it's a duplicate, otherwise returns the completion unchanged.
    /// </summary>
    private static string? RejectDuplicate(string typedText, string completion)
    {
        if (string.IsNullOrWhiteSpace(completion) || typedText.Length < 10)
            return completion;

        var cleanCompletion = completion.Trim();
        if (cleanCompletion.Length < 8)
            return completion;

        // Check if the completion (or a substantial portion) already appears in the typed text
        // Use a sliding window: if any 8+ word sequence from the completion exists in the typed text, reject it
        var completionWords = cleanCompletion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typedLower = typedText.ToLowerInvariant();

        if (completionWords.Length >= 4)
        {
            // Check 4-word windows from the completion against the typed text
            for (int i = 0; i <= completionWords.Length - 4; i++)
            {
                var phrase = string.Join(" ", completionWords[i..(i + 4)]).ToLowerInvariant();
                if (typedLower.Contains(phrase))
                    return null;
            }
        }

        return completion;
    }

    /// <summary>
    /// Trim trailing partial words so completions always end on a word boundary.
    /// A "complete" ending is one that ends with a space, punctuation, or is the
    /// end of a full word followed by nothing.
    /// </summary>
    private static string TrimToWholeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
            return trimmed;

        // If it ends with punctuation or a space, it's already clean
        char last = trimmed[^1];
        if (char.IsPunctuation(last) || char.IsWhiteSpace(last))
            return trimmed;

        // Find the last space — everything after it is the last word.
        // If there's no space at all, the entire text is one word — keep it.
        int lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace < 0)
            return trimmed;

        // Check if the last "word" looks like a fragment (single char that isn't 'I' or 'a')
        string lastWord = trimmed[(lastSpace + 1)..];
        if (lastWord.Length == 1 && lastWord != "I" && lastWord != "a" && lastWord != "A")
            return trimmed[..lastSpace].TrimEnd();

        // Otherwise keep it — it's likely a whole word, just no trailing punctuation
        return trimmed;
    }

    private class GeminiResponse { [JsonPropertyName("candidates")] public GeminiCandidate[]? Candidates { get; set; } }
    private class GeminiCandidate { [JsonPropertyName("content")] public GeminiContent? Content { get; set; } }
    private class GeminiContent { [JsonPropertyName("parts")] public GeminiPart[]? Parts { get; set; } }
    private class GeminiPart { [JsonPropertyName("text")] public string? Text { get; set; } }
}
