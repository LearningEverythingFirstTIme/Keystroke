using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

/// <summary>
/// Prediction engine that uses a local Ollama instance for completions.
/// - Instruct models (llama3.2, qwen2.5:1.5b, etc.): /api/chat with a minimal system prompt.
/// - Base models (anything with "base" in the tag): /api/generate for raw continuation.
/// </summary>
public class OllamaPredictionEngine : PredictionEngineBase, IPredictionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly bool _isBaseModel;

    // Large local models (30B+) can take 90-120s on first load; smaller models ~5-20s
    public int TimeoutMs => 120000;

    public OllamaPredictionEngine(string model = "llama3.2:latest", string endpoint = "http://localhost:11434")
        : base("ollama.log")
    {
        _model       = model;
        _endpoint    = endpoint.TrimEnd('/');
        _isBaseModel = model.Contains("base", StringComparison.OrdinalIgnoreCase);
        // Use InfiniteTimeSpan so the prediction CancellationToken (TimeoutMs) governs
        // timeouts — not a hard HttpClient limit. Large models (30B+) need 60-120s cold start.
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_endpoint}/api/tags", TimeSpan.FromSeconds(3));
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync();
            var doc  = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models)) return false;
            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), _model, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    public async Task<string?> GenerateTextAsync(string systemPrompt, string userPrompt, int maxTokens = 200, CancellationToken ct = default)
    {
        try
        {
            if (_isBaseModel)
            {
                var rawPrompt = $"{systemPrompt}\n\n{userPrompt}";
                var rawBody = new
                {
                    model = _model, prompt = rawPrompt, stream = false, keep_alive = "10m",
                    options = new { temperature = 0.4, num_predict = maxTokens }
                };
                var rawJson = JsonSerializer.Serialize(rawBody);
                var rawResp = await _httpClient.PostAsync($"{_endpoint}/api/generate",
                    new StringContent(rawJson, Encoding.UTF8, "application/json"), ct);
                if (!rawResp.IsSuccessStatusCode) return null;
                var rawRespBody = await rawResp.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<OllamaGenerateResponse>(rawRespBody)?.Response?.Trim();
            }

            var body = new
            {
                model = _model, stream = false, keep_alive = "10m",
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                options = new { temperature = 0.4, num_predict = maxTokens }
            };
            var json = JsonSerializer.Serialize(body);
            var response = await _httpClient.PostAsync($"{_endpoint}/api/chat",
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) return null;
            var respBody = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<OllamaChatResponse>(respBody)?.Message?.Content?.Trim();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log($"GenerateText error: {ex.Message}"); return null; }
    }

    // ── Instruct path: /api/chat (non-Qwen3) and /api/generate raw (Qwen3) ──

    private bool IsQwen3 => _model.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ollama-specific system instruction — much shorter than the cloud variant.
    /// Explicitly forbids responding to the text, which prevents instruct models from
    /// treating the prompt as a conversation and outputting replies instead of continuations.
    /// </summary>
    private string BuildOllamaSystemInstruction(ContextSnapshot context)
    {
        var sb = new StringBuilder();
        sb.Append("You complete partial text. Output ONLY the continuation words. Do not respond to, discuss, or comment on the text.");

        if (context.HasAppContext)
        {
            var category = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
            var toneHint = AppCategory.GetToneHint(category);
            sb.Append($" {toneHint}");

            if (StyleProfileService != null)
            {
                var styleHint = StyleProfileService.GetStyleHint(category.ToString());
                if (!string.IsNullOrEmpty(styleHint))
                    sb.Append($" User's style: {styleHint}");
            }
        }

        return sb.ToString();
    }

    private string BuildUserMessage(ContextSnapshot context)
    {
        // Rolling context omitted here — when it contains conversation/chat text it causes the
        // model to respond as a dialogue participant instead of completing the current sentence.
        // The "Complete:" label anchors the model to exactly what needs continuing.
        return $"Complete: {context.TypedText}";
    }

    private object[] BuildMessages(ContextSnapshot context)
    {
        var examples = LearningService?.GetExamples(context, 3) ?? [];
        var messages = new List<object>();

        messages.Add(new { role = "system", content = BuildOllamaSystemInstruction(context) });

        foreach (var ex in examples)
        {
            var fewShotUser = $"Text to complete:\n{ex.Prefix}";
            messages.Add(new { role = "user",      content = fewShotUser              });
            messages.Add(new { role = "assistant", content = ex.Completion.TrimStart() });
        }

        messages.Add(new { role = "user", content = BuildUserMessage(context) });

        if (examples.Count > 0)
            Log($"Included {examples.Count} few-shot examples for {context.ProcessName}");

        return messages.ToArray();
    }

    /// <summary>
    /// Build a raw ChatML prompt for Qwen3 via /api/generate with raw:true.
    /// Ends on "&lt;|im_start|&gt;assistant\n" so the model MUST continue directly as assistant.
    /// This bypasses Ollama's chat template and eliminates narration.
    /// </summary>
    private string BuildRawChatMLPrompt(ContextSnapshot context)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<|im_start|>system");
        sb.Append(BuildOllamaSystemInstruction(context));
        sb.AppendLine("<|im_end|>");

        var examples = LearningService?.GetExamples(context, 3) ?? [];
        foreach (var ex in examples)
        {
            sb.AppendLine("<|im_start|>user");
            sb.AppendLine($"Complete: {ex.Prefix}");
            sb.AppendLine("<|im_end|>");
            sb.AppendLine("<|im_start|>assistant");
            sb.AppendLine("<think>\n\n</think>");
            sb.AppendLine(ex.Completion.TrimStart());
            sb.AppendLine("<|im_end|>");
        }

        if (examples.Count > 0)
            Log($"Included {examples.Count} few-shot examples for {context.ProcessName}");

        sb.AppendLine("<|im_start|>user");
        sb.Append(BuildUserMessage(context));
        sb.AppendLine();
        sb.AppendLine("<|im_end|>");

        // Assistant turn with empty think block — tells Qwen3 thinking is done, generate directly.
        sb.Append("<|im_start|>assistant\n<think>\n\n</think>\n");

        return sb.ToString();
    }

    private async Task<string?> PredictChatAsync(ContextSnapshot context, CancellationToken ct)
    {
        // Qwen3 models narrate via /api/chat — use raw ChatML via /api/generate instead
        if (IsQwen3) return await PredictRawChatMLAsync(context, ct);

        var prefix     = context.TypedText;
        var dynamicTemp = GetDynamicTemperature(context);
        var body = new
        {
            model    = _model,
            messages = BuildMessages(context),
            stream   = false,
            keep_alive = "10m",
            options  = new { temperature = dynamicTemp, num_predict = GetAdaptiveMaxTokens(prefix), top_p = 0.9 }
        };
        var json     = JsonSerializer.Serialize(body);
        var category = context.HasAppContext
            ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
            : AppCategory.Category.Unknown;
        Log($"=== Chat: \"{prefix}\" [model={_model}, cat={category}, temp={dynamicTemp:F2}] ===");

        var response = await _httpClient.PostAsync($"{_endpoint}/api/chat",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode)
        {
            Log($"Chat error {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            return null;
        }
        var respBody   = await response.Content.ReadAsStringAsync(ct);
        var completion = JsonSerializer.Deserialize<OllamaChatResponse>(respBody)
            ?.Message?.Content?.Trim();
        Log($"Raw: {completion ?? "(null)"}");
        return ProcessCompletion(prefix, completion);
    }

    /// <summary>
    /// Qwen3 via /api/generate with raw:true — manual ChatML template forces direct continuation.
    /// </summary>
    private async Task<string?> PredictRawChatMLAsync(ContextSnapshot context, CancellationToken ct)
    {
        var prefix     = context.TypedText;
        var dynamicTemp = GetDynamicTemperature(context);
        var prompt     = BuildRawChatMLPrompt(context);
        var body = new
        {
            model      = _model,
            prompt,
            raw        = true,
            stream     = false,
            keep_alive = "10m",
            options    = new { temperature = dynamicTemp, num_predict = GetAdaptiveMaxTokens(prefix), top_p = 0.9, stop = new[] { "<|im_end|>", "<|im_start|>" } }
        };
        var json     = JsonSerializer.Serialize(body);
        var category = context.HasAppContext
            ? AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle)
            : AppCategory.Category.Unknown;
        Log($"=== RawChatML: \"{prefix}\" [model={_model}, cat={category}, temp={dynamicTemp:F2}] ===");

        var response = await _httpClient.PostAsync($"{_endpoint}/api/generate",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode)
        {
            Log($"RawChatML error {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            return null;
        }
        var respBody   = await response.Content.ReadAsStringAsync(ct);
        var completion = JsonSerializer.Deserialize<OllamaGenerateResponse>(respBody)?.Response?.Trim();
        Log($"RawChatML raw: {completion ?? "(null)"}");
        return ProcessCompletion(prefix, completion);
    }

    private async Task<string?> PredictChatWithTempAsync(ContextSnapshot context, double temp, CancellationToken ct)
    {
        var prefix = context.TypedText;

        if (IsQwen3)
        {
            var prompt  = BuildRawChatMLPrompt(context);
            var rawBody = new
            {
                model = _model, prompt, raw = true, stream = false, keep_alive = "10m",
                options = new { temperature = temp, num_predict = MaxOutputTokens, top_p = 0.95, stop = new[] { "<|im_end|>", "<|im_start|>" } }
            };
            var rawJson = JsonSerializer.Serialize(rawBody);
            var rawResp = await _httpClient.PostAsync($"{_endpoint}/api/generate",
                new StringContent(rawJson, Encoding.UTF8, "application/json"), ct);
            if (!rawResp.IsSuccessStatusCode) return null;
            var rawRespBody   = await rawResp.Content.ReadAsStringAsync(ct);
            var rawCompletion = JsonSerializer.Deserialize<OllamaGenerateResponse>(rawRespBody)?.Response?.Trim();
            return ProcessCompletion(prefix, rawCompletion);
        }

        var body = new
        {
            model    = _model,
            messages = BuildMessages(context),
            stream   = false,
            keep_alive = "10m",
            options  = new { temperature = temp, num_predict = MaxOutputTokens, top_p = 0.95 }
        };
        var json     = JsonSerializer.Serialize(body);
        var response = await _httpClient.PostAsync($"{_endpoint}/api/chat",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode) return null;
        var respBody   = await response.Content.ReadAsStringAsync(ct);
        var completion = JsonSerializer.Deserialize<OllamaChatResponse>(respBody)
            ?.Message?.Content?.Trim();
        return ProcessCompletion(prefix, completion);
    }

    // ── Base model path: /api/generate ───────────────────────────────────────

    private string BuildRawPrompt(ContextSnapshot context)
    {
        var sb = new StringBuilder();
        if (context.HasRollingContext)
        {
            var rolling = context.RollingContext!;
            if (rolling.Length > 150) rolling = rolling[^150..];
            sb.Append(rolling);
            if (!rolling.EndsWith(' ') && !rolling.EndsWith('\n'))
                sb.Append(' ');
        }
        sb.Append(context.TypedText);
        return sb.ToString();
    }

    private async Task<string?> PredictGenerateAsync(ContextSnapshot context, CancellationToken ct)
    {
        var prefix = context.TypedText;
        var prompt = BuildRawPrompt(context);
        var body   = new
        {
            model   = _model,
            prompt,
            stream  = false,
            options = new { temperature = Temperature, num_predict = MaxOutputTokens, top_p = 0.9, top_k = 10 },
            stop    = new[] { "\n" }
        };
        var json = JsonSerializer.Serialize(body);
        Log($"=== Generate: \"{prefix}\" [model={_model}] ===");

        var response = await _httpClient.PostAsync($"{_endpoint}/api/generate",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode)
        {
            Log($"Error {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            return null;
        }
        var respBody   = await response.Content.ReadAsStringAsync(ct);
        var completion = JsonSerializer.Deserialize<OllamaGenerateResponse>(respBody)?.Response?.Trim();
        Log($"Raw: {completion ?? "(null)"}");
        return ProcessCompletion(prefix, completion);
    }

    private async Task<string?> PredictGenerateWithTempAsync(ContextSnapshot context, double temp, CancellationToken ct)
    {
        var prefix = context.TypedText;
        var prompt = BuildRawPrompt(context);
        var body   = new
        {
            model   = _model,
            prompt,
            stream  = false,
            options = new { temperature = temp, num_predict = MaxOutputTokens, top_p = 0.95 },
            stop    = new[] { "\n" }
        };
        var json     = JsonSerializer.Serialize(body);
        var response = await _httpClient.PostAsync($"{_endpoint}/api/generate",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        if (!response.IsSuccessStatusCode) return null;
        var respBody = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OllamaGenerateResponse>(respBody)?.Response?.Trim() is string c
            ? ProcessCompletion(prefix, c) : null;
    }

    // ── Public IPredictionEngine interface ───────────────────────────────────

    public Task<string?> PredictAsync(ContextSnapshot context, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return Task.FromResult<string?>(null);
        if (_isBaseModel) return PredictGenerateAsync(context, ct);
        if (IsQwen3)      return PredictRawChatMLAsync(context, ct);
        return PredictChatAsync(context, ct);
    }

    public async Task<string?> PredictStreamingAsync(ContextSnapshot context, Action<string> onChunk, CancellationToken ct = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return null;

        if (_isBaseModel)
            return await PredictStreamingGenerateAsync(context, onChunk, ct);

        if (IsQwen3)
            return await PredictStreamingRawChatMLAsync(context, onChunk, ct);

        // Other instruct models: /api/chat streaming
        var dynamicTemp = GetDynamicTemperature(context);
        var body = new
        {
            model    = _model,
            messages = BuildMessages(context),
            stream   = true,
            keep_alive = "10m",
            options  = new { temperature = dynamicTemp, num_predict = GetAdaptiveMaxTokens(prefix), top_p = 0.9 }
        };
        var json = JsonSerializer.Serialize(body);
        Log($"=== Chat stream: \"{prefix}\" [model={_model}] ===");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { Log($"Chat stream error {response.StatusCode}"); return null; }

        var rawCompletion = new StringBuilder();
        var degenDetector = CreateDegenerationDetector();
        using var stream  = await response.Content.ReadAsStreamAsync(ct);
        using var reader  = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
                var text  = chunk?.Message?.Content;
                if (!string.IsNullOrEmpty(text))
                {
                    if (degenDetector.IsDegenerate(text))
                    {
                        Log($"Chat stream aborted: degeneration detected after {rawCompletion.Length} chars");
                        break;
                    }
                    rawCompletion.Append(text);
                    onChunk(text);
                }
            }
            catch (JsonException) { }
        }

        var raw    = rawCompletion.ToString();
        Log($"Chat raw ({raw.Length} chars): \"{raw[..Math.Min(raw.Length, 120)]}\"");
        var result = ProcessCompletion(prefix, raw);
        Log($"Chat stream done: \"{result ?? "(rejected)"}\"");
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    /// Qwen3 streaming via /api/generate with raw ChatML. Buffers full response then delivers
    /// after stripping think tags and post-processing, since partial tokens may contain garbage.
    /// </summary>
    private async Task<string?> PredictStreamingRawChatMLAsync(ContextSnapshot context, Action<string> onChunk, CancellationToken ct)
    {
        var prefix     = context.TypedText;
        var dynamicTemp = GetDynamicTemperature(context);
        var prompt     = BuildRawChatMLPrompt(context);
        var body = new
        {
            model      = _model,
            prompt,
            raw        = true,
            stream     = true,
            keep_alive = "10m",
            options    = new { temperature = dynamicTemp, num_predict = GetAdaptiveMaxTokens(prefix), top_p = 0.9, stop = new[] { "<|im_end|>", "<|im_start|>" } }
        };
        var json = JsonSerializer.Serialize(body);
        Log($"=== RawChatML stream: \"{prefix}\" [model={_model}] ===");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/generate")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { Log($"RawChatML stream error {response.StatusCode}"); return null; }

        var rawCompletion = new StringBuilder();
        using var stream  = await response.Content.ReadAsStreamAsync(ct);
        using var reader  = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var chunk = JsonSerializer.Deserialize<OllamaGenerateChunk>(line);
                var text  = chunk?.Response;
                if (!string.IsNullOrEmpty(text))
                    rawCompletion.Append(text);
            }
            catch (JsonException) { }
        }

        var raw    = rawCompletion.ToString();
        Log($"RawChatML raw ({raw.Length} chars): \"{raw[..Math.Min(raw.Length, 120)]}\"");
        var result = ProcessCompletion(prefix, raw) ?? "";
        Log($"RawChatML stream done: \"{result}\"");
        if (!string.IsNullOrWhiteSpace(result))
            onChunk(result);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private async Task<string?> PredictStreamingGenerateAsync(ContextSnapshot context, Action<string> onChunk, CancellationToken ct)
    {
        var prefix = context.TypedText;
        var prompt = BuildRawPrompt(context);
        var body   = new
        {
            model   = _model,
            prompt,
            stream  = true,
            options = new { temperature = Temperature, num_predict = MaxOutputTokens, top_p = 0.9, top_k = 10 },
            stop    = new[] { "\n" }
        };
        var json = JsonSerializer.Serialize(body);
        Log($"=== Generate stream: \"{prefix}\" [model={_model}] ===");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/generate")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { Log($"Stream error {response.StatusCode}"); return null; }

        var fullCompletion = new StringBuilder();
        bool isFirstChunk  = true;
        var degenDetector  = CreateDegenerationDetector();
        using var stream   = await response.Content.ReadAsStreamAsync(ct);
        using var reader   = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var chunk = JsonSerializer.Deserialize<OllamaGenerateChunk>(line);
                var text  = chunk?.Response;
                if (string.IsNullOrEmpty(text)) continue;

                if (isFirstChunk)
                {
                    text = text.TrimStart('"', '*', '`', '#', ' ', '\n');
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        text = text[prefix.Length..];
                    if (text.Length > 0 && !prefix.EndsWith(" ") && !text.StartsWith(" "))
                        text = " " + text;
                    isFirstChunk = false;
                }

                if (degenDetector.IsDegenerate(text))
                {
                    Log($"Generate stream aborted: degeneration detected after {fullCompletion.Length} chars");
                    break;
                }

                fullCompletion.Append(text);
                onChunk(text);
            }
            catch (JsonException) { }
        }

        var result = ProcessCompletion(prefix, fullCompletion.ToString()) ?? "";
        Log($"Generate stream done: \"{result}\"");
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public async Task<List<string>> FetchAlternativesAsync(ContextSnapshot context, int count = 3, CancellationToken ct = default)
    {
        var results = new List<string>();
        var prefix  = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return results;

        var tasks = new List<Task<string?>>();
        for (int i = 0; i < count; i++)
        {
            var altTemp = Math.Min(Temperature + (i * 0.2), 1.0);
            tasks.Add(_isBaseModel
                ? PredictGenerateWithTempAsync(context, altTemp, ct)
                : PredictChatWithTempAsync(context, altTemp, ct));
        }
        try
        {
            var completions = await Task.WhenAll(tasks);
            foreach (var c in completions)
                if (!string.IsNullOrWhiteSpace(c) && !results.Contains(c)) results.Add(c);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Alt error: {ex.Message}"); }
        return results;
    }

    // ── Shared post-processing ───────────────────────────────────────────────

    // Phrases that indicate the model narrated/analyzed instead of completing
    private static readonly string[] NarratorPhrases =
    [
        "okay, let's", "okay, let me", "okay, i", "okay, the user",
        "let me ", "the user is", "the user's", "the user want",
        "i need to", "i'll ", "i will ", "looking at", "based on",
        "it seems", "it looks", "this appears", "the text ", "the context",
        "the sentence", "the completion", "they're trying", "they want",
        "to complete", "continuing the", "here's the", "i can see",
        "they mention", "they said", "this is a"
    ];

    private string? ProcessCompletion(string prefix, string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion)) return null;

        // Strip Qwen3 chain-of-thought blocks before anything else
        completion = StripThinkTags(completion);

        // Reject narrator-style responses where the model described the task instead of doing it
        var lower = completion.TrimStart().ToLowerInvariant();
        foreach (var phrase in NarratorPhrases)
            if (lower.StartsWith(phrase)) { Log($"Rejected narrator: {completion[..Math.Min(40, completion.Length)]}"); return null; }

        completion = completion.Trim('"', '*', '`', '#', '-', '=', '~').Trim();

        foreach (var label in new[] { "Completion:", "Output:", "Input:", "Assistant:" })
            if (completion.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                completion = completion[label.Length..].TrimStart();

        if (completion.StartsWith("[")) return null;
        if (string.IsNullOrWhiteSpace(completion)) return null;
        if (completion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            completion = completion[prefix.Length..].TrimStart();
        if (completion.Length > 0 && !prefix.EndsWith(" ") && !completion.StartsWith(" "))
            completion = " " + completion;

        completion = TrimToWholeWords(completion);
        completion = RejectDuplicate(prefix, completion!);
        if (!string.IsNullOrWhiteSpace(completion))
            RecordRecentCompletion(completion);
        return string.IsNullOrWhiteSpace(completion) ? null : completion;
    }

    // ── Response DTOs ────────────────────────────────────────────────────────

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
    }

    private class OllamaGenerateChunk
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
        [JsonPropertyName("done")]     public bool Done { get; set; }
    }

    private class OllamaChatResponse
    {
        [JsonPropertyName("message")] public OllamaChatMessage? Message { get; set; }
    }

    private class OllamaChatChunk
    {
        [JsonPropertyName("message")] public OllamaChatMessage? Message { get; set; }
        [JsonPropertyName("done")]    public bool Done { get; set; }
    }

    private class OllamaChatMessage
    {
        [JsonPropertyName("role")]    public string? Role    { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}

internal static class HttpClientExtensions
{
    public static async Task<HttpResponseMessage> GetAsync(this HttpClient client, string url, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await client.GetAsync(url, cts.Token);
    }
}
