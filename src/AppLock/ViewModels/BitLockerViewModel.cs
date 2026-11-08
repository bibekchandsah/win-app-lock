using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AppLock.Models;
using AppLock.Services.BitLocker;

namespace AppLock.ViewModels;

public partial class BitLockerViewModel : ObservableObject
{
    private readonly BitLockerSupportService _supportService;
    private readonly IBitLockerService _bitLockerService;
    private readonly VolumeWatcher _volumeWatcher;
    private readonly AppSettings? _settings;
    private readonly Action<AppSettings>? _saveSettings;
    private bool _isRefreshing;
    private readonly SavedPasswordStore? _savedPasswordStore;
    private readonly StartupTaskService? _startupTaskService;

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    private bool _isSupported;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ObservableCollection<DriveItemViewModel> _drives = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDrive))]
    [NotifyPropertyChangedFor(nameof(CanLockSelected))]
    [NotifyPropertyChangedFor(nameof(CanUnlockSelected))]
    [NotifyPropertyChangedFor(nameof(CanAttemptUnlock))]
    [NotifyPropertyChangedFor(nameof(CanTurnOnSelected))]
    [NotifyPropertyChangedFor(nameof(CanTurnOffSelected))]
    [NotifyPropertyChangedFor(nameof(CanSuspendSelected))]
    [NotifyPropertyChangedFor(nameof(CanBackupSelected))]
    [NotifyPropertyChangedFor(nameof(CanSuspendOrResumeSelected))]
    [NotifyPropertyChangedFor(nameof(CanChangePasswordSelected))]
    [NotifyPropertyChangedFor(nameof(CanManageProtectorsSelected))]
    [NotifyPropertyChangedFor(nameof(CanOpenInExplorerSelected))]
    [NotifyPropertyChangedFor(nameof(SuspendButtonLabel))]
    [NotifyPropertyChangedFor(nameof(SuspendButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(IsSelectedDriveLocked))]
    [NotifyPropertyChangedFor(nameof(QuickUnlockTitle))]
    [NotifyPropertyChangedFor(nameof(LockTooltip))]
    [NotifyPropertyChangedFor(nameof(UnlockTooltip))]
    [NotifyPropertyChangedFor(nameof(TurnOnTooltip))]
    [NotifyPropertyChangedFor(nameof(TurnOffTooltip))]
    [NotifyPropertyChangedFor(nameof(SuspendTooltip))]
    [NotifyPropertyChangedFor(nameof(BackupKeyTooltip))]
    [NotifyPropertyChangedFor(nameof(HasSavedPasswordForSelected))]
    [NotifyPropertyChangedFor(nameof(LockLabel))]
    [NotifyPropertyChangedFor(nameof(UnlockLabel))]
    private DriveItemViewModel? _selectedDrive;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private BitLockerStatusSeverity _statusSeverity = BitLockerStatusSeverity.Informational;

    [ObservableProperty]
    private string _lastRefreshedText = "";

    [ObservableProperty]
    private bool _hasDrives;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAttemptUnlock))]
    [NotifyPropertyChangedFor(nameof(CanUnlockSelected))]
    [NotifyPropertyChangedFor(nameof(UnlockTooltip))]
    private bool _isCooldownActive;

    [ObservableProperty]
    private int _cooldownSecondsRemaining;

    [ObservableProperty]
    private string _cooldownMessage = "";

    [ObservableProperty]
    private int _failedUnlockAttempts;

    [ObservableProperty]
    private bool _openDriveAfterUnlock;

    [ObservableProperty]
    private bool _autoUnlockOnAppOpen;

    [ObservableProperty]
    private string _lastUnlockedSummary = "Not recorded";

    [ObservableProperty]
    private bool _toastNotifications = true;

    [ObservableProperty]
    private int _refreshIntervalSeconds = 5;

    [ObservableProperty]
    private bool _autoClearClipboard = true;

    [ObservableProperty]
    private bool _requireHelloOnAppStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDriveAutoUnlockDisplay))]
    private bool _isSelectedDriveAutoUnlock;

    public string SelectedDriveAutoUnlockDisplay => IsSelectedDriveAutoUnlock ? "On" : "Off";

    public Action? OnCooldownStarted { get; set; }
    public Action? OnCooldownEnded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedPasswordForSelected))]
    private bool _hasSavedPasswords;

    public bool HasSelectedDrive => SelectedDrive != null;
    public bool CanLockSelected => SelectedDrive != null && SelectedDrive.CanLock && IsElevated;
    public bool CanUnlockSelected => SelectedDrive != null && SelectedDrive.CanUnlock && IsElevated;
    public bool HasSavedPasswordForSelected => SelectedDrive != null && HasSavedPasswords;
    public bool CanAttemptUnlock => CanUnlockSelected && !IsCooldownActive;
    public bool IsSelectedDriveLocked => SelectedDrive != null && SelectedDrive.IsLocked;
    public string QuickUnlockTitle => SelectedDrive != null ? $"Unlock Volume {SelectedDrive.DisplayName}" : "Unlock Volume";

    public bool CanTurnOnSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus == ProtectionStatus.Off && IsElevated;
    public bool CanTurnOffSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus != ProtectionStatus.Off && IsElevated;
    public bool CanSuspendSelected => CanSuspendOrResumeSelected;
    public bool CanSuspendOrResumeSelected => SelectedDrive != null &&
        (SelectedDrive.ProtectionStatus == ProtectionStatus.On || SelectedDrive.ProtectionStatus == ProtectionStatus.Suspended) &&
        IsElevated;
    public string SuspendButtonLabel => SelectedDrive?.ProtectionStatus == ProtectionStatus.Suspended ? "Resume protection" : "Suspend protection";
    public string SuspendButtonGlyph => SelectedDrive?.ProtectionStatus == ProtectionStatus.Suspended ? "" : "";
    public bool CanBackupSelected => SelectedDrive != null && SelectedDrive.HasProtectors && IsElevated;
    public bool CanChangePasswordSelected => SelectedDrive != null && SelectedDrive.HasPasswordProtector && IsElevated;
    public bool CanManageProtectorsSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus != ProtectionStatus.Off && IsElevated;
    public bool CanOpenInExplorerSelected => SelectedDrive != null && !SelectedDrive.IsLocked;

    public string LockLabel => SelectedDrive != null && !string.IsNullOrEmpty(SelectedDrive.DriveLetter)
        ? $"Lock {SelectedDrive.DriveLetter}"
        : "Lock";

    public string UnlockLabel => SelectedDrive != null && !string.IsNullOrEmpty(SelectedDrive.DriveLetter)
        ? $"Unlock {SelectedDrive.DriveLetter}"
        : "Unlock";

    public string LockTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to lock.";
            if (SelectedDrive.IsOsDrive) return "The OS drive cannot be locked.";
            if (SelectedDrive.IsAppDrive) return "The drive containing AppLock cannot be locked.";
            if (!SelectedDrive.IsUnlocked) return "The drive is already locked.";
            if (!IsElevated) return "Administrator rights are required to lock drives.";
            return $"Lock drive {SelectedDrive.DriveLetter} (Force dismount)";
        }
    }

    public string UnlockTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to unlock.";
            if (!SelectedDrive.IsLocked) return "The drive is already unlocked.";
            if (IsCooldownActive) return $"Unlock is disabled during cooldown ({CooldownSecondsRemaining}s remaining).";
            if (!IsElevated) return "Administrator rights are required to unlock drives.";
            return $"Unlock drive {SelectedDrive.DriveLetter}";
        }
    }

    public string TurnOnTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to turn on BitLocker.";
            if (!IsElevated) return "Administrator rights are required to turn on BitLocker.";
            if (SelectedDrive.ProtectionStatus != ProtectionStatus.Off) return "BitLocker is already enabled on this drive.";
            return $"Turn on BitLocker encryption for drive {SelectedDrive.DriveLetter}";
        }
    }

    public string TurnOffTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to turn off BitLocker.";
            if (!IsElevated) return "Administrator rights are required to turn off BitLocker.";
            if (SelectedDrive.ProtectionStatus == ProtectionStatus.Off) return "BitLocker is not enabled on this drive.";
            return $"Turn off BitLocker encryption (decrypt) for drive {SelectedDrive.DriveLetter}";
        }
    }

    public string SuspendTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to suspend or resume BitLocker.";
            if (!IsElevated) return "Administrator rights are required.";
            if (SelectedDrive.ProtectionStatus == ProtectionStatus.Off) return "BitLocker is not enabled on this drive.";
            if (SelectedDrive.ProtectionStatus == ProtectionStatus.Suspended) return $"Resume BitLocker protection for drive {SelectedDrive.DriveLetter}";
            return $"Suspend BitLocker protection for drive {SelectedDrive.DriveLetter}";
        }
    }

    public string BackupKeyTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to view or back up recovery keys.";
            if (!IsElevated) return "Administrator rights are required.";
            if (!SelectedDrive.HasProtectors) return "No key protectors found for this drive.";
            return $"View and back up BitLocker recovery keys for drive {SelectedDrive.DriveLetter}";
        }
    }

    public BitLockerViewModel(
        BitLockerSupportService supportService,
        IBitLockerService bitLockerService,
        VolumeWatcher volumeWatcher,
        AppSettings? settings = null,
        Action<AppSettings>? saveSettings = null,
        SavedPasswordStore? savedPasswordStore = null,
        StartupTaskService? startupTaskService = null)
    {
        _supportService = supportService;
        _bitLockerService = bitLockerService;
        _volumeWatcher = volumeWatcher;
        _settings = settings;
        _saveSettings = saveSettings;
        _savedPasswordStore = savedPasswordStore;
        _startupTaskService = startupTaskService;

        _isSupported = supportService.IsSupported;
        _isElevated = supportService.IsElevated;

        if (_settings != null)
        {
            _openDriveAfterUnlock = _settings.BitLockerOpenDriveAfterUnlock;
            _autoUnlockOnAppOpen = _settings.BitLockerAutoUnlockOnAppOpen;
            _toastNotifications = _settings.BitLockerToastNotifications;
            _refreshIntervalSeconds = _settings.BitLockerRefreshIntervalSeconds > 0 ? _settings.BitLockerRefreshIntervalSeconds : 5;
            _autoClearClipboard = _settings.BitLockerAutoClearClipboard;
            _requireHelloOnAppStart = _settings.BitLockerRequireHelloOnAppStart;
            if (!string.IsNullOrEmpty(_settings.BitLockerLastUnlockedSummary))
            {
                _lastUnlockedSummary = _settings.BitLockerLastUnlockedSummary;
            }
        }
    }

    /// <summary>Called when SelectedDrive changes — refreshes HasSavedPasswordForSelected.</summary>
    partial void OnSelectedDriveChanged(DriveItemViewModel? value)
    {
        _ = RefreshHasSavedPasswordAsync();
        UpdateSelectedDriveAutoUnlockStatus();
    }

    private void UpdateSelectedDriveAutoUnlockStatus()
    {
        if (_settings == null || SelectedDrive == null)
        {
            IsSelectedDriveAutoUnlock = false;
            return;
        }

        if (_settings.BitLockerAutoUnlockVolumeGuids.Count == 0)
        {
            IsSelectedDriveAutoUnlock = HasSavedPasswords;
        }
        else
        {
            var guid = SelectedDrive.VolumeGuid;
            var letter = SelectedDrive.DriveLetter;
            IsSelectedDriveAutoUnlock = (!string.IsNullOrEmpty(guid) && _settings.BitLockerAutoUnlockVolumeGuids.Contains(guid, StringComparer.OrdinalIgnoreCase)) ||
                                        (!string.IsNullOrEmpty(letter) && _settings.BitLockerAutoUnlockVolumeGuids.Contains(letter, StringComparer.OrdinalIgnoreCase));
        }
    }

    partial void OnAutoUnlockOnAppOpenChanged(bool value)
    {
        if (_settings != null)
        {
            _settings.BitLockerAutoUnlockOnAppOpen = value;
            try
            {
                _saveSettings?.Invoke(_settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save BitLockerAutoUnlockOnAppOpen: {ex.Message}");
            }
        }

        if (value && _startupTaskService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _startupTaskService.CreateOrUpdateTaskAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BitLockerViewModel] Auto setup startup task failed: {ex.Message}");
                }
            });
        }
    }

    partial void OnRequireHelloOnAppStartChanged(bool value)
    {
        if (_settings != null)
        {
            _settings.BitLockerRequireHelloOnAppStart = value;
            try
            {
                _saveSettings?.Invoke(_settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save BitLockerRequireHelloOnAppStart: {ex.Message}");
            }
        }
    }

    public void SetSelectedDriveAutoUnlock(bool enabled)
    {
        if (_settings == null || SelectedDrive == null) return;

        var key = !string.IsNullOrEmpty(SelectedDrive.VolumeGuid) ? SelectedDrive.VolumeGuid : SelectedDrive.DriveLetter;
        if (string.IsNullOrEmpty(key)) return;

        if (enabled)
        {
            if (!_settings.BitLockerAutoUnlockVolumeGuids.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                _settings.BitLockerAutoUnlockVolumeGuids.Add(key);
            }
        }
        else
        {
            _settings.BitLockerAutoUnlockVolumeGuids.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, SelectedDrive.DriveLetter, StringComparison.OrdinalIgnoreCase));
        }

        IsSelectedDriveAutoUnlock = enabled;
        try
        {
            _saveSettings?.Invoke(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save BitLockerAutoUnlockVolumeGuids: {ex.Message}");
        }
    }

    public async Task<StartupTaskInfo> GetStartupTaskStatusAsync(CancellationToken ct = default)
    {
        if (_startupTaskService == null) return new StartupTaskInfo();
        return await _startupTaskService.GetStatusAsync(ct);
    }

    public async Task<bool> CreateStartupTaskAsync(CancellationToken ct = default)
    {
        if (_startupTaskService == null) return false;
        return await _startupTaskService.CreateOrUpdateTaskAsync(ct);
    }

    public async Task<bool> DeleteStartupTaskAsync(CancellationToken ct = default)
    {
        if (_startupTaskService == null) return false;
        return await _startupTaskService.DeleteTaskAsync(ct);
    }

    public async Task<bool> RunStartupTaskNowAsync(CancellationToken ct = default)
    {
        if (_startupTaskService == null) return false;
        return await _startupTaskService.RunTaskNowAsync(ct);
    }

    public bool NeedsSavedPasswordNotice => _settings != null && !_settings.BitLockerHasShownSavedPasswordNotice;

    public void MarkSavedPasswordNoticeShown()
    {
        if (_settings != null)
        {
            _settings.BitLockerHasShownSavedPasswordNotice = true;
            try
            {
                _saveSettings?.Invoke(_settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save BitLockerHasShownSavedPasswordNotice: {ex.Message}");
            }
        }
    }

    private async Task RefreshHasSavedPasswordAsync()
    {
        if (_savedPasswordStore == null || SelectedDrive == null)
        {
            HasSavedPasswords = false;
            return;
        }
        var entry = await _savedPasswordStore.GetForVolumeAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter);
        HasSavedPasswords = entry != null;
    }

    /// <summary>Saves the passphrase for the currently selected drive (called after successful unlock).</summary>
    public async Task SavePassphraseForSelectedAsync(string passphrase, string label = "")
    {
        if (_savedPasswordStore == null || SelectedDrive == null || string.IsNullOrEmpty(passphrase)) return;
        var volumeLabel = !string.IsNullOrEmpty(label) ? label : SelectedDrive.VolumeLabel;
        await _savedPasswordStore.SaveAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter, passphrase, volumeLabel);
        HasSavedPasswords = true;
    }

    /// <summary>Returns the decrypted saved passphrase for the selected drive, or null if none saved.</summary>
    public async Task<string?> GetSavedPassphraseAsync()
    {
        if (_savedPasswordStore == null || SelectedDrive == null) return null;
        return await _savedPasswordStore.GetDecryptedPassphraseAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter);
    }

    /// <summary>Unlocks the selected drive using its saved passphrase. Returns false if no saved password exists.</summary>
    public async Task<bool> UnlockWithSavedPassphraseAsync()
    {
        var passphrase = await GetSavedPassphraseAsync();
        if (passphrase == null) return false;

        var result = await UnlockWithPassphraseAsync(passphrase);
        if (!result.Success && _savedPasswordStore != null && SelectedDrive != null)
        {
            // If the saved password stopped working (changed elsewhere), mark it as invalid
            await _savedPasswordStore.MarkInvalidAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter);
            StatusMessage = "Saved password is no longer valid for this drive. Please enter the new password.";
            StatusSeverity = BitLockerStatusSeverity.Warning;
        }
        return true;
    }

    /// <summary>Deletes the saved passphrase for the selected drive.</summary>
    public async Task DeleteSavedPassphraseAsync()
    {
        if (_savedPasswordStore == null || SelectedDrive == null) return;
        await _savedPasswordStore.DeleteAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter);
        HasSavedPasswords = false;
    }

    /// <summary>Returns all saved password entries (for the Manage dialog).</summary>
    public async Task<IReadOnlyList<BitLockerSavedPassword>> GetAllSavedPasswordsAsync()
    {
        if (_savedPasswordStore == null) return Array.Empty<BitLockerSavedPassword>();
        return await _savedPasswordStore.GetAllAsync();
    }

    /// <summary>Updates a saved password for a drive/volume.</summary>
    public async Task UpdateSavedPasswordAsync(string volumeGuid, string driveLetter, string newPassphrase)
    {
        if (_savedPasswordStore == null || string.IsNullOrEmpty(newPassphrase)) return;
        await _savedPasswordStore.SaveAsync(volumeGuid, driveLetter, newPassphrase);
        await RefreshHasSavedPasswordAsync();
    }

    /// <summary>Removes a saved password by volume guid / drive letter (for the Manage dialog).</summary>
    public async Task RemoveSavedPasswordAsync(string? volumeGuid, string? driveLetter)
    {
        if (_savedPasswordStore == null) return;
        await _savedPasswordStore.DeleteAsync(volumeGuid, driveLetter);
        await RefreshHasSavedPasswordAsync();
    }

    /// <summary>Removes a saved password by drive letter (for backward compatibility).</summary>
    public Task RemoveSavedPasswordAsync(string driveLetter) =>
        RemoveSavedPasswordAsync(null, driveLetter);

    /// <summary>Clears all saved passwords (requires confirmation from caller).</summary>
    public async Task ClearAllSavedPasswordsAsync()
    {
        if (_savedPasswordStore == null) return;
        await _savedPasswordStore.ClearAllAsync();
        HasSavedPasswords = false;
    }

    partial void OnOpenDriveAfterUnlockChanged(bool value)
    {
        if (_settings != null)
        {
            _settings.BitLockerOpenDriveAfterUnlock = value;
            try
            {
                _saveSettings?.Invoke(_settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save BitLockerOpenDriveAfterUnlock: {ex.Message}");
            }
        }
    }

    partial void OnToastNotificationsChanged(bool value)
    {
        if (_settings != null)
        {
            _settings.BitLockerToastNotifications = value;
            try { _saveSettings?.Invoke(_settings); } catch { }
        }
    }

    partial void OnRefreshIntervalSecondsChanged(int value)
    {
        if (_settings != null && value > 0)
        {
            _settings.BitLockerRefreshIntervalSeconds = value;
            try { _saveSettings?.Invoke(_settings); } catch { }
        }
    }

    partial void OnAutoClearClipboardChanged(bool value)
    {
        if (_settings != null)
        {
            _settings.BitLockerAutoClearClipboard = value;
            try { _saveSettings?.Invoke(_settings); } catch { }
        }
    }

    public void RecordFailedAttempt(string? customError = null)
    {
        FailedUnlockAttempts++;
        if (FailedUnlockAttempts >= 5)
        {
            TriggerCooldown(30);
        }
        else
        {
            int remaining = 5 - FailedUnlockAttempts;
            StatusMessage = customError != null
                ? $"{customError} ({remaining} attempt{(remaining == 1 ? "" : "s")} remaining)"
                : $"Wrong password or recovery key. {remaining} attempt{(remaining == 1 ? "" : "s")} remaining.";
            StatusSeverity = BitLockerStatusSeverity.Error;
        }
    }

    public void RecordSuccessfulAttempt()
    {
        FailedUnlockAttempts = 0;
        IsCooldownActive = false;
        CooldownSecondsRemaining = 0;
        CooldownMessage = "";
        OnCooldownEnded?.Invoke();
    }

    public void TriggerCooldown(int seconds = 30)
    {
        FailedUnlockAttempts = 5;
        IsCooldownActive = true;
        CooldownSecondsRemaining = seconds;
        CooldownMessage = $"Too many failed attempts. Cooldown: {seconds}s";
        StatusMessage = $"Too many failed attempts. Cooldown: {seconds}s";
        StatusSeverity = BitLockerStatusSeverity.Error;
        OnCooldownStarted?.Invoke();
    }

    public void TickCooldown()
    {
        if (!IsCooldownActive) return;

        CooldownSecondsRemaining--;
        if (CooldownSecondsRemaining <= 0)
        {
            CooldownSecondsRemaining = 0;
            IsCooldownActive = false;
            FailedUnlockAttempts = 0;
            CooldownMessage = "";
            StatusMessage = "Cooldown period ended. You may try unlocking again.";
            StatusSeverity = BitLockerStatusSeverity.Informational;
            OnCooldownEnded?.Invoke();
        }
        else
        {
            CooldownMessage = $"Too many failed attempts. Cooldown: {CooldownSecondsRemaining}s";
            StatusMessage = $"Too many failed attempts. Cooldown: {CooldownSecondsRemaining}s";
            StatusSeverity = BitLockerStatusSeverity.Error;
        }
    }

    public async Task<BitLockerActionResult> UnlockWithPassphraseAsync(string passphrase, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        if (IsCooldownActive)
            return BitLockerActionResult.Fail($"Unlock is disabled during cooldown ({CooldownSecondsRemaining}s remaining).");

        if (string.IsNullOrEmpty(passphrase))
        {
            StatusMessage = "Please enter a BitLocker password.";
            StatusSeverity = BitLockerStatusSeverity.Warning;
            return BitLockerActionResult.Fail("Password cannot be empty.");
        }

        IsLoading = true;
        StatusMessage = $"Unlocking drive {SelectedDrive.DriveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var driveLetter = SelectedDrive.DriveLetter;
            var result = await _bitLockerService.UnlockWithPassphraseAsync(driveLetter, passphrase, ct);

            if (result.Success)
            {
                RecordSuccessfulAttempt();
                StatusMessage = $"Drive {driveLetter} unlocked successfully.";
                StatusSeverity = BitLockerStatusSeverity.Success;

                if (OpenDriveAfterUnlock)
                {
                    TryOpenDriveInExplorer(driveLetter);
                }

                await RefreshDrivesAsync();
                return result;
            }
            else
            {
                RecordFailedAttempt(result.ErrorMessage);
                return result;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unlocking drive: {ex.Message}");
            RecordFailedAttempt(ex.Message);
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<BitLockerActionResult> UnlockWithRecoveryKeyAsync(string recoveryKey, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        if (IsCooldownActive)
            return BitLockerActionResult.Fail($"Unlock is disabled during cooldown ({CooldownSecondsRemaining}s remaining).");

        if (string.IsNullOrWhiteSpace(recoveryKey))
        {
            StatusMessage = "Please enter a 48-digit recovery key.";
            StatusSeverity = BitLockerStatusSeverity.Warning;
            return BitLockerActionResult.Fail("Recovery key cannot be empty.");
        }

        IsLoading = true;
        StatusMessage = $"Unlocking drive {SelectedDrive.DriveLetter} with recovery key...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var driveLetter = SelectedDrive.DriveLetter;
            var result = await _bitLockerService.UnlockWithNumericalPasswordAsync(driveLetter, recoveryKey, ct);

            if (result.Success)
            {
                RecordSuccessfulAttempt();
                StatusMessage = $"Drive {driveLetter} unlocked with recovery key.";
                StatusSeverity = BitLockerStatusSeverity.Success;

                if (OpenDriveAfterUnlock)
                {
                    TryOpenDriveInExplorer(driveLetter);
                }

                await RefreshDrivesAsync();
                return result;
            }
            else
            {
                RecordFailedAttempt(result.ErrorMessage);
                return result;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unlocking drive with recovery key: {ex.Message}");
            RecordFailedAttempt(ex.Message);
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<string?> GenerateRecoveryPasswordAsync(string driveLetter)
    {
        return await _bitLockerService.GenerateRecoveryPasswordAsync(driveLetter);
    }

    public async Task<string?> GetRecoveryKeyAsync(string protectorId, CancellationToken ct = default)
    {
        if (SelectedDrive == null || string.IsNullOrWhiteSpace(protectorId)) return null;
        var key = await _bitLockerService.GetRecoveryKeyAsync(SelectedDrive.DriveLetter, protectorId, ct);
        if (!string.IsNullOrEmpty(key))
        {
            BitLockerLogger.Log($"Retrieved recovery key for protector {protectorId}", SelectedDrive.DriveLetter);
        }
        return key;
    }

    public async Task<string> GenerateDiagnosticsReportAsync(CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# AppLock BitLocker Diagnostic Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## System & Environment");
        sb.AppendLine($"- OS Architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"- OS Description: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"- Is Elevated (Admin): {IsElevated}");
        sb.AppendLine($"- BitLocker Supported: {IsSupported}");

        var taskInfo = await GetStartupTaskStatusAsync(ct);
        sb.AppendLine($"- Scheduled Task Exists: {taskInfo.Exists}");
        sb.AppendLine($"- Scheduled Task State: {taskInfo.State}");
        sb.AppendLine($"- Scheduled Task Last Run: {taskInfo.LastRunTime}");
        sb.AppendLine($"- Scheduled Task Last Result: {taskInfo.LastResult}");
        sb.AppendLine();

        sb.AppendLine("## Detected Drives");
        if (Drives.Count == 0)
        {
            sb.AppendLine("No drives detected.");
        }
        else
        {
            foreach (var d in Drives)
            {
                sb.AppendLine($"### Drive {d.DisplayName}");
                sb.AppendLine($"- Drive Letter: {d.DriveLetter}");
                sb.AppendLine($"- Volume GUID: {d.VolumeGuid}");
                sb.AppendLine($"- Drive Type: {d.DriveTypeDisplay}");
                sb.AppendLine($"- Protection Status: {d.ProtectionStatusDisplay}");
                sb.AppendLine($"- Lock Status: {d.LockStatusDisplay}");
                sb.AppendLine($"- Conversion Status: {d.ConversionStatus}");
                sb.AppendLine($"- Encryption Percent: {d.EncryptionPercent}%");
                sb.AppendLine($"- Encryption Method: {d.EncryptionMethod}");
                sb.AppendLine($"- Capacity: {d.CapacityDisplay}");
                sb.AppendLine($"- Key Protectors ({d.KeyProtectors.Count}):");
                foreach (var p in d.KeyProtectors)
                {
                    sb.AppendLine($"  - [{p.TypeCode}] {p.TypeName}: {p.Id}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Recent BitLocker Action Logs (Sanitized)");
        var logs = BitLockerLogger.GetRecentLogs();
        if (logs.Count == 0)
        {
            sb.AppendLine("No actions logged yet.");
        }
        else
        {
            foreach (var line in logs)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    public async Task<BitLockerActionResult> TurnOnBitLockerAsync(BitLockerTurnOnRequest request, CancellationToken ct = default)
    {
        if (request == null) return BitLockerActionResult.Fail("Invalid request.");

        IsLoading = true;
        StatusMessage = $"Starting BitLocker encryption on drive {request.DriveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var result = await _bitLockerService.TurnOnBitLockerAsync(request, ct);
            if (result.Success)
            {
                StatusMessage = $"BitLocker encryption initiated for Drive {request.DriveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to enable BitLocker on {request.DriveLetter}: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to enable BitLocker: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<BitLockerActionResult> TurnOffBitLockerAsync(string driveLetter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return BitLockerActionResult.Fail("No drive letter specified.");

        IsLoading = true;
        StatusMessage = $"Decrypting drive {driveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var result = await _bitLockerService.TurnOffBitLockerAsync(driveLetter, ct);
            if (result.Success)
            {
                StatusMessage = $"BitLocker decryption started on drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to turn off BitLocker on {driveLetter}: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to turn off BitLocker: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<BitLockerActionResult> SuspendProtectionAsync(int rebootCount = 1, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        IsLoading = true;
        var driveLetter = SelectedDrive.DriveLetter;
        StatusMessage = $"Suspending protection on drive {driveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var result = await _bitLockerService.SuspendProtectionAsync(driveLetter, rebootCount, ct);
            if (result.Success)
            {
                StatusMessage = $"BitLocker protection suspended on drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Warning;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to suspend protection on {driveLetter}: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to suspend protection: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<BitLockerActionResult> ResumeProtectionAsync(CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        IsLoading = true;
        var driveLetter = SelectedDrive.DriveLetter;
        StatusMessage = $"Resuming protection on drive {driveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var result = await _bitLockerService.ResumeProtectionAsync(driveLetter, ct);
            if (result.Success)
            {
                StatusMessage = $"BitLocker protection resumed on drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to resume protection on {driveLetter}: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to resume protection: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<BitLockerActionResult> ChangePasswordAsync(string oldPassphrase, string newPassphrase, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        IsLoading = true;
        var driveLetter = SelectedDrive.DriveLetter;
        StatusMessage = $"Changing password on drive {driveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var result = await _bitLockerService.ChangePasswordAsync(driveLetter, oldPassphrase, newPassphrase, ct);
            if (result.Success)
            {
                StatusMessage = $"Password changed successfully for drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;

                if (_savedPasswordStore != null)
                {
                    var saved = await _savedPasswordStore.GetForVolumeAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter);
                    if (saved != null)
                    {
                        await _savedPasswordStore.SaveAsync(SelectedDrive.VolumeGuid, SelectedDrive.DriveLetter, newPassphrase, SelectedDrive.VolumeLabel);
                    }
                }

                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to change password on {driveLetter}: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to change password: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<IReadOnlyList<KeyProtectorInfo>> GetKeyProtectorsAsync(CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return Array.Empty<KeyProtectorInfo>();

        return await _bitLockerService.GetKeyProtectorsAsync(SelectedDrive.DriveLetter, ct);
    }

    public async Task<BitLockerActionResult> DeleteKeyProtectorAsync(string protectorId, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        var driveLetter = SelectedDrive.DriveLetter;
        try
        {
            var result = await _bitLockerService.DeleteKeyProtectorAsync(driveLetter, protectorId, ct);
            if (result.Success)
            {
                StatusMessage = $"Key protector removed from drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to delete key protector: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to delete protector: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
    }

    public async Task<BitLockerActionResult> AddPasswordProtectorAsync(string passphrase, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        var driveLetter = SelectedDrive.DriveLetter;
        try
        {
            var result = await _bitLockerService.AddPasswordProtectorAsync(driveLetter, passphrase, ct);
            if (result.Success)
            {
                StatusMessage = $"Password protector added to drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to add password protector: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add protector: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
    }

    public async Task<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(string? recoveryPassword = null, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        var driveLetter = SelectedDrive.DriveLetter;
        try
        {
            var result = await _bitLockerService.AddRecoveryPasswordProtectorAsync(driveLetter, recoveryPassword, ct);
            if (result.Success)
            {
                StatusMessage = $"Recovery key protector added to drive {driveLetter}.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
            }
            else
            {
                StatusMessage = $"Failed to add recovery key: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
            }
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to add recovery key: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
    }

    public async Task<BitLockerActionResult> LockSelectedDriveAsync(bool forceDismount = true, CancellationToken ct = default)
    {
        if (SelectedDrive == null)
            return BitLockerActionResult.Fail("No drive selected.");

        if (!CanLockSelected)
            return BitLockerActionResult.Fail(SelectedDrive.LockDisabledReason);

        IsLoading = true;
        StatusMessage = $"Locking drive {SelectedDrive.DriveLetter}...";
        StatusSeverity = BitLockerStatusSeverity.Informational;

        try
        {
            var driveLetter = SelectedDrive.DriveLetter;
            var result = await _bitLockerService.LockAsync(driveLetter, forceDismount, ct);

            if (result.Success)
            {
                StatusMessage = $"Drive {driveLetter} locked.";
                StatusSeverity = BitLockerStatusSeverity.Success;
                await RefreshDrivesAsync();
                return result;
            }
            else
            {
                StatusMessage = $"Failed to lock drive {driveLetter}: {result.ErrorMessage}";
                StatusSeverity = BitLockerStatusSeverity.Error;
                return result;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error locking drive: {ex.Message}");
            StatusMessage = $"Failed to lock drive {SelectedDrive.DriveLetter}: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
            return BitLockerActionResult.Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public static void TryOpenDriveInExplorer(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return;

        try
        {
            var normalized = BitLockerService.NormalizeDriveLetter(driveLetter) + "\\";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{normalized}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Explorer for {driveLetter}: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RefreshDrivesAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        IsLoading = true;

        try
        {
            var selectedLetter = SelectedDrive?.DriveLetter;
            var selectedGuid = SelectedDrive?.VolumeGuid;

            var rawDrives = await _bitLockerService.GetDrivesAsync();

            var currentByKey = new Dictionary<string, BitLockerDrive>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in rawDrives)
            {
                var key = !string.IsNullOrEmpty(d.DriveLetter) ? d.DriveLetter : d.VolumeGuid;
                if (!string.IsNullOrEmpty(key))
                {
                    currentByKey[key] = d;
                }
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. In-place update existing items, or remove if no longer detected (avoids UI flicker and rebuilds)
            for (int i = Drives.Count - 1; i >= 0; i--)
            {
                var vm = Drives[i];
                var key = !string.IsNullOrEmpty(vm.DriveLetter) ? vm.DriveLetter : vm.VolumeGuid;

                if (!string.IsNullOrEmpty(key) && currentByKey.TryGetValue(key, out var updatedModel))
                {
                    seenKeys.Add(key);
                    vm.Model = updatedModel;
                }
                else
                {
                    Drives.RemoveAt(i);
                }
            }

            // 2. Add newly attached drives
            foreach (var d in rawDrives)
            {
                var key = !string.IsNullOrEmpty(d.DriveLetter) ? d.DriveLetter : d.VolumeGuid;
                if (string.IsNullOrEmpty(key) || !seenKeys.Contains(key))
                {
                    var vm = new DriveItemViewModel(d);
                    Drives.Add(vm);
                }
            }

            // 3. Preserve selected drive
            DriveItemViewModel? newSelected = null;
            if (!string.IsNullOrEmpty(selectedLetter))
            {
                newSelected = Drives.FirstOrDefault(d => string.Equals(d.DriveLetter, selectedLetter, StringComparison.OrdinalIgnoreCase));
            }
            if (newSelected == null && !string.IsNullOrEmpty(selectedGuid))
            {
                newSelected = Drives.FirstOrDefault(d => string.Equals(d.VolumeGuid, selectedGuid, StringComparison.OrdinalIgnoreCase));
            }

            SelectedDrive = newSelected ?? Drives.FirstOrDefault();

            HasDrives = Drives.Count > 0;
            IsEmpty = Drives.Count == 0;

            var timeStr = DateTime.Now.ToString("HH:mm:ss");
            LastRefreshedText = $"Refreshed at {timeStr}";

            // Keep status message if an action (like unlock/lock) set a success/error message recently
            if (StatusSeverity == BitLockerStatusSeverity.Informational)
            {
                StatusMessage = Drives.Count == 0
                    ? "No BitLocker-capable drives found."
                    : $"Ready • {Drives.Count} drive{(Drives.Count == 1 ? "" : "s")} detected.";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh BitLocker drives: {ex.Message}");
            StatusMessage = $"Refresh failed: {ex.Message}";
            StatusSeverity = BitLockerStatusSeverity.Error;
        }
        finally
        {
            IsLoading = false;
            _isRefreshing = false;
        }
    }
}
