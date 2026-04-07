namespace KeystrokeApp.Services;

/// <summary>
/// Bundles all context available at prediction time.
/// The prediction engine uses this to build a richer prompt.
/// </summary>
public class ContextSnapshot
{
    /// <summary>
    /// What the user has typed (from TypingBuffer).
    /// </summary>
    public required string TypedText { get; init; }

    /// <summary>
    /// Process name of the active window (e.g. "chrome", "WINWORD", "notepad").
    /// </summary>
    public string ProcessName { get; init; } = "";

    /// <summary>
    /// Title of the active window (e.g. "My Document - Google Docs").
    /// </summary>
    public string WindowTitle { get; init; } = "";

    /// <summary>
    /// Privacy-safe app context string suitable for outbound prompts.
    /// This should never contain the raw window title.
    /// </summary>
    public string SafeContextLabel { get; init; } = "";

    /// <summary>
    /// OCR-captured text from the screen. Null if OCR hasn't run yet or failed.
    /// </summary>
    public string? ScreenText { get; init; }

    /// <summary>
    /// Recently accepted text from previous completions in the same session.
    /// Provides continuity across completion sessions.
    /// </summary>
    public string? RollingContext { get; init; }

    /// <summary>
    /// Whether any meaningful context beyond the typed text is available.
    /// </summary>
    public bool HasAppContext => !string.IsNullOrEmpty(ProcessName);
    public bool HasScreenContext => !string.IsNullOrEmpty(ScreenText);
    public bool HasRollingContext => !string.IsNullOrEmpty(RollingContext);
}
