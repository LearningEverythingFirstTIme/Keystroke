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
/// Prediction engine backed by OpenRouter (https://openrouter.ai).
/// OpenRouter exposes an OpenAI-compatible /v1/chat/completions endpoint
/// that proxies hundreds of models — any model ID from openrouter.ai/models works.
///
/// Required headers beyond standard Authorization:
///   HTTP-Referer: identifies the calling app (shown in OpenRouter dashboard)
///   X-Title:      display name shown alongside your usage
/// </summary>
public class OpenRouterPredictionEngine : PredictionEngineBase, IPredictionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    public OpenRouterPredictionEngine(string apiKey, string model = "google/gemini-flash-2.0")
        : base("openrouter.log")
    {
        _model = model;

        // Reasoning-first models (MiniMax, Kimi) are given 45s — they must complete a full
        // reasoning chain before producing output, which takes several seconds longer.
        _httpClient = CreatePooledHttpClient(TimeSpan.FromSeconds(45));
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        // OpenRouter asks apps to identify themselves so they can show usage stats per app
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/nickklos/keystroke");
        _httpClient.DefaultRequestHeaders.Add("X-Title", "Keystroke");
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<string?> GenerateTextAsync(string systemPrompt, string userPrompt, int maxTokens = 200, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                model = _model, max_completion_tokens = maxTokens, temperature = 0.4,
                reasoning = new { effort = "none" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };
            var json = JsonSerializer.Serialize(body);
            var response = await _httpClient.PostAsync(Endpoint,
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                ReportFailure(ClassifyHttpResponse(response, err));
                return null;
            }
            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<ChatResponse>(respBody);
            return result?.Choices?[0]?.Message?.Content?.Trim();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"GenerateText error: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
    }

    // ── Token sizing (override: reasoning models need a larger budget) ─────────

    protected override int GetAdaptiveMaxTokens(string prefix)
    {
        // Reasoning-first models need a large budget: the reasoning phase consumes
        // tokens before the actual completion arrives in delta.content. Without this,
        // the token limit is exhausted mid-think and content never arrives.
        if (IsReasoningFirstModel()) return 400;

        var words = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < 4) return Math.Min(25, MaxOutputTokens);
        if (words < 8) return Math.Min(60, MaxOutputTokens);
        return MaxOutputTokens;
    }

    // ── IPredictionEngine — non-streaming ─────────────────────────────────────

    public async Task<string?> PredictAsync(ContextSnapshot context, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return null;
        if (IsRateLimited()) return null;

        try
        {
            var adaptiveTokens = GetAdaptiveMaxTokens(prefix);
            var dynamicTemp    = GetDynamicTemperature(context);

            // Reasoning-first models: use minimal effort so they think briefly then emit
            // the actual completion in delta.content. Non-reasoning models: disable thinking
            // entirely so the token budget goes fully to content.
            var reasoningParam = IsReasoningFirstModel()
                ? (object)new { effort = "minimal" }
                : new { effort = "none" };

            var body = new
            {
                model                 = _model,
                max_completion_tokens = adaptiveTokens,
                temperature           = dynamicTemp,
                top_p                 = 0.9,
                reasoning             = reasoningParam,
                messages              = BuildMessages(context)
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            Log($"=== Request: \"{prefix}\" [model={_model}, cat={category}, temp={dynamicTemp:F1}, tokens={adaptiveTokens}, reasoning={( IsReasoningFirstModel() ? "minimal/capture" : "none" )}] ===");

            var response = await _httpClient.PostAsync(
                Endpoint,
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

            var respBody = await response.Content.ReadAsStringAsync(ct);
            var result   = JsonSerializer.Deserialize<ChatResponse>(respBody);
            var msg      = result?.Choices is { Length: > 0 } ch ? ch[0]?.Message : null;

            if (IsReasoningFirstModel())
                Log($"[non-stream] content={msg?.Content?.Length ?? 0}chars  reasoning={msg?.Reasoning?.Length ?? 0}chars");

            var completion = !string.IsNullOrWhiteSpace(msg?.Content)
                ? msg!.Content!.Trim()
                : (IsReasoningFirstModel() ? msg?.Reasoning?.Trim() : null);

            Log($"Completion: {completion ?? "(null)"}");
            var processed = PostProcessCompletion(prefix, completion);
            if (!string.IsNullOrWhiteSpace(processed))
                RecordRecentCompletion(processed);
            return processed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Exception: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
    }

    // ── Reasoning-first model path (non-streaming) ───────────────────────────
    // MiniMax M-series, Kimi K2.x: their streaming endpoint never produces delta.content.
    // Strategy: link the per-keypress CT with a 45 s timeout cap so fresh predictions
    // cancel stale in-flight reasoning requests (which otherwise burn API quota). Keep
    // the request-ID counter as belt-and-braces in case a response lands right as the
    // next keystroke arrives — only the most recent request ever delivers its result.

    private int _reasoningRequestId;

    private async Task<string?> PredictReasoningModelAsync(
        ContextSnapshot context, Action<string> onChunk, CancellationToken ct = default)
    {
        var prefix    = context.TypedText;
        var requestId = Interlocked.Increment(ref _reasoningRequestId);

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
                reasoning             = new { effort = "minimal" },
                messages              = BuildMessages(context)
            };

            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            Log($"=== Reasoning-model request #{requestId}: \"{prefix}\" [model={_model}, cat={category}, tokens={adaptiveTokens}] ===");

            // Link caller's CT with a 45 s timeout cap. If a fresh prediction kicks off
            // (new keystroke → new CTS), this stale request is cancelled rather than
            // left running to burn quota. The requestId post-check below handles the
            // narrow race where a response lands between arrival and cancellation.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

            var response = await _httpClient.PostAsync(
                Endpoint,
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                Log($"Reasoning-model #{requestId} error {response.StatusCode}: {err}");
                CheckRateLimitResponse(response, err);
                ReportFailure(ClassifyHttpResponse(response, err));
                return null;
            }

            var respBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var result   = JsonSerializer.Deserialize<ChatResponse>(respBody);
            var msg      = result?.Choices is { Length: > 0 } ch ? ch[0]?.Message : null;

            Log($"[reasoning-model #{requestId}] content={msg?.Content?.Length ?? 0}chars  reasoning={msg?.Reasoning?.Length ?? 0}chars");

            // Discard if a newer request has already been issued
            if (requestId != Volatile.Read(ref _reasoningRequestId))
            {
                Log($"Reasoning-model #{requestId} discarded (superseded by #{_reasoningRequestId})");
                return null;
            }

            var raw        = !string.IsNullOrWhiteSpace(msg?.Content) ? msg!.Content!.Trim() : msg?.Reasoning?.Trim();
            var completion = PostProcessCompletion(prefix, raw);
            if (!string.IsNullOrWhiteSpace(completion))
            {
                onChunk(completion!);
                Log($"Reasoning-model #{requestId} complete: {completion!.Length} chars");
            }
            else
            {
                Log($"Reasoning-model #{requestId} complete: 0 chars (rejected)");
            }
            return completion;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Reasoning-model #{requestId} exception: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
    }

    // ── IPredictionEngine — streaming ─────────────────────────────────────────

    public async Task<string?> PredictStreamingAsync(
        ContextSnapshot context, Action<string> onChunk, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return null;
        if (IsRateLimited()) return null;

        // Reasoning-first models (MiniMax, Kimi): their streaming endpoint never emits
        // delta.content — all tokens go to delta.reasoning and the actual completion is
        // never delivered. Use the non-streaming endpoint instead: wait for the full
        // response (reasoning + answer), then deliver the answer via onChunk in one shot.
        if (IsReasoningFirstModel())
            return await PredictReasoningModelAsync(context, onChunk, ct);

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
                reasoning             = (object)new { effort = "none" },
                stream                = true,
                messages              = BuildMessages(context)
            };

            var json     = JsonSerializer.Serialize(body);
            var category = context.HasAppContext
                ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
                : AppCategory.Category.Unknown;
            Log($"=== Stream: \"{prefix}\" [model={_model}, cat={category}, temp={dynamicTemp:F1}, tokens={adaptiveTokens}] ===");

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
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

            int rawChunkCount   = 0;
            int reasoningChunks = 0;

            var result = await ParseSseStreamAsync(response, prefix, dataJson =>
            {
                if (rawChunkCount < 3)
                {
                    Log($"[raw#{rawChunkCount}] {dataJson}");
                    rawChunkCount++;
                }
                var chunk = JsonSerializer.Deserialize<StreamChunk>(dataJson);
                var delta = chunk?.Choices?[0]?.Delta;
                if (!string.IsNullOrEmpty(delta?.Reasoning) && string.IsNullOrEmpty(delta?.Content))
                    reasoningChunks++;
                return delta?.Content ?? delta?.ReasoningContent;
            }, onChunk, ct);

            if (reasoningChunks > 0)
                Log($"[reasoning-capture] Captured {reasoningChunks} reasoning chunks as completion text");

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Stream exception: {ex}"); ReportFailure(ClassifyException(ex)); return null; }
    }

    // ── IPredictionEngine — alternatives ──────────────────────────────────────

    public async Task<List<string>> FetchAlternativesAsync(
        ContextSnapshot context, int count = 3, CancellationToken ct = default)
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

            Log($"=== Alternatives: \"{prefix}\" (count={count}, temp={altTemp:F1}) ===");

            var tasks = Enumerable.Range(0, count).Select(async _ =>
            {
                var body = new
                {
                    model                 = _model,
                    max_completion_tokens = adaptiveTokens,
                    temperature           = altTemp,
                    top_p                 = 0.95,
                    reasoning             = new { effort = "low" },
                    messages              = BuildMessages(context)
                };
                var json     = JsonSerializer.Serialize(body);
                var response = await _httpClient.PostAsync(
                    Endpoint,
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    ct);
                if (!response.IsSuccessStatusCode) return null;
                var body2  = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<ChatResponse>(body2);
                return result?.Choices is { Length: > 0 } ch ? ch[0]?.Message?.Content?.Trim() : null;
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

    // ── Prompt construction ───────────────────────────────────────────────────

    private object[] BuildMessages(ContextSnapshot context)
    {
        var systemText = BuildSystemInstruction(context);
        var examples   = LearningService?.GetExamples(context, 3) ?? [];
        var messages   = new List<object>();

        messages.Add(new { role = "system", content = systemText });

        foreach (var ex in examples)
        {
            var fewShotUser = $"[Application: {ex.Context}]\n\n<complete_this>\n{ex.Prefix}\n</complete_this>";
            messages.Add(new { role = "user",      content = fewShotUser  });
            messages.Add(new { role = "assistant", content = ex.Completion });
        }

        messages.Add(new { role = "user", content = BuildUserPrompt(context) });

        if (examples.Count > 0)
            Log($"Included {examples.Count} few-shot turns for {context.ProcessName}");

        return messages.ToArray();
    }

    // Delegate to the centralized list in OpenRouterModelService so Settings UI and
    // engine logic always agree on which models are reasoning-first.
    private bool IsReasoningFirstModel() =>
        OpenRouterModelService.IsReasoningFirstModel(_model);

    // ── JSON response DTOs ────────────────────────────────────────────────────

    private class ChatResponse
    {
        [JsonPropertyName("choices")] public ChatChoice[]? Choices { get; set; }
    }
    private class ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
    private class ChatMessage
    {
        [JsonPropertyName("content")]   public string? Content   { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    }
    private class StreamChunk
    {
        [JsonPropertyName("choices")] public StreamChoice[]? Choices { get; set; }
    }
    private class StreamChoice
    {
        [JsonPropertyName("delta")] public StreamDelta? Delta { get; set; }
    }
    private class StreamDelta
    {
        [JsonPropertyName("content")]           public string? Content          { get; set; }
        // OpenRouter legacy field — some providers route chain-of-thought here
        [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
        // Newer OpenRouter unified field — MiniMax M-series, Kimi K2.x, etc. stream
        // their chain-of-thought here.
        [JsonPropertyName("reasoning")]         public string? Reasoning        { get; set; }
    }
}
