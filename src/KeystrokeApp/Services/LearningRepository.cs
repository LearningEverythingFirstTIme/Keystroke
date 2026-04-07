using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

public sealed class LearningRepository
{
    private readonly string _legacyPath;
    private readonly string _eventPath;
    private readonly ContextFingerprintService _fingerprints;
    private readonly LearningContextPreferencesService _preferences;
    private readonly object _lock = new();
    private LearningCorpusSnapshot _snapshot = new();
    private long _legacySize;
    private long _eventSize;
    private long _preferencesSize;
    private DateTime _legacyWriteUtc;
    private DateTime _eventWriteUtc;
    private DateTime _preferencesWriteUtc;

    public LearningRepository(
        ContextFingerprintService? fingerprints = null,
        LearningContextPreferencesService? preferences = null,
        string? legacyPath = null,
        string? eventPath = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");

        _legacyPath = legacyPath ?? Path.Combine(appData, "completions.jsonl");
        _eventPath = eventPath ?? Path.Combine(appData, "learning-events.v2.jsonl");
        _fingerprints = fingerprints ?? new ContextFingerprintService();
        _preferences = preferences ?? new LearningContextPreferencesService();
    }

    public LearningCorpusSnapshot GetSnapshot(bool forceRefresh = false)
    {
        if (forceRefresh || HasChanged())
            Refresh();

        lock (_lock)
        {
            return _snapshot;
        }
    }

    public void Refresh()
    {
        var allPositives = new List<LearningEvidence>();
        var allNegatives = new List<LearningEvidence>();
        var eventDualWriteIndex = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        int dedupedLegacyCount = 0;

        LoadEventEvidence(allPositives, allNegatives, eventDualWriteIndex);
        LoadLegacyEvidence(allPositives, allNegatives, eventDualWriteIndex, ref dedupedLegacyCount);

        var preferences = _preferences.GetSnapshot(forceRefresh: true);
        var contexts = BuildContextSummaries(allPositives, allNegatives, preferences);

        var filteredPositives = allPositives
            .Where(e => !preferences.DisabledContextKeys.Contains(e.SubcontextKey))
            .OrderByDescending(e => e.TimestampUtc)
            .ToList();
        var filteredNegatives = allNegatives
            .Where(e => !preferences.DisabledContextKeys.Contains(e.SubcontextKey))
            .OrderByDescending(e => e.TimestampUtc)
            .ToList();

        var snapshot = new LearningCorpusSnapshot
        {
            PositiveEvidence = filteredPositives,
            NegativeEvidence = filteredNegatives,
            Contexts = contexts,
            LastActivity = allPositives.Concat(allNegatives)
                .OrderByDescending(e => e.TimestampUtc)
                .Select(e => (DateTime?)e.TimestampUtc)
                .FirstOrDefault(),
            PinnedContextKeys = preferences.PinnedContextKeys,
            DisabledContextKeys = preferences.DisabledContextKeys,
            LegacyEvidenceCount = allPositives.Count(e => e.SourceType == LearningSourceType.LegacyAccepted) +
                allNegatives.Count(e => e.SourceType == LearningSourceType.LegacyDismissed),
            EventEvidenceCount = allPositives.Count(e => e.SourceType != LearningSourceType.LegacyAccepted) +
                allNegatives.Count(e => e.SourceType != LearningSourceType.LegacyDismissed),
            DedupedLegacyCount = dedupedLegacyCount
        };

        lock (_lock)
        {
            _snapshot = snapshot;
        }
    }

    private Dictionary<string, LearningContextSummary> BuildContextSummaries(
        List<LearningEvidence> positives,
        List<LearningEvidence> negatives,
        LearningContextPreferencesSnapshot preferences)
    {
        var summaries = positives
            .Concat(negatives)
            .Where(e => !string.IsNullOrWhiteSpace(e.SubcontextKey))
            .GroupBy(e => e.SubcontextKey)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.TimestampUtc).FirstOrDefault();
                if (latest == null) return null!;
                int nativeCount = g.Count(x => !x.IsNegative && x.SourceType == LearningSourceType.NativeWriting);
                int assistCount = g.Count(x => !x.IsNegative && x.SourceType != LearningSourceType.NativeWriting && x.SourceType != LearningSourceType.LegacyAccepted);
                int legacyCount = g.Count(x => !x.IsNegative && x.SourceType == LearningSourceType.LegacyAccepted);
                int negativeCount = g.Count(x => x.IsNegative);
                float avgQuality = g.Where(x => !x.IsNegative).DefaultIfEmpty().Average(x => x?.QualityScore ?? 0.5f);
                int totalPositive = nativeCount + assistCount + legacyCount;
                float matchRate = totalPositive + negativeCount > 0
                    ? (float)totalPositive / (totalPositive + negativeCount)
                    : 0f;
                double confidence = Math.Clamp(
                    (nativeCount * 0.25) +
                    (assistCount * 0.08) +
                    (legacyCount * 0.03) +
                    (avgQuality * 0.25) +
                    (matchRate * 0.2) +
                    (latest.ContextConfidence * 0.2),
                    0,
                    1);

                var preference = preferences.Items.GetValueOrDefault(g.Key);
                return new LearningContextSummary
                {
                    ContextKey = g.Key,
                    ContextLabel = latest.SubcontextLabel,
                    Category = latest.Category,
                    NativeCount = nativeCount,
                    AssistCount = assistCount,
                    LegacyCount = legacyCount,
                    NegativeCount = negativeCount,
                    AverageQuality = MathF.Round(avgQuality, 3),
                    Confidence = Math.Round(confidence, 3),
                    LastActivity = latest.TimestampUtc,
                    MatchRate = MathF.Round(matchRate, 3),
                    IsPinned = preference?.IsPinned == true,
                    IsDisabled = preference?.IsDisabled == true
                };
            })
            .Where(c => c != null)
            .OrderByDescending(c => c.IsPinned)
            .ThenBy(c => c.IsDisabled)
            .ThenByDescending(c => c.Confidence)
            .ThenByDescending(c => c.LastActivity)
            .ToDictionary(c => c.ContextKey, c => c);

        foreach (var preference in preferences.Items.Values)
        {
            if (summaries.ContainsKey(preference.ContextKey))
                continue;

            summaries[preference.ContextKey] = new LearningContextSummary
            {
                ContextKey = preference.ContextKey,
                ContextLabel = preference.Label,
                Category = preference.Category,
                LastActivity = preference.UpdatedAt,
                IsPinned = preference.IsPinned,
                IsDisabled = preference.IsDisabled
            };
        }

        return summaries
            .Values
            .OrderByDescending(c => c.IsPinned)
            .ThenBy(c => c.IsDisabled)
            .ThenByDescending(c => c.Confidence)
            .ThenByDescending(c => c.LastActivity)
            .ToDictionary(c => c.ContextKey, c => c);
    }

    private bool HasChanged()
    {
        var legacyChanged = HasFileChanged(_legacyPath, ref _legacySize, ref _legacyWriteUtc);
        var eventChanged = HasFileChanged(_eventPath, ref _eventSize, ref _eventWriteUtc);
        var preferencesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke",
            "learning-context-preferences.json");
        var preferenceChanged = HasFileChanged(preferencesPath, ref _preferencesSize, ref _preferencesWriteUtc);
        return legacyChanged || eventChanged || preferenceChanged;
    }

    private static bool HasFileChanged(string path, ref long previousSize, ref DateTime previousWriteUtc)
    {
        if (!File.Exists(path))
        {
            var changed = previousSize != 0 || previousWriteUtc != DateTime.MinValue;
            previousSize = 0;
            previousWriteUtc = DateTime.MinValue;
            return changed;
        }

        var info = new FileInfo(path);
        if (info.Length != previousSize || info.LastWriteTimeUtc != previousWriteUtc)
        {
            previousSize = info.Length;
            previousWriteUtc = info.LastWriteTimeUtc;
            return true;
        }

        return false;
    }

    private void LoadLegacyEvidence(
        List<LearningEvidence> positives,
        List<LearningEvidence> negatives,
        Dictionary<string, List<DateTime>> dualWriteIndex,
        ref int dedupedLegacyCount)
    {
        if (!File.Exists(_legacyPath))
            return;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var line in File.ReadLines(_legacyPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<LegacyCompletionRecord>(line, options);
                if (entry == null || string.IsNullOrWhiteSpace(entry.Completion))
                    continue;

                var fp = _fingerprints.Create(entry.App, entry.Window);
                var evidence = new LearningEvidence
                {
                    TimestampUtc = entry.Timestamp,
                    Prefix = entry.Prefix ?? "",
                    Completion = entry.Completion ?? "",
                    ProcessName = entry.App ?? "",
                    Category = string.IsNullOrWhiteSpace(entry.Category) ? fp.Category : entry.Category,
                    SafeContextLabel = fp.SafeContextLabel,
                    ProcessKey = fp.ProcessKey,
                    WindowKey = fp.WindowKey,
                    SubcontextKey = fp.SubcontextKey,
                    ProcessLabel = fp.ProcessLabel,
                    WindowLabel = fp.WindowLabel,
                    SubcontextLabel = fp.SubcontextLabel,
                    QualityScore = entry.QualityScore <= 0 ? 0.5f : entry.QualityScore,
                    SourceWeight = entry.Action == "accepted"
                        ? (entry.EditedAfter ? 0.35f : 0.5f)
                        : 1.0f,
                    IsNegative = entry.Action == "dismissed",
                    SourceType = entry.Action == "dismissed"
                        ? LearningSourceType.LegacyDismissed
                        : LearningSourceType.LegacyAccepted,
                    WasUntouched = entry.Action == "accepted" && !entry.EditedAfter,
                    ContextConfidence = fp.Confidence
                };

                if (IsCoveredByEvent(evidence, dualWriteIndex))
                {
                    dedupedLegacyCount++;
                    continue;
                }

                if (evidence.IsNegative)
                    negatives.Add(evidence);
                else if (evidence.Completion.Length >= 3)
                    positives.Add(evidence);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LearningRepo] Skipping malformed legacy line: {ex.Message}");
            }
        }
    }

    private void LoadEventEvidence(
        List<LearningEvidence> positives,
        List<LearningEvidence> negatives,
        Dictionary<string, List<DateTime>> dualWriteIndex)
    {
        if (!File.Exists(_eventPath))
            return;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var records = new List<LearningEventRecord>();
        foreach (var line in File.ReadLines(_eventPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<LearningEventRecord>(line, options);
                if (entry == null)
                    continue;
                records.Add(entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LearningRepo] Skipping malformed event line: {ex.Message}");
            }
        }

        var untouchedKeys = records
            .Where(r => r.EventType == "accepted_text_untouched")
            .Select(BuildEventIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in records)
        {
            if (entry.EventType == "suggestion_full_accept" &&
                untouchedKeys.Contains(BuildEventIdentityKey(entry)))
            {
                continue;
            }

            var completion = entry.EventType switch
            {
                "manual_continuation_committed" => entry.UserWrittenText,
                "accepted_text_untouched" => entry.AcceptedText,
                "suggestion_full_accept" => entry.AcceptedText,
                "suggestion_partial_accept" => entry.AcceptedText,
                "suggestion_dismiss" => entry.ShownCompletion,
                "suggestion_typed_past" => entry.ShownCompletion,
                _ => ""
            };

            if (string.IsNullOrWhiteSpace(completion))
                continue;

            var evidence = new LearningEvidence
            {
                TimestampUtc = entry.TimestampUtc,
                Prefix = entry.TypedPrefix ?? "",
                Completion = completion,
                ProcessName = entry.ProcessName ?? "",
                Category = entry.Category,
                SafeContextLabel = entry.SafeContextLabel,
                ProcessKey = entry.ContextKeys.ProcessKey,
                WindowKey = entry.ContextKeys.WindowKey,
                SubcontextKey = entry.ContextKeys.SubcontextKey,
                ProcessLabel = entry.ContextKeys.ProcessLabel,
                WindowLabel = entry.ContextKeys.WindowLabel,
                SubcontextLabel = entry.ContextKeys.SubcontextLabel,
                QualityScore = entry.QualityScore,
                SourceWeight = entry.SourceWeight,
                IsNegative = entry.EventType is "suggestion_dismiss" or "suggestion_typed_past",
                WasUntouched = entry.EventType == "accepted_text_untouched" || entry.UntouchedForMs > 0,
                SourceType = entry.EventType switch
                {
                    "manual_continuation_committed" => LearningSourceType.NativeWriting,
                    "accepted_text_untouched" => LearningSourceType.AssistAcceptedUntouched,
                    "suggestion_partial_accept" => LearningSourceType.AssistPartial,
                    "suggestion_typed_past" => LearningSourceType.TypedPast,
                    "suggestion_dismiss" => LearningSourceType.Dismissed,
                    _ => LearningSourceType.AssistAccepted
                },
                ContextConfidence = entry.Confidence
            };

            if (IsDualWriteCandidate(evidence))
                AddDualWriteSignature(dualWriteIndex, evidence);

            if (evidence.IsNegative)
                negatives.Add(evidence);
            else if (evidence.Completion.Length >= 3)
                positives.Add(evidence);
        }
    }

    private static bool IsCoveredByEvent(LearningEvidence evidence, Dictionary<string, List<DateTime>> dualWriteIndex)
    {
        if (!IsDualWriteCandidate(evidence))
            return false;

        var key = BuildDualWriteKey(evidence);
        if (!dualWriteIndex.TryGetValue(key, out var timestamps))
            return false;

        return timestamps.Any(ts => Math.Abs((ts - evidence.TimestampUtc).TotalSeconds) <= 5);
    }

    private static bool IsDualWriteCandidate(LearningEvidence evidence)
    {
        return evidence.SourceType is
            LearningSourceType.LegacyAccepted or
            LearningSourceType.LegacyDismissed or
            LearningSourceType.AssistAccepted or
            LearningSourceType.AssistAcceptedUntouched or
            LearningSourceType.Dismissed;
    }

    private static void AddDualWriteSignature(
        Dictionary<string, List<DateTime>> dualWriteIndex,
        LearningEvidence evidence)
    {
        var key = BuildDualWriteKey(evidence);
        if (!dualWriteIndex.TryGetValue(key, out var times))
        {
            times = [];
            dualWriteIndex[key] = times;
        }

        times.Add(evidence.TimestampUtc);
    }

    private static string BuildDualWriteKey(LearningEvidence evidence)
    {
        static string Normalize(string value) =>
            value.Trim().Replace("\r", "").Replace("\n", " ").ToLowerInvariant();

        return string.Join("|",
            evidence.IsNegative ? "neg" : "pos",
            Normalize(evidence.ProcessName),
            Normalize(evidence.Category),
            Normalize(evidence.Prefix),
            Normalize(evidence.Completion));
    }

    private static string BuildEventIdentityKey(LearningEventRecord record)
    {
        return string.Join("|",
            record.SuggestionId,
            record.RequestId,
            record.ContextKeys.SubcontextKey,
            record.TypedPrefix?.Trim() ?? "",
            record.AcceptedText?.Trim() ?? "");
    }

    private sealed class LegacyCompletionRecord
    {
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string Completion { get; set; } = "";
        public string App { get; set; } = "";
        public string Window { get; set; } = "";
        public string Category { get; set; } = "";
        public int LatencyMs { get; set; } = -1;
        public int CycleDepth { get; set; }
        public bool EditedAfter { get; set; }
        public float QualityScore { get; set; } = 0.5f;
    }
}

public sealed class LearningCorpusSnapshot
{
    public List<LearningEvidence> PositiveEvidence { get; init; } = new();
    public List<LearningEvidence> NegativeEvidence { get; init; } = new();
    public Dictionary<string, LearningContextSummary> Contexts { get; init; } = new();
    public HashSet<string> PinnedContextKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledContextKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? LastActivity { get; init; }
    public int LegacyEvidenceCount { get; init; }
    public int EventEvidenceCount { get; init; }
    public int DedupedLegacyCount { get; init; }
}

public sealed class LearningEvidence
{
    public DateTime TimestampUtc { get; init; }
    public string Prefix { get; init; } = "";
    public string Completion { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string Category { get; init; } = "";
    public string SafeContextLabel { get; init; } = "";
    public string ProcessKey { get; init; } = "";
    public string WindowKey { get; init; } = "";
    public string SubcontextKey { get; init; } = "";
    public string ProcessLabel { get; init; } = "";
    public string WindowLabel { get; init; } = "";
    public string SubcontextLabel { get; init; } = "";
    public float QualityScore { get; init; }
    public float SourceWeight { get; init; }
    public bool IsNegative { get; init; }
    public bool WasUntouched { get; init; }
    public LearningSourceType SourceType { get; init; }
    public double ContextConfidence { get; init; }
}

public enum LearningSourceType
{
    LegacyAccepted,
    LegacyDismissed,
    AssistAccepted,
    AssistAcceptedUntouched,
    AssistPartial,
    NativeWriting,
    Dismissed,
    TypedPast
}

public sealed class LearningContextSummary
{
    public string ContextKey { get; init; } = "";
    public string ContextLabel { get; init; } = "";
    public string Category { get; init; } = "";
    public int NativeCount { get; init; }
    public int AssistCount { get; init; }
    public int LegacyCount { get; init; }
    public int NegativeCount { get; init; }
    public float AverageQuality { get; init; }
    public double Confidence { get; init; }
    public float MatchRate { get; init; }
    public DateTime LastActivity { get; init; }
    public bool IsPinned { get; init; }
    public bool IsDisabled { get; init; }
}
