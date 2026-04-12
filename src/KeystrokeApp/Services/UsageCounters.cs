using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

public sealed class UsageCounters
{
    public const int DailyFreeLimit = 50;

    private readonly string _dataPath;
    private readonly Func<DateOnly> _todayProvider;
    private readonly object _lock = new();
    private readonly HashSet<string> _countedSuggestionIds = new(StringComparer.Ordinal);
    private UsageCountersState _state;

    public UsageCounters(string? dataPath = null, Func<DateOnly>? todayProvider = null)
    {
        _dataPath = dataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke",
            "usage.json");
        _todayProvider = todayProvider ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _state = LoadState();
        RefreshDailyRolloverCore();
    }

    public UsageCountersSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            RefreshDailyRolloverCore();
            return CreateSnapshot();
        }
    }

    public bool CanRequestPrediction(bool limitEnabled, bool personalizedAiEnabled)
    {
        var snapshot = GetSnapshot();
        return !limitEnabled || personalizedAiEnabled || !snapshot.IsDailyLimitReached;
    }

    public UsageAcceptanceResult RecordAcceptedSuggestion(string? suggestionId)
    {
        lock (_lock)
        {
            RefreshDailyRolloverCore();

            if (!string.IsNullOrWhiteSpace(suggestionId) && !_countedSuggestionIds.Add(suggestionId))
                return new UsageAcceptanceResult(false, CreateSnapshot());

            _state.TotalAcceptedSuggestions++;
            _state.DailyAcceptedSuggestions++;
            SaveStateCore();
            return new UsageAcceptanceResult(true, CreateSnapshot());
        }
    }

    public bool MarkLearningNudgeShown()
    {
        lock (_lock)
        {
            if (_state.LearningNudgeShown)
                return false;

            _state.LearningNudgeShown = true;
            SaveStateCore();
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _countedSuggestionIds.Clear();
            _state = new UsageCountersState
            {
                DailyAcceptedDateLocal = _todayProvider()
            };
            SaveStateCore();
        }
    }

    private UsageCountersState LoadState()
    {
        try
        {
            if (!File.Exists(_dataPath))
                return new UsageCountersState
                {
                    DailyAcceptedDateLocal = _todayProvider()
                };

            var json = File.ReadAllText(_dataPath);
            return JsonSerializer.Deserialize<UsageCountersState>(json) ?? new UsageCountersState
            {
                DailyAcceptedDateLocal = _todayProvider()
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UsageCounters] Load failed: {ex.Message}");
            return new UsageCountersState
            {
                DailyAcceptedDateLocal = _todayProvider()
            };
        }
    }

    private void RefreshDailyRolloverCore()
    {
        var today = _todayProvider();
        if (_state.DailyAcceptedDateLocal == today)
            return;

        _state.DailyAcceptedDateLocal = today;
        _state.DailyAcceptedSuggestions = 0;
        _countedSuggestionIds.Clear(); // Old IDs are irrelevant for the new day
        SaveStateCore();
    }

    private UsageCountersSnapshot CreateSnapshot()
    {
        return new UsageCountersSnapshot(
            _state.TotalAcceptedSuggestions,
            _state.DailyAcceptedSuggestions,
            _state.DailyAcceptedDateLocal,
            _state.LearningNudgeShown);
    }

    private void SaveStateCore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _dataPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _dataPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Usage counter persistence must never crash the app.
            System.Diagnostics.Debug.WriteLine($"[UsageCounters] Save failed: {ex.Message}");
        }
    }
}

public sealed record UsageCountersSnapshot(
    int TotalAcceptedSuggestions,
    int DailyAcceptedSuggestions,
    DateOnly DailyAcceptedDateLocal,
    bool LearningNudgeShown)
{
    public int RemainingFreeSuggestions => Math.Max(0, UsageCounters.DailyFreeLimit - DailyAcceptedSuggestions);
    public bool IsDailyLimitReached => DailyAcceptedSuggestions >= UsageCounters.DailyFreeLimit;
}

public sealed record UsageAcceptanceResult(bool Counted, UsageCountersSnapshot Snapshot);

public sealed class UsageCountersState
{
    public int TotalAcceptedSuggestions { get; set; }
    public int DailyAcceptedSuggestions { get; set; }
    public DateOnly DailyAcceptedDateLocal { get; set; }
    public bool LearningNudgeShown { get; set; }
}
