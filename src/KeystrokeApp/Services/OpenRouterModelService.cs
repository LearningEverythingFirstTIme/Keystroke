using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

/// <summary>
/// Immutable info record for a single OpenRouter model.
/// </summary>
public record OpenRouterModelInfo(
    string Id,              // "anthropic/claude-haiku-4-5"
    string DisplayName,     // "Claude Haiku 4.5"
    string Provider,        // "Anthropic"  (formatted)
    decimal InputPricePer1M,// cost per 1M input tokens in USD
    bool IsRecommended      // true = known-good for autocomplete
);

/// <summary>
/// Fetches and caches the OpenRouter model list.
/// The /api/v1/models endpoint is public — no API key required.
/// Cache TTL is 1 hour; call InvalidateCache() to force a refresh.
/// Thread-safe via SemaphoreSlim.
/// </summary>
public static class OpenRouterModelService
{
    // Models known to work well for autocomplete: fast, cheap, strong instruction-following.
    // Also includes hybrid reasoning models that emit a clean final answer in delta.content
    // after a brief thinking phase (these benefit from the reasoning:{effort:"low"} param).
    // Shown with ⭐ in the settings dropdown.
    public static readonly HashSet<string> RecommendedModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── OpenAI ────────────────────────────────────────────────────────────
        // GPT-5.4 series (March 2026) — nano is the fastest/cheapest option
        "openai/gpt-5.4-nano",
        "openai/gpt-5.4-mini",
        // GPT-5 series (Aug 2025)
        "openai/gpt-5-nano",
        "openai/gpt-5-mini-2025-08-07",
        // GPT-4.1 series — still good value
        "openai/gpt-4.1-mini",
        "openai/gpt-4.1-nano",
        // Free open-source OpenAI model
        "openai/gpt-oss-20b:free",

        // ── Anthropic ─────────────────────────────────────────────────────────
        "anthropic/claude-4.5-haiku-20251001",
        "anthropic/claude-haiku-4-5",

        // ── Google ────────────────────────────────────────────────────────────
        // Gemini 3.1 Flash Lite — best-tested model for this app, 2.5x faster TTFT
        "google/gemini-3.1-flash-lite-preview-20260303",
        "google/gemini-3-flash-preview-20251217",
        // Gemini 2.5 series — hybrid reasoning, final answer arrives in delta.content
        "google/gemini-2.5-flash",
        "google/gemini-2.5-flash-lite",
        "google/gemini-2.0-flash-lite",
        "google/gemini-2.0-flash-lite:free",
        // Free open Gemma models
        "google/gemma-3-4b-it:free",
        "google/gemma-3-12b-it:free",

        // ── Meta ──────────────────────────────────────────────────────────────
        // Llama 4 — MoE architecture means low active-parameter count = fast
        "meta-llama/llama-4-scout-17b-16e-instruct",
        "meta-llama/llama-4-maverick-17b-128e-instruct",
        "meta-llama/llama-3.3-70b-instruct",
        "meta-llama/llama-3.3-70b-instruct:free",

        // ── DeepSeek ──────────────────────────────────────────────────────────
        // V3.2 is the best non-reasoning value (~$0.26/$0.38 per 1M tokens)
        "deepseek/deepseek-v3.2-20251201",
        "deepseek/deepseek-chat-v3-0324",
        "deepseek/deepseek-chat",
        // R1 is hybrid: emits <think>…</think> then final answer in content
        "deepseek/deepseek-r1",
        "deepseek/deepseek-r1:free",

        // ── Mistral ───────────────────────────────────────────────────────────
        "mistralai/mistral-small-2603",           // Mistral Small 4 (March 2026)
        "mistralai/mistral-small-3.2-24b-instruct",
        "mistralai/devstral-small:free",

        // ── Qwen ──────────────────────────────────────────────────────────────
        // Qwen3.5 Flash — latest flash-tier, use effort:none to skip thinking
        "qwen/qwen3.5-flash-20260224",
        "qwen/qwen3-8b-04-28",
        "qwen/qwen3-8b",
        "qwen/qwen3-8b:free",
        "qwen/qwen3-14b",
        "qwen/qwen3-30b-a3b",

        // ── ByteDance Seed ────────────────────────────────────────────────────
        // Seed 1.6 Flash — ~45 tok/s, very cheap ($0.075/$0.30)
        "bytedance/seed-1.6-flash",

        // ── StepFun ───────────────────────────────────────────────────────────
        "stepfun/step-3.5-flash",
        "stepfun/step-3.5-flash:free",

        // ── Liquid ────────────────────────────────────────────────────────────
        // LFM 2.5 — 1.2B params, absolute minimum latency, free
        "liquid/lfm-2.5-1.2b-instruct:free",
    };

    // Models that route ALL tokens to delta.reasoning with delta.content permanently empty.
    // The settings UI shows a warning when one of these is selected.
    // Also used by OpenRouterPredictionEngine to choose the non-streaming reasoning path.
    public static readonly string[] ReasoningFirstPrefixes =
    [
        "minimax/minimax-m",   // MiniMax M-series — content always empty on OpenRouter
        "moonshot/kimi-k2",    // Kimi K2 — same issue
        "qwen/qwq",            // QwQ series — unverified with effort:none
    ];

    // Models confirmed to route all output to delta.reasoning with content always empty,
    // even when reasoning:{effort:"none"} is sent. Un-filter a model here once it's
    // verified to produce delta.content output.
    private static readonly string[] PureReasoningModelPrefixes = ReasoningFirstPrefixes;

    private const string ModelsEndpoint = "https://openrouter.ai/api/v1/models";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static List<OpenRouterModelInfo>? _cache;
    private static DateTime _cacheTimestamp = DateTime.MinValue;

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke", "openrouter.log");

    public static bool IsRecommended(string modelId) =>
        RecommendedModelIds.Contains(modelId);

    /// <summary>
    /// Returns true if this model is known to route all output through the reasoning
    /// channel (delta.reasoning / message.reasoning), making it unreliable for autocomplete.
    /// </summary>
    public static bool IsReasoningFirstModel(string modelId)
    {
        foreach (var prefix in ReasoningFirstPrefixes)
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Force the next GetModelsAsync call to re-fetch from the network.
    /// </summary>
    public static void InvalidateCache()
    {
        _cache = null;
        _cacheTimestamp = DateTime.MinValue;
    }

    /// <summary>
    /// Returns the cached model list, fetching from the network if the cache is
    /// missing or stale. Returns the last-known-good list on network failure.
    /// </summary>
    public static async Task<IReadOnlyList<OpenRouterModelInfo>> GetModelsAsync(
        CancellationToken ct = default)
    {
        // Fast path — return valid cache without taking the semaphore
        if (_cache != null && (DateTime.UtcNow - _cacheTimestamp) < CacheTtl)
            return _cache;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check inside the lock
            if (_cache != null && (DateTime.UtcNow - _cacheTimestamp) < CacheTtl)
                return _cache;

            var fresh = await FetchAndParseAsync(ct);
            _cache = fresh;
            _cacheTimestamp = DateTime.UtcNow;
            return _cache;
        }
        catch (OperationCanceledException)
        {
            return _cache ?? new List<OpenRouterModelInfo>();
        }
        catch (Exception ex)
        {
            Log($"Fetch failed: {ex.Message}");
            return _cache ?? new List<OpenRouterModelInfo>();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<List<OpenRouterModelInfo>> FetchAndParseAsync(CancellationToken ct)
    {
        Log("Fetching model list from OpenRouter...");
        var response = await _httpClient.GetAsync(ModelsEndpoint, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var api = JsonSerializer.Deserialize<ApiResponse>(json);
        if (api?.Data == null)
        {
            Log("Response had no 'data' array");
            return new List<OpenRouterModelInfo>();
        }

        var results = new List<OpenRouterModelInfo>();
        foreach (var dto in api.Data)
        {
            if (string.IsNullOrWhiteSpace(dto.Id)) continue;

            // Skip models that don't produce text output
            if (!IsTextOutputModel(dto.Architecture?.Modality)) continue;

            // Skip pure-reasoning-only models — their output never reaches delta.content
            if (IsPureReasoningModel(dto.Id)) continue;

            var provider   = ParseProvider(dto.Id);
            var name       = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name;
            var pricePer1M = ParsePricePer1M(dto.Pricing?.Prompt);
            var recommended = RecommendedModelIds.Contains(dto.Id);

            results.Add(new OpenRouterModelInfo(
                Id:               dto.Id,
                DisplayName:      name,
                Provider:         FormatProvider(provider),
                InputPricePer1M:  pricePer1M,
                IsRecommended:    recommended
            ));
        }

        // Sort: recommended first, then by provider, then by display name
        results.Sort((a, b) =>
        {
            if (a.IsRecommended != b.IsRecommended)
                return a.IsRecommended ? -1 : 1;
            var prov = string.Compare(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase);
            return prov != 0 ? prov
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        Log($"Loaded {results.Count} text-output models " +
            $"({results.Count(m => m.IsRecommended)} recommended)");
        return results;
    }

    /// <summary>
    /// Returns true if the model produces text output.
    /// Handles "text->text", "text+image->text", "multimodal->text", etc.
    /// Unknown/null modalities are included by default.
    /// </summary>
    private static bool IsTextOutputModel(string? modality)
    {
        if (string.IsNullOrEmpty(modality)) return true;
        var arrow = modality.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0) return modality.Contains("text", StringComparison.OrdinalIgnoreCase);
        return modality[(arrow + 2)..].Contains("text", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPureReasoningModel(string modelId)
    {
        foreach (var prefix in PureReasoningModelPrefixes)
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ParseProvider(string modelId)
    {
        var slash = modelId.IndexOf('/');
        return slash > 0 ? modelId[..slash] : modelId;
    }

    private static string FormatProvider(string raw) => raw.ToLowerInvariant() switch
    {
        "openai"     => "OpenAI",
        "anthropic"  => "Anthropic",
        "google"     => "Google",
        "meta-llama" => "Meta",
        "mistralai"  => "Mistral",
        "cohere"     => "Cohere",
        "perplexity" => "Perplexity",
        "deepseek"   => "DeepSeek",
        "x-ai"       => "xAI",
        "microsoft"  => "Microsoft",
        "nvidia"     => "NVIDIA",
        "qwen"       => "Qwen",
        "bytedance"  => "ByteDance",
        "stepfun"    => "StepFun",
        "liquid"     => "Liquid",
        "openrouter" => "OpenRouter",
        _ => raw.Length > 0 ? char.ToUpper(raw[0]) + raw[1..] : raw
    };

    /// <summary>
    /// Converts the per-token price string (e.g. "0.00000025") to per-1M-token cost.
    /// </summary>
    private static decimal ParsePricePer1M(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        return decimal.TryParse(raw,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var perToken) ? perToken * 1_000_000m : 0m;
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [ModelSvc] {msg}\n"); }
        catch (IOException) { }
    }

    // ── JSON response DTOs ────────────────────────────────────────────────────

    private class ApiResponse
    {
        [JsonPropertyName("data")] public List<ModelDto>? Data { get; set; }
    }

    private class ModelDto
    {
        [JsonPropertyName("id")]           public string?       Id           { get; set; }
        [JsonPropertyName("name")]         public string?       Name         { get; set; }
        [JsonPropertyName("architecture")] public ArchDto?      Architecture { get; set; }
        [JsonPropertyName("pricing")]      public PricingDto?   Pricing      { get; set; }
    }

    private class ArchDto
    {
        [JsonPropertyName("modality")] public string? Modality { get; set; }
    }

    private class PricingDto
    {
        [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    }
}
