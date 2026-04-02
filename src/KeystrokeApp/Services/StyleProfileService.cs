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

    private readonly string _profilePath;
    private readonly string _trackingPath;
    private readonly string _logPath;
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

    public StyleProfileService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _profilePath  = Path.Combine(appData, "style-profile.json");
        _trackingPath = Path.Combine(appData, "tracking.jsonl");
        _logPath      = Path.Combine(appData, "style-profile.log");
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

    public string? GetStyleHint(string category)
    {
        lock (_lock)
        {
            if (_profile == null) return null;

            if (_profile.CategoryProfiles.TryGetValue(category, out var catProfile)
                && !string.IsNullOrWhiteSpace(catProfile))
                return catProfile;

            return string.IsNullOrWhiteSpace(_profile.GeneralProfile) ? null : _profile.GeneralProfile;
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

    private void LoadProfile()
    {
        try
        {
            if (!File.Exists(_profilePath)) return;
            var json = File.ReadAllText(_profilePath);
            _profile = JsonSerializer.Deserialize<StyleProfileData>(json);
            Log($"Loaded: categories={_profile?.CategoryProfiles?.Count ?? 0}, general={_profile?.GeneralProfile?.Length ?? 0} chars");
        }
        catch (Exception ex) { Log($"Load error: {ex.Message}"); }
    }

    private void SaveProfile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var json = JsonSerializer.Serialize(_profile, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _profilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _profilePath, overwrite: true);
        }
        catch (Exception ex) { Log($"Save error: {ex.Message}"); }
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

            var systemPrompt = "You analyze writing patterns. Output ONLY a concise style profile (2-3 sentences, max 100 words) that would help an AI match this user's writing style. Be specific and observant, not generic. Focus on tone, vocabulary, sentence structure, and common phrases.";

            foreach (var group in categoryGroups)
            {
                if (ct.IsCancellationRequested) break;

                var samples = group.OrderByDescending(e => e.Timestamp).Take(MaxSamplesPerCategory).ToList();
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

            var allSamples = entries.OrderByDescending(e => e.Timestamp).Take(MaxSamplesForGeneral).ToList();
            var generalSystemPrompt = "You analyze writing patterns. Output ONLY a concise overall style profile (2-3 sentences, max 100 words) describing this user's general writing tendencies. Be specific and observant.";
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

            lock (_lock) { _profile = newProfile; }
            SaveProfile();
            Log("Profile generation complete");

            // Notify subscribers (e.g. LearningScoreService) so they can recompute scores.
            ProfileUpdated?.Invoke();
        }
        catch (OperationCanceledException) { Log("Generation cancelled"); }
        catch (Exception ex) { Log($"Generate error: {ex.Message}"); }
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
            if (!File.Exists(_trackingPath)) return entries;
            var lines = File.ReadAllLines(_trackingPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<StyleTrackingEntry>(line, options);
                    if (entry != null && entry.Action == "accepted" && !string.IsNullOrWhiteSpace(entry.Completion))
                        entries.Add(entry);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log($"Read error: {ex.Message}"); }
        return entries;
    }

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
    }
}
