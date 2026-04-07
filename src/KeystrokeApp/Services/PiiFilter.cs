using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Filters personally identifiable information (PII) and sensitive data
/// from text before it is sent to external AI providers.
/// </summary>
public static class PiiFilter
{
    /// <summary>
     /// Scrub all recognized PII patterns from the input text.
     /// Returns the sanitized text. Returns null/empty inputs unchanged.
     /// </summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var matches = SensitiveDataDetector.Detect(text);
        if (matches.Count == 0)
            return text;

        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        foreach (var match in matches)
        {
            if (match.Start < cursor)
                continue;

            builder.Append(text, cursor, match.Start - cursor);
            builder.Append(match.Replacement);
            cursor = match.Start + match.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }
}
