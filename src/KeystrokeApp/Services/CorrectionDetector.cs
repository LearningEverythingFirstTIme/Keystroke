namespace KeystrokeApp.Services;

/// <summary>
/// Detects and characterizes corrections the user makes after accepting a suggestion.
/// Tracks backspace presses (deletion from the original completion) and characters
/// typed (replacement text) during a 1500ms watch window after text injection.
///
/// The watch window captures the user's immediate intent:
///   - Backspace only → truncation (completion was too long)
///   - Backspace then type → ending replacement (wrong words at the end)
///   - No edits → the completion was perfect (untouched signal)
///
/// Typo-correction within replacement text is handled automatically: a backspace
/// that follows typed characters removes from the replacement buffer, not from
/// the deletion count. Only backspaces that reach "past" the typed characters
/// increment the deletion count into the original completion.
///
/// Usage:
///   1. Call StartWatching(callback) immediately after text injection.
///   2. Call OnBackspace() from the input listener whenever Backspace fires.
///   3. Call OnCharacterTyped(c) from the input listener for each character.
///   4. After the watch window expires, callback is invoked with a CorrectionInfo.
///
/// The callback fires exactly once on the thread-pool after the window expires.
/// It is safe to call OnBackspace(), OnCharacterTyped(), or StartWatching() from any thread.
/// </summary>
public sealed class CorrectionDetector : IDisposable
{
    /// <summary>
    /// How long after text injection we watch for a corrective edit.
    /// 1500ms is wide enough to catch deliberate corrections but short enough
    /// to not misfire on normal typing that happens to follow an acceptance.
    /// </summary>
    private const int WatchWindowMs = 1500;

    private volatile bool _isWatching;
    private volatile bool _backspaceDetected;
    private int _backspaceCount;
    private readonly List<char> _charsTyped = new();
    private Action<CorrectionInfo>? _callback;
    private Timer? _timer;
    private readonly object _lock = new();

    /// <summary>
    /// Begin a new watch window. Any previously active window is abandoned
    /// (its callback will NOT fire — the new one supersedes it).
    /// </summary>
    /// <param name="onComplete">
    /// Invoked on a thread-pool thread after <see cref="WatchWindowMs"/> ms.
    /// Parameter contains the full correction details captured during the window.
    /// </param>
    public void StartWatching(Action<CorrectionInfo> onComplete)
    {
        lock (_lock)
        {
            // Cancel any previous watch without firing its callback.
            _timer?.Dispose();
            _isWatching = true;
            _backspaceDetected = false;
            _backspaceCount = 0;
            _charsTyped.Clear();
            _callback = onComplete;

            _timer = new Timer(_ =>
            {
                Action<CorrectionInfo>? cb;
                CorrectionInfo info;
                lock (_lock)
                {
                    if (!_isWatching) return; // already superseded
                    _isWatching = false;
                    info = new CorrectionInfo
                    {
                        EditDetected = _backspaceDetected,
                        BackspaceCount = _backspaceCount,
                        ReplacementText = new string(_charsTyped.ToArray())
                    };
                    cb = _callback;
                    _callback = null;
                }
                cb?.Invoke(info);

            }, null, WatchWindowMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Signal that a Backspace key was pressed. Call this from the input listener
    /// on every Backspace event — it's a no-op when no watch is active.
    ///
    /// If the user has typed replacement characters, backspace removes from that
    /// buffer first (typo correction). Only when the replacement buffer is empty
    /// does backspace increment the deletion count into the original completion.
    /// </summary>
    public void OnBackspace()
    {
        if (!_isWatching) return;
        lock (_lock)
        {
            if (!_isWatching) return;
            _backspaceDetected = true;
            if (_charsTyped.Count > 0)
                _charsTyped.RemoveAt(_charsTyped.Count - 1);
            else
                _backspaceCount++;
        }
    }

    /// <summary>
    /// Signal that a printable character was typed during the watch window.
    /// Characters typed after backspaces are captured as replacement text.
    /// Characters typed without any prior backspace are still recorded but
    /// do NOT set EditDetected (preserving backward-compatible behavior where
    /// only backspaces count as edits for quality scoring).
    /// </summary>
    public void OnCharacterTyped(char c)
    {
        if (!_isWatching) return;
        lock (_lock)
        {
            if (!_isWatching) return;
            _charsTyped.Add(c);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isWatching = false;
            _callback = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>
/// Correction details captured during the post-acceptance watch window.
/// Carries the raw signals from the 1500ms observation period.
/// </summary>
public sealed class CorrectionInfo
{
    /// <summary>
    /// True if backspace was detected during the watch window. Matches legacy
    /// editedAfter behavior — characters typed alone are NOT counted as edits
    /// to preserve quality score backward compatibility.
    /// </summary>
    public bool EditDetected { get; init; }

    /// <summary>
    /// Net backspace count into the original completion text. Backspaces that
    /// removed the user's own typed replacement characters are excluded.
    /// Represents how many characters were deleted from the end of the
    /// accepted suggestion.
    /// </summary>
    public int BackspaceCount { get; init; }

    /// <summary>
    /// Characters typed during the watch window after backspacing. This is the
    /// user's replacement text. Empty if the user only deleted (truncation)
    /// or if no backspaces occurred (continuation, not a correction).
    /// </summary>
    public string ReplacementText { get; init; } = "";

    /// <summary>
    /// True when the correction constitutes a meaningful edit: at least one
    /// backspace into the original completion, optionally followed by replacement text.
    /// </summary>
    public bool HasCorrection => BackspaceCount > 0;

    /// <summary>
    /// Returns a short classification label for logging/diagnostics.
    /// </summary>
    public string CorrectionType() =>
        !HasCorrection ? "none"
        : BackspaceCount <= 2 && ReplacementText.Length <= 2 ? "minor"
        : ReplacementText.Length == 0 ? "truncated"
        : "replaced_ending";
}
