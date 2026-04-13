using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

public sealed class LearningContextMaintenanceService
{
    private readonly string _legacyPath;
    private readonly string _eventPath;
    private readonly string[] _derivedArtifacts;
    private readonly ContextFingerprintService _fingerprints;
    private readonly object? _eventWriteLock;
    private readonly object? _legacyWriteLock;

    public LearningContextMaintenanceService(
        ContextFingerprintService? fingerprints = null,
        string? legacyPath = null,
        string? eventPath = null,
        string? appDataPath = null,
        object? eventWriteLock = null,
        object? legacyWriteLock = null)
    {
        var root = appDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");

        _legacyPath = legacyPath ?? Path.Combine(root, "completions.jsonl");
        _eventPath = eventPath ?? Path.Combine(root, "tracking.jsonl");
        _derivedArtifacts =
        [
            Path.Combine(root, "style-profile.json"),
            Path.Combine(root, "vocabulary-profile.json"),
            Path.Combine(root, "learning-scores.json"),
            Path.Combine(root, "correction-patterns.json"),
            Path.Combine(root, "context-adaptive-settings.json")
        ];
        _fingerprints = fingerprints ?? new ContextFingerprintService();
        _eventWriteLock = eventWriteLock;
        _legacyWriteLock = legacyWriteLock;
    }

    public void ClearContext(string contextKey)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return;

        // Acquire the same locks the live writers use so that no appends
        // can land between the read and the atomic replace.
        RunUnderLock(_eventWriteLock, () =>
            RewriteJsonLines(_eventPath, line =>
            {
                var record = JsonSerializer.Deserialize<LearningEventRecord>(line, JsonOptions);
                return record == null || !string.Equals(record.ContextKeys.SubcontextKey, contextKey, StringComparison.OrdinalIgnoreCase);
            }));

        RunUnderLock(_legacyWriteLock, () =>
            RewriteJsonLines(_legacyPath, line =>
            {
                var record = JsonSerializer.Deserialize<LegacyCompletionRecord>(line, JsonOptions);
                if (record == null)
                    return true;

                var fingerprint = _fingerprints.Create(record.App, record.Window);
                return !string.Equals(fingerprint.SubcontextKey, contextKey, StringComparison.OrdinalIgnoreCase);
            }));
    }

    /// <summary>
    /// Removes only assist-preference data (accepted model completions) for a context,
    /// keeping native writing examples and negative evidence. This lets users clear stale
    /// assist patterns without losing their genuine voice data.
    /// </summary>
    public void ClearAssistData(string contextKey)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return;

        // Keep: manual_continuation_committed, suggestion_dismiss, suggestion_typed_past
        // Remove: suggestion_full_accept, suggestion_partial_accept, accepted_text_untouched
        var assistEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "suggestion_full_accept",
            "suggestion_partial_accept",
            "accepted_text_untouched"
        };

        RunUnderLock(_eventWriteLock, () =>
            RewriteJsonLines(_eventPath, line =>
            {
                var record = JsonSerializer.Deserialize<LearningEventRecord>(line, JsonOptions);
                if (record == null) return true;

                // Only remove assist events that match this context
                if (!string.Equals(record.ContextKeys.SubcontextKey, contextKey, StringComparison.OrdinalIgnoreCase))
                    return true;

                return !assistEventTypes.Contains(record.EventType);
            }));

        // Legacy store doesn't distinguish native/assist — skip it for this operation.
    }

    private static void RunUnderLock(object? lockObj, Action action)
    {
        if (lockObj != null)
            lock (lockObj) { action(); }
        else
            action();
    }

    public void InvalidateDerivedArtifacts()
    {
        foreach (var path in _derivedArtifacts)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LearningMaintenance] Failed to delete {path}: {ex.Message}");
            }
        }
    }

    private static void RewriteJsonLines(string path, Func<string, bool> keepLine)
    {
        if (!File.Exists(path))
            return;

        var kept = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            bool keep;
            try
            {
                keep = keepLine(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LearningMaintenance] Skipping malformed line in {path}: {ex.Message}");
                keep = true;
            }

            if (keep)
                kept.Add(line);
        }

        // Write to temp file first, then atomically replace to prevent
        // data corruption if the process crashes mid-write.
        var tempPath = path + ".tmp";
        File.WriteAllLines(tempPath, kept);
        File.Move(tempPath, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class LegacyCompletionRecord
    {
        public string App { get; set; } = "";
        public string Window { get; set; } = "";
    }
}
