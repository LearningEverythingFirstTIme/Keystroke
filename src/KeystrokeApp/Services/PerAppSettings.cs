namespace KeystrokeApp.Services;

public static class PerAppSettings
{
    public const string AllowAllExceptBlocked = "allow_all_except_blocked";
    public const string AllowListedOnly = "allow_listed_only";

    public static bool IsEnabled(AppConfig config, string? processName)
    {
        var normalizedProcess = NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(normalizedProcess))
            return true;

        var blocked = NormalizeProcessList(config.BlockedProcesses);
        if (blocked.Contains(normalizedProcess, StringComparer.OrdinalIgnoreCase))
            return false;

        return NormalizeMode(config.AppFilteringMode) switch
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
}
