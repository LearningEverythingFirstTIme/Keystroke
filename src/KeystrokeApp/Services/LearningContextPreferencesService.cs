using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KeystrokeApp.Services;

public sealed class LearningContextPreferencesService
{
    private readonly string _path;
    private readonly object _lock = new();
    private LearningContextPreferencesState _state = new();
    private long _lastSize;
    private DateTime _lastWriteUtc;

    public LearningContextPreferencesService(string? path = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke");
        _path = path ?? Path.Combine(appData, "learning-context-preferences.json");
    }

    public LearningContextPreferencesSnapshot GetSnapshot(bool forceRefresh = false)
    {
        if (forceRefresh || HasChanged())
            Refresh();

        lock (_lock)
        {
            var items = _state.Contexts.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value with { });

            return new LearningContextPreferencesSnapshot
            {
                Items = items,
                PinnedContextKeys = items.Values
                    .Where(v => v.IsPinned)
                    .Select(v => v.ContextKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                DisabledContextKeys = items.Values
                    .Where(v => v.IsDisabled)
                    .Select(v => v.ContextKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public bool IsPinned(string? contextKey)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return false;

        var snapshot = GetSnapshot();
        return snapshot.PinnedContextKeys.Contains(contextKey);
    }

    public bool IsDisabled(string? contextKey)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return false;

        var snapshot = GetSnapshot();
        return snapshot.DisabledContextKeys.Contains(contextKey);
    }

    public void SetPinned(string contextKey, string label, string category, bool isPinned)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return;

        lock (_lock)
        {
            var existing = _state.Contexts.GetValueOrDefault(contextKey)
                ?? new LearningContextPreference
                {
                    ContextKey = contextKey,
                    Label = label,
                    Category = category
                };

            existing = existing with
            {
                Label = string.IsNullOrWhiteSpace(label) ? existing.Label : label,
                Category = string.IsNullOrWhiteSpace(category) ? existing.Category : category,
                IsPinned = isPinned,
                UpdatedAt = DateTime.UtcNow
            };

            if (!existing.IsPinned && !existing.IsDisabled)
                _state.Contexts.Remove(contextKey);
            else
                _state.Contexts[contextKey] = existing;

            SaveLocked();
        }
    }

    public void SetDisabled(string contextKey, string label, string category, bool isDisabled)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return;

        lock (_lock)
        {
            var existing = _state.Contexts.GetValueOrDefault(contextKey)
                ?? new LearningContextPreference
                {
                    ContextKey = contextKey,
                    Label = label,
                    Category = category
                };

            existing = existing with
            {
                Label = string.IsNullOrWhiteSpace(label) ? existing.Label : label,
                Category = string.IsNullOrWhiteSpace(category) ? existing.Category : category,
                IsDisabled = isDisabled,
                UpdatedAt = DateTime.UtcNow
            };

            if (!existing.IsPinned && !existing.IsDisabled)
                _state.Contexts.Remove(contextKey);
            else
                _state.Contexts[contextKey] = existing;

            SaveLocked();
        }
    }

    public void Remove(string contextKey)
    {
        if (string.IsNullOrWhiteSpace(contextKey))
            return;

        lock (_lock)
        {
            if (_state.Contexts.Remove(contextKey))
                SaveLocked();
        }
    }

    public void Refresh()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _state = new LearningContextPreferencesState();
                    _lastSize = 0;
                    _lastWriteUtc = DateTime.MinValue;
                    return;
                }

                var json = File.ReadAllText(_path);
                _state = JsonSerializer.Deserialize<LearningContextPreferencesState>(json)
                    ?? new LearningContextPreferencesState();

                var info = new FileInfo(_path);
                _lastSize = info.Length;
                _lastWriteUtc = info.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContextPreferences] Refresh failed: {ex.Message}");
                _state = new LearningContextPreferencesState();
            }
        }
    }

    private bool HasChanged()
    {
        if (!File.Exists(_path))
            return _lastSize != 0 || _lastWriteUtc != DateTime.MinValue;

        var info = new FileInfo(_path);
        return info.Length != _lastSize || info.LastWriteTimeUtc != _lastWriteUtc;
    }

    private void SaveLocked()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);

        var info = new FileInfo(_path);
        _lastSize = info.Length;
        _lastWriteUtc = info.LastWriteTimeUtc;
    }

    private sealed class LearningContextPreferencesState
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, LearningContextPreference> Contexts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record LearningContextPreference
{
    public string ContextKey { get; init; } = "";
    public string Label { get; init; } = "";
    public string Category { get; init; } = "";
    public bool IsPinned { get; init; }
    public bool IsDisabled { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class LearningContextPreferencesSnapshot
{
    public Dictionary<string, LearningContextPreference> Items { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PinnedContextKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledContextKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
