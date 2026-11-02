namespace AppLock.Models;

/// <summary>Stores an encrypted BitLocker passphrase for a specific drive volume.</summary>
public sealed class BitLockerSavedPassword
{
    /// <summary>Drive letter (e.g., "D:") this saved password applies to.</summary>
    public string DriveLetter { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted password bytes stored as a Base64 string.</summary>
    public string EncryptedPasswordBase64 { get; set; } = string.Empty;

    /// <summary>Optional user-supplied label (e.g., "Work USB").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>UTC time when this entry was saved.</summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

