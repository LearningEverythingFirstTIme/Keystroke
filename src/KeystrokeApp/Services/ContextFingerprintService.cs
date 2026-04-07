using System.Security.Cryptography;
using System.Text;

namespace KeystrokeApp.Services;

public sealed class ContextFingerprintService
{
    public ContextFingerprint Create(
        string processName,
        string windowTitle,
        string? screenText = null,
        string? rollingContext = null)
    {
        var category = AppCategory.GetEffectiveCategory(processName, windowTitle);
        var normalizedProcess = Normalize(processName);
        var windowFamily = BuildWindowFamily(windowTitle);
        var subcontext = BuildSubcontext(category, processName, windowTitle, screenText, rollingContext);

        double confidence = 0.2;
        if (!string.IsNullOrWhiteSpace(normalizedProcess))
            confidence += 0.2;
        if (!string.IsNullOrWhiteSpace(windowFamily))
            confidence += 0.2;
        if (!string.IsNullOrWhiteSpace(subcontext.Key))
            confidence += 0.3;
        if (!string.IsNullOrWhiteSpace(screenText) || !string.IsNullOrWhiteSpace(rollingContext))
            confidence += 0.1;

        return new ContextFingerprint
        {
            Category = category.ToString(),
            ProcessKey = BuildOpaqueKey("process", normalizedProcess),
            WindowKey = BuildOpaqueKey("window", windowFamily),
            SubcontextKey = BuildOpaqueKey("subcontext", subcontext.Key),
            ProcessLabel = string.IsNullOrWhiteSpace(processName) ? category.ToString() : processName,
            WindowLabel = string.IsNullOrWhiteSpace(windowFamily) ? category.ToString() : windowFamily,
            SubcontextLabel = string.IsNullOrWhiteSpace(subcontext.Label) ? GetFallbackLabel(category, processName) : subcontext.Label,
            Confidence = Math.Round(Math.Clamp(confidence, 0.05, 0.95), 2),
            SafeContextLabel = $"{processName} ({category})"
        };
    }

    private static (string Key, string Label) BuildSubcontext(
        AppCategory.Category category,
        string processName,
        string windowTitle,
        string? screenText,
        string? rollingContext)
    {
        var title = windowTitle?.Trim() ?? "";
        var parts = title
            .Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.Equals(p, processName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string primary = parts.Count > 0 ? parts[0] : title;

        switch (category)
        {
            case AppCategory.Category.Chat:
                return BuildLabeledKey(
                    !string.IsNullOrWhiteSpace(primary) ? primary : ExtractTopic(screenText, rollingContext),
                    "chat");

            case AppCategory.Category.Email:
                return BuildLabeledKey(
                    !string.IsNullOrWhiteSpace(primary) ? primary : ExtractTopic(screenText, rollingContext),
                    "email");

            case AppCategory.Category.Document:
                return BuildLabeledKey(primary, "doc");

            case AppCategory.Category.Code:
            case AppCategory.Category.Terminal:
                {
                    var candidate = parts.Count > 1 ? parts[0] : ExtractProjectHint(title, rollingContext, screenText);
                    return BuildLabeledKey(candidate, "project");
                }

            case AppCategory.Category.Browser:
                {
                    var topic = ExtractBrowserTopic(title, screenText, rollingContext);
                    return BuildLabeledKey(topic, "browser");
                }

            default:
                return BuildLabeledKey(primary, category.ToString().ToLowerInvariant());
        }
    }

    private static (string Key, string Label) BuildLabeledKey(string? candidate, string prefix)
    {
        var normalized = Normalize(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
            return ("", "");

        var label = candidate!.Trim();
        if (label.Length > 48)
            label = label[..48].TrimEnd() + "...";

        return ($"{prefix}:{normalized}", label);
    }

    private static string BuildWindowFamily(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
            return "";

        var parts = windowTitle
            .Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .ToArray();

        return Normalize(string.Join(" ", parts));
    }

    private static string ExtractBrowserTopic(string title, string? screenText, string? rollingContext)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return title;
        return ExtractTopic(screenText, rollingContext);
    }

    private static string ExtractProjectHint(string title, string? rollingContext, string? screenText)
    {
        var sources = new[] { title, rollingContext, screenText };
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            var tokens = source
                .Split([' ', '\t', '\r', '\n', '\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 3 && char.IsLetter(t[0]))
                .Take(6)
                .ToArray();

            if (tokens.Length > 0)
                return tokens[0];
        }

        return title;
    }

    private static string ExtractTopic(string? screenText, string? rollingContext)
    {
        foreach (var source in new[] { screenText, rollingContext })
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            var tokens = source
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim(',', '.', ':', ';', '!', '?', '"', '\'', '(', ')', '[', ']'))
                .Where(t => t.Length >= 3 && !CommonStopWords.Contains(t))
                .Take(4)
                .ToArray();

            if (tokens.Length > 0)
                return string.Join(" ", tokens);
        }

        return "";
    }

    private static string GetFallbackLabel(AppCategory.Category category, string processName)
    {
        if (!string.IsNullOrWhiteSpace(processName))
            return $"{processName} {category}".Trim();
        return category.ToString();
    }

    private static string BuildOpaqueKey(string prefix, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var input = Encoding.UTF8.GetBytes($"{prefix}:{value}");
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder(value.Length);
        bool previousSeparator = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                previousSeparator = false;
            }
            else if (!previousSeparator)
            {
                sb.Append('-');
                previousSeparator = true;
            }
        }

        return sb.ToString().Trim('-');
    }

    private static readonly HashSet<string> CommonStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "have", "will", "your",
        "just", "into", "about", "they", "them", "you", "are", "but", "not"
    };
}

public sealed class ContextFingerprint
{
    public string Category { get; init; } = "";
    public string ProcessKey { get; init; } = "";
    public string WindowKey { get; init; } = "";
    public string SubcontextKey { get; init; } = "";
    public string ProcessLabel { get; init; } = "";
    public string WindowLabel { get; init; } = "";
    public string SubcontextLabel { get; init; } = "";
    public string SafeContextLabel { get; init; } = "";
    public double Confidence { get; init; }
}
