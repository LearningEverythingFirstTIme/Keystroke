namespace KeystrokeApp.Services;

/// <summary>
/// Root persistence model for the analytics dashboard.
/// Stored in %AppData%/Keystroke/analytics-daily.json.
/// </summary>
public class AnalyticsStore
{
    public DateTime LastAggregatedEventTimestamp { get; set; }
    public List<AnalyticsDailyRollup> Rollups { get; set; } = new();
    public int CumulativeAccepted { get; set; }
    public int CumulativeNative { get; set; }
    public int CurrentStreak { get; set; }
    public int LongestStreak { get; set; }
    public string StreakAnchorDate { get; set; } = "";
    public Dictionary<string, List<ScoreSnapshot>> ScoreHistory { get; set; } = new();
    public List<WeekSummary> WeeklySummaries { get; set; } = new();
    public List<AchievedMilestone> AchievedMilestones { get; set; } = new();
}

public class AnalyticsDailyRollup
{
    public string Date { get; set; } = "";
    public int TotalAccepted { get; set; }
    public int TotalDismissed { get; set; }
    public int TotalTypedPast { get; set; }
    public int TotalPartialAccepts { get; set; }
    public int TotalNativeCommits { get; set; }
    public int TotalUntouched { get; set; }
    public int WordsAssisted { get; set; }
    public int WordsNative { get; set; }
    public int TotalCorrections { get; set; }
    public float AvgQualityScore { get; set; }
    public double AvgLatencyMs { get; set; }
    public Dictionary<string, CategoryDayStats> CategoryBreakdown { get; set; } = new();
    public int[] HourAcceptDistribution { get; set; } = new int[24];
    public int[] HourDismissDistribution { get; set; } = new int[24];
    public List<ContextDayStats> TopContexts { get; set; } = new();
}

public class CategoryDayStats
{
    public int Accepted { get; set; }
    public int Dismissed { get; set; }
    public int NativeCommits { get; set; }
    public float AvgQuality { get; set; }
    public int Corrections { get; set; }
    public int WordsAssisted { get; set; }
}

public class ContextDayStats
{
    public string ContextKey { get; set; } = "";
    public string ContextLabel { get; set; } = "";
    public string Category { get; set; } = "";
    public int Accepted { get; set; }
    public int Dismissed { get; set; }
    public float AvgQuality { get; set; }
}

public class ScoreSnapshot
{
    public string Date { get; set; } = "";
    public int Score { get; set; }
}

public class WeekSummary
{
    public string WeekStart { get; set; } = "";
    public int TotalAccepted { get; set; }
    public int TotalDismissed { get; set; }
    public int TotalNativeCommits { get; set; }
    public int WordsAssisted { get; set; }
    public float AvgQuality { get; set; }
    public float AcceptanceRate { get; set; }
    public int TotalCorrections { get; set; }
    public string TopCategory { get; set; } = "";
    public float TopCategoryRate { get; set; }
}

public class AchievedMilestone
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string DateAchieved { get; set; } = "";
}

public static class MilestoneDefinitions
{
    public static readonly (string Id, int Threshold, string Field, string Label)[] All =
    {
        ("accept_10",     10,   "accepted", "First steps \u2014 your profile is starting to form"),
        ("accept_50",     50,   "accepted", "Getting personal \u2014 patterns are emerging"),
        ("accept_100",    100,  "accepted", "In sync \u2014 Keystroke is adapting to your voice"),
        ("accept_250",    250,  "accepted", "Deep understanding \u2014 strong context-specific patterns"),
        ("accept_500",    500,  "accepted", "Writing partner \u2014 the system knows your style cold"),
        ("accept_1000",   1000, "accepted", "Veteran \u2014 over a thousand personalized completions"),
        ("native_10",     10,   "native",   "Your own words \u2014 native writing is shaping the profile"),
        ("native_50",     50,   "native",   "Authentic voice \u2014 your manual writing is the strongest signal"),
        ("streak_7",      7,    "streak",   "Week streak \u2014 seven days of active writing"),
        ("streak_30",     30,   "streak",   "Monthly streak \u2014 consistently building your profile"),
    };
}
