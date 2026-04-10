using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KeystrokeApp.Services;

public enum GeminiApiKeyValidationStatus
{
    Valid,
    Missing,
    InvalidFormat,
    Unauthorized,
    QuotaLimited,
    NetworkError,
    Timeout,
    UnknownError
}

public sealed record GeminiApiKeyValidationResult(
    GeminiApiKeyValidationStatus Status,
    string Message)
{
    public bool IsValid => Status == GeminiApiKeyValidationStatus.Valid;
}

public sealed class GeminiApiKeyValidationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GeminiApiKeyValidationService()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(8) }, ownsClient: true)
    {
    }

    internal GeminiApiKeyValidationService(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<GeminiApiKeyValidationResult> ValidateAsync(
        string? apiKey,
        string model = AppConfig.DefaultGeminiModel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.Missing,
                "Paste your Gemini API key to continue.");
        }

        var trimmedKey = apiKey.Trim();
        if (trimmedKey.Length < 16)
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.InvalidFormat,
                "That key looks too short. Copy the full Gemini API key from Google AI Studio and try again.");
        }

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", trimmedKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = "Reply with OK." }
                    }
                }
            },
            generationConfig = new
            {
                maxOutputTokens = 4,
                temperature = 0.0,
                thinkingConfig = new { thinkingBudget = 0 }
            }
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return new GeminiApiKeyValidationResult(
                    GeminiApiKeyValidationStatus.Valid,
                    "Gemini key verified. Keystroke is ready to use Gemini 3.1 Flash-Lite Preview.");
            }

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            return MapFailure(response.StatusCode, errorBody);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.Timeout,
                "Gemini validation timed out. Check your connection and try again.");
        }
        catch (HttpRequestException)
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.NetworkError,
                "Keystroke could not reach Gemini. Check your network connection and try again.");
        }
        catch (Exception ex)
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.UnknownError,
                $"Gemini validation failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private static GeminiApiKeyValidationResult MapFailure(HttpStatusCode statusCode, string errorBody)
    {
        var normalized = errorBody?.Trim() ?? "";
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            normalized.Contains("api key not valid", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("invalid api key", StringComparison.OrdinalIgnoreCase))
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.Unauthorized,
                "That Gemini API key was rejected. Make sure you copied the full key from Google AI Studio.");
        }

        if ((int)statusCode == 429 ||
            normalized.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.QuotaLimited,
                "Gemini reached a quota or billing limit. Confirm the key is active and try again later.");
        }

        if (statusCode == HttpStatusCode.BadRequest)
        {
            return new GeminiApiKeyValidationResult(
                GeminiApiKeyValidationStatus.InvalidFormat,
                "Gemini could not use that key request. Re-copy the key from Google AI Studio and try again.");
        }

        return new GeminiApiKeyValidationResult(
            GeminiApiKeyValidationStatus.UnknownError,
            "Gemini could not verify the key right now. Try again in a moment.");
    }
}
