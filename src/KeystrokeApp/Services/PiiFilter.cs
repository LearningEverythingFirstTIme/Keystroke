using System.Text.RegularExpressions;

namespace KeystrokeApp.Services;

/// <summary>
/// Filters personally identifiable information (PII) and sensitive data
/// from text before it is sent to external AI providers.
/// </summary>
public static partial class PiiFilter
{
    // --- Compiled regex patterns for common PII ---

    // Credit card numbers: 13-19 digits, optionally separated by spaces or dashes
    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b")]
    private static partial Regex CreditCardRegex();

    // SSN: XXX-XX-XXXX
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SsnRegex();

    // Email addresses
    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    // Phone numbers: various formats (US-centric + international)
    [GeneratedRegex(@"(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b")]
    private static partial Regex PhoneRegex();

    // IP addresses (v4)
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpAddressRegex();

    // API keys / tokens: long alphanumeric strings with common prefixes
    [GeneratedRegex(@"\b(?:sk-[a-zA-Z0-9\-_]{20,}|AIzaSy[a-zA-Z0-9\-_]{30,}|ghp_[a-zA-Z0-9]{36,}|AKIA[A-Z0-9]{16})\b")]
    private static partial Regex ApiKeyRegex();

    // Passwords in common patterns like "password: xxx" or "pwd=xxx"
    [GeneratedRegex(@"(?:password|passwd|pwd|secret|token)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFieldRegex();

    private static readonly (Regex Pattern, string Replacement)[] Filters =
    [
        (CreditCardRegex(),    "[CREDIT_CARD]"),
        (SsnRegex(),           "[SSN]"),
        (ApiKeyRegex(),        "[API_KEY]"),
        (PasswordFieldRegex(), "[PASSWORD_FIELD]"),
        (EmailRegex(),         "[EMAIL]"),
        (PhoneRegex(),         "[PHONE]"),
        (IpAddressRegex(),     "[IP_ADDRESS]"),
    ];

    /// <summary>
    /// Scrub all recognized PII patterns from the input text.
    /// Returns the sanitized text. Returns null/empty inputs unchanged.
    /// </summary>
    public static string? Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;
        foreach (var (pattern, replacement) in Filters)
        {
            result = pattern.Replace(result, replacement);
        }

        return result;
    }

    /// <summary>
    /// Returns true if the credit card number passes the Luhn check,
    /// confirming it's likely a real card number rather than a random digit sequence.
    /// Used to reduce false positives on the credit card pattern.
    /// </summary>
    private static bool PassesLuhnCheck(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(digits[i])) continue;
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
