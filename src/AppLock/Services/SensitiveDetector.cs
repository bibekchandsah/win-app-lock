using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AppLock.Services;

/// <summary>
/// Detects sensitive data in clipboard text, including credit card numbers (Luhn checked),
/// private keys, JWT/API tokens, and password-like tokens from browsers or password managers.
/// </summary>
public static class SensitiveDetector
{
    private static readonly Regex PrivateKeyRegex = new(
        @"-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JwtRegex = new(
        @"\beyJ[a-zA-Z0-9-_=]+\.[a-zA-Z0-9-_=]+\.[a-zA-Z0-9-_.+/=]+\b",
        RegexOptions.Compiled);

    private static readonly Regex ApiTokenRegex = new(
        @"\b(?:ghp_[a-zA-Z0-9]{36}|github_pat_[a-zA-Z0-9_]{82}|AKIA[0-9A-Z]{16}|xox[baprs]-[0-9a-zA-Z]{10,48}|sk_live_[0-9a-zA-Z]{24,}|AIza[0-9A-Za-z-_]{35}|Bearer\s+[A-Za-z0-9-_~+/=]{20,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CardCandidateRegex = new(
        @"\b(?:\d[ -]*?){13,19}\b",
        RegexOptions.Compiled);

    private static readonly string[] BrowserOrPasswordApps = new[]
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
        "keepass.exe", "bitwarden.exe", "1password.exe", "lastpass.exe", "dashlane.exe"
    };

    /// <summary>
    /// Checks if the text content matches any sensitive data criteria.
    /// </summary>
    public static bool IsSensitive(string? text, string? sourceProcess = null, string? sourceTitle = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();

        // 1. Private key blocks
        if (PrivateKeyRegex.IsMatch(trimmed))
        {
            return true;
        }

        // 2. JWT tokens
        if (JwtRegex.IsMatch(trimmed))
        {
            return true;
        }

        // 3. API keys and Bearer tokens
        if (ApiTokenRegex.IsMatch(trimmed))
        {
            return true;
        }

        // 4. Credit card numbers (validated via Luhn algorithm)
        if (ContainsValidCreditCard(trimmed))
        {
            return true;
        }

        // 5. Password-like single token copied from browser or password context
        if (IsPasswordLikeToken(trimmed, sourceProcess, sourceTitle))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if string contains a sequence of digits that passes Luhn's algorithm.
    /// </summary>
    public static bool ContainsValidCreditCard(string text)
    {
        var matches = CardCandidateRegex.Matches(text);
        foreach (Match match in matches)
        {
            string digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length >= 13 && digits.Length <= 19 && IsLuhnValid(digits))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Luhn algorithm (mod 10 checksum) for credit cards.
    /// </summary>
    public static bool IsLuhnValid(string digits)
    {
        if (string.IsNullOrEmpty(digits)) return false;

        int sum = 0;
        bool alternate = false;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(digits[i])) return false;
            int n = digits[i] - '0';

            if (alternate)
            {
                n *= 2;
                if (n > 9)
                {
                    n -= 9;
                }
            }

            sum += n;
            alternate = !alternate;
        }

        return (sum % 10 == 0);
    }

    /// <summary>
    /// Detects high-entropy single-token passwords (no spaces, 8-128 chars, 3+ character classes).
    /// </summary>
    public static bool IsPasswordLikeToken(string text, string? sourceProcess, string? sourceTitle)
    {
        if (text.Length < 8 || text.Length > 128) return false;
        if (text.Any(char.IsWhiteSpace)) return false;

        // Check character classes
        int classes = 0;
        if (text.Any(char.IsLower)) classes++;
        if (text.Any(char.IsUpper)) classes++;
        if (text.Any(char.IsDigit)) classes++;
        if (text.Any(c => !char.IsLetterOrDigit(c))) classes++;

        if (classes < 3) return false;

        // Context check: browser, password manager, or title suggesting credentials
        if (!string.IsNullOrEmpty(sourceProcess) && BrowserOrPasswordApps.Any(app => string.Equals(app, sourceProcess, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(sourceTitle))
        {
            string titleLower = sourceTitle.ToLowerInvariant();
            if (titleLower.Contains("password") || titleLower.Contains("login") || titleLower.Contains("sign in") || titleLower.Contains("credentials"))
            {
                return true;
            }
        }

        return false;
    }
}
