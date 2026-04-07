using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeystrokeApp.Services;

/// <summary>
/// Detects sensitive patterns in text so callers can either redact them or
/// block egress entirely for high-risk inputs like secrets and credentials.
/// Supports optional user-defined rules from %AppData%/Keystroke/privacy-rules.json.
/// </summary>
public static partial class SensitiveDataDetector
{
    public sealed record SensitiveMatch(
        string Kind,
        int Start,
        int Length,
        string Replacement,
        bool ShouldBlockPrediction);

    private sealed class CustomRule
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Replacement { get; set; } = "[REDACTED]";
        public bool BlockPrediction { get; set; }
    }

    private static readonly Lazy<List<(Regex Pattern, string Name, string Replacement, bool BlockPrediction)>> CustomRules =
        new(LoadCustomRules);

    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b")]
    private static partial Regex CreditCardCandidateRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?:\+\d{1,3}[-.\s]?)?(?:\(?\d{1,4}\)?[-.\s]?){1,3}\d{3,4}[-.\s]?\d{3,4}\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b")]
    private static partial Regex IPv4Regex();

    [GeneratedRegex(@"\b(?:[A-F0-9]{1,4}:){2,7}[A-F0-9]{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IPv6Regex();

    [GeneratedRegex(@"\b(?:password|passwd|pwd|secret|token|access[_ -]?token|refresh[_ -]?token|api[_ -]?key)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFieldRegex();

    [GeneratedRegex(@"\b(?:sk-[a-zA-Z0-9\-_]{20,}|AIzaSy[a-zA-Z0-9\-_]{30,}|ghp_[a-zA-Z0-9]{36,}|github_pat_[a-zA-Z0-9_]{20,}|AKIA[0-9A-Z]{16}|ASIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z\-_]{20,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedSecretRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b(?:aws_secret_access_key|AWS_SECRET_ACCESS_KEY)\s*[:=]\s*[A-Za-z0-9/+=]{40}\b", RegexOptions.IgnoreCase)]
    private static partial Regex AwsSecretRegex();

    [GeneratedRegex(@"\b(?:-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyBlockRegex();

    [GeneratedRegex(@"\b(?:ya29\.[A-Za-z0-9\-_]+|1//[A-Za-z0-9\-_]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleOAuthTokenRegex();

    [GeneratedRegex(@"\b[a-zA-Z0-9_\-]{20,}\.[a-zA-Z0-9_\-]{20,}\.[a-zA-Z0-9_\-]{20,}\b")]
    private static partial Regex GenericThreePartTokenRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b\d{1,5}\s+[A-Za-z0-9.'#\- ]+\s+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr|Lane|Ln|Court|Ct|Way|Place|Pl)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StreetAddressRegex();

    private static readonly (Func<Regex> Factory, string Name, string Replacement, bool BlockPrediction)[] RegexRules =
    [
        (SsnRegex, "SSN", "[SSN]", true),
        (EmailRegex, "Email", "[EMAIL]", false),
        (PhoneRegex, "Phone", "[PHONE]", false),
        (IPv4Regex, "IPv4", "[IP_ADDRESS]", false),
        (IPv6Regex, "IPv6", "[IP_ADDRESS]", false),
        (PasswordFieldRegex, "PasswordField", "[PASSWORD_FIELD]", true),
        (PrefixedSecretRegex, "ApiKey", "[API_KEY]", true),
        (JwtRegex, "JWT", "[API_TOKEN]", true),
        (BearerTokenRegex, "BearerToken", "[API_TOKEN]", true),
        (AwsSecretRegex, "AwsSecret", "[API_KEY]", true),
        (PrivateKeyBlockRegex, "PrivateKey", "[PRIVATE_KEY]", true),
        (GoogleOAuthTokenRegex, "OAuthToken", "[API_TOKEN]", true),
        (GenericThreePartTokenRegex, "ThreePartToken", "[API_TOKEN]", true),
        (IbanRegex, "IBAN", "[BANK_ACCOUNT]", true),
        (StreetAddressRegex, "Address", "[ADDRESS]", false),
    ];

    public static IReadOnlyList<SensitiveMatch> Detect(string? text)
    {
        var matches = new List<SensitiveMatch>();
        if (string.IsNullOrWhiteSpace(text))
            return matches;

        foreach (Match match in CreditCardCandidateRegex().Matches(text))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (PassesLuhnCheck(digitsOnly))
            {
                matches.Add(new SensitiveMatch(
                    "CreditCard",
                    match.Index,
                    match.Length,
                    "[CREDIT_CARD]",
                    true));
            }
        }

        foreach (var (factory, name, replacement, blockPrediction) in RegexRules)
        {
            foreach (Match match in factory().Matches(text))
            {
                matches.Add(new SensitiveMatch(
                    name,
                    match.Index,
                    match.Length,
                    replacement,
                    blockPrediction));
            }
        }

        foreach (var (pattern, name, replacement, blockPrediction) in CustomRules.Value)
        {
            foreach (Match match in pattern.Matches(text))
            {
                matches.Add(new SensitiveMatch(
                    name,
                    match.Index,
                    match.Length,
                    replacement,
                    blockPrediction));
            }
        }

        return matches
            .OrderBy(m => m.Start)
            .ThenByDescending(m => m.Length)
            .ToList();
    }

    public static bool ContainsBlockingSensitiveData(string? text) =>
        Detect(text).Any(m => m.ShouldBlockPrediction);

    private static List<(Regex Pattern, string Name, string Replacement, bool BlockPrediction)> LoadCustomRules()
    {
        var results = new List<(Regex Pattern, string Name, string Replacement, bool BlockPrediction)>();
        try
        {
            var rulesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Keystroke",
                "privacy-rules.json");

            if (!File.Exists(rulesPath))
                return results;

            var json = File.ReadAllText(rulesPath);
            var rules = JsonSerializer.Deserialize<List<CustomRule>>(json) ?? [];

            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern))
                    continue;

                results.Add((
                    new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase),
                    string.IsNullOrWhiteSpace(rule.Name) ? "CustomRule" : rule.Name,
                    string.IsNullOrWhiteSpace(rule.Replacement) ? "[REDACTED]" : rule.Replacement,
                    rule.BlockPrediction));
            }
        }
        catch
        {
            // Invalid user-defined rules should not break predictions.
        }

        return results;
    }

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
