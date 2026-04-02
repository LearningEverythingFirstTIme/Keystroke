namespace KeystrokeApp.Services;

/// <summary>
/// Detects when the user presses Backspace shortly after accepting a suggestion,
/// which signals that the injected completion was not actually what they wanted.
///
/// Usage:
///   1. Call StartWatching(callback) immediately after text injection.
///   2. Call OnBackspace() from the keyboard hook whenever Backspace fires.
///   3. After the watch window expires, callback is invoked with editedAfter=true/false.
///
/// The callback fires exactly once on the thread-pool after the window expires.
/// It is safe to call OnBackspace() or StartWatching() from any thread.
/// </summary>
public sealed class PostEditDetector : IDisposable
{
    /// <summary>
    /// How long after text injection we watch for a corrective backspace.
    /// 1500ms is wide enough to catch deliberate corrections but short enough
    /// to not misfire on normal typing that happens to follow an acceptance.
    /// </summary>
    private const int WatchWindowMs = 1500;

    private volatile bool _isWatching;
    private volatile bool _editDetected;
    private Action<bool>? _callback;
    private Timer? _timer;
    private readonly object _lock = new();

    /// <summary>
    /// Begin a new watch window. Any previously active window is abandoned
    /// (its callback will NOT fire — the new one supersedes it).
    /// </summary>
    /// <param name="onComplete">
    /// Invoked on a thread-pool thread after <see cref="WatchWindowMs"/> ms.
    /// Parameter is <c>true</c> if backspace was detected within the window.
    /// </param>
    public void StartWatching(Action<bool> onComplete)
    {
        lock (_lock)
        {
            // Cancel any previous watch without firing its callback.
            _timer?.Dispose();
            _isWatching    = true;
            _editDetected  = false;
            _callback      = onComplete;

            _timer = new Timer(_ =>
            {
                Action<bool>? cb;
                bool detected;
                lock (_lock)
                {
                    if (!_isWatching) return; // already superseded
                    _isWatching = false;
                    detected    = _editDetected;
                    cb          = _callback;
                    _callback   = null;
                }
                cb?.Invoke(detected);

            }, null, WatchWindowMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Signal that a Backspace key was pressed. Call this from the keyboard hook
    /// on every Backspace event — it's a no-op when no watch is active.
    /// </summary>
    public void OnBackspace()
    {
        if (_isWatching)
            _editDetected = true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isWatching = false;
            _callback   = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
