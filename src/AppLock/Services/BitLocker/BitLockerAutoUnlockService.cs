using System.Diagnostics;
using AppLock.Models;

namespace AppLock.Services.BitLocker;

/// <summary>
/// Service responsible for automatically unlocking BitLocker drives that have saved passwords
/// when AppLock opens, when hotplugged, or in headless mode via Task Scheduler.
/// </summary>
public sealed class BitLockerAutoUnlockService
{
    private readonly BitLockerSupportService _supportService;
    private readonly IBitLockerService _bitLockerService;
    private readonly SavedPasswordStore _savedPasswordStore;
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly Action<string, string>? _notifySuccess;
    private readonly Action<string, string>? _notifyWarning;

    public BitLockerAutoUnlockService(
        BitLockerSupportService supportService,
        IBitLockerService bitLockerService,
        SavedPasswordStore savedPasswordStore,
        AppSettings settings,
        Action<AppSettings> saveSettings,
        Action<string, string>? notifySuccess = null,
        Action<string, string>? notifyWarning = null)
    {
        _supportService = supportService;
        _bitLockerService = bitLockerService;
        _savedPasswordStore = savedPasswordStore;
        _settings = settings;
        _saveSettings = saveSettings;
        _notifySuccess = notifySuccess;
        _notifyWarning = notifyWarning;
    }

    /// <summary>
    /// Checks all detected drives and unlocks any locked drive that has a valid saved password
    /// and is enabled for auto-unlock.
    /// </summary>
    public async Task<int> UnlockEligibleDrivesAsync(bool showNotifications = true, CancellationToken ct = default)
    {
        if (!_supportService.IsSupported || !_supportService.IsElevated)
        {
            Debug.WriteLine("[BitLockerAutoUnlock] BitLocker not supported or not elevated.");
            return 0;
        }

        int unlockedCount = 0;
        try
        {
            var drives = await _bitLockerService.GetDrivesAsync(ct);
            var savedPasswords = await _savedPasswordStore.GetAllAsync();

            if (savedPasswords.Count == 0) return 0;

            foreach (var drive in drives)
            {
                if (drive.LockStatus != LockStatus.Locked) continue;

                // Check if included in auto-unlock
                // If BitLockerAutoUnlockVolumeGuids is empty, treat all saved drives as eligible;
                // otherwise check if drive's guid or letter is listed.
                bool isIncluded = _settings.BitLockerAutoUnlockVolumeGuids.Count == 0 ||
                    (!string.IsNullOrEmpty(drive.VolumeGuid) && _settings.BitLockerAutoUnlockVolumeGuids.Contains(drive.VolumeGuid, StringComparer.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(drive.DriveLetter) && _settings.BitLockerAutoUnlockVolumeGuids.Contains(drive.DriveLetter, StringComparer.OrdinalIgnoreCase));

                if (!isIncluded) continue;

                // Retrieve saved password
                var saved = await _savedPasswordStore.GetForVolumeAsync(drive.VolumeGuid, drive.DriveLetter);
                if (saved == null || !saved.IsValid) continue;

                var passphrase = await _savedPasswordStore.GetDecryptedPassphraseAsync(drive.VolumeGuid, drive.DriveLetter);
                if (string.IsNullOrEmpty(passphrase)) continue;

                Debug.WriteLine($"[BitLockerAutoUnlock] Attempting auto-unlock for {drive.DisplayName}...");
                var result = await _bitLockerService.UnlockWithPassphraseAsync(drive.DriveLetter, passphrase, ct);

                if (result.Success)
                {
                    unlockedCount++;
                    var summary = $"Drive {drive.DriveLetter} ({DateTime.Now:HH:mm})";
                    _settings.BitLockerLastUnlockedSummary = summary;
                    _saveSettings(_settings);

                    if (showNotifications)
                    {
                        _notifySuccess?.Invoke(
                            "BitLocker Auto-Unlock",
                            $"Drive {drive.DisplayName} was automatically unlocked.");
                    }

                    if (_settings.BitLockerOpenDriveAfterUnlock)
                    {
                        BitLockerService.TryOpenDriveInExplorer(drive.DriveLetter);
                    }
                }
                else
                {
                    Debug.WriteLine($"[BitLockerAutoUnlock] Failed to unlock {drive.DriveLetter}: {result.ErrorMessage}");
                    await _savedPasswordStore.MarkInvalidAsync(drive.VolumeGuid, drive.DriveLetter);

                    if (showNotifications)
                    {
                        _notifyWarning?.Invoke(
                            "BitLocker Auto-Unlock Failed",
                            $"Could not unlock {drive.DisplayName}. The saved password may be outdated.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BitLockerAutoUnlock] Error: {ex.Message}");
        }

        return unlockedCount;
    }

#if !APPLOCK_TESTS
    /// <summary>
    /// Executes headless BitLocker auto-unlock for the scheduled task (`--bitlocker-autounlock`).
    /// </summary>
    public static async Task RunHeadlessAsync()
    {
        try
        {
            var support = new BitLockerSupportService();
            if (!support.IsSupported || !support.IsElevated) return;

            var settingsStore = new SettingsStore();
            var settings = settingsStore.Load();

            // If user configured to require Windows Hello before auto-unlocking, prompt for Hello:
            if (settings.BitLockerRequireHelloOnAppStart)
            {
                BitLockerLogger.Log("Headless auto-unlock: Windows Hello verification required by user settings.");
                var hello = new HelloService();
                bool verified = false;
                try
                {
                    if (await hello.IsAvailableAsync())
                    {
                        verified = await hello.RequestVerificationAsync("Unlock BitLocker drives");
                    }
                    else
                    {
                        BitLockerLogger.Log("Headless auto-unlock: Windows Hello is not available.");
                    }
                }
                catch (Exception ex)
                {
                    BitLockerLogger.Log($"Headless auto-unlock: Hello verification error: {ex.Message}");
                }

                if (!verified)
                {
                    BitLockerLogger.Log("Headless auto-unlock aborted: Windows Hello verification was not successful.");
                    return;
                }

                BitLockerLogger.Log("Headless auto-unlock: Windows Hello verified successfully.");
            }

            var bitLockerService = new BitLockerService(support);
            var savedStore = new SavedPasswordStore();
            var notificationService = new NotificationService();

            var service = new BitLockerAutoUnlockService(
                support,
                bitLockerService,
                savedStore,
                settings,
                s => settingsStore.Save(s),
                (t, m) => notificationService.ShowSuccess(t, m),
                (t, m) => notificationService.ShowWarning(t, m));

            await service.UnlockEligibleDrivesAsync(showNotifications: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BitLockerAutoUnlock] Headless run error: {ex.Message}");
        }
    }
#endif
}
