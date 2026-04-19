using System.IO;
using System.Text;

namespace KeystrokeApp.Services;

public class AcceptanceLearningService
{
    private static readonly OutboundPrivacyService OutboundPrivacy = new();
    private readonly LearningRepository _repository;
    private readonly LearningRetrievalService _retrieval;
    private readonly LearningContextPreferencesService _preferences;
    private readonly string _logPath;
    private readonly string _dataFilePath;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    private const int RefreshIntervalSeconds = 5;
    private const int SessionMaxItems = 8;
    private const int SessionWindowMinutes = 15;
    private const int SessionHintMinItems = 2;
    private const int SessionHintMaxItems = 5;
    private const float SessionMinQuality = 0.45f;

    private readonly Queue<SessionAccept> _sessionBuffer = new();
    private readonly object _sessionLock = new();

    public AcceptanceLearningService(
        LearningRepository repository,
        LearningRetrievalService retrieval,
        LearningContextPreferencesService preferences)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _retrieval = retrieval ?? throw new ArgumentNullException(nameof(retrieval));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _logPath = Path.Combine(appData, "learning.log");
        _dataFilePath = Path.Combine(appData, "learning.db");
    }

    public List<FewShotExample> GetExamples(ContextSnapshot context, int count = 3)
    {
        RefreshIfNeeded();

        int requested = GetAdaptiveExampleCount(count);
        if (context.TypedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3)
            requested = Math.Min(1, requested);

        var snapshot = _repository.GetSnapshot();
        return _retrieval.GetCandidates(snapshot, context, negatives: false, requested)
            .Select(candidate => new FewShotExample
            {
                Prefix = candidate.Evidence.Prefix,
                Completion = candidate.Evidence.Completion,
                Context = $"{candidate.Evidence.SubcontextLabel} ({candidate.Evidence.Category})",
                SourceType = candidate.Evidence.SourceType.ToString(),
                ContextMatchLevel = candidate.ContextMatchLevel,
                Confidence = candidate.Confidence,
                WasUntouched = candidate.Evidence.WasUntouched
            })
            .Select(OutboundPrivacy.SanitizeFewShotExample)
            .Where(e => !string.IsNullOrWhiteSpace(e.Completion))
            .ToList();
    }

    public List<FewShotExample> GetNegativeExamples(ContextSnapshot context, int count = 2)
    {
        RefreshIfNeeded();

        var snapshot = _repository.GetSnapshot();
        return _retrieval.GetCandidates(snapshot, context, negatives: true, count)
            .Select(candidate => new FewShotExample
            {
                Prefix = candidate.Evidence.Prefix,
                Completion = candidate.Evidence.Completion,
                Context = $"{candidate.Evidence.SubcontextLabel} ({candidate.Evidence.Category})",
                IsNegative = true,
                SourceType = candidate.Evidence.SourceType.ToString(),
                ContextMatchLevel = candidate.ContextMatchLevel,
                Confidence = candidate.Confidence,
                WasUntouched = candidate.Evidence.WasUntouched
            })
            .Select(OutboundPrivacy.SanitizeFewShotExample)
            .Where(e => !string.IsNullOrWhiteSpace(e.Completion))
            .ToList();
    }

    public void AddToSession(string prefix, string completion, string contextKey, float qualityScore)
    {
        if (string.IsNullOrWhiteSpace(completion) || qualityScore < SessionMinQuality)
            return;

        if (_preferences.IsDisabled(contextKey))
            return;

        lock (_sessionLock)
        {
            PruneSessionBuffer();
            _sessionBuffer.Enqueue(new SessionAccept
            {
                Timestamp = DateTime.UtcNow,
                Prefix = PiiFilter.Scrub(prefix) ?? "",
                Completion = PiiFilter.Scrub(completion) ?? "",
                ContextKey = contextKey
            });
            while (_sessionBuffer.Count > SessionMaxItems)
                _sessionBuffer.Dequeue();
        }
    }

    public string? GetSessionModeHint(string? subcontextKey = null, string? category = null)
    {
        if (!string.IsNullOrWhiteSpace(subcontextKey) && _preferences.IsDisabled(subcontextKey))
            return null;

        lock (_sessionLock)
        {
            PruneSessionBuffer();

            List<SessionAccept> items = [];
            if (!string.IsNullOrWhiteSpace(subcontextKey))
            {
                items = _sessionBuffer
                    .Where(s => string.Equals(s.ContextKey, subcontextKey, StringComparison.OrdinalIgnoreCase))
                    .TakeLast(SessionHintMaxItems)
                    .ToList();
            }

            if (items.Count < SessionHintMinItems && !string.IsNullOrWhiteSpace(category))
            {
                items = _sessionBuffer
                    .Where(s => string.Equals(s.ContextKey, category, StringComparison.OrdinalIgnoreCase))
                    .TakeLast(SessionHintMaxItems)
                    .ToList();
            }

            if (items.Count < SessionHintMinItems)
                items = _sessionBuffer.TakeLast(SessionHintMaxItems).ToList();

            return items.Count >= SessionHintMinItems ? FormatSessionHint(items) : null;
        }
    }

    public LearningStats GetStats()
    {
        RefreshIfNeeded();
        var snapshot = _repository.GetSnapshot();
        var dataFileInfo = new FileInfo(_dataFilePath);

        var byCategory = snapshot.PositiveEvidence
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        var negativesByCategory = snapshot.NegativeEvidence
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        var avgQualityByCategory = snapshot.PositiveEvidence
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => (float)g.Average(x => x.QualityScore));

        var overallAvgQuality = snapshot.PositiveEvidence.Count > 0
            ? (float)snapshot.PositiveEvidence.Average(e => e.QualityScore)
            : 0f;

        return new LearningStats
        {
            TotalAccepted = snapshot.PositiveEvidence.Count,
            TotalDismissed = snapshot.NegativeEvidence.Count,
            LastEntryTime = snapshot.LastActivity,
            ByCategory = byCategory,
            DismissedByCategory = negativesByCategory,
            AvgQualityByCategory = avgQualityByCategory,
            OverallAvgQuality = overallAvgQuality,
            DataFilePath = _dataFilePath,
            DataFileExists = dataFileInfo.Exists,
            DataFileSize = dataFileInfo.Exists ? dataFileInfo.Length : 0,
            ContextSummaries = snapshot.Contexts.Values
                .OrderByDescending(c => c.IsPinned)
                .ThenBy(c => c.IsDisabled)
                .ThenByDescending(c => c.Confidence)
                .ThenByDescending(c => c.LastActivity)
                .Take(8)
                .ToList(),
            EventEvidenceCount = snapshot.EventEvidenceCount
        };
    }

    public ContextSignal GetContextSignal(ContextSnapshot context)
    {
        RefreshIfNeeded();
        var snapshot = _repository.GetSnapshot();
        if (snapshot.DisabledContextKeys.Contains(context.SubcontextKey))
        {
            return new ContextSignal
            {
                Category = context.Category,
                ContextKey = context.SubcontextKey,
                ContextLabel = context.SubcontextLabel,
                Confidence = 0,
                NativeCount = 0,
                AssistCount = 0,
                IsDisabled = true
            };
        }

        var sameContext = snapshot.PositiveEvidence
            .Where(e => string.Equals(e.SubcontextKey, context.SubcontextKey, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();

        var sameCategory = snapshot.PositiveEvidence
            .Where(e => string.Equals(e.Category, context.Category, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();

        double confidence = 0.15;
        if (sameCategory.Count >= 3)
            confidence += 0.2;
        if (sameContext.Count >= 2)
            confidence += 0.35;
        if (sameContext.Count > 0)
            confidence += sameContext.Average(e => e.QualityScore) * 0.2;
        if (snapshot.Contexts.TryGetValue(context.SubcontextKey, out var summary))
            confidence += summary.Confidence * 0.2;

        return new ContextSignal
        {
            Category = context.Category,
            ContextKey = context.SubcontextKey,
            Confidence = Math.Round(Math.Clamp(confidence, 0.05, 0.98), 3),
            NativeCount = sameContext.Count(e => e.SourceType == LearningSourceType.NativeWriting),
            AssistCount = sameContext.Count(e => e.SourceType != LearningSourceType.NativeWriting),
            LastActivity = sameContext
                .OrderByDescending(e => e.TimestampUtc)
                .Select(e => (DateTime?)e.TimestampUtc)
                .FirstOrDefault(),
            ContextLabel = context.SubcontextLabel,
            IsPinned = snapshot.PinnedContextKeys.Contains(context.SubcontextKey)
        };
    }

    public void Refresh()
    {
        _repository.Refresh();
        _lastRefreshUtc = DateTime.UtcNow;
        Log($"Learning repository refreshed at {_lastRefreshUtc:HH:mm:ss}");
    }

    private static int GetAdaptiveExampleCount(int requested)
    {
        return requested switch
        {
            <= 1 => 1,
            2 => 2,
            _ => Math.Min(3, requested)
        };
    }

    private static string FormatSessionHint(List<SessionAccept> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recently committed writing in this context:");
        var endings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int included = 0;

        foreach (var item in items)
        {
            var trimmed = item.Completion.Trim();
            if (trimmed.Length < 12)
                continue;

            var ending = GetTrailingWords(trimmed, 2);
            if (!string.IsNullOrWhiteSpace(ending) && !endings.Add(ending))
                continue;

            sb.AppendLine($"  \"{trimmed}\"");
            included++;
        }

        return included >= 2 ? sb.ToString().TrimEnd() : "";
    }

    private static string GetTrailingWords(string text, int count)
    {
        var words = text.Trim().TrimEnd('.', ',', '!', '?', ';', ':')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "";
        return string.Join(" ", words.TakeLast(Math.Min(count, words.Length)));
    }

    private void RefreshIfNeeded()
    {
        if ((DateTime.UtcNow - _lastRefreshUtc).TotalSeconds > RefreshIntervalSeconds)
            Refresh();
    }

    private void PruneSessionBuffer()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-SessionWindowMinutes);
        while (_sessionBuffer.Count > 0 && _sessionBuffer.Peek().Timestamp < cutoff)
            _sessionBuffer.Dequeue();
    }

    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Learning] {msg}\n");
        }
        catch (IOException)
        {
        }
    }

    private sealed class SessionAccept
    {
        public DateTime Timestamp { get; init; }
        public string Prefix { get; init; } = "";
        public string Completion { get; init; } = "";
        public string ContextKey { get; init; } = "";
    }

    public sealed class FewShotExample
    {
        public string Prefix { get; set; } = "";
        public string Completion { get; set; } = "";
        public string Context { get; set; } = "";
        public bool IsNegative { get; set; }
        public string SourceType { get; set; } = "";
        public string ContextMatchLevel { get; set; } = "";
        public double Confidence { get; set; }
        public bool WasUntouched { get; set; }
    }

    public sealed class LearningStats
    {
        public int TotalAccepted { get; set; }
        public int TotalDismissed { get; set; }
        public DateTime? LastEntryTime { get; set; }
        public Dictionary<string, int> ByCategory { get; set; } = new();
        public Dictionary<string, int> DismissedByCategory { get; set; } = new();
        public Dictionary<string, float> AvgQualityByCategory { get; set; } = new();
        public float OverallAvgQuality { get; set; }
        public string DataFilePath { get; set; } = "";
        public bool DataFileExists { get; set; }
        public long DataFileSize { get; set; }
        public int EventEvidenceCount { get; set; }
        public List<LearningContextSummary> ContextSummaries { get; set; } = new();
    }

    public sealed class ContextSignal
    {
        public string Category { get; init; } = "";
        public string ContextKey { get; init; } = "";
        public string ContextLabel { get; init; } = "";
        public double Confidence { get; init; }
        public int NativeCount { get; init; }
        public int AssistCount { get; init; }
        public DateTime? LastActivity { get; init; }
        public bool IsPinned { get; init; }
        public bool IsDisabled { get; init; }
    }
}
