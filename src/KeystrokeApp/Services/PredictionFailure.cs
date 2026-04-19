namespace KeystrokeApp.Services;

/// <summary>
/// Category of prediction-engine failure. Distinguishes cases that the UI and
/// retry logic should treat differently — a 401 should surface to the user, a
/// transient 503 should not. Kept small and provider-agnostic.
/// </summary>
public enum PredictionFailureKind
{
    /// <summary>401 / 403 or equivalent — the user's API key is missing, revoked, or lacks access. User must act. </summary>
    AuthFailure,
    /// <summary>429 or provider-specific rate-limit body. Transient but not silently retryable on the hot path.</summary>
    RateLimit,
    /// <summary>Network error, 5xx, or timeout. Safe to retry on the next keystroke.</summary>
    Transient,
    /// <summary>2xx response with an unparseable body, or a shape we don't understand.</summary>
    MalformedResponse,
    /// <summary>Fallback when a more specific kind can't be determined.</summary>
    Unknown
}

/// <summary>
/// A typed failure signal from a prediction engine. Replaces silent
/// <c>catch (Exception) { return null; }</c> with enough structure for the
/// tray/tooltip layer to react and for reliability tracing to record a
/// meaningful record. Does NOT carry the exception itself — the engine's
/// local log still owns the full stack.
///
/// <see cref="Retryable"/> is a hint to the caller: auth failures are not
/// retryable (the next keystroke will fail identically), while transient
/// failures are.
/// </summary>
public sealed record PredictionFailure(
    PredictionFailureKind Kind,
    string ProviderName,
    string Message,
    bool Retryable,
    int? HttpStatusCode = null);
