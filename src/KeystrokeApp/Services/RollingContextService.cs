using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Maintains a rolling window of recently accepted text to provide
/// continuity across completion sessions. Cleared when switching apps
/// or after extended pauses.
/// </summary>
public class RollingContextService
{
    private readonly int _maxChars;
    private readonly StringBuilder _context;
    private readonly object _lock = new();
    
    private string _currentProcess = "";
    private string _currentWindowTitle = "";
    private DateTime _lastAppendTime;
    private readonly TimeSpan _contextTimeout;

    public RollingContextService(int maxChars = 500, int timeoutMinutes = 5)
    {
        _maxChars = maxChars;
        _contextTimeout = TimeSpan.FromMinutes(timeoutMinutes);
        _context = new StringBuilder(maxChars);
        _lastAppendTime = DateTime.MinValue;
    }

    /// <summary>
    /// Appends accepted text to the rolling context.
    /// If the app/window changed, clears the context first.
    /// </summary>
    public void AppendAccepted(string text, string processName, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            
            // Clear context if:
            // 1. App changed
            // 2. Window title changed significantly (new document/conversation)
            // 3. Too much time passed since last input
            bool appChanged = !string.Equals(_currentProcess, processName, StringComparison.OrdinalIgnoreCase);
            bool windowChanged = !string.Equals(_currentWindowTitle, windowTitle, StringComparison.OrdinalIgnoreCase);
            bool timedOut = (now - _lastAppendTime) > _contextTimeout;
            
            if (appChanged || windowChanged || timedOut)
            {
                _context.Clear();
                _currentProcess = processName;
                _currentWindowTitle = windowTitle;
            }

            // Append the new text
            _context.Append(text);
            
            // Trim to max size (keep the end, it's more recent/relevant)
            if (_context.Length > _maxChars)
            {
                var excess = _context.Length - _maxChars;
                _context.Remove(0, excess);
                
                // Try to find a clean word boundary to start from
                int firstSpace = -1;
                for (int i = 0; i < _context.Length && i < 20; i++)
                {
                    if (_context[i] == ' ') { firstSpace = i; break; }
                }
                if (firstSpace > 0)
                {
                    _context.Remove(0, firstSpace + 1);
                }
            }

            _lastAppendTime = now;
        }
    }

    /// <summary>
    /// Gets the current rolling context (recently accepted text).
    /// Returns empty string if context is stale or from different app.
    /// </summary>
    public string GetContext(string? forProcessName = null, string? forWindowTitle = null)
    {
        lock (_lock)
        {
            // If requesting for a specific app/window, verify it matches
            if (forProcessName != null && 
                !string.Equals(_currentProcess, forProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            // Check for timeout
            if ((DateTime.UtcNow - _lastAppendTime) > _contextTimeout)
            {
                return "";
            }

            return _context.ToString();
        }
    }

    /// <summary>
    /// Clears all context (e.g., on manual reset or app switch).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _context.Clear();
            _currentProcess = "";
            _currentWindowTitle = "";
            _lastAppendTime = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Returns true if we have meaningful context available.
    /// </summary>
    public bool HasContext { get { lock (_lock) return _context.Length > 20; } }

    /// <summary>
    /// Gets metadata about the current context for debugging.
    /// </summary>
    public ContextInfo GetInfo()
    {
        lock (_lock)
        {
            return new ContextInfo
            {
                Length = _context.Length,
                Process = _currentProcess,
                WindowTitle = _currentWindowTitle,
                LastActivity = _lastAppendTime,
                IsStale = (DateTime.UtcNow - _lastAppendTime) > _contextTimeout
            };
        }
    }

    public class ContextInfo
    {
        public int Length { get; set; }
        public string Process { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public DateTime LastActivity { get; set; }
        public bool IsStale { get; set; }
    }
}
