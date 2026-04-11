using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Persists accepted-completion counts across sessions.
/// Stored in usage.json separately from config.json so we never
/// re-encrypt API keys or touch sensitive settings on every acceptance.
/// </summary>
public class UsageCounters
{
    /// <summary>Total completions accepted since first install.</summary>
    public int TotalAccepted { get; set; } = 0;

    /// <summary>Completions accepted on <see cref="DailyDate"/>.</summary>
    public int DailyCount { get; set; } = 0;

    /// <summary>The calendar date (yyyy-MM-dd) that <see cref="DailyCount"/> applies to.</summary>
    public string DailyDate { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke",
        "usage.json"
    );

    public static UsageCounters Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<UsageCounters>(json) ?? new UsageCounters();
            }
        }
        catch (Exception) { /* Corrupt or missing file — start fresh */ }
        return new UsageCounters();
    }

    /// <summary>The number of free completions allowed per day.</summary>
    public const int DailyLimit = 50;

    /// <summary>Daily count at which the approaching-limit warning balloon is shown.</summary>
    public const int WarningThreshold = 40;

    /// <summary>
    /// Returns true if today's accepted count has reached the daily free limit.
    /// Always returns false for a new day (counter not yet incremented today).
    /// </summary>
    public bool IsLimitReached()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        return DailyDate == today && DailyCount >= DailyLimit;
    }

    /// <summary>
    /// Increments both the all-time and today's accepted-completion counters.
    /// Automatically resets the daily counter when the calendar date changes.
    /// </summary>
    public void IncrementAccepted()
    {
        TotalAccepted++;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (DailyDate != today)
        {
            DailyDate  = today;
            DailyCount = 0;
        }
        DailyCount++;
    }

    /// <summary>
    /// Writes the counters to disk atomically (temp-file + rename).
    /// Safe to call from a background thread.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json     = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception) { /* Non-critical: a missed counter save is acceptable */ }
    }
}
