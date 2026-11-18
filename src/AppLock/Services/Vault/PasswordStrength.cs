using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace AppLock.Services.Vault;

/// <summary>
/// Offline password-strength estimator.
/// Checks length and a bundled common-passwords list.
/// Returns a score 0–100 and a label.
/// </summary>
public static class PasswordStrength
{
    private static readonly Lazy<System.Collections.Generic.HashSet<string>> _commonPasswords =
        new(LoadCommonPasswords, isThreadSafe: true);

    public enum Level { VeryWeak, Weak, Fair, Strong, VeryStrong }

    public record Result(int Score, Level Label, string Description, bool MeetsMinimum);

    /// <summary>Minimum character count required by the vault spec.</summary>
    public const int MinLength = 10;

    public static Result Evaluate(string password)
    {
        if (string.IsNullOrEmpty(password))
            return new(0, Level.VeryWeak, "No password", false);

        int len = password.Length;

        // Common password check (case-insensitive)
        bool isCommon = _commonPasswords.Value.Contains(password.ToLowerInvariant());

        // Length score (0–60 points)
        int lengthScore = Math.Min(60, len * 4);

        // Character variety (0–40 points)
        bool hasLower = false, hasUpper = false, hasDigit = false, hasSymbol = false;
        foreach (char c in password)
        {
            if (char.IsLower(c)) hasLower = true;
            else if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else hasSymbol = true;
        }
        int varietyScore = (hasLower ? 10 : 0) + (hasUpper ? 10 : 0) + (hasDigit ? 10 : 0) + (hasSymbol ? 10 : 0);

        int score = lengthScore + varietyScore;

        // Penalty for common passwords
        if (isCommon)
            score = Math.Min(score, 20);

        score = Math.Clamp(score, 0, 100);

        bool meetsMinimum = len >= MinLength && !isCommon;

        Level label = score switch
        {
            < 20 => Level.VeryWeak,
            < 40 => Level.Weak,
            < 60 => Level.Fair,
            < 80 => Level.Strong,
            _    => Level.VeryStrong
        };

        string description = isCommon
            ? "This is a commonly-used password — choose a different one."
            : label switch
            {
                Level.VeryWeak   => "Very weak",
                Level.Weak       => "Weak — add more characters or symbols",
                Level.Fair       => "Fair",
                Level.Strong     => "Strong",
                Level.VeryStrong => "Very strong",
                _                => ""
            };

        return new(score, label, description, meetsMinimum);
    }

    private static System.Collections.Generic.HashSet<string> LoadCommonPasswords()
    {
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try to load from bundled asset file first
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var assetPath = Path.Combine(exeDir, "Assets", "Vault", "common-passwords.txt");
            if (File.Exists(assetPath))
            {
                foreach (var line in File.ReadLines(assetPath, Encoding.UTF8))
                {
                    var t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith('#'))
                        set.Add(t);
                }
                return set;
            }
        }
        catch { }

        // Fallback: a small inline list of the most common passwords
        foreach (var p in FallbackCommonPasswords)
            set.Add(p);

        return set;
    }

    private static readonly string[] FallbackCommonPasswords =
    [
        "password", "password1", "password123", "123456", "12345678", "1234567890",
        "qwerty", "qwerty123", "abc123", "letmein", "monkey", "dragon", "master",
        "123456789", "login", "welcome", "admin", "admin123", "iloveyou", "sunshine",
        "princess", "football", "shadow", "superman", "michael", "jessica", "charlie",
        "donald", "trustno1", "baseball", "batman", "access", "696969", "mustang",
        "passw0rd", "pass@123", "hello123", "test123", "1q2w3e4r", "qwerty1",
        "password!", "mypassword", "password01", "changeme", "default", "hunter2"
    ];
}
