using System.Security.Cryptography;
using System.Text.Json;
using AppLock.Models;

namespace AppLock.Services.BitLocker;

/// <summary>
/// Persists BitLocker passphrases encrypted with DPAPI (current-user scope)
/// to %APPDATA%\AppLock\bitlocker-passwords.json.
/// All encrypt/decrypt operations are synchronous-in-memory; file I/O is async.
/// </summary>
public sealed class SavedPasswordStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppLock",
        "bitlocker-passwords.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private List<BitLockerSavedPassword> _cache = new();
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Returns all saved entries, loading from disk on first call.</summary>
    public async Task<IReadOnlyList<BitLockerSavedPassword>> GetAllAsync()
    {
        await EnsureLoadedAsync();
        return _cache.AsReadOnly();
    }

    /// <summary>Returns the saved entry for <paramref name="driveLetter"/>, or null.</summary>
    public async Task<BitLockerSavedPassword?> GetForDriveAsync(string driveLetter)
    {
        await EnsureLoadedAsync();
        var normalized = NormalizeLetter(driveLetter);
        return _cache.FirstOrDefault(p =>
            string.Equals(NormalizeLetter(p.DriveLetter), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Decrypts and returns the plaintext passphrase for <paramref name="driveLetter"/>, or null if not found.</summary>
    public async Task<string?> GetDecryptedPassphraseAsync(string driveLetter)
    {
        var entry = await GetForDriveAsync(driveLetter);
        if (entry == null) return null;
        return TryDecrypt(entry.EncryptedPasswordBase64);
    }

    /// <summary>Saves (or replaces) the encrypted passphrase for a drive.</summary>
    public async Task SaveAsync(string driveLetter, string passphrase, string label = "")
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var normalized = NormalizeLetter(driveLetter);
            var encrypted = Encrypt(passphrase);

            var existing = _cache.FirstOrDefault(p =>
                string.Equals(NormalizeLetter(p.DriveLetter), normalized, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.EncryptedPasswordBase64 = encrypted;
                existing.SavedAt = DateTime.UtcNow;
                existing.Label = label;
            }
            else
            {
                _cache.Add(new BitLockerSavedPassword
                {
                    DriveLetter = normalized,
                    EncryptedPasswordBase64 = encrypted,
                    Label = label,
                    SavedAt = DateTime.UtcNow
                });
            }

            await PersistAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the saved entry for <paramref name="driveLetter"/>.</summary>
    public async Task<bool> DeleteAsync(string driveLetter)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var normalized = NormalizeLetter(driveLetter);
            var removed = _cache.RemoveAll(p =>
                string.Equals(NormalizeLetter(p.DriveLetter), normalized, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) await PersistAsync();
            return removed > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes all saved passwords.</summary>
    public async Task ClearAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _cache.Clear();
            await PersistAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_loaded) return;
            _cache = await LoadFromDiskAsync();
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<List<BitLockerSavedPassword>> LoadFromDiskAsync()
    {
        try
        {
            if (!File.Exists(StorePath)) return new();
            var json = await File.ReadAllTextAsync(StorePath);
            return JsonSerializer.Deserialize<List<BitLockerSavedPassword>>(json, _jsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SavedPasswordStore] Load failed: {ex.Message}");
            return new();
        }
    }

    private async Task PersistAsync()
    {
        // Caller must hold _lock
        try
        {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_cache, _jsonOptions);
            await File.WriteAllTextAsync(StorePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SavedPasswordStore] Save failed: {ex.Message}");
        }
    }

    private static string Encrypt(string plaintext)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(plaintext);
        // CurrentUser scope — only decryptable on this machine by this user
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? TryDecrypt(string base64)
    {
        try
        {
            var data = Convert.FromBase64String(base64);
            var plain = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SavedPasswordStore] Decrypt failed: {ex.Message}");
            return null;
        }
    }

    private static string NormalizeLetter(string driveLetter) =>
        driveLetter.TrimEnd('\\').TrimEnd('/').ToUpperInvariant();
}

