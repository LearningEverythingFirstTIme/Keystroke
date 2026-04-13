using System.Diagnostics;
using System.IO;

namespace KeystrokeApp.Services;

public sealed class LearningContextMaintenanceService
{
    private readonly LearningDatabase? _database;
    private readonly string[] _derivedArtifacts;

    public LearningContextMaintenanceService(
        LearningDatabase? database = null,
        ContextFingerprintService? fingerprints = null,
        string? appDataPath = null)
    {
        var root = appDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");

        _database = database;
        _derivedArtifacts =
        [
            Path.Combine(root, "style-profile.json"),
            Path.Combine(root, "vocabulary-profile.json"),
            Path.Combine(root, "learning-scores.json"),
            Path.Combine(root, "correction-patterns.json"),
            Path.Combine(root, "context-adaptive-settings.json")
        ];
    }

    public void ClearContext(string contextKey)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return;

        _database?.DeleteBySubcontextKey(contextKey);
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

        _database?.DeleteAssistBySubcontextKey(contextKey);
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
}
