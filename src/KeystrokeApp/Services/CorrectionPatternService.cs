using System.IO;
using System.Text;
using System.Text.Json;

namespace KeystrokeApp.Services;

/// <summary>
/// Extracts deterministic correction patterns from the user's post-acceptance edits.
/// When a user accepts a suggestion and immediately backspaces to fix the ending,
/// the system captures (deletedSuffix → replacementText) pairs. This service
/// accumulates those pairs and extracts recurring patterns:
///
///   - Word replacements: "gonna" → "going to" (3x) — vocabulary preference
///   - Truncation tendency: user deletes the last ~4 words on average — length preference
///   - Ending rewrites: user consistently replaces the last 1-2 words — phrasing preference
///   - Avoided words: words that appear in deletions but never in replacements
///
/// Like VocabularyProfileService, all analysis is deterministic (no LLM call).
/// The extracted patterns are injected into the prediction prompt as soft hints.
/// </summary>
public class CorrectionPatternService
{
    // ── Thresholds ────────────────────────────────────────────────────────────
    private const int MinCorrectionsForAnalysis = 5;
    private const int MinReplacementFrequency = 2;
    private const int MaxReplacementsReported = 5;
    private const int MaxAvoidedWordsReported = 5;
    private const int MaxCorrectionEntries = 500;

    /// <summary>Profiles older than this are suppressed to prevent stale hints.</summary>
    private static readonly TimeSpan MaxPatternAge = TimeSpan.FromDays(7);

    // ── File paths ────────────────────────────────────────────────────────────
    private readonly string _patternPath;
    private readonly string _logPath;
    private readonly LearningDatabase? _database;

    // ── State ─────────────────────────────────────────────────────────────────
    private CorrectionPatterns? _patterns;
    private int _correctionCount;
    private int _patternInterval;
    private bool _isGenerating;
    private CancellationTokenSource? _generateCts;
    private readonly object _lock = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public CorrectionPatternService(LearningDatabase? database = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _patternPath = Path.Combine(appData, "correction-patterns.json");
        _logPath = Path.Combine(appData, "correction-patterns.log");
        _database = database;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start(int interval)
    {
        _patternInterval = interval;
        LoadPatterns();
        Log($"Started. Interval={interval}, HasPatterns={_patterns != null}");
    }

    public void UpdateInterval(int interval) => _patternInterval = interval;

    /// <summary>
    /// Called when a correction is detected (user backspaced after acceptance).
    /// Triggers pattern re-extraction when enough corrections accumulate.
    /// </summary>
    public void OnCorrectionDetected()
    {
        lock (_lock)
        {
            _correctionCount++;
            if (_correctionCount >= _patternInterval && !_isGenerating)
            {
                _correctionCount = 0;
                _ = Task.Run(GenerateAsync).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Log($"Unobserved error: {t.Exception.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    /// <summary>
    /// Returns a compact correction-pattern hint for prompt injection,
    /// or null if no patterns have been extracted for this category.
    /// </summary>
    public string? GetCorrectionHint(string category, string? subcontextKey = null)
    {
        lock (_lock)
        {
            if (_patterns == null) return null;

            if ((DateTime.UtcNow - _patterns.LastUpdated) > MaxPatternAge)
                return null;

            CategoryCorrectionPatterns? patterns = null;

            // Prefer subcontext-specific patterns if available.
            if (!string.IsNullOrWhiteSpace(subcontextKey) &&
                _patterns.Contexts.TryGetValue(subcontextKey, out var contextPatterns))
                patterns = contextPatterns;
            else if (_patterns.Categories.TryGetValue(category, out var catPatterns))
                patterns = catPatterns;

            if (patterns == null || patterns.TotalCorrections < MinCorrectionsForAnalysis)
                return null;

            return BuildHintText(patterns);
        }
    }

    public CorrectionPatterns? GetPatterns()
    {
        lock (_lock) return _patterns;
    }

    public void InvalidatePatterns()
    {
        lock (_lock)
        {
            _generateCts?.Cancel();
            _patterns = null;
            _correctionCount = 0;

            try
            {
                if (File.Exists(_patternPath))
                    File.Delete(_patternPath);
            }
            catch (Exception ex) { Log($"Invalidate error: {ex.Message}"); }
        }
    }

    public void CancelGeneration()
    {
        lock (_lock) { _generateCts?.Cancel(); }
    }

    // ── Generation ────────────────────────────────────────────────────────────

    private async Task GenerateAsync()
    {
        lock (_lock)
        {
            _isGenerating = true;
            _generateCts?.Cancel();
            _generateCts?.Dispose();
            _generateCts = new CancellationTokenSource();
        }
        var ct = _generateCts!.Token;

        try
        {
            var corrections = LoadCorrectionEntries();
            Log($"Generating patterns from {corrections.Count} corrections...");

            var newPatterns = new CorrectionPatterns
            {
                LastUpdated = DateTime.UtcNow,
                EntriesProcessed = corrections.Count
            };

            // ── Category-level patterns ──────────────────────────────────────
            var categoryGroups = corrections
                .GroupBy(c => c.Category)
                .Where(g => g.Count() >= MinCorrectionsForAnalysis);

            foreach (var group in categoryGroups)
            {
                if (ct.IsCancellationRequested) break;
                var patterns = AnalyzeCorrections(group.ToList());
                newPatterns.Categories[group.Key] = patterns;
                Log($"{group.Key}: {patterns.TotalCorrections} corrections, " +
                    $"{patterns.TruncationCount} truncations, " +
                    $"{patterns.FrequentReplacements.Count} word replacements");
            }

            // ── Subcontext-level patterns ────────────────────────────────────
            var contextGroups = corrections
                .Where(c => !string.IsNullOrWhiteSpace(c.SubcontextKey))
                .GroupBy(c => c.SubcontextKey)
                .Where(g => g.Count() >= MinCorrectionsForAnalysis)
                .OrderByDescending(g => g.Count())
                .Take(6);

            foreach (var group in contextGroups)
            {
                if (ct.IsCancellationRequested) break;
                var patterns = AnalyzeCorrections(group.ToList());
                newPatterns.Contexts[group.Key] = patterns;
                var label = group.FirstOrDefault()?.ContextLabel ?? group.Key;
                newPatterns.ContextLabels[group.Key] = label;
            }

            lock (_lock)
            {
                if (ct.IsCancellationRequested) return;
                _patterns = newPatterns;
                SavePatterns(newPatterns);
            }
            Log("Pattern generation complete.");
        }
        catch (OperationCanceledException) { Log("Generation cancelled"); }
        catch (Exception ex) { Log($"Generate error: {ex.Message}"); }
        finally { lock (_lock) { _isGenerating = false; } }

        await Task.CompletedTask;
    }

    // ── Analysis ──────────────────────────────────────────────────────────────

    private static CategoryCorrectionPatterns AnalyzeCorrections(List<CorrectionEntry> corrections)
    {
        var truncations = corrections.Where(c => c.CorrectionType == "truncated").ToList();
        var replacements = corrections.Where(c => c.CorrectionType == "replaced_ending").ToList();

        return new CategoryCorrectionPatterns
        {
            TotalCorrections = corrections.Count,
            TruncationCount = truncations.Count,
            AvgTruncationChars = truncations.Count > 0
                ? truncations.Average(c => c.BackspaceCount)
                : 0,
            EndingRewriteCount = replacements.Count,
            FrequentReplacements = ExtractFrequentReplacements(replacements),
            AvoidedWords = ExtractAvoidedWords(corrections),
            TruncationRate = corrections.Count > 0
                ? (double)truncations.Count / corrections.Count
                : 0
        };
    }

    /// <summary>
    /// Finds word-level replacements that occur frequently. If the user consistently
    /// replaces "option" → "plan", that's a strong signal about word preference.
    /// </summary>
    private static List<WordReplacement> ExtractFrequentReplacements(List<CorrectionEntry> replacements)
    {
        var pairs = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in replacements)
        {
            var deleted = NormalizeToWords(entry.DeletedSuffix);
            var replaced = NormalizeToWords(entry.ReplacementText);

            if (string.IsNullOrWhiteSpace(deleted)) continue;

            if (!pairs.TryGetValue(deleted, out var replacementCounts))
            {
                replacementCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                pairs[deleted] = replacementCounts;
            }

            var key = string.IsNullOrWhiteSpace(replaced) ? "(removed)" : replaced;
            replacementCounts[key] = replacementCounts.GetValueOrDefault(key, 0) + 1;
        }

        return pairs
            .SelectMany(p => p.Value
                .Where(r => r.Value >= MinReplacementFrequency)
                .Select(r => new WordReplacement
                {
                    Original = p.Key,
                    Replacement = r.Key,
                    Count = r.Value
                }))
            .OrderByDescending(r => r.Count)
            .Take(MaxReplacementsReported)
            .ToList();
    }

    /// <summary>
    /// Finds words that appear frequently in deleted suffixes but rarely or never
    /// in replacement text. These are words the AI should avoid using.
    /// </summary>
    private static List<string> ExtractAvoidedWords(List<CorrectionEntry> corrections)
    {
        var deletedWordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var replacementWordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in corrections)
        {
            foreach (var word in SplitWords(entry.DeletedSuffix))
            {
                if (word.Length < 4 || CommonWords.Contains(word)) continue;
                deletedWordFreq[word] = deletedWordFreq.GetValueOrDefault(word, 0) + 1;
            }

            foreach (var word in SplitWords(entry.ReplacementText))
            {
                if (word.Length < 4) continue;
                replacementWordFreq[word] = replacementWordFreq.GetValueOrDefault(word, 0) + 1;
            }
        }

        return deletedWordFreq
            .Where(kv => kv.Value >= MinReplacementFrequency)
            .Where(kv => !replacementWordFreq.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(MaxAvoidedWordsReported)
            .Select(kv => kv.Key)
            .ToList();
    }

    // ── Hint generation ───────────────────────────────────────────────────────

    private static string? BuildHintText(CategoryCorrectionPatterns patterns)
    {
        var parts = new List<string>();

        if (patterns.TruncationRate >= 0.30)
        {
            int avgWords = Math.Max(1, (int)Math.Round(patterns.AvgTruncationChars / 5.0));
            parts.Add($"User often shortens completions by ~{avgWords} word{(avgWords != 1 ? "s" : "")}; prefer concise endings.");
        }

        foreach (var replacement in patterns.FrequentReplacements.Take(3))
        {
            if (replacement.Replacement == "(removed)")
                parts.Add($"User removes \"{replacement.Original}\" ({replacement.Count}x); avoid this word/phrase.");
            else
                parts.Add($"User prefers \"{replacement.Replacement}\" over \"{replacement.Original}\" ({replacement.Count}x).");
        }

        if (patterns.AvoidedWords.Count > 0)
        {
            var words = string.Join(", ", patterns.AvoidedWords.Take(3).Select(w => $"\"{w}\""));
            parts.Add($"User frequently deletes: {words}.");
        }

        if (parts.Count == 0) return null;

        return "Correction patterns: " + string.Join(" ", parts);
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private List<CorrectionEntry> LoadCorrectionEntries()
    {
        if (_database == null) return [];

        var records = _database.GetCorrectionEvents(MaxCorrectionEntries);
        var entries = new List<CorrectionEntry>();

        foreach (var record in records)
        {
            if (ContaminationFilter.IsContaminated(record.DeletedSuffix) ||
                ContaminationFilter.IsContaminated(record.CorrectedText))
                continue;

            entries.Add(new CorrectionEntry
            {
                Timestamp = record.TimestampUtc,
                Category = record.Category,
                SubcontextKey = record.ContextKeys.SubcontextKey,
                ContextLabel = record.ContextKeys.SubcontextLabel,
                DeletedSuffix = record.DeletedSuffix,
                ReplacementText = record.CorrectedText,
                BackspaceCount = record.CorrectionBackspaces,
                CorrectionType = record.CorrectionType
            });
        }

        return entries;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadPatterns()
    {
        try
        {
            if (!File.Exists(_patternPath)) return;
            var json = File.ReadAllText(_patternPath);
            _patterns = JsonSerializer.Deserialize<CorrectionPatterns>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Log($"Loaded: {_patterns?.Categories.Count ?? 0} categories");
        }
        catch (Exception ex) { Log($"Load error: {ex}"); }
    }

    private void SavePatterns(CorrectionPatterns patterns)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_patternPath)!);
            var json = JsonSerializer.Serialize(patterns,
                new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _patternPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _patternPath, overwrite: true);
        }
        catch (Exception ex) { Log($"Save error: {ex}"); }
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [Correction] {msg}\n"); }
        catch (IOException) { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Normalizes text to lower-case trimmed words for comparison.</summary>
    private static string NormalizeToWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return string.Join(" ", text.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.TrimEnd('.', ',', '!', '?', ';', ':')));
    }

    private static string[] SplitWords(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '.', ',', '!', '?', ';', ':', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries);

    private sealed class CorrectionEntry
    {
        public DateTime Timestamp { get; init; }
        public string Category { get; init; } = "";
        public string SubcontextKey { get; init; } = "";
        public string ContextLabel { get; init; } = "";
        public string DeletedSuffix { get; init; } = "";
        public string ReplacementText { get; init; } = "";
        public int BackspaceCount { get; init; }
        public string CorrectionType { get; init; } = "";
    }

    // ── Common words baseline (shared with VocabularyProfileService logic) ────
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "are", "was", "were", "be", "been",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "can", "that", "this", "these", "those",
        "it", "its", "not", "just", "also", "very", "much", "more", "most",
        "some", "any", "all", "than", "then", "so", "if", "when", "where",
        "how", "what", "who", "which", "there", "here", "now", "only", "well",
        "even", "back", "still", "into", "over", "about", "such", "through"
    };
}

// ── Data models ──────────────────────────────────────────────────────────────

public class CorrectionPatterns
{
    public DateTime LastUpdated { get; set; }
    public int EntriesProcessed { get; set; }
    public Dictionary<string, CategoryCorrectionPatterns> Categories { get; set; } = new();
    public Dictionary<string, CategoryCorrectionPatterns> Contexts { get; set; } = new();
    public Dictionary<string, string> ContextLabels { get; set; } = new();
}

public class CategoryCorrectionPatterns
{
    public int TotalCorrections { get; set; }
    public int TruncationCount { get; set; }
    public double AvgTruncationChars { get; set; }
    public int EndingRewriteCount { get; set; }
    public double TruncationRate { get; set; }
    public List<WordReplacement> FrequentReplacements { get; set; } = new();
    public List<string> AvoidedWords { get; set; } = new();
}

public class WordReplacement
{
    public string Original { get; set; } = "";
    public string Replacement { get; set; } = "";
    public int Count { get; set; }
}
