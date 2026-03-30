using System;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

/// <summary>
/// A simple debounce timer that waits for a pause before firing.
/// If restarted during the wait, the previous action is cancelled.
/// </summary>
public class DebounceTimer : IDisposable
{
    private readonly int _delayMs;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Fired when the debounce period completes without being restarted.
    /// </summary>
    public event Action? DebounceComplete;

    /// <summary>
    /// Create a new debounce timer with the specified delay.
    /// </summary>
    public DebounceTimer(int delayMs)
    {
        _delayMs = delayMs;
    }

    /// <summary>
    /// Start or restart the debounce timer.
    /// If already waiting, the previous wait is cancelled.
    /// </summary>
    public void Restart()
    {
        CancellationTokenSource? oldCts;
        CancellationToken token;

        lock (_lock)
        {
            if (_disposed) return;

            // Cancel any existing timer
            oldCts = _cts;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        // Cancel and dispose the old one outside the lock
        oldCts?.Cancel();
        oldCts?.Dispose();

        // Start the new timer
        _ = WaitAndFireAsync(token);
    }

    /// <summary>
    /// Cancel any pending debounce.
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;

        lock (_lock)
        {
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task WaitAndFireAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delayMs, token);

            if (!token.IsCancellationRequested)
            {
                DebounceComplete?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when Restart() is called or Cancel()
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }
}
