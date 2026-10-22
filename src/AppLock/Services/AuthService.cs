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
