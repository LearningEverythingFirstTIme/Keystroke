namespace KeystrokeApp.Services;

/// <summary>
/// Interface for prediction engines.
/// </summary>
public interface IPredictionEngine
{
    /// <summary>
    /// How long to wait for a prediction before timing out.
    /// Local models need much more time than cloud APIs.
    /// </summary>
    int TimeoutMs => 8000;
    /// <summary>
    /// Get a text completion for the given context.
    /// Returns null if no prediction available or on error.
    /// </summary>
    Task<string?> PredictAsync(ContextSnapshot context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream a text completion, calling onChunk for each piece of text as it arrives.
    /// Returns the full completion when done, or null on error.
    /// Default implementation falls back to PredictAsync (no streaming).
    /// </summary>
    Task<string?> PredictStreamingAsync(ContextSnapshot context, Action<string> onChunk, CancellationToken cancellationToken = default)
        => PredictAsync(context, cancellationToken);

    /// <summary>
    /// Fetch multiple alternative completions for cycling through with Ctrl+Up/Down.
    /// Default implementation makes parallel PredictAsync calls with higher temperature.
    /// </summary>
    Task<List<string>> FetchAlternativesAsync(ContextSnapshot context, int count = 3, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<string>());

    /// <summary>
    /// General-purpose non-streaming text generation. Used by StyleProfileService
    /// to generate style analysis summaries. Returns raw response text, or null on error.
    /// </summary>
    Task<string?> GenerateTextAsync(string systemPrompt, string userPrompt, int maxTokens = 200, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
