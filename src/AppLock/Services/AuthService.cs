using System.Security.Cryptography;
using AppLock.Models;

namespace AppLock.Services;

public class AuthService
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private int _failedAttempts;
    private DateTime? _lockoutUntil;

    private const int KeySize = 32; // 256 bits
    private const int Iterations = 200000; // PBKDF2 iterations
    private const int SaltSize = 16; // 128 bits

    public AuthService(SettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
    }

    public bool IsFirstRun => _settings.IsFirstRun;

    public bool IsLockedOut
    {
        get
        {
            if (_lockoutUntil.HasValue && DateTime.Now < _lockoutUntil.Value)
            {
                return true;
            }

            // Lockout expired
            if (_lockoutUntil.HasValue && DateTime.Now >= _lockoutUntil.Value)
            {
                _lockoutUntil = null;
                _failedAttempts = 0;
            }

            return false;
        }
    }

    public TimeSpan? LockoutTimeRemaining
    {
        get
        {
            if (_lockoutUntil.HasValue && DateTime.Now < _lockoutUntil.Value)
            {
                return _lockoutUntil.Value - DateTime.Now;
            }
            return null;
        }
    }

    public void SetupPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (password.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters", nameof(password));

        // Generate salt
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);

        // Hash password
        byte[] hash = HashPassword(password, salt);

        // Store protected hash and salt
        _settings.PasswordHash = hash;
        _settings.PasswordSalt = salt;
        _settings.IsFirstRun = false;

        _settingsStore.Save(_settings);
    }

    public bool VerifyPassword(string password)
    {
        if (IsLockedOut)
        {
            throw new InvalidOperationException("Account is locked out due to too many failed attempts");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            RecordFailedAttempt();
            return false;
        }

        try
        {
            // Hash the provided password with the stored salt
            byte[] hash = HashPassword(password, _settings.PasswordSalt);

            // Constant-time comparison
            bool isValid = CryptographicOperations.FixedTimeEquals(hash, _settings.PasswordHash);

            if (isValid)
            {
                // Reset failed attempts on success
                _failedAttempts = 0;
                _lockoutUntil = null;
                return true;
            }
            else
            {
                RecordFailedAttempt();
                return false;
            }
        }
        catch
        {
            RecordFailedAttempt();
            return false;
        }
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (!VerifyPassword(currentPassword))
        {
            throw new UnauthorizedAccessException("Current password is incorrect");
        }

        if (newPassword.Length < 6)
        {
            throw new ArgumentException("New password must be at least 6 characters", nameof(newPassword));
        }

        SetPassword(newPassword);
    }

    public void SetPassword(string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            throw new ArgumentException("Password cannot be empty", nameof(newPassword));

        if (newPassword.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters", nameof(newPassword));

        // Generate new salt
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = HashPassword(newPassword, salt);

        _settings.PasswordHash = hash;
        _settings.PasswordSalt = salt;

        _settingsStore.Save(_settings);

        // Reset failed attempts
        _failedAttempts = 0;
        _lockoutUntil = null;
    }

    // -------------------------------------------------------------------------
    // Per-app custom password helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sets (or replaces) a custom password for a specific lock rule.
    /// The hash+salt are stored directly on the <paramref name="rule"/> object.
    /// Caller is responsible for persisting the settings afterward.
    /// </summary>
    public void SetAppPassword(LockRule rule, string password)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));
        if (password.Length < 4)
            throw new ArgumentException("App password must be at least 4 characters", nameof(password));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = HashPassword(password, salt);
        rule.CustomPasswordHash = hash;
        rule.CustomPasswordSalt = salt;
    }

    /// <summary>
    /// Clears the custom password from a lock rule, reverting to the admin password.
    /// </summary>
    public static void RemoveAppPassword(LockRule rule)
    {
        if (rule == null) return;
        rule.CustomPasswordHash = Array.Empty<byte>();
        rule.CustomPasswordSalt = Array.Empty<byte>();
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against the rule's custom password only.
    /// Returns false if no custom password is set.
    /// </summary>
    public bool VerifyAppPassword(LockRule rule, string password)
    {
        if (rule == null || !rule.HasCustomPassword) return false;
        if (string.IsNullOrWhiteSpace(password)) return false;

        try
        {
            byte[] hash = HashPassword(password, rule.CustomPasswordSalt);
            return CryptographicOperations.FixedTimeEquals(hash, rule.CustomPasswordHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies <paramref name="password"/> against the rule's custom password when one is set,
    /// otherwise falls back to the global admin password.
    /// </summary>
    public bool VerifyAppOrAdminPassword(LockRule? rule, string password)
    {
        if (rule != null && rule.HasCustomPassword)
            return VerifyAppPassword(rule, password);

        return VerifyPassword(password);
    }

    public bool HasSecurityQuestion =>
        !string.IsNullOrWhiteSpace(_settings.SecurityQuestion) &&
        _settings.SecurityAnswerHash != null &&
        _settings.SecurityAnswerHash.Length > 0;

    public string GetSecurityQuestion() => _settings.SecurityQuestion ?? string.Empty;

    public void SetSecurityQuestion(string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Security question cannot be empty", nameof(question));
        if (string.IsNullOrWhiteSpace(answer))
            throw new ArgumentException("Answer cannot be empty", nameof(answer));

        string normalizedAnswer = answer.Trim().ToLowerInvariant();

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = HashPassword(normalizedAnswer, salt);

        _settings.SecurityQuestion = question.Trim();
        _settings.SecurityAnswerHash = hash;
        _settings.SecurityAnswerSalt = salt;

        _settingsStore.Save(_settings);
    }

    public bool VerifySecurityAnswer(string answer)
    {
        if (!HasSecurityQuestion || string.IsNullOrWhiteSpace(answer))
            return false;

        string normalizedAnswer = answer.Trim().ToLowerInvariant();

        try
        {
            byte[] hash = HashPassword(normalizedAnswer, _settings.SecurityAnswerSalt);
            return CryptographicOperations.FixedTimeEquals(hash, _settings.SecurityAnswerHash);
        }
        catch
        {
            return false;
        }
    }

    private byte[] HashPassword(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256);

        return pbkdf2.GetBytes(KeySize);
    }

    private void RecordFailedAttempt()
    {
        _failedAttempts++;

        if (_failedAttempts >= _settings.MaxFailedAttempts)
        {
            _lockoutUntil = DateTime.Now.AddMinutes(_settings.LockoutDurationMinutes);
        }
    }
}
