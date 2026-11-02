using System.Security.Cryptography;
using System.Text.Json;
using AppLock.Models;

namespace AppLock.Services.BitLocker;

/// <summary>
/// Persists BitLocker passphrases encrypted with DPAPI (current-user scope plus per-app entropy)
/// to %APPDATA%\AppLock\bitlocker-passwords.json.
/// Keyed primarily by Volume GUID (fallback to DriveLetter).
/// Never stores plaintext, never logs plaintext.
/// </summary>
public sealed class SavedPasswordStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppLock",
        "bitlocker-passwords.json");

    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("AppLock.BitLocker.PasswordStore.Entropy.v1");

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

    /// <summary>Finds a saved password matching either VolumeGuid or DriveLetter.</summary>
    public async Task<BitLockerSavedPassword?> GetForVolumeAsync(string? volumeGuid, string? driveLetter)
    {
        await EnsureLoadedAsync();
        var normGuid = NormalizeGuid(volumeGuid);
        var normLetter = NormalizeLetter(driveLetter);

        return _cache.FirstOrDefault(p =>
            (!string.IsNullOrEmpty(normGuid) && string.Equals(NormalizeGuid(p.VolumeGuid), normGuid, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(normLetter) && string.Equals(NormalizeLetter(p.DriveLetter), normLetter, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Returns the saved entry matching driveLetter.</summary>
    public Task<BitLockerSavedPassword?> GetForDriveAsync(string driveLetter) =>
        GetForVolumeAsync(null, driveLetter);

    /// <summary>Decrypts and returns the plaintext passphrase, or null if not found.</summary>
    public async Task<string?> GetDecryptedPassphraseAsync(string? volumeGuid, string? driveLetter)
    {
        var entry = await GetForVolumeAsync(volumeGuid, driveLetter);
        if (entry == null) return null;
        return TryDecrypt(entry.EncryptedPasswordBase64);
    }

    /// <summary>Decrypts and returns the plaintext passphrase for drive letter.</summary>
    public Task<string?> GetDecryptedPassphraseAsync(string driveLetter) =>
        GetDecryptedPassphraseAsync(null, driveLetter);

    /// <summary>Saves (or replaces) the encrypted passphrase for a volume.</summary>
    public async Task SaveAsync(string volumeGuid, string driveLetter, string passphrase, string label = "")
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var normGuid = NormalizeGuid(volumeGuid);
            var normLetter = NormalizeLetter(driveLetter);
            var encrypted = Encrypt(passphrase);

            var existing = _cache.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(normGuid) && string.Equals(NormalizeGuid(p.VolumeGuid), normGuid, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(normLetter) && string.Equals(NormalizeLetter(p.DriveLetter), normLetter, StringComparison.OrdinalIgnoreCase)));

            if (existing != null)
            {
                if (!string.IsNullOrEmpty(normGuid)) existing.VolumeGuid = normGuid;
                if (!string.IsNullOrEmpty(normLetter)) existing.DriveLetter = normLetter;
                existing.EncryptedPasswordBase64 = encrypted;
                existing.SavedAt = DateTime.UtcNow;
                existing.IsValid = true;
                if (!string.IsNullOrEmpty(label)) existing.Label = label;
            }
            else
            {
                _cache.Add(new BitLockerSavedPassword
                {
                    VolumeGuid = normGuid,
                    DriveLetter = normLetter,
                    EncryptedPasswordBase64 = encrypted,
                    Label = label,
                    SavedAt = DateTime.UtcNow,
                    IsValid = true
                });
            }

            await PersistAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Backward-compatible overload for saving by drive letter only.</summary>
    public Task SaveAsync(string driveLetter, string passphrase, string label = "") =>
        SaveAsync(string.Empty, driveLetter, passphrase, label);

    /// <summary>Marks a saved password as invalid (e.g. when unlock fails with it).</summary>
    public async Task<bool> MarkInvalidAsync(string? volumeGuid, string? driveLetter)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var normGuid = NormalizeGuid(volumeGuid);
            var normLetter = NormalizeLetter(driveLetter);

            var entry = _cache.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(normGuid) && string.Equals(NormalizeGuid(p.VolumeGuid), normGuid, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(normLetter) && string.Equals(NormalizeLetter(p.DriveLetter), normLetter, StringComparison.OrdinalIgnoreCase)));

            if (entry != null)
            {
                entry.IsValid = false;
                await PersistAsync();
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the saved entry for a volume.</summary>
    public async Task<bool> DeleteAsync(string? volumeGuid, string? driveLetter)
    {
        await EnsureLoadedAsync();
        await _lock.WaitAsync();
        try
        {
            var normGuid = NormalizeGuid(volumeGuid);
            var normLetter = NormalizeLetter(driveLetter);

            var removed = _cache.RemoveAll(p =>
                (!string.IsNullOrEmpty(normGuid) && string.Equals(NormalizeGuid(p.VolumeGuid), normGuid, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(normLetter) && string.Equals(NormalizeLetter(p.DriveLetter), normLetter, StringComparison.OrdinalIgnoreCase)));

            if (removed > 0) await PersistAsync();
            return removed > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the saved entry for drive letter.</summary>
    public Task<bool> DeleteAsync(string driveLetter) =>
        DeleteAsync(null, driveLetter);

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
        // CurrentUser scope with per-app entropy
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? TryDecrypt(string base64)
    {
        try
        {
            var data = Convert.FromBase64String(base64);
            // Try with per-app entropy
            try
            {
                var plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // Fallback for legacy entries without optional entropy
                var plainLegacy = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(plainLegacy);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SavedPasswordStore] Decrypt failed: {ex.Message}");
            return null;
        }
    }

    private static string NormalizeLetter(string? driveLetter) =>
        string.IsNullOrWhiteSpace(driveLetter) ? string.Empty : driveLetter.TrimEnd('\\').TrimEnd('/').ToUpperInvariant();

    private static string NormalizeGuid(string? guid) =>
        string.IsNullOrWhiteSpace(guid) ? string.Empty : guid.Trim();
}
