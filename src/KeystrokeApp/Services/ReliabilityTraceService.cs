using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

public sealed record ReliabilityTraceEvent(
    DateTime TimestampUtc,
    string Area,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, string>? Data = null
);

public sealed class ReliabilityTraceService
{
    private const int MaxRecentEvents = 200;
    private readonly string _logPath;
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<ReliabilityTraceEvent> _recentEvents = new();

    public event Action<ReliabilityTraceEvent>? EventRecorded;

    public ReliabilityTraceService()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke",
            "reliability.log");
    }

    public string LogPath => _logPath;

    public IReadOnlyList<ReliabilityTraceEvent> GetRecentEvents()
        => _recentEvents.ToArray();

    public void Trace(
        string area,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var evt = new ReliabilityTraceEvent(
            DateTime.UtcNow,
            area,
            eventName,
            message,
            data);

        _recentEvents.Enqueue(evt);
        while (_recentEvents.Count > MaxRecentEvents && _recentEvents.TryDequeue(out _))
        {
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var json = JsonSerializer.Serialize(evt);
            lock (_fileLock)
            {
                File.AppendAllText(_logPath, json + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }

        EventRecorded?.Invoke(evt);
    }
}
