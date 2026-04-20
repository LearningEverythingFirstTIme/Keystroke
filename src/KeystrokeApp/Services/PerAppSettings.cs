namespace KeystrokeApp.Services;

public static class PerAppSettings
{
    public const string AllowAllExceptBlocked = "allow_all_except_blocked";
    public const string AllowListedOnly = "allow_listed_only";
    public const string PresetEverywhereExceptBlocked = "everywhere_except_blocked";
    public const string PresetChatAndEmailOnly = "chat_and_email_only";
    public const string PresetWritingAppsOnly = "writing_apps_only";
    public const string PresetManualAllowList = "manual_allow_list";

    private static readonly string[] ChatAndEmailProcesses =
    [
        "discord",
        "slack",
        "teams",
        "olk",
        "outlook",
        "thunderbird",
        "msteams"
    ];

    private static readonly string[] WritingProcesses =
    [
        "code",
        "devenv",
        "discord",
        "idea",
        "idea64",
        "notepad",
        "notepad++",
        "obsidian",
        "olk",
        "outlook",
        "pycharm",
        "slack",
        "sublime_text",
        "teams",
        "thunderbird",
        "webstorm",
        "winword"
    ];

    public static bool IsEnabled(AppConfig config, string? processName)
    {
        var normalizedProcess = NormalizeProcessName(processName);
        var mode = NormalizeMode(config.AppFilteringMode);
        var blocked = NormalizeProcessList(config.BlockedProcesses);

        if (string.IsNullOrWhiteSpace(normalizedProcess))
        {
            // Unknown window — foreground lookup failed (protected process, elevated
            // target, UWP sandbox, process exited mid-check, etc.). Fail safe whenever
            // the user has expressed any app-gating intent: an allow-list mode, or an
            // explicit block list. In the plain default with no guards, stay permissive
            // so ordinary but hard-to-inspect windows still get predictions.
            if (mode == AllowListedOnly) return false;
            if (blocked.Count > 0) return false;
            return true;
        }

        if (blocked.Contains(normalizedProcess, StringComparer.OrdinalIgnoreCase))
            return false;

        return mode switch
        {
            AllowListedOnly => NormalizeProcessList(config.AllowedProcesses)
                .Contains(normalizedProcess, StringComparer.OrdinalIgnoreCase),
            _ => true
        };
    }

    public static string NormalizeMode(string? mode) => mode switch
    {
        AllowListedOnly => AllowListedOnly,
        _ => AllowAllExceptBlocked
    };

    public static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return "";

        var trimmed = processName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        return trimmed.ToLowerInvariant();
    }

    public static List<string> ParseProcessList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return NormalizeProcessList(text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static List<string> NormalizeProcessList(IEnumerable<string>? values)
    {
        if (values == null)
            return [];

        return values
            .Select(NormalizeProcessName)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();
    }

    public static string FormatProcessList(IEnumerable<string>? values) =>
        string.Join(Environment.NewLine, NormalizeProcessList(values));

    public static string GetAvailabilityReason(AppConfig config, string? processName)
    {
        var normalized = NormalizeProcessName(processName);
        var mode = NormalizeMode(config.AppFilteringMode);
        var blocked = NormalizeProcessList(config.BlockedProcesses);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (mode == AllowListedOnly)
                return "Blocked: active app could not be identified (allow-list mode).";
            if (blocked.Count > 0)
                return "Blocked: active app could not be identified.";
            return "No active app detected.";
        }

        if (blocked.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return "Blocked explicitly.";

        return mode switch
        {
            AllowListedOnly when !NormalizeProcessList(config.AllowedProcesses).Contains(normalized, StringComparer.OrdinalIgnoreCase)
                => "Not on the allow list.",
            _ => "Allowed."
        };
    }

    public static void ApplyPreset(AppConfig config, string presetId)
    {
        switch (presetId)
        {
            case PresetChatAndEmailOnly:
                config.AppFilteringMode = AllowListedOnly;
                config.AllowedProcesses = NormalizeProcessList(ChatAndEmailProcesses);
                config.BlockedProcesses = [];
                break;

            case PresetWritingAppsOnly:
                config.AppFilteringMode = AllowListedOnly;
                config.AllowedProcesses = NormalizeProcessList(WritingProcesses);
                config.BlockedProcesses = [];
                break;

            case PresetManualAllowList:
                config.AppFilteringMode = AllowListedOnly;
                config.AllowedProcesses = [];
                break;

            default:
                config.AppFilteringMode = AllowAllExceptBlocked;
                config.AllowedProcesses = [];
                break;
        }
    }
}
