using System;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Tracks prediction acceptance/dismissal for future analysis.
/// Writes structured JSONL to %AppData%/Keystroke/tracking.jsonl
/// </summary>
public class AcceptanceTracker
{
    private readonly string _trackingPath;

    public AcceptanceTracker()
    {
        _trackingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "tracking.jsonl");
    }

    public void LogAccepted(string prefix, string completion, string processName, string windowTitle)
    {
        WriteEntry("accepted", prefix, completion, processName, windowTitle);
    }

    public void LogDismissed(string prefix, string completion, string processName, string windowTitle)
    {
        WriteEntry("dismissed", prefix, completion, processName, windowTitle);
    }

    public void LogIgnored(string prefix, string completion, string processName, string windowTitle)
    {
        WriteEntry("ignored", prefix, completion, processName, windowTitle);
    }

    /// <summary>
    /// If the tracking file exceeds maxLines, rewrites it keeping only the most recent entries.
    /// Call once at startup — keeps the file from growing unbounded over months of use.
    /// </summary>
    public void PruneIfNeeded(int maxLines = 2000)
    {
        try
        {
            if (!File.Exists(_trackingPath))
                return;

            var lines = File.ReadAllLines(_trackingPath);
            if (lines.Length <= maxLines)
                return;

            // Keep the most recent maxLines entries and rewrite
            var trimmed = lines[^maxLines..];
            File.WriteAllLines(_trackingPath, trimmed);
        }
        catch { }
    }

    private void WriteEntry(string action, string prefix, string completion, string processName, string windowTitle)
    {
        try
        {
            var entry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                action,
                prefix,
                completion,
                app = processName,
                window = windowTitle,
                category = AppCategory.GetEffectiveCategory(processName, windowTitle).ToString()
            };

            var json = JsonSerializer.Serialize(entry);
            File.AppendAllText(_trackingPath, json + "\n");
        }
        catch { }
    }
}
