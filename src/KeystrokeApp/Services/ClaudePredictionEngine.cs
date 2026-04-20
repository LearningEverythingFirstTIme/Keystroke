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
/// Claude (Anthropic) prediction engine implementation.
/// Uses the Messages API for chat completions.
/// </summary>
public class ClaudePredictionEngine : PredictionEngineBase, IPredictionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;

    // Claude still uses slightly tighter limits than other cloud engines
    protected override int RollingContextLimit => 800;
    protected override int ScreenContextLimit  => 2400;

    public ClaudePredictionEngine(string apiKey, string model = "claude-haiku-4-5-20251001")
        : base("claude.log")
    {
        _apiKey   = apiKey;
        _model    = model;
        _endpoint = "https://api.anthropic.com/v1/messages";
        _httpClient = CreatePooledHttpClient(TimeSpan.FromSeconds(15));
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2024-10-22");
    }

    public async Task<string?> PredictAsync(ContextSnapshot context, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3)
            return null;

        if (IsRateLimited()) return null;

        try
        {
            var systemText    = BuildSystemInstruction(context);
            var dynamicTemp   = GetDynamicTemperature(context);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);
            Log($"Request size estimate: sys={systemText.Length} chars");

            var body = new
            {
                model      = _model,
                max_tokens = adaptiveTokens,
                temperature = dynamicTemp,
                system     = systemText,
                messages   = BuildMessages(context)
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            var rollingCtxLen = context.RollingContext?.Length ?? 0;
            Log($"=== Request for: \"{prefix}\" [app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, rolling={rollingCtxLen}, tokens={adaptiveTokens}] ===");

            var response = await _httpClient.PostAsync(
                _endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                Log($"Error {response.StatusCode}: {err}");
                CheckRateLimitResponse(response, err);
                ReportFailure(ClassifyHttpResponse(response, err));
                return null;
            }

            var respBody   = await response.Content.ReadAsStringAsync(ct);
            var result     = JsonSerializer.Deserialize<ClaudeResponse>(respBody);
            var completion = result?.Content is { Length: > 0 } content ? content[0]?.Text?.Trim() : null;
            Log($"Completion: {completion ?? "(null)"}");

            if (string.IsNullOrWhiteSpace(completion))
                return null;

            var processed = PostProcessCompletion(prefix, completion);
            if (!string.IsNullOrWhiteSpace(processed))
                RecordRecentCompletion(processed);
            return processed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Exception: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
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
                model      = _model,
                max_tokens = adaptiveTokens,
                temperature = dynamicTemp,
                system     = systemText,
                stream     = true,
                messages   = BuildMessages(context)
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            Log($"=== Stream for: \"{prefix}\" [app={context.ProcessName}, cat={category}, temp={dynamicTemp:F1}, tokens={adaptiveTokens}] ===");

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
                ReportFailure(ClassifyHttpResponse(response, err));
                return null;
            }

            return await ParseSseStreamAsync(response, prefix, dataJson =>
            {
                var chunk = JsonSerializer.Deserialize<ClaudeStreamEvent>(dataJson);
                return chunk?.Delta?.Text;
            }, onChunk, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Stream exception: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
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
            var systemText     = BuildSystemInstruction(context);
            var dynamicTemp    = GetDynamicTemperature(context);
            var altTemp        = Math.Min(dynamicTemp + 0.3, 1.5);
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);

            Log($"=== Alternatives for: \"{prefix}\" (count={count}, temp={altTemp:F1}, tokens={adaptiveTokens}) ===");

            var tasks = Enumerable.Range(0, count).Select(async _ =>
            {
                var body = new
                {
                    model      = _model,
                    max_tokens = adaptiveTokens,
                    temperature = altTemp,
                    system     = systemText,
                    messages   = BuildMessages(context)
                };
                var json     = JsonSerializer.Serialize(body);
                var response = await _httpClient.PostAsync(
                    _endpoint,
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    ct);
                if (!response.IsSuccessStatusCode) return null;
                var respBody = await response.Content.ReadAsStringAsync(ct);
                var result   = JsonSerializer.Deserialize<ClaudeResponse>(respBody);
                return result?.Content is { Length: > 0 } c ? c[0]?.Text?.Trim() : null;
            });

            var completions = await Task.WhenAll(tasks);

            foreach (var text in completions)
            {
                var processed = PostProcessCompletion(prefix, text);
                if (!string.IsNullOrWhiteSpace(processed) && !results.Contains(processed))
                    results.Add(processed);
            }

            Log($"Got {results.Count} alternatives");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Alternatives error: {ex}"); }

        return results;
    }

    /// <summary>
    /// Builds the messages array with few-shot conversation turns followed by the real request.
    /// </summary>
    private object[] BuildMessages(ContextSnapshot context)
    {
        var examples = LearningService?.GetExamples(context, 3) ?? [];
        var messages = new List<object>();

        foreach (var ex in examples)
        {
            var fewShotUser = $"[Application: {ex.Context}]\n\n<complete_this>\n{ex.Prefix}\n</complete_this>";
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
                model = _model, max_tokens = maxTokens, temperature = 0.4,
                system = systemPrompt,
                messages = new object[] { new { role = "user", content = userPrompt } }
            };
            var json = JsonSerializer.Serialize(body);
            var response = await _httpClient.PostAsync(_endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                ReportFailure(ClassifyHttpResponse(response, err));
                return null;
            }
            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<ClaudeResponse>(respBody);
            return result?.Content?[0]?.Text?.Trim();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"GenerateText error: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
    }

    public void Dispose() => _httpClient.Dispose();

    // ── Response DTOs ─────────────────────────────────────────────────────────

    private class ClaudeResponse
    {
        [JsonPropertyName("content")]
        public ClaudeContent[]? Content { get; set; }
    }

    private class ClaudeContent
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class ClaudeStreamEvent
    {
        [JsonPropertyName("type")]  public string?     Type  { get; set; }
        [JsonPropertyName("delta")] public ClaudeDelta? Delta { get; set; }
    }

    private class ClaudeDelta
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
