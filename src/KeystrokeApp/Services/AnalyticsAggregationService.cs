using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Incrementally aggregates raw learning events into daily rollups, weekly
/// summaries, streak counters, milestones, and extended score history.
/// Persists to %AppData%/Keystroke/analytics-daily.json.
///
/// All public methods are thread-safe. Refresh() is designed to be called
/// on a background thread — the caller should marshal results to the UI.
/// </summary>
public class AnalyticsAggregationService
{
    private readonly string _storePath;
    private readonly string _trackingPath;
    private readonly string _legacyPath;
    private readonly object _lock = new();
    private AnalyticsStore _store = new();

    private const int MaxDailyRollups = 90;
    private const int MaxWeeklySummaries = 12;
    private const int MaxScoreSnapshots = 90;

    public AnalyticsAggregationService(
        string? storePath = null,
        string? trackingPath = null,
        string? legacyPath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");

        _storePath = storePath ?? Path.Combine(appData, "analytics-daily.json");
        _trackingPath = trackingPath ?? Path.Combine(appData, "tracking.jsonl");
        _legacyPath = legacyPath ?? Path.Combine(appData, "completions.jsonl");

        LoadFromDisk();
    }

    /// <summary>Returns a snapshot of the current store. Fast — safe for UI thread.</summary>
    public AnalyticsStore GetStore()
    {
        lock (_lock) { return _store; }
    }

    /// <summary>
    /// Records a score snapshot from LearningScoreService.Recompute().
    /// Called via the ScoreComputed event — safe from any thread.
    /// </summary>
    public void RecordScoreSnapshot(string category, int score)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (!_store.ScoreHistory.TryGetValue(category, out var history))
            {
                history = new List<ScoreSnapshot>();
                _store.ScoreHistory[category] = history;
            }

            // Update today's snapshot if already recorded, otherwise append
            if (history.Count > 0 && history[^1].Date == today)
                history[^1].Score = score;
            else
                history.Add(new ScoreSnapshot { Date = today, Score = score });

            if (history.Count > MaxScoreSnapshots)
                history.RemoveRange(0, history.Count - MaxScoreSnapshots);
        }

        SaveToDisk();
    }

    /// <summary>
    /// Incrementally parses new events and updates all rollups, summaries,
    /// streaks, and milestones. Safe to call from a background thread.
    /// </summary>
    public void Refresh()
    {
        try
        {
            var watermark = _store.LastAggregatedEventTimestamp;
            var newEvents = ParseEventsAfter(_trackingPath, watermark, isLegacy: false);

            // On first run with no watermark, also pull legacy data
            if (watermark == DateTime.MinValue)
            {
                var legacyEvents = ParseEventsAfter(_legacyPath, DateTime.MinValue, isLegacy: true);
                // Merge, dedup by rough timestamp proximity
                newEvents = DeduplicateLegacy(newEvents, legacyEvents);
            }

            if (newEvents.Count == 0)
                return;

            lock (_lock)
            {
                ApplyEvents(newEvents);
                UpdateStreaks();
                CheckMilestones();
                RebuildWeeklySummaries();
                PruneOldRollups();

                var latestTimestamp = newEvents.Max(e => e.TimestampUtc);
                if (latestTimestamp > _store.LastAggregatedEventTimestamp)
                    _store.LastAggregatedEventTimestamp = latestTimestamp;
            }

            SaveToDisk();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Analytics] Refresh failed: {ex.Message}");
        }
    }

    // ── Event parsing ─────────────────────────────────────────────────────

    private sealed class RawEvent
    {
        public DateTime TimestampUtc { get; init; }
        public DateTime TimestampLocal { get; init; }
        public string EventType { get; init; } = "";
        public string Category { get; init; } = "";
        public string ContextKey { get; init; } = "";
        public string ContextLabel { get; init; } = "";
        public string AcceptedText { get; init; } = "";
        public string UserWrittenText { get; init; } = "";
        public float QualityScore { get; init; }
        public int LatencyMs { get; init; }
        public string CorrectionType { get; init; } = "";
    }

    private static List<RawEvent> ParseEventsAfter(string path, DateTime watermark, bool isLegacy)
    {
        var events = new List<RawEvent>();
        if (!File.Exists(path))
            return events;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                if (isLegacy)
                {
                    var entry = JsonSerializer.Deserialize<LegacyEventLine>(line, options);
                    if (entry == null || entry.Timestamp <= watermark)
                        continue;

                    events.Add(new RawEvent
                    {
                        TimestampUtc = entry.Timestamp,
                        TimestampLocal = entry.Timestamp.ToLocalTime(),
                        EventType = entry.Action == "accepted" ? "suggestion_full_accept"
                                  : entry.Action == "dismissed" ? "suggestion_dismiss"
                                  : "",
                        Category = entry.Category ?? "",
                        AcceptedText = entry.Completion ?? "",
                        QualityScore = entry.QualityScore > 0 ? entry.QualityScore : 0.5f,
                        LatencyMs = entry.LatencyMs
                    });
                }
                else
                {
                    var entry = JsonSerializer.Deserialize<LearningEventRecord>(line, options);
                    if (entry == null || entry.TimestampUtc <= watermark)
                        continue;

                    events.Add(new RawEvent
                    {
                        TimestampUtc = entry.TimestampUtc,
                        TimestampLocal = entry.TimestampUtc.ToLocalTime(),
                        EventType = entry.EventType,
                        Category = entry.Category,
                        ContextKey = entry.ContextKeys.SubcontextKey,
                        ContextLabel = entry.ContextKeys.SubcontextLabel,
                        AcceptedText = entry.AcceptedText,
                        UserWrittenText = entry.UserWrittenText,
                        QualityScore = entry.QualityScore,
                        LatencyMs = entry.LatencyMs,
                        CorrectionType = entry.CorrectionType
                    });
                }
            }
            catch { /* skip malformed lines */ }
        }

        return events;
    }

    private static List<RawEvent> DeduplicateLegacy(List<RawEvent> v2Events, List<RawEvent> legacyEvents)
    {
        // Build a set of V2 timestamps for quick dedup
        var v2Timestamps = new HashSet<long>(v2Events.Select(e => e.TimestampUtc.Ticks / TimeSpan.TicksPerSecond));
        var dedupedLegacy = legacyEvents
            .Where(e => !v2Timestamps.Contains(e.TimestampUtc.Ticks / TimeSpan.TicksPerSecond))
            .ToList();

        return v2Events.Concat(dedupedLegacy).OrderBy(e => e.TimestampUtc).ToList();
    }

    // Minimal model for reading legacy completions.jsonl lines
    private sealed class LegacyEventLine
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        public string Completion { get; set; } = "";
        public string Category { get; set; } = "";
        public float QualityScore { get; set; }
        public int LatencyMs { get; set; }
    }

    // ── Core aggregation ──────────────────────────────────────────────────

    private void ApplyEvents(List<RawEvent> events)
    {
        foreach (var evt in events)
        {
            var dateKey = evt.TimestampLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var rollup = GetOrCreateRollup(dateKey);
            var hour = evt.TimestampLocal.Hour;

            switch (evt.EventType)
            {
                case "suggestion_full_accept":
                case "accepted_text_untouched":
                    rollup.TotalAccepted++;
                    _store.CumulativeAccepted++;
                    rollup.WordsAssisted += CountWords(evt.AcceptedText);
                    if (hour >= 0 && hour < 24) rollup.HourAcceptDistribution[hour]++;
                    if (evt.EventType == "accepted_text_untouched")
                        rollup.TotalUntouched++;
                    AddToCategoryStats(rollup, evt.Category, accepted: 1, wordsAssisted: CountWords(evt.AcceptedText));
                    AddToContextStats(rollup, evt);
                    AccumulateQuality(rollup, evt.QualityScore);
                    AccumulateLatency(rollup, evt.LatencyMs);
                    break;

                case "suggestion_partial_accept":
                    rollup.TotalPartialAccepts++;
                    rollup.TotalAccepted++;
                    _store.CumulativeAccepted++;
                    rollup.WordsAssisted += CountWords(evt.AcceptedText);
                    if (hour >= 0 && hour < 24) rollup.HourAcceptDistribution[hour]++;
                    AddToCategoryStats(rollup, evt.Category, accepted: 1, wordsAssisted: CountWords(evt.AcceptedText));
                    AddToContextStats(rollup, evt);
                    AccumulateQuality(rollup, evt.QualityScore);
                    break;

                case "suggestion_dismiss":
                    rollup.TotalDismissed++;
                    if (hour >= 0 && hour < 24) rollup.HourDismissDistribution[hour]++;
                    AddToCategoryStats(rollup, evt.Category, dismissed: 1);
                    break;

                case "suggestion_typed_past":
                    rollup.TotalTypedPast++;
                    rollup.TotalDismissed++;
                    if (hour >= 0 && hour < 24) rollup.HourDismissDistribution[hour]++;
                    AddToCategoryStats(rollup, evt.Category, dismissed: 1);
                    break;

                case "manual_continuation_committed":
                    rollup.TotalNativeCommits++;
                    _store.CumulativeNative++;
                    rollup.WordsNative += CountWords(evt.UserWrittenText);
                    AddToCategoryStats(rollup, evt.Category, nativeCommits: 1);
                    break;
            }

            // Correction tracking
            if (!string.IsNullOrEmpty(evt.CorrectionType) &&
                evt.CorrectionType != "none")
            {
                rollup.TotalCorrections++;
                if (!string.IsNullOrEmpty(evt.Category))
                {
                    var catStats = GetOrCreateCategoryStats(rollup, evt.Category);
                    catStats.Corrections++;
                }
            }
        }
    }

    private AnalyticsDailyRollup GetOrCreateRollup(string dateKey)
    {
        var existing = _store.Rollups.FirstOrDefault(r => r.Date == dateKey);
        if (existing != null)
            return existing;

        var rollup = new AnalyticsDailyRollup { Date = dateKey };
        _store.Rollups.Add(rollup);
        return rollup;
    }

    private static CategoryDayStats GetOrCreateCategoryStats(AnalyticsDailyRollup rollup, string category)
    {
        if (string.IsNullOrEmpty(category))
            return new CategoryDayStats();

        if (!rollup.CategoryBreakdown.TryGetValue(category, out var stats))
        {
            stats = new CategoryDayStats();
            rollup.CategoryBreakdown[category] = stats;
        }
        return stats;
    }

    private static void AddToCategoryStats(AnalyticsDailyRollup rollup, string category,
        int accepted = 0, int dismissed = 0, int nativeCommits = 0, int wordsAssisted = 0)
    {
        if (string.IsNullOrEmpty(category))
            return;

        var stats = GetOrCreateCategoryStats(rollup, category);
        stats.Accepted += accepted;
        stats.Dismissed += dismissed;
        stats.NativeCommits += nativeCommits;
        stats.WordsAssisted += wordsAssisted;
    }

    private static void AddToContextStats(AnalyticsDailyRollup rollup, RawEvent evt)
    {
        if (string.IsNullOrEmpty(evt.ContextKey))
            return;

        var existing = rollup.TopContexts.FirstOrDefault(c => c.ContextKey == evt.ContextKey);
        if (existing == null)
        {
            existing = new ContextDayStats
            {
                ContextKey = evt.ContextKey,
                ContextLabel = evt.ContextLabel,
                Category = evt.Category
            };
            rollup.TopContexts.Add(existing);
        }

        if (evt.EventType.Contains("accept"))
        {
            existing.Accepted++;
            // Running average quality
            int total = existing.Accepted;
            existing.AvgQuality = ((existing.AvgQuality * (total - 1)) + evt.QualityScore) / total;
        }
        else
        {
            existing.Dismissed++;
        }
    }

    // Quality/latency use running averages across the day
    private static void AccumulateQuality(AnalyticsDailyRollup rollup, float quality)
    {
        int count = rollup.TotalAccepted;
        if (count <= 1)
            rollup.AvgQualityScore = quality;
        else
            rollup.AvgQualityScore = ((rollup.AvgQualityScore * (count - 1)) + quality) / count;
    }

    private static void AccumulateLatency(AnalyticsDailyRollup rollup, int latencyMs)
    {
        if (latencyMs < 0) return;
        int count = rollup.TotalAccepted;
        if (count <= 1)
            rollup.AvgLatencyMs = latencyMs;
        else
            rollup.AvgLatencyMs = ((rollup.AvgLatencyMs * (count - 1)) + latencyMs) / count;
    }

    // ── Streaks ───────────────────────────────────────────────────────────

    private void UpdateStreaks()
    {
        var activeDates = _store.Rollups
            .Where(r => r.TotalAccepted + r.TotalNativeCommits > 0)
            .Select(r => r.Date)
            .OrderByDescending(d => d)
            .ToList();

        if (activeDates.Count == 0)
        {
            _store.CurrentStreak = 0;
            _store.StreakAnchorDate = "";
            return;
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Current streak: count consecutive days backwards from today/yesterday
        int streak = 0;
        var checkDate = activeDates[0] == today ? DateTime.Now.Date : DateTime.Now.Date.AddDays(-1);

        // Only start counting if the most recent active day is today or yesterday
        if (activeDates[0] == today || activeDates[0] == yesterday)
        {
            var activeSet = new HashSet<string>(activeDates);
            while (activeSet.Contains(checkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            {
                streak++;
                checkDate = checkDate.AddDays(-1);
            }
        }

        _store.CurrentStreak = streak;
        _store.StreakAnchorDate = activeDates[0];
        if (streak > _store.LongestStreak)
            _store.LongestStreak = streak;
    }

    // ── Milestones ────────────────────────────────────────────────────────

    private void CheckMilestones()
    {
        var achieved = new HashSet<string>(_store.AchievedMilestones.Select(m => m.Id));
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var (id, threshold, field, label) in MilestoneDefinitions.All)
        {
            if (achieved.Contains(id))
                continue;

            int current = field switch
            {
                "accepted" => _store.CumulativeAccepted,
                "native" => _store.CumulativeNative,
                "streak" => _store.LongestStreak,
                _ => 0
            };

            if (current >= threshold)
            {
                _store.AchievedMilestones.Add(new AchievedMilestone
                {
                    Id = id,
                    Label = label,
                    DateAchieved = today
                });
            }
        }
    }

    // ── Weekly summaries ──────────────────────────────────────────────────

    private void RebuildWeeklySummaries()
    {
        // Group rollups by ISO week start (Monday)
        var weekGroups = _store.Rollups
            .Where(r => DateTime.TryParseExact(r.Date, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .GroupBy(r =>
            {
                var date = DateTime.ParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
                return date.AddDays(-daysSinceMonday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            })
            .OrderByDescending(g => g.Key)
            .Take(MaxWeeklySummaries);

        _store.WeeklySummaries = weekGroups.Select(g =>
        {
            int totalAccepted = g.Sum(r => r.TotalAccepted);
            int totalDismissed = g.Sum(r => r.TotalDismissed);
            int totalShown = totalAccepted + totalDismissed;
            int totalNative = g.Sum(r => r.TotalNativeCommits);

            // Find top category by accepted count
            var catTotals = new Dictionary<string, (int Accepted, int Dismissed)>();
            foreach (var rollup in g)
            {
                foreach (var (cat, stats) in rollup.CategoryBreakdown)
                {
                    if (!catTotals.TryGetValue(cat, out var existing))
                        existing = (0, 0);
                    catTotals[cat] = (existing.Accepted + stats.Accepted, existing.Dismissed + stats.Dismissed);
                }
            }

            var topCat = catTotals
                .OrderByDescending(c => c.Value.Accepted)
                .FirstOrDefault();

            float topCatRate = 0f;
            if (topCat.Value.Accepted + topCat.Value.Dismissed > 0)
                topCatRate = (float)topCat.Value.Accepted / (topCat.Value.Accepted + topCat.Value.Dismissed);

            // Weighted average quality across days
            float avgQuality = 0f;
            int qualityDays = 0;
            foreach (var r in g.Where(r => r.TotalAccepted > 0))
            {
                avgQuality += r.AvgQualityScore * r.TotalAccepted;
                qualityDays += r.TotalAccepted;
            }
            if (qualityDays > 0) avgQuality /= qualityDays;

            return new WeekSummary
            {
                WeekStart = g.Key,
                TotalAccepted = totalAccepted,
                TotalDismissed = totalDismissed,
                TotalNativeCommits = totalNative,
                WordsAssisted = g.Sum(r => r.WordsAssisted),
                AvgQuality = MathF.Round(avgQuality, 3),
                AcceptanceRate = totalShown > 0 ? MathF.Round((float)totalAccepted / totalShown, 3) : 0f,
                TotalCorrections = g.Sum(r => r.TotalCorrections),
                TopCategory = topCat.Key ?? "",
                TopCategoryRate = MathF.Round(topCatRate, 3)
            };
        }).ToList();
    }

    // ── Pruning ───────────────────────────────────────────────────────────

    private void PruneOldRollups()
    {
        if (_store.Rollups.Count <= MaxDailyRollups)
            return;

        _store.Rollups = _store.Rollups
            .OrderByDescending(r => r.Date)
            .Take(MaxDailyRollups)
            .ToList();
    }

    // ── Query helpers (called from UI code-behind) ────────────────────────

    /// <summary>Gets the current week's summary, or null if no data.</summary>
    public WeekSummary? GetCurrentWeekSummary()
    {
        var today = DateTime.Now;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            return _store.WeeklySummaries.FirstOrDefault(w => w.WeekStart == weekStart);
        }
    }

    /// <summary>Gets last week's summary, or null if no data.</summary>
    public WeekSummary? GetPreviousWeekSummary()
    {
        var today = DateTime.Now;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var prevWeekStart = today.AddDays(-daysSinceMonday - 7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            return _store.WeeklySummaries.FirstOrDefault(w => w.WeekStart == prevWeekStart);
        }
    }

    /// <summary>Gets the last N daily rollups, most recent first.</summary>
    public List<AnalyticsDailyRollup> GetRecentRollups(int days = 30)
    {
        lock (_lock)
        {
            return _store.Rollups
                .OrderByDescending(r => r.Date)
                .Take(days)
                .Reverse()
                .ToList();
        }
    }

    /// <summary>Gets score history for a category (up to 90 snapshots).</summary>
    public List<ScoreSnapshot> GetScoreHistory(string category)
    {
        lock (_lock)
        {
            return _store.ScoreHistory.TryGetValue(category, out var history)
                ? new List<ScoreSnapshot>(history)
                : new List<ScoreSnapshot>();
        }
    }

    /// <summary>Gets all achieved milestones.</summary>
    public List<AchievedMilestone> GetMilestones()
    {
        lock (_lock)
        {
            return new List<AchievedMilestone>(_store.AchievedMilestones);
        }
    }

    /// <summary>Gets the next unachieved milestone and current progress toward it.</summary>
    public (string Id, string Label, int Current, int Threshold)? GetNextMilestone()
    {
        lock (_lock)
        {
            var achieved = new HashSet<string>(_store.AchievedMilestones.Select(m => m.Id));
            foreach (var (id, threshold, field, label) in MilestoneDefinitions.All)
            {
                if (achieved.Contains(id)) continue;

                int current = field switch
                {
                    "accepted" => _store.CumulativeAccepted,
                    "native" => _store.CumulativeNative,
                    "streak" => _store.LongestStreak,
                    _ => 0
                };

                return (id, label, current, threshold);
            }
            return null;
        }
    }

    /// <summary>
    /// Computes a weighted average score across all categories from the latest
    /// score snapshots. Returns 0 if no score data exists.
    /// </summary>
    public int GetWeightedAverageScore()
    {
        lock (_lock)
        {
            if (_store.ScoreHistory.Count == 0) return 0;

            double total = 0;
            int count = 0;
            foreach (var (_, history) in _store.ScoreHistory)
            {
                if (history.Count > 0)
                {
                    total += history[^1].Score;
                    count++;
                }
            }

            return count > 0 ? (int)Math.Round(total / count) : 0;
        }
    }

    /// <summary>Gets the weighted average score from last week's final snapshots.</summary>
    public int GetPreviousWeekAverageScore()
    {
        var today = DateTime.Now;
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var prevWeekEnd = today.AddDays(-daysSinceMonday - 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prevWeekStart = today.AddDays(-daysSinceMonday - 7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_lock)
        {
            if (_store.ScoreHistory.Count == 0) return 0;

            double total = 0;
            int count = 0;
            foreach (var (_, history) in _store.ScoreHistory)
            {
                var snapshot = history
                    .Where(s => string.Compare(s.Date, prevWeekStart, StringComparison.Ordinal) >= 0
                             && string.Compare(s.Date, prevWeekEnd, StringComparison.Ordinal) <= 0)
                    .OrderByDescending(s => s.Date)
                    .FirstOrDefault();

                if (snapshot != null)
                {
                    total += snapshot.Score;
                    count++;
                }
            }

            return count > 0 ? (int)Math.Round(total / count) : 0;
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ── Persistence ───────────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);
            var loaded = JsonSerializer.Deserialize<AnalyticsStore>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded != null)
            {
                lock (_lock) { _store = loaded; }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Analytics] Load failed: {ex.Message}");
        }
    }

    private void SaveToDisk()
    {
        try
        {
            AnalyticsStore snapshot;
            lock (_lock) { snapshot = _store; }

            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var json = JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _storePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Analytics] Save failed: {ex.Message}");
        }
    }
}
