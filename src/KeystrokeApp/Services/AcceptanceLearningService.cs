using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Learns from user's accepted completions to provide few-shot examples
/// for better predictions. Reads from completions.jsonl and finds similar
/// past completions based on app context and prefix similarity.
///
/// Also tracks dismissed completions and exposes them as negative examples
/// so prediction engines can avoid repeating rejected patterns.
///
/// Sub-Phase C: maintains a lightweight in-memory session buffer so the model
/// can see the last few completions the user actually accepted this session,
/// enabling it to match voice, topic, and momentum in real time.
/// </summary>
public class AcceptanceLearningService
{
    private readonly string _dataPath;
    private readonly string _logPath;
    private readonly List<TrackingEntry> _acceptedEntries;
    private readonly List<TrackingEntry> _dismissedEntries;
    private readonly object _lock = new();
    private DateTime _lastFileRead;
    private long _lastFileSize;

    // Configuration
    private const int MaxExamplesToReturn = 3;
    private const int MaxNegativeExamplesToReturn = 2;
    private const int MaxPrefixLengthForMatching = 50;
    private const int MinCompletionLength = 25;
    private const int MaxAcceptedEntries = 1000;
    private const int MaxDismissedEntries = 500;

    // Deduplication: reject a candidate whose completion is this similar
    // to an already-selected example (Jaccard on words, 0–1 scale).
    private const double DeduplicationThreshold = 0.7;

    // Minimum relevance score to be considered a useful example.
    private const double MinRelevanceScore = 0.3;

    // ── Sub-Phase C: session buffer ───────────────────────────────────────────
    // Keeps the last N completions accepted during this process run (rolling
    // 15-minute window). Used to inject a "writing mode" hint into the prompt
    // so the model continues in the same voice and topic without needing to
    // scan the JSONL file.
    private const int SessionMaxItems     = 8;
    private const int SessionWindowMinutes = 15;
    private const int SessionHintMinItems = 2;   // minimum items before hint is shown
    private const int SessionHintMaxItems = 5;   // cap to keep the prompt concise

    // Scoring: entries younger than SessionWindowMinutes get an extra boost so
    // in-session examples always outrank equivalent historical entries.
    private const double SessionBoostMultiplier = 1.5;

    // Quality gate: only completions with quality ≥ this threshold enter the
    // session buffer. Low-quality accepts (slow, heavily cycled, immediately
    // edited) are weak evidence of desired voice and can amplify bad patterns.
    private const float SessionMinQuality = 0.6f;

    private readonly Queue<SessionAccept> _sessionBuffer = new();
    private readonly object               _sessionLock   = new();

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Learning] {msg}\n"); }
        catch (IOException) { }
    }

    public AcceptanceLearningService()
    {
        _dataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "completions.jsonl");
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "learning.log");
        _acceptedEntries  = new List<TrackingEntry>();
        _dismissedEntries = new List<TrackingEntry>();
        _lastFileRead = DateTime.MinValue;
        _lastFileSize = 0;

        Log($"Initialized. Looking for completions data at: {_dataPath}");
        Log($"File exists: {File.Exists(_dataPath)}");
        if (File.Exists(_dataPath))
        {
            var info = new FileInfo(_dataPath);
            Log($"File size: {info.Length} bytes");
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets few-shot examples similar to the current context.
    /// Deduplicates results so no two completions are excessively similar.
    /// Returns empty list if no relevant examples found.
    /// For very short prefixes (&lt;3 words), returns at most 1 example to avoid
    /// training the model to always complete ambiguous openings the same way.
    /// </summary>
    public List<FewShotExample> GetExamples(ContextSnapshot context, int count = 3)
    {
        RefreshIfNeeded();

        lock (_lock)
        {
            // Sub-Phase C: reduce the example count when data is still sparse.
            // Sparse data produces low-confidence evidence; using fewer examples
            // avoids biasing the model toward unrepresentative early accepts.
            int adaptiveCount = GetAdaptiveExampleCount(count);

            // Short-prefix suppression: very short prefixes are highly ambiguous.
            // Injecting multiple few-shot examples for "I", "Hey", "Thanks" causes
            // the model to always complete them the same way. Cap to 1 example.
            var prefixWords = context.TypedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (prefixWords.Length < 3)
                adaptiveCount = Math.Min(1, adaptiveCount);

            var scored = _acceptedEntries
                .Where(e => IsRelevant(e, context))
                .Select(e => (Entry: e, Score: CalculateRelevanceScore(e, context)))
                .Where(x => x.Score > MinRelevanceScore)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Timestamp);

            // Greedy deduplication: select up to `adaptiveCount` examples whose
            // completions are not too similar to any already-selected completion,
            // AND whose endings are sufficiently diverse (prevents "all end with X" loops).
            var selected = new List<FewShotExample>();
            foreach (var (entry, _) in scored)
            {
                bool tooSimilar = selected.Any(s =>
                    JaccardSimilarity(s.Completion, entry.Completion) > DeduplicationThreshold);

                // Ending diversity: reject if this completion's last 2 words match
                // any already-selected example's last 2 words (prevents reinforcing
                // repetitive closing patterns like "all day", "right now", etc.)
                bool endingDuplicate = selected.Any(s =>
                    GetTrailingWords(s.Completion, 2) == GetTrailingWords(entry.Completion, 2)
                    && GetTrailingWords(entry.Completion, 2).Length > 3);

                if (!tooSimilar && !endingDuplicate)
                {
                    selected.Add(new FewShotExample
                    {
                        Prefix     = entry.Prefix,
                        Completion = entry.Completion,
                        Context    = $"{entry.App} ({entry.Category})"
                    });

                    if (selected.Count >= adaptiveCount) break;
                }
            }

            return selected;
        }
    }

    /// <summary>
    /// Reduces the requested example count when the training corpus is still small.
    /// With few examples, quality variance is high — fewer examples prevents the model
    /// from over-indexing on unrepresentative early accepts.
    /// </summary>
    private int GetAdaptiveExampleCount(int requested)
    {
        int total = _acceptedEntries.Count;
        if (total < 10) return Math.Min(1, requested);
        if (total < 30) return Math.Min(2, requested);
        return requested;
    }

    /// <summary>
    /// Gets dismissed completions similar to the current context.
    /// Used to inject "avoid these patterns" guidance into the system prompt.
    /// Returns empty list if no relevant dismissed examples found.
    ///
    /// Context-continuity filter: only dismissals where the user continued typing
    /// in the same app category are included. A dismiss followed by an app switch
    /// or long gap likely means the user changed their mind about what to type,
    /// not that the completion was bad — those shouldn't pollute anti-repetition.
    /// </summary>
    public List<FewShotExample> GetNegativeExamples(ContextSnapshot context, int count = 2)
    {
        RefreshIfNeeded();

        lock (_lock)
        {
            var currentCategory = AppCategory.GetEffectiveCategory(
                context.ProcessName, context.WindowTitle).ToString();

            return _dismissedEntries
                .Where(e => IsRelevant(e, context))
                // Context continuity: only use dismissals from the same category
                // that have a follow-up entry (accepted or dismissed) in the same
                // category within 60 seconds — proving the user continued typing
                // in the same context and the dismissal was about completion quality.
                .Where(e => HasFollowUpInSameContext(e))
                .Select(e => (Entry: e, Score: CalculateRelevanceScore(e, context)))
                .Where(x => x.Score > MinRelevanceScore)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Timestamp)
                .Take(count)
                .Select(x => new FewShotExample
                {
                    Prefix     = x.Entry.Prefix,
                    Completion = x.Entry.Completion,
                    Context    = $"{x.Entry.App} ({x.Entry.Category})",
                    IsNegative = true
                })
                .ToList();
        }
    }

    // ── Sub-Phase C: Session buffer API ──────────────────────────────────────

    /// <summary>
    /// Records a newly accepted completion in the in-memory session buffer.
    /// Called immediately when the user presses Tab — no disk I/O involved.
    /// Only high-quality accepts (quality ≥ SessionMinQuality) enter the buffer;
    /// low-quality accepts are weak evidence of desired voice and can amplify
    /// bad patterns through the feedback loop.
    /// </summary>
    public void AddToSession(string prefix, string completion, string category, float qualityScore)
    {
        if (string.IsNullOrWhiteSpace(completion)) return;
        if (qualityScore < SessionMinQuality) return;

        lock (_sessionLock)
        {
            PruneSessionBuffer();
            _sessionBuffer.Enqueue(new SessionAccept
            {
                Timestamp  = DateTime.UtcNow,
                Prefix     = prefix,
                Completion = completion,
                Category   = category
            });
            // Hard cap — oldest entries fall off automatically
            while (_sessionBuffer.Count > SessionMaxItems)
                _sessionBuffer.Dequeue();
        }
    }

    /// <summary>
    /// Returns a formatted block of recent session completions for injection into
    /// the system prompt, or <c>null</c> if there is not enough session data yet.
    ///
    /// Tries to match the requested <paramref name="category"/> first; falls back
    /// to the full session buffer so cross-context sessions still benefit.
    /// </summary>
    public string? GetSessionModeHint(string? category = null)
    {
        lock (_sessionLock)
        {
            PruneSessionBuffer();

            List<SessionAccept> items;

            // Category-matched items first
            if (category != null)
            {
                items = _sessionBuffer
                    .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
                    .TakeLast(SessionHintMaxItems)
                    .ToList();

                if (items.Count >= SessionHintMinItems)
                    return FormatSessionHint(items);
            }

            // Fall back to full buffer (e.g. user switched apps mid-session)
            items = _sessionBuffer.TakeLast(SessionHintMaxItems).ToList();
            return items.Count >= SessionHintMinItems ? FormatSessionHint(items) : null;
        }
    }

    private static string FormatSessionHint(List<SessionAccept> items)
    {
        // Deduplicate: skip completions whose last 2 words match an already-included one,
        // AND skip completions that are too short to be useful style signals.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Recently accepted completions this session (for voice/topic continuity, not templates to copy):");
        var includedEndings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int included = 0;
        foreach (var item in items)
        {
            var trimmed = item.Completion.Trim();
            if (trimmed.Length < 15) continue; // too short to be a useful style signal

            // Check ending diversity
            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ending = words.Length >= 2
                ? string.Join(" ", words[^2..]).ToLowerInvariant()
                : trimmed.ToLowerInvariant();

            if (ending.Length > 3 && !includedEndings.Add(ending))
                continue; // same ending as a previously included item

            sb.AppendLine($"  \"{trimmed}\"");
            included++;
        }
        return included >= 2 ? sb.ToString().TrimEnd() : "";
    }

    private void PruneSessionBuffer()
    {
        // Called inside _sessionLock — removes expired entries from the front of the queue
        var cutoff = DateTime.UtcNow.AddMinutes(-SessionWindowMinutes);
        while (_sessionBuffer.Count > 0 && _sessionBuffer.Peek().Timestamp < cutoff)
            _sessionBuffer.Dequeue();
    }

    /// <summary>
    /// Gets quick stats about the learning data for debugging and the Settings panel.
    /// Includes both accepted and dismissed counts so callers can compute acceptance rates.
    /// </summary>
    public LearningStats GetStats()
    {
        RefreshIfNeeded();

        lock (_lock)
        {
            var byCategory = _acceptedEntries
                .GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            var dismissedByCategory = _dismissedEntries
                .GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            // Average quality per category — only include entries with real signal data
            // (legacy entries default to 0.5; we still average them in but they won't
            // skew results dramatically once real-signal entries accumulate).
            var avgQualityByCategory = _acceptedEntries
                .GroupBy(e => e.Category)
                .ToDictionary(
                    g => g.Key,
                    g => g.Average(e => e.QualityScore));

            float overallAvgQuality = _acceptedEntries.Count > 0
                ? (float)_acceptedEntries.Average(e => e.QualityScore)
                : 0f;

            bool fileExists = File.Exists(_dataPath);
            long fileSize   = fileExists ? new FileInfo(_dataPath).Length : 0;

            return new LearningStats
            {
                TotalAccepted         = _acceptedEntries.Count,
                TotalDismissed        = _dismissedEntries.Count,
                LastEntryTime         = _acceptedEntries.Count > 0
                    ? _acceptedEntries.Max(e => e.Timestamp)
                    : null,
                ByCategory            = byCategory,
                DismissedByCategory   = dismissedByCategory,
                AvgQualityByCategory  = avgQualityByCategory
                    .ToDictionary(k => k.Key, k => (float)k.Value),
                OverallAvgQuality     = overallAvgQuality,
                DataFilePath          = _dataPath,
                DataFileExists        = fileExists,
                DataFileSize          = fileSize
            };
        }
    }

    /// <summary>Forces a full refresh of the data from disk.</summary>
    public void Refresh()
    {
        if (!File.Exists(_dataPath))
            return;

        try
        {
            var fileInfo = new FileInfo(_dataPath);
            Log($"Refreshing from {_dataPath}");
            Log($"File size: {fileInfo.Length}, Last read: {_lastFileSize}");

            lock (_lock)
            {
                // File was truncated/rotated — reload from the start.
                if (fileInfo.Length < _lastFileSize)
                {
                    Log("File truncated, clearing cache");
                    _acceptedEntries.Clear();
                    _dismissedEntries.Clear();
                }

                using var stream = File.Open(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                if (fileInfo.Length > _lastFileSize && _lastFileSize > 0)
                {
                    stream.Seek(_lastFileSize, SeekOrigin.Begin);
                    Log($"Seeking to position {_lastFileSize}");
                }

                int linesRead      = 0;
                int linesAccepted  = 0;
                int linesDismissed = 0;

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    linesRead++;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<TrackingEntry>(line, options);
                        if (entry == null) continue;

                        if (entry.Action == "accepted" &&
                            entry.Completion?.Length >= MinCompletionLength)
                        {
                            // Skip entries whose completions contain prompt leakage
                            // or known poisoned patterns — these pollute few-shot
                            // examples and style/vocab analysis.
                            if (!IsCompletionContaminated(entry.Completion))
                            {
                                _acceptedEntries.Add(entry);
                                linesAccepted++;
                            }
                        }
                        else if (entry.Action == "dismissed" &&
                                 !string.IsNullOrWhiteSpace(entry.Completion))
                        {
                            _dismissedEntries.Add(entry);
                            linesDismissed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Parse error: {ex.Message} on line: {line[..Math.Min(50, line.Length)]}");
                    }
                }

                Log($"Read {linesRead} lines, +{linesAccepted} accepted, +{linesDismissed} dismissed. " +
                    $"Total: {_acceptedEntries.Count} accepted, {_dismissedEntries.Count} dismissed");

                // Trim to recent entries to prevent unbounded memory growth.
                TrimEntries(_acceptedEntries,  MaxAcceptedEntries);
                TrimEntries(_dismissedEntries, MaxDismissedEntries);

                _lastFileRead = DateTime.UtcNow;
                _lastFileSize = fileInfo.Length;
            }
        }
        catch (Exception ex)
        {
            Log($"Refresh error: {ex.Message}");
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void RefreshIfNeeded()
    {
        if ((DateTime.UtcNow - _lastFileRead).TotalSeconds > 5)
            Refresh();
    }

    private static void TrimEntries(List<TrackingEntry> list, int maxCount)
    {
        if (list.Count <= maxCount) return;
        list.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp)); // newest first
        list.RemoveRange(maxCount, list.Count - maxCount);
    }

    /// <summary>
    /// Checks if a past entry is relevant to the current context.
    /// Allows exact or adjacent category matches (Chat↔Email, Code↔Terminal).
    /// </summary>
    private bool IsRelevant(TrackingEntry entry, ContextSnapshot context)
    {
        var currentCategory = AppCategory.GetEffectiveCategory(
            context.ProcessName, context.WindowTitle);

        if (!Enum.TryParse<AppCategory.Category>(entry.Category, out var entryCategory))
            return false;

        if (!IsSameOrAdjacentCategory(entryCategory, currentCategory))
            return false;

        if (entry.Prefix.Length < 3 || entry.Prefix.Length > 100)
            return false;

        return true;
    }

    /// <summary>
    /// Returns true if a dismissed entry has a follow-up entry (accepted or dismissed)
    /// in the same app category within 60 seconds. This proves the user continued
    /// typing in the same context, meaning the dismissal was about completion quality
    /// rather than the user switching tasks or changing their mind.
    /// Must be called inside _lock.
    /// </summary>
    private bool HasFollowUpInSameContext(TrackingEntry dismissed)
    {
        var cutoff = dismissed.Timestamp.AddSeconds(60);

        // Check accepted entries for a follow-up
        foreach (var e in _acceptedEntries)
        {
            if (e.Timestamp > dismissed.Timestamp && e.Timestamp <= cutoff
                && string.Equals(e.Category, dismissed.Category, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check other dismissed entries for a follow-up
        foreach (var e in _dismissedEntries)
        {
            if (e == dismissed) continue;
            if (e.Timestamp > dismissed.Timestamp && e.Timestamp <= cutoff
                && string.Equals(e.Category, dismissed.Category, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if two categories share enough stylistic overlap to be
    /// useful as few-shot examples for each other.
    /// </summary>
    private static bool IsSameOrAdjacentCategory(AppCategory.Category a, AppCategory.Category b)
    {
        if (a == b) return true;
        // Chat ↔ Email: both conversational prose
        if ((a == AppCategory.Category.Chat  && b == AppCategory.Category.Email) ||
            (a == AppCategory.Category.Email && b == AppCategory.Category.Chat))
            return true;
        // Code ↔ Terminal: both technical / precise
        if ((a == AppCategory.Category.Code     && b == AppCategory.Category.Terminal) ||
            (a == AppCategory.Category.Terminal && b == AppCategory.Category.Code))
            return true;
        return false;
    }

    /// <summary>
    /// Calculates a relevance score (0–1) using bigram-based Jaccard similarity.
    ///
    /// Bigrams ("word1 word2") capture phrase-level similarity, which is
    /// significantly more discriminating than single-word overlap.  For example,
    /// "Thanks for your" and "Thanks for the" share the unigrams "Thanks"/"for"
    /// but NOT the bigram "for your" / "for the", giving a more accurate score.
    ///
    /// Unigrams are included in the ngram set so very short prefixes still match.
    /// </summary>
    private double CalculateRelevanceScore(TrackingEntry entry, ContextSnapshot context)
    {
        double score = 0;

        var currentWords = context.TypedText.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entryWords = entry.Prefix.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Bigram Jaccard similarity (replaces simple word-overlap)
        if (currentWords.Length > 0 && entryWords.Length > 0)
        {
            var currentNgrams = BuildNgrams(currentWords);
            var entryNgrams   = BuildNgrams(entryWords);

            int intersection = currentNgrams.Count(n => entryNgrams.Contains(n));
            int union        = currentNgrams.Count + entryNgrams.Count - intersection;
            double jaccard   = union > 0 ? (double)intersection / union : 0;
            score += jaccard * 0.6;
        }

        // Strong signal: same first word (phrase likely continues the same way)
        if (currentWords.Length > 0 && entryWords.Length > 0 &&
            currentWords[0] == entryWords[0])
        {
            score += 0.3;
        }

        // Bonus: same app process
        if (string.Equals(entry.App, context.ProcessName, StringComparison.OrdinalIgnoreCase))
            score += 0.1;

        // Bonus: exact category match (adjacent-only entries still qualify, just no bonus)
        var currentCategory = AppCategory.GetEffectiveCategory(context.ProcessName, context.WindowTitle);
        if (Enum.TryParse<AppCategory.Category>(entry.Category, out var entryCategory) &&
            entryCategory == currentCategory)
            score += 0.1;

        // Recency — weighted heavily so in-session examples dominate
        var age = DateTime.UtcNow - entry.Timestamp;
        if (age.TotalMinutes < 15)
            score += 0.3;   // current session: strong signal
        else if (age.TotalHours < 1)
            score += 0.15;  // within the hour
        else if (age.TotalHours < 24)
            score += 0.05;  // today: minor boost

        // Sub-Phase C: session boost multiplier — entries from the last 15 minutes
        // rank above equivalent historical entries even when textual similarity is
        // slightly lower. Combined with the recency bonus above this ensures in-session
        // evidence dominates the few-shot selection whenever it exists.
        if (age.TotalMinutes < SessionWindowMinutes)
            score *= SessionBoostMultiplier;

        // Quality multiplier — high-confidence evidence scores its full relevance;
        // low-quality evidence (slow accepts, heavy cycling, immediate corrections)
        // is down-weighted so it doesn't crowd out better examples.
        // Multiplier range: 0.5× (quality=0) … 1.0× (quality=1.0)
        double qualityMultiplier = 0.5 + (entry.QualityScore * 0.5);
        score *= qualityMultiplier;

        return Math.Min(score, 1.0);
    }

    /// <summary>
    /// Builds a set of unigrams + bigrams from a word array.
    /// Including unigrams ensures short texts (1–2 words) still produce overlap.
    /// </summary>
    private static HashSet<string> BuildNgrams(string[] words)
    {
        var ngrams = new HashSet<string>(words); // unigrams
        for (int i = 0; i < words.Length - 1; i++)
            ngrams.Add($"{words[i]} {words[i + 1]}"); // bigrams
        return ngrams;
    }

    /// <summary>
    /// Checks if a completion contains phrases that indicate prompt leakage
    /// or known contamination patterns. Delegates to the shared ContaminationFilter
    /// so all learning services use the same phrase list.
    /// </summary>
    private static bool IsCompletionContaminated(string completion) =>
        ContaminationFilter.IsContaminated(completion);

    /// <summary>
    /// Returns the last N words of a string, lowercased and trimmed.
    /// Used for ending-diversity checks in few-shot selection.
    /// </summary>
    private static string GetTrailingWords(string text, int count)
    {
        var words = text.Trim().TrimEnd('.', ',', '!', '?', ';', ':')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < count) return text.Trim().ToLowerInvariant();
        return string.Join(" ", words[^count..]).ToLowerInvariant();
    }

    /// <summary>
    /// Jaccard similarity on the word sets of two completion strings (0–1).
    /// Used during deduplication to avoid returning near-identical examples.
    /// </summary>
    private static double JaccardSimilarity(string a, string b)
    {
        var aWords = new HashSet<string>(
            a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var bWords = new HashSet<string>(
            b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        int intersection = aWords.Count(w => bWords.Contains(w));
        int union        = aWords.Count + bWords.Count - intersection;
        return union > 0 ? (double)intersection / union : 0;
    }

    // ── Data model ────────────────────────────────────────────────────────────

    private class TrackingEntry
    {
        public DateTime Timestamp    { get; set; }
        public string   Action       { get; set; } = "";
        public string   Prefix       { get; set; } = "";
        public string   Completion   { get; set; } = "";
        public string   App          { get; set; } = "";
        public string   Window       { get; set; } = "";
        public string   Category     { get; set; } = "";

        // Sub-Phase A signal fields — may be absent in legacy entries; defaults are safe.
        public int   LatencyMs    { get; set; } = -1;     // -1 = unknown (legacy)
        public int   CycleDepth   { get; set; } = 0;
        public bool  EditedAfter  { get; set; } = false;
        public float QualityScore { get; set; } = 0.5f;   // 0.5 = neutral (legacy)
    }

    public class FewShotExample
    {
        public string Prefix     { get; set; } = "";
        public string Completion { get; set; } = "";
        public string Context    { get; set; } = "";
        /// <summary>True if the user dismissed/rejected this completion.</summary>
        public bool   IsNegative { get; set; } = false;
    }

    /// <summary>In-memory record for the session buffer (Sub-Phase C).</summary>
    private class SessionAccept
    {
        public DateTime Timestamp  { get; set; }
        public string   Prefix     { get; set; } = "";
        public string   Completion { get; set; } = "";
        public string   Category   { get; set; } = "";
    }

    public class LearningStats
    {
        public int                     TotalAccepted         { get; set; }
        public int                     TotalDismissed        { get; set; }
        public DateTime?               LastEntryTime         { get; set; }
        public Dictionary<string, int>   ByCategory          { get; set; } = new();
        public Dictionary<string, int>   DismissedByCategory { get; set; } = new();
        /// <summary>Average quality score (0–1) per accepted category. Only populated for
        /// categories that have at least one entry with a non-legacy quality score.</summary>
        public Dictionary<string, float> AvgQualityByCategory { get; set; } = new();
        /// <summary>Overall average quality score across all accepted entries.</summary>
        public float                   OverallAvgQuality     { get; set; }
        public string                  DataFilePath          { get; set; } = "";
        public bool                    DataFileExists        { get; set; }
        public long                    DataFileSize          { get; set; }
    }
}
