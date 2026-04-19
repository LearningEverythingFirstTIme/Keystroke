using System.Diagnostics;

namespace KeystrokeApp.Services;

public sealed class LearningRepository
{
    private readonly LearningDatabase? _database;
    private readonly LearningContextPreferencesService _preferences;
    private readonly object _lock = new();
    private LearningCorpusSnapshot _snapshot = new();
    private long _lastWriteVersion = -1;

    public LearningRepository(
        LearningContextPreferencesService preferences,
        LearningDatabase? database = null,
        ContextFingerprintService? fingerprints = null)
    {
        _database = database;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
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

        LoadEvidence(allPositives, allNegatives);

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
            EventEvidenceCount = allPositives.Count + allNegatives.Count
        };

        lock (_lock)
        {
            _snapshot = snapshot;
        }
    }

    private void LoadEvidence(List<LearningEvidence> positives, List<LearningEvidence> negatives)
    {
        if (_database == null) return;

        var records = _database.GetAllEvents();

        // Build set of untouched keys for dedup (same logic as before)
        var untouchedKeys = records
            .Where(r => r.EventType == "accepted_text_untouched")
            .Select(BuildEventIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in records)
        {
            // Skip full_accept if an untouched confirmation exists for the same suggestion
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

            if (evidence.IsNegative)
                negatives.Add(evidence);
            else if (evidence.Completion.Length >= 3)
                positives.Add(evidence);
        }
    }

    private bool HasChanged()
    {
        if (_database == null) return false;

        var currentVersion = _database.WriteVersion;
        if (currentVersion != _lastWriteVersion)
        {
            _lastWriteVersion = currentVersion;
            return true;
        }

        return false;
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

    private static string BuildEventIdentityKey(LearningEventRecord record)
    {
        return string.Join("|",
            record.SuggestionId,
            record.RequestId,
            record.ContextKeys.SubcontextKey,
            record.TypedPrefix?.Trim() ?? "",
            record.AcceptedText?.Trim() ?? "");
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
    public int EventEvidenceCount { get; init; }
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
