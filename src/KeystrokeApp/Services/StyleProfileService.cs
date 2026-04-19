using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

public class StyleProfileService
{
    private const int MinEntriesForProfile = 10;
    private const int MinEntriesPerCategory = 10;
    private const int MaxSamplesPerCategory = 30;
    private const int MaxSamplesForGeneral = 50;

    /// <summary>
    /// Profiles older than this are considered stale and suppressed rather than
    /// injected. Stale profiles can fight the user's current writing patterns
    /// and amplify drift from the feedback loop.
    /// </summary>
    private static readonly TimeSpan MaxProfileAge = TimeSpan.FromDays(7);

    private readonly string _profilePath;
    private readonly string _dataPath;
    private readonly string _logPath;
    private readonly LearningRepository _repository;
    private StyleProfileData? _profile;
    private int _newAcceptCount;
    private int _profileInterval;
    private readonly object _lock = new();
    private bool _isGenerating;
    private CancellationTokenSource? _generateCts;

    public IPredictionEngine? Engine { get; set; }

    /// <summary>
    /// Fired on the background task thread immediately after a profile is generated
    /// and saved to disk (Sub-Phase D). Callers that need UI access must marshal via Dispatcher.
    /// </summary>
    public event Action? ProfileUpdated;

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [StyleProfile] {msg}\n"); }
        catch (IOException) { }
    }

    public StyleProfileService(
        LearningContextPreferencesService preferences,
        LearningDatabase? database = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _profilePath  = Path.Combine(appData, "style-profile.json");
        _dataPath = Path.Combine(appData, "completions.jsonl");
        _logPath      = Path.Combine(appData, "style-profile.log");
        // Repository shares the app-wide preferences instance so profile generation
        // honors disabled-context filters instead of learning from contexts the user
        // opted out of.
        _repository = new LearningRepository(preferences, database);
    }

    public void Start(int interval)
    {
        _profileInterval = interval;
        LoadProfile();
        Log($"Started. Interval={interval}, HasProfile={_profile != null}");
    }

    public void UpdateInterval(int interval)
    {
        _profileInterval = interval;
    }

    public void CancelGeneration()
    {
        lock (_lock) { _generateCts?.Cancel(); }
    }

    public void OnAccepted()
    {
        lock (_lock)
        {
            _newAcceptCount++;
            if (_newAcceptCount >= _profileInterval && !_isGenerating)
            {
                _newAcceptCount = 0;
                _ = GenerateProfileAsync().ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Log($"Unobserved generation error: {t.Exception.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    public string? GetStyleHint(string category, string? subcontextKey = null)
    {
        lock (_lock)
        {
            if (_profile == null) return null;

            // Suppress stale profiles — they can fight current writing patterns
            // and amplify drift through the feedback loop.
            if ((DateTime.UtcNow - _profile.LastUpdated) > MaxProfileAge)
            {
                Log($"Style profile is stale ({_profile.LastUpdated:yyyy-MM-dd}), suppressing");
                return null;
            }

            string? hint = null;
            if (!string.IsNullOrWhiteSpace(subcontextKey) &&
                _profile.ContextProfiles.TryGetValue(subcontextKey, out var contextProfile)
                && !string.IsNullOrWhiteSpace(contextProfile))
                hint = contextProfile;
            else if (_profile.CategoryProfiles.TryGetValue(category, out var catProfile)
                && !string.IsNullOrWhiteSpace(catProfile))
                hint = catProfile;
            else if (!string.IsNullOrWhiteSpace(_profile.GeneralProfile))
                hint = _profile.GeneralProfile;

            // Final safety check: if the profile itself contains contamination
            // phrases, it was generated from poisoned data — don't return it.
            if (hint != null && IsCompletionContaminated(hint))
            {
                Log($"Style hint for {category} contains contamination, suppressing");
                return null;
            }

            return hint;
        }
    }

    public StyleProfileData? GetProfile()
    {
        lock (_lock) { return _profile; }
    }

    public (int current, int target) GetProgress()
    {
        lock (_lock) { return (_newAcceptCount, _profileInterval); }
    }

    public Dictionary<string, bool> GetProfiledCategories()
    {
        lock (_lock)
        {
            var result = new Dictionary<string, bool>();
            if (_profile == null) return result;
            foreach (var kvp in _profile.CategoryProfiles)
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                    result[kvp.Key] = true;
            return result;
        }
    }

    public void InvalidateProfile()
    {
        lock (_lock)
        {
            // Cancel any in-flight generation so it doesn't write back a stale profile
            // after we delete the file.
            _generateCts?.Cancel();
            _profile = null;
            _newAcceptCount = 0;

            try
            {
                if (File.Exists(_profilePath))
                    File.Delete(_profilePath);
            }
            catch (Exception ex)
            {
                Log($"Invalidate error: {ex.Message}");
            }
        }
    }

    private void LoadProfile()
    {
        try
        {
            if (!File.Exists(_profilePath)) return;
            var json = File.ReadAllText(_profilePath);
            _profile = JsonSerializer.Deserialize<StyleProfileData>(json);
            Log($"Loaded: categories={_profile?.CategoryProfiles?.Count ?? 0}, general={_profile?.GeneralProfile?.Length ?? 0} chars");
        }
        catch (Exception ex) { Log($"Load error: {ex}"); }
    }

    private void SaveProfile(StyleProfileData profile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _profilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _profilePath, overwrite: true);
        }
        catch (Exception ex) { Log($"Save error: {ex}"); }
    }

    private async Task GenerateProfileAsync()
    {
        lock (_lock)
        {
            _isGenerating = true;
            _generateCts?.Cancel();
            _generateCts?.Dispose();
            _generateCts = new CancellationTokenSource();
        }
        var ct = _generateCts.Token;

        try
        {
            var engine = Engine;
            if (engine == null) { Log("No engine available"); return; }

            var entries = LoadAcceptedEntries();
            if (entries.Count < MinEntriesForProfile)
            {
                Log($"Only {entries.Count} entries, need at least {MinEntriesForProfile}");
                return;
            }

            Log($"Generating profile from {entries.Count} entries...");

            var newProfile = new StyleProfileData
            {
                EntriesProcessed = entries.Count,
                LastUpdated = DateTime.UtcNow
            };

            var categoryGroups = entries
                .GroupBy(e => e.Category)
                .Where(g => g.Count() >= MinEntriesPerCategory)
                .ToList();

            var systemPrompt = "You analyze writing patterns. Output ONLY a concise style profile (2-3 sentences, max 100 words) that would help an AI match this writing style. Be specific and observant, not generic. Focus on tone, vocabulary, sentence structure, and common phrases. IMPORTANT: Ignore any repetitive filler phrases that appear to be artifacts or autocomplete noise (e.g. 'all day', 'the user', 'honestly', 'right now'). Only report genuine, distinctive stylistic patterns. Never use the phrase 'the user' in your output — describe patterns in third person ('tends to', 'favors', 'prefers').";

            foreach (var group in categoryGroups)
            {
                if (ct.IsCancellationRequested) break;

                var samples = group
                    .OrderByDescending(e => e.SourceWeight)
                    .ThenByDescending(e => e.Timestamp)
                    .Take(MaxSamplesPerCategory)
                    .ToList();
                var category = group.Key;
                Log($"Generating for {category} ({samples.Count} samples)");

                var userPrompt = BuildCategoryPrompt(category, samples);
                var result = await engine.GenerateTextAsync(systemPrompt, userPrompt, 200, ct);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    newProfile.CategoryProfiles[category] = result.Trim();
                    Log($"{category}: {result[..Math.Min(80, result.Length)]}...");
                }
            }

            if (ct.IsCancellationRequested) return;

            var contextGroups = entries
                .Where(e => e.SourceType == LearningSourceType.NativeWriting && !string.IsNullOrWhiteSpace(e.SubcontextKey))
                .GroupBy(e => e.SubcontextKey)
                .Where(g => g.Count() >= 6)
                .OrderByDescending(g => g.Count())
                .Take(6)
                .ToList();

            foreach (var group in contextGroups)
            {
                if (ct.IsCancellationRequested) break;

                var samples = group
                    .OrderByDescending(e => e.SourceWeight)
                    .ThenByDescending(e => e.Timestamp)
                    .Take(18)
                    .ToList();
                var contextKey = group.Key;
                var firstSample = samples.FirstOrDefault();
                if (firstSample == null) continue;
                var contextLabel = firstSample.ContextLabel;
                var userPrompt = BuildCategoryPrompt($"{firstSample.Category} / {contextLabel}", samples);
                var result = await engine.GenerateTextAsync(systemPrompt, userPrompt, 180, ct);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    newProfile.ContextProfiles[contextKey] = result.Trim();
                    newProfile.ContextLabels[contextKey] = contextLabel;
                    Log($"Context {contextLabel}: {result[..Math.Min(80, result.Length)]}...");
                }
            }

            var allSamples = entries.OrderByDescending(e => e.Timestamp).Take(MaxSamplesForGeneral).ToList();
            var generalSystemPrompt = "You analyze writing patterns. Output ONLY a concise overall style profile (2-3 sentences, max 100 words) describing general writing tendencies. Be specific and observant. Ignore any repetitive filler phrases that appear to be autocomplete artifacts. Never use the phrase 'the user' — describe patterns impersonally ('tends to', 'favors', 'prefers').";
            var generalPrompt = BuildGeneralPrompt(allSamples);
            var generalResult = await engine.GenerateTextAsync(generalSystemPrompt, generalPrompt, 200, ct);
            if (!string.IsNullOrWhiteSpace(generalResult))
            {
                newProfile.GeneralProfile = generalResult.Trim();
                Log($"General: {generalResult[..Math.Min(80, generalResult.Length)]}...");
            }

            // Sub-Phase D: push a quality snapshot so LearningScoreService can detect
            // sustained quality drift across multiple profile refresh cycles.
            float avgQuality = entries.Count > 0
                ? (float)entries.Average(e => e.QualityScore)
                : 0.5f;

            newProfile.QualitySnapshots.Add(new StyleProfileData.QualitySnapshot
            {
                Timestamp   = DateTime.UtcNow,
                AvgQuality  = avgQuality,
                SampleCount = entries.Count
            });
            // Keep only the last 3 snapshots — oldest drops off automatically.
            if (newProfile.QualitySnapshots.Count > 3)
                newProfile.QualitySnapshots.RemoveRange(0, newProfile.QualitySnapshots.Count - 3);

            Log($"Quality snapshot: avg={avgQuality:F2} samples={entries.Count}");

            lock (_lock)
            {
                if (ct.IsCancellationRequested) return;
                _profile = newProfile;
                SaveProfile(newProfile);
            }
            Log("Profile generation complete");

            // Notify subscribers (e.g. LearningScoreService) so they can recompute scores.
            ProfileUpdated?.Invoke();
        }
        catch (OperationCanceledException) { Log("Generation cancelled"); }
        catch (Exception ex) { Log($"Generate error: {ex}"); }
        finally { lock (_lock) { _isGenerating = false; } }
    }

    private static string BuildCategoryPrompt(string category, List<StyleTrackingEntry> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Analyze these text completions a user accepted in a {category} context.");
        sb.AppendLine("Each shows: [what they typed] -> [completion they accepted].");
        sb.AppendLine();
        foreach (var s in samples)
            sb.AppendLine($"[{s.Prefix}] -> [{s.Completion}]");
        sb.AppendLine();
        sb.AppendLine("Identify the user's distinctive writing patterns: tone and formality level, vocabulary preferences, sentence length and structure, common phrases or expressions, punctuation habits.");
        return sb.ToString();
    }

    private static string BuildGeneralPrompt(List<StyleTrackingEntry> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze these text completions a user accepted across all application contexts.");
        sb.AppendLine("Each shows: [what they typed] -> [completion they accepted].");
        sb.AppendLine();
        foreach (var s in samples)
            sb.AppendLine($"[{s.Prefix}] -> [{s.Completion}]");
        sb.AppendLine();
        sb.AppendLine("Describe this user's overall writing style in 2-3 sentences.");
        return sb.ToString();
    }

    private List<StyleTrackingEntry> LoadAcceptedEntries()
    {
        var entries = new List<StyleTrackingEntry>();
        try
        {
            var snapshot = _repository.GetSnapshot(forceRefresh: true);
            foreach (var evidence in snapshot.PositiveEvidence)
            {
                if (string.IsNullOrWhiteSpace(evidence.Completion) || IsCompletionContaminated(evidence.Completion))
                    continue;

                if (evidence.SourceWeight < 0.35f)
                    continue;

                entries.Add(new StyleTrackingEntry
                {
                    Timestamp = evidence.TimestampUtc,
                    Action = "accepted",
                    Prefix = evidence.Prefix,
                    Completion = evidence.Completion,
                    App = evidence.ProcessName,
                    Window = evidence.WindowLabel,
                    Category = evidence.Category,
                    QualityScore = evidence.QualityScore,
                    SourceWeight = evidence.SourceWeight,
                    SourceType = evidence.SourceType,
                    SubcontextKey = evidence.SubcontextKey,
                    ContextLabel = evidence.SubcontextLabel
                });
            }
        }
        catch (Exception ex) { Log($"Read error: {ex}"); }
        return entries;
    }

    /// <summary>
    /// Filters out completions with known contamination (prompt leakage, repetitive patterns)
    /// so they don't influence style profile generation. Delegates to the shared filter.
    /// </summary>
    private static bool IsCompletionContaminated(string completion) =>
        ContaminationFilter.IsContaminated(completion);

    private class StyleTrackingEntry
    {
        public DateTime Timestamp    { get; set; }
        public string   Action       { get; set; } = "";
        public string   Prefix       { get; set; } = "";
        public string   Completion   { get; set; } = "";
        public string   App          { get; set; } = "";
        public string   Window       { get; set; } = "";
        public string   Category     { get; set; } = "";
        /// <summary>Sub-Phase A signal — safe default 0.5 for legacy entries.</summary>
        public float    QualityScore { get; set; } = 0.5f;
        public float    SourceWeight { get; set; } = 0.5f;
        public LearningSourceType SourceType { get; set; } = LearningSourceType.LegacyAccepted;
        public string   SubcontextKey { get; set; } = "";
        public string   ContextLabel { get; set; } = "";
    }
}
