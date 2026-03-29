using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Learns from user's accepted completions to provide few-shot examples
/// for better predictions. Reads from tracking.jsonl and finds similar
/// past completions based on app context and prefix similarity.
/// </summary>
public class AcceptanceLearningService
{
    private readonly string _trackingPath;
    private readonly string _logPath;
    private readonly List<TrackingEntry> _acceptedEntries;
    private readonly object _lock = new();
    private DateTime _lastFileRead;
    private long _lastFileSize;

    // Configuration
    private const int MaxExamplesToReturn = 3;
    private const int MaxPrefixLengthForMatching = 50;
    private const int MinCompletionLength = 10;

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Learning] {msg}\n"); }
        catch { }
    }

    public AcceptanceLearningService()
    {
        _trackingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "tracking.jsonl");
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "learning.log");
        _acceptedEntries = new List<TrackingEntry>();
        _lastFileRead = DateTime.MinValue;
        _lastFileSize = 0;
        
        // Debug: log the path we're looking for
        Log($"Initialized. Looking for tracking data at: {_trackingPath}");
        Log($"File exists: {File.Exists(_trackingPath)}");
        if (File.Exists(_trackingPath))
        {
            var info = new FileInfo(_trackingPath);
            Log($"File size: {info.Length} bytes");
        }
    }

    /// <summary>
    /// Gets few-shot examples similar to the current context.
    /// Returns empty list if no relevant examples found.
    /// </summary>
    public List<FewShotExample> GetExamples(ContextSnapshot context, int count = 3)
    {
        // Refresh data from disk if needed
        RefreshIfNeeded();

        lock (_lock)
        {
            var candidates = _acceptedEntries
                .Where(e => IsRelevant(e, context))
                .Select(e => new
                {
                    Entry = e,
                    Score = CalculateRelevanceScore(e, context)
                })
                .Where(x => x.Score > 0.3) // Minimum relevance threshold
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Timestamp) // Prefer recent
                .Take(count)
                .Select(x => new FewShotExample
                {
                    Prefix = x.Entry.Prefix,
                    Completion = x.Entry.Completion,
                    Context = $"{x.Entry.App} ({x.Entry.Category})"
                })
                .ToList();

            return candidates;
        }
    }

    /// <summary>
    /// Gets quick stats about the learning data for debugging.
    /// </summary>
    public LearningStats GetStats()
    {
        RefreshIfNeeded();

        lock (_lock)
        {
            var byCategory = _acceptedEntries
                .GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            // Check file existence for debugging
            bool fileExists = File.Exists(_trackingPath);
            long fileSize = fileExists ? new FileInfo(_trackingPath).Length : 0;

            return new LearningStats
            {
                TotalAccepted = _acceptedEntries.Count,
                LastEntryTime = _acceptedEntries.Count > 0 
                    ? _acceptedEntries.Max(e => e.Timestamp) 
                    : null,
                ByCategory = byCategory,
                DataFilePath = _trackingPath,
                DataFileExists = fileExists,
                DataFileSize = fileSize
            };
        }
    }

    /// <summary>
    /// Forces a refresh of the data from disk.
    /// </summary>
    public void Refresh()
    {
        if (!File.Exists(_trackingPath))
            return;

        try
        {
            var fileInfo = new FileInfo(_trackingPath);
            Log($"Refreshing from {_trackingPath}");
            Log($"File size: {fileInfo.Length}, Last read: {_lastFileSize}");
            
            lock (_lock)
            {
                // Only read new lines if file has grown
                if (fileInfo.Length < _lastFileSize)
                {
                    // File was truncated/rotated, reload everything
                    Log("File truncated, clearing cache");
                    _acceptedEntries.Clear();
                }

                using var stream = File.Open(_trackingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                // Skip to where we left off if file grew
                if (fileInfo.Length > _lastFileSize && _lastFileSize > 0)
                {
                    stream.Seek(_lastFileSize, SeekOrigin.Begin);
                    Log($"Seeking to position {_lastFileSize}");
                }

                int linesRead = 0;
                int linesAccepted = 0;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    linesRead++;
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var options = new JsonSerializerOptions 
                        { 
                            PropertyNameCaseInsensitive = true 
                        };
                        var entry = JsonSerializer.Deserialize<TrackingEntry>(line, options);
                        if (entry != null && 
                            entry.Action == "accepted" &&
                            entry.Completion?.Length >= MinCompletionLength)
                        {
                            _acceptedEntries.Add(entry);
                            linesAccepted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Parse error: {ex.Message} on line: {line[..Math.Min(50, line.Length)]}");
                    }
                }

                Log($"Read {linesRead} lines, accepted {linesAccepted} entries. Total: {_acceptedEntries.Count}");

                // Trim to recent entries to prevent unbounded growth
                const int maxEntries = 1000;
                if (_acceptedEntries.Count > maxEntries)
                {
                    _acceptedEntries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                    _acceptedEntries.RemoveRange(maxEntries, _acceptedEntries.Count - maxEntries);
                }

                _lastFileRead = DateTime.UtcNow;
                _lastFileSize = fileInfo.Length;
            }
        }
        catch (Exception ex)
        {
            Log($"Refresh error: {ex.Message}");
        }
    }

    private void RefreshIfNeeded()
    {
        // Refresh if it's been more than 5 seconds since last read
        if ((DateTime.UtcNow - _lastFileRead).TotalSeconds > 5)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Checks if a past entry is relevant to the current context.
    /// </summary>
    private bool IsRelevant(TrackingEntry entry, ContextSnapshot context)
    {
        // Must be same app category for relevance
        var currentCategory = AppCategory.GetEffectiveCategory(
            context.ProcessName, context.WindowTitle);
        
        if (!Enum.TryParse<AppCategory.Category>(entry.Category, out var entryCategory))
            return false;

        // Category must match exactly
        if (entryCategory != currentCategory)
            return false;

        // Skip very short or very long prefixes
        if (entry.Prefix.Length < 3 || entry.Prefix.Length > 100)
            return false;

        return true;
    }

    /// <summary>
    /// Calculates a relevance score (0-1) for a past entry.
    /// Higher = more similar to current context.
    /// </summary>
    private double CalculateRelevanceScore(TrackingEntry entry, ContextSnapshot context)
    {
        double score = 0;

        // Prefix similarity (word overlap)
        var currentWords = context.TypedText.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entryWords = entry.Prefix.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (currentWords.Length > 0 && entryWords.Length > 0)
        {
            var commonWords = currentWords.Intersect(entryWords).Count();
            var totalWords = Math.Max(currentWords.Length, entryWords.Length);
            score += (commonWords / (double)totalWords) * 0.6;
        }

        // Prefix starts with same word (strong signal)
        if (currentWords.Length > 0 && entryWords.Length > 0 &&
            currentWords[0] == entryWords[0])
        {
            score += 0.3;
        }

        // Same app (bonus)
        if (string.Equals(entry.App, context.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.1;
        }

        // Recency bonus (within last hour)
        var age = DateTime.UtcNow - entry.Timestamp;
        if (age.TotalHours < 1)
        {
            score += 0.05;
        }

        return Math.Min(score, 1.0);
    }

    private class TrackingEntry
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string Completion { get; set; } = "";
        public string App { get; set; } = "";
        public string Window { get; set; } = "";
        public string Category { get; set; } = "";
    }

    public class FewShotExample
    {
        public string Prefix { get; set; } = "";
        public string Completion { get; set; } = "";
        public string Context { get; set; } = "";
    }

    public class LearningStats
    {
        public int TotalAccepted { get; set; }
        public DateTime? LastEntryTime { get; set; }
        public Dictionary<string, int> ByCategory { get; set; } = new();
        public string DataFilePath { get; set; } = "";
        public bool DataFileExists { get; set; }
        public long DataFileSize { get; set; }
    }
}
