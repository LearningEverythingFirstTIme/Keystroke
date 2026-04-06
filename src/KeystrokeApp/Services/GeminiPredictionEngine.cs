using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

public class GeminiPredictionEngine : PredictionEngineBase, IPredictionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _streamEndpoint;

    public GeminiPredictionEngine(string apiKey, string model = "gemini-3.1-flash-lite-preview")
        : base("gemini.log")
    {
        _apiKey         = apiKey;
        _endpoint       = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        _streamEndpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse";
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<string?> PredictAsync(ContextSnapshot context, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3)
            return null;

        if (IsRateLimited()) return null;

        try
        {
            var systemText     = BuildSystemInstruction(context);
            var dynamicTemp    = GetDynamicTemperature(context);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            var body = new
            {
                systemInstruction = new { parts = new object[] { new { text = systemText } } },
                contents          = BuildContents(context),
                generationConfig  = new
                {
                    maxOutputTokens = adaptiveTokens,
                    temperature     = dynamicTemp,
                    topP            = 0.9,
                    thinkingConfig  = new { thinkingBudget = 0 }
                }
            };

            var json     = JsonSerializer.Serialize(body);
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

            var respBody   = await response.Content.ReadAsStringAsync(ct);
            var result     = JsonSerializer.Deserialize<GeminiResponse>(respBody);
            var completion = result?.Candidates is { Length: > 0 } cands
                && cands[0]?.Content?.Parts is { Length: > 0 } parts
                ? parts[0]?.Text?.Trim() : null;
            Log($"Completion: {completion ?? "(null)"}");

            if (string.IsNullOrWhiteSpace(completion)) return null;

            completion = completion.Trim('"').Trim();
            if (completion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                completion = completion[prefix.Length..].TrimStart();

            completion = TrimToWholeWords(completion);
            completion = RejectDuplicate(prefix, completion!);
            if (!string.IsNullOrWhiteSpace(completion))
                RecordRecentCompletion(completion);
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
            var systemText     = BuildSystemInstruction(context);
            var dynamicTemp    = GetDynamicTemperature(context);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            var body = new
            {
                systemInstruction = new { parts = new object[] { new { text = systemText } } },
                contents          = BuildContents(context),
                generationConfig  = new
                {
                    maxOutputTokens = adaptiveTokens,
                    temperature     = dynamicTemp,
                    topP            = 0.9,
                    thinkingConfig  = new { thinkingBudget = 0 }
                }
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            var rollingCtxLen = context.RollingContext?.Length ?? 0;
            Log($"=== Stream for: \"{prefix}\" [app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, rolling={rollingCtxLen}, tokens={adaptiveTokens}] ===");

            using var request = new HttpRequestMessage(HttpMethod.Post, _streamEndpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                Log($"Stream error {response.StatusCode}: {err}");
                CheckRateLimitResponse(response, err);
                return null;
            }

            var fullCompletion = new StringBuilder();
            bool isFirstChunk  = true;
            var degenDetector  = CreateDegenerationDetector();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data: ")) continue;

                var dataJson = line[6..];
                try
                {
                    var chunk = JsonSerializer.Deserialize<GeminiResponse>(dataJson);
                    var text  = chunk?.Candidates?[0]?.Content?.Parts?[0]?.Text;

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

                        // Abort early if the model is degenerating (repeating characters)
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

            var result = TrimToWholeWords(fullCompletion.ToString().TrimEnd('"').Trim());
            result = RejectDuplicate(prefix, result) ?? "";
            Log($"Stream complete: {result.Length} chars{(string.IsNullOrWhiteSpace(result) ? " (rejected as duplicate)" : "")}");
            if (!string.IsNullOrWhiteSpace(result))
                RecordRecentCompletion(result);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"Stream exception: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Fetch multiple alternative completions using candidateCount.
    /// Uses the non-streaming endpoint with slightly higher temperature for variety.
    /// </summary>
    public async Task<List<string>> FetchAlternativesAsync(ContextSnapshot context, int count = 3, CancellationToken ct = default)
    {
        var results = new List<string>();
        var prefix  = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return results;
        if (IsRateLimited()) return results;

        try
        {
            var systemText     = BuildSystemInstruction(context);
            var dynamicTemp    = GetDynamicTemperature(context);
            var altTemp        = Math.Min(dynamicTemp + 0.3, 1.5);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            var body = new
            {
                systemInstruction = new { parts = new object[] { new { text = systemText } } },
                contents          = BuildContents(context),
                generationConfig  = new
                {
                    maxOutputTokens = adaptiveTokens,
                    candidateCount  = count,
                    temperature     = altTemp,
                    topP            = 0.95,
                    thinkingConfig  = new { thinkingBudget = 0 }
                }
            };

            var json = JsonSerializer.Serialize(body);
            Log($"=== Alternatives for: \"{prefix}\" (count={count}, temp={altTemp:F1}, tokens={adaptiveTokens}) ===");

            var response = await _httpClient.PostAsync(
                _endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode) return results;

            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result   = JsonSerializer.Deserialize<GeminiResponse>(respBody);

            if (result?.Candidates == null) return results;

            foreach (var candidate in result.Candidates)
            {
                var text = candidate?.Content?.Parts?[0]?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                text = text.Trim('"');
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    text = text[prefix.Length..];
                if (text.Length > 0 && !prefix.EndsWith(" ") && !text.StartsWith(" "))
                    text = " " + text;
                text = TrimToWholeWords(text.Trim('"'));
                text = RejectDuplicate(prefix, text!);
                if (!string.IsNullOrWhiteSpace(text) && !results.Contains(text))
                    results.Add(text);
            }

            Log($"Got {results.Count} alternatives");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Alternatives error: {ex.Message}"); }

        return results;
    }

    /// <summary>
    /// Builds the contents array with few-shot conversation turns.
    /// Gemini uses "model" for the assistant role in multi-turn contents.
    /// </summary>
    private object[] BuildContents(ContextSnapshot context)
    {
        var examples = LearningService?.GetExamples(context, 3) ?? [];
        var contents = new List<object>();

        foreach (var ex in examples)
        {
            var fewShotUser = $"[Application: {ex.Context}]\n\n<complete_this>\n{ex.Prefix}\n</complete_this>";
            contents.Add(new { role = "user",  parts = new[] { new { text = fewShotUser  } } });
            contents.Add(new { role = "model", parts = new[] { new { text = ex.Completion } } });
        }

        contents.Add(new { role = "user", parts = new[] { new { text = BuildUserPrompt(context) } } });

        if (examples.Count > 0)
            Log($"Included {examples.Count} few-shot conversation turns for {context.ProcessName}");

        return contents.ToArray();
    }

    // ── Response DTOs ─────────────────────────────────────────────────────────

    public async Task<string?> GenerateTextAsync(string systemPrompt, string userPrompt, int maxTokens = 200, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                systemInstruction = new { parts = new object[] { new { text = systemPrompt } } },
                contents = new object[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                generationConfig = new { maxOutputTokens = maxTokens, temperature = 0.4, topP = 0.9, thinkingConfig = new { thinkingBudget = 0 } }
            };
            var json = JsonSerializer.Serialize(body);
            var response = await _httpClient.PostAsync(_endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) return null;
            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GeminiResponse>(respBody);
            return result?.Candidates?[0]?.Content?.Parts?[0]?.Text?.Trim();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"GenerateText error: {ex.Message}"); return null; }
    }

    private class GeminiResponse  { [JsonPropertyName("candidates")] public GeminiCandidate[]? Candidates { get; set; } }
    private class GeminiCandidate { [JsonPropertyName("content")]    public GeminiContent?     Content    { get; set; } }
    private class GeminiContent   { [JsonPropertyName("parts")]      public GeminiPart[]?      Parts      { get; set; } }
    private class GeminiPart      { [JsonPropertyName("text")]       public string?             Text       { get; set; } }
}
