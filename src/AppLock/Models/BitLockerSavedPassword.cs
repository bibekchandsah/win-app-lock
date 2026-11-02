namespace AppLock.Models;

/// <summary>Stores an encrypted BitLocker passphrase for a specific drive volume.</summary>
public sealed class BitLockerSavedPassword
{
    /// <summary>Volume GUID (primary unique key across drive letter changes).</summary>
    public string VolumeGuid { get; set; } = string.Empty;

    /// <summary>Drive letter (e.g., "D:") this saved password applies to.</summary>
    public string DriveLetter { get; set; } = string.Empty;

    /// <summary>DPAPI-encrypted password bytes stored as a Base64 string with per-app entropy.</summary>
    public string EncryptedPasswordBase64 { get; set; } = string.Empty;

    /// <summary>Optional user-supplied label (e.g., "Work USB").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>UTC time when this entry was saved.</summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this saved password is still valid (marked false if changed elsewhere).</summary>
    public bool IsValid { get; set; } = true;
}
