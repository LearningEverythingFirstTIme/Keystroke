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

    // Phone numbers: US formats + international (e.g., +44 20 7946 0958, +49 30 12345678)
    [GeneratedRegex(@"(?:\+\d{1,3}[-.\s]?)?(?:\(?\d{1,4}\)?[-.\s]?){1,3}\d{3,4}[-.\s]?\d{3,4}\b")]
    private static partial Regex PhoneRegex();

    // IP addresses (v4) — validated to 0-255 per octet
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b")]
    private static partial Regex IpAddressRegex();

    // API keys / tokens: long alphanumeric strings with common prefixes
    // Includes: OpenAI sk-, Google AIzaSy, GitHub ghp_, AWS access key AKIA, AWS secret key (40-char base64)
    [GeneratedRegex(@"\b(?:sk-[a-zA-Z0-9\-_]{20,}|AIzaSy[a-zA-Z0-9\-_]{30,}|ghp_[a-zA-Z0-9]{36,}|AKIA[A-Z0-9]{16}|(?:aws_secret_access_key|AWS_SECRET_ACCESS_KEY)\s*[:=]\s*[A-Za-z0-9/+=]{40})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    // Passwords in common patterns like "password: xxx" or "pwd=xxx"
    [GeneratedRegex(@"(?:password|passwd|pwd|secret|token)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFieldRegex();

    private static readonly (Regex Pattern, string Replacement)[] Filters =
    [
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

        // Credit cards need special handling — verify with Luhn check to avoid
        // false positives on order IDs, tracking numbers, etc.
        var result = CreditCardRegex().Replace(text, match =>
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            return PassesLuhnCheck(digitsOnly) ? "[CREDIT_CARD]" : match.Value;
        });

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
