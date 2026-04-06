using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

/// <summary>
/// GPT-5 (OpenAI) prediction engine implementation.
/// Supports GPT-5.4, GPT-5.4 mini, GPT-5.4 nano, and other OpenAI chat models.
/// </summary>
public class Gpt5PredictionEngine : PredictionEngineBase, IPredictionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _endpoint;

    public Gpt5PredictionEngine(string apiKey, string model = "gpt-5.4-nano")
        : base("gpt5.log")
    {
        _model    = model;
        _endpoint = "https://api.openai.com/v1/chat/completions";
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
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
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);
            var dynamicTemp    = GetDynamicTemperature(context);

            var body = new
            {
                model                 = _model,
                max_completion_tokens = adaptiveTokens,
                temperature           = dynamicTemp,
                top_p                 = 0.9,
                messages              = BuildMessages(context)
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            var rollingCtxLen = context.RollingContext?.Length ?? 0;
            Log($"=== Request for: \"{prefix}\" [model={_model}, app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, rolling={rollingCtxLen}, tokens={adaptiveTokens}] ===");

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
            var result     = JsonSerializer.Deserialize<Gpt5Response>(respBody);
            var completion = result?.Choices is { Length: > 0 } choices ? choices[0]?.Message?.Content?.Trim() : null;
            Log($"Completion: {completion ?? "(null)"}");

            if (string.IsNullOrWhiteSpace(completion)) return null;

            completion = completion.Trim('"').Trim();
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
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);
            var dynamicTemp    = GetDynamicTemperature(context);

            var body = new
            {
                model                 = _model,
                max_completion_tokens = adaptiveTokens,
                temperature           = dynamicTemp,
                top_p                 = 0.9,
                stream                = true,
                messages              = BuildMessages(context)
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            Log($"=== Stream for: \"{prefix}\" [model={_model}, app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, tokens={adaptiveTokens}] ===");

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
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
                if (dataJson == "[DONE]") continue;

                try
                {
                    var chunk = JsonSerializer.Deserialize<Gpt5StreamChunk>(dataJson);
                    var text  = chunk?.Choices?[0]?.Delta?.Content;

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

            var result = TrimToWholeWords(fullCompletion.ToString().TrimEnd('"').Trim());
            result = RejectDuplicate(prefix, result) ?? "";
            Log($"Stream complete: {result.Length} chars{(string.IsNullOrWhiteSpace(result) ? " (rejected as duplicate)" : "")}");
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"Stream exception: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Fetch multiple alternative completions by making parallel requests with higher temperature.
    /// </summary>
    public async Task<List<string>> FetchAlternativesAsync(ContextSnapshot context, int count = 3, CancellationToken ct = default)
    {
        var results = new List<string>();
        var prefix  = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return results;
        if (IsRateLimited()) return results;

        try
        {
            var dynamicTemp    = GetDynamicTemperature(context);
            var altTemp        = Math.Min(dynamicTemp + 0.3, 1.5);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            Log($"=== Alternatives for: \"{prefix}\" (count={count}, temp={altTemp:F1}, tokens={adaptiveTokens}) ===");

            var tasks = Enumerable.Range(0, count).Select(async _ =>
            {
                var body = new
                {
                    model                 = _model,
                    max_completion_tokens = adaptiveTokens,
                    temperature           = altTemp,
                    top_p                 = 0.95,
                    messages              = BuildMessages(context)
                };
                var json     = JsonSerializer.Serialize(body);
                var response = await _httpClient.PostAsync(
                    _endpoint,
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    ct);
                if (!response.IsSuccessStatusCode) return null;
                var respBody = await response.Content.ReadAsStringAsync(ct);
                var result   = JsonSerializer.Deserialize<Gpt5Response>(respBody);
                return result?.Choices is { Length: > 0 } ch ? ch[0]?.Message?.Content?.Trim() : null;
            });

            var completions = await Task.WhenAll(tasks);

            foreach (var text in completions)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var processed = text.Trim('"');
                if (processed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    processed = processed[prefix.Length..];
                if (!prefix.EndsWith(" ") && processed.Length > 0 && !processed.StartsWith(" "))
                    processed = " " + processed;
                processed = TrimToWholeWords(processed.Trim('"'));
                processed = RejectDuplicate(prefix, processed!);
                if (!string.IsNullOrWhiteSpace(processed) && !results.Contains(processed))
                    results.Add(processed);
            }

            Log($"Got {results.Count} alternatives");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Alternatives error: {ex.Message}"); }

        return results;
    }

    /// <summary>
    /// Builds the full messages array: system message, few-shot turns, then the real request.
    /// </summary>
    private object[] BuildMessages(ContextSnapshot context)
    {
        var systemText = BuildSystemInstruction(context);
        var examples   = LearningService?.GetExamples(context, 3) ?? [];
        var messages   = new List<object>();

        messages.Add(new { role = "system", content = systemText });

        foreach (var ex in examples)
        {
            var fewShotUser = $"[Application: {ex.Context}]\n\nThe user is currently typing the following text. Predict what comes next:\n\n{ex.Prefix}";
            messages.Add(new { role = "user",      content = fewShotUser  });
            messages.Add(new { role = "assistant", content = ex.Completion });
        }

        messages.Add(new { role = "user", content = BuildUserPrompt(context) });

        if (examples.Count > 0)
            Log($"Included {examples.Count} few-shot conversation turns for {context.ProcessName}");

        return messages.ToArray();
    }

    public async Task<string?> GenerateTextAsync(string systemPrompt, string userPrompt, int maxTokens = 200, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                model = _model, max_completion_tokens = maxTokens, temperature = 0.4,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };
            var json = JsonSerializer.Serialize(body);
            var response = await _httpClient.PostAsync(_endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) return null;
            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<Gpt5Response>(respBody);
            return result?.Choices?[0]?.Message?.Content?.Trim();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"GenerateText error: {ex.Message}"); return null; }
    }

    // ── Response DTOs ─────────────────────────────────────────────────────────

    private class Gpt5Response
    {
        [JsonPropertyName("choices")]
        public Gpt5Choice[]? Choices { get; set; }
    }

    private class Gpt5Choice
    {
        [JsonPropertyName("message")]
        public Gpt5Message? Message { get; set; }
    }

    private class Gpt5Message
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private class Gpt5StreamChunk
    {
        [JsonPropertyName("choices")]
        public Gpt5StreamChoice[]? Choices { get; set; }
    }

    private class Gpt5StreamChoice
    {
        [JsonPropertyName("delta")]
        public Gpt5Delta? Delta { get; set; }
    }

    private class Gpt5Delta
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
