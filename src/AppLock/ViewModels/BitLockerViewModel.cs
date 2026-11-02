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
    [NotifyPropertyChangedFor(nameof(IsSelectedDriveLocked))]
    [NotifyPropertyChangedFor(nameof(QuickUnlockTitle))]
    [NotifyPropertyChangedFor(nameof(LockTooltip))]
    [NotifyPropertyChangedFor(nameof(UnlockTooltip))]
    [NotifyPropertyChangedFor(nameof(HasSavedPasswordForSelected))]
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
    public bool CanSuspendSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus == ProtectionStatus.On && IsElevated;
    public bool CanBackupSelected => SelectedDrive != null && SelectedDrive.HasProtectors && IsElevated;

    public string LockTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to lock.";
            if (!IsElevated) return "Administrator rights are required to lock drives.";
            if (SelectedDrive.IsOsDrive) return "The OS drive cannot be locked.";
            if (SelectedDrive.IsAppDrive) return "The drive containing AppLock cannot be locked.";
            if (!SelectedDrive.IsUnlocked) return "The drive is already locked.";
            return $"Lock drive {SelectedDrive.DriveLetter} (Force dismount)";
        }
    }

    public string UnlockTooltip
    {
        get
        {
            if (SelectedDrive == null) return "Select a drive to unlock.";
            if (!IsElevated) return "Administrator rights are required to unlock drives.";
            if (!SelectedDrive.IsLocked) return "The drive is already unlocked.";
            if (IsCooldownActive) return $"Unlock is disabled during cooldown ({CooldownSecondsRemaining}s remaining).";
            return $"Unlock drive {SelectedDrive.DriveLetter}";
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

            Drives.Clear();
            DriveItemViewModel? newSelected = null;

            foreach (var d in rawDrives)
            {
                var vm = new DriveItemViewModel(d);
                Drives.Add(vm);

                if (newSelected == null)
                {
                    if (!string.IsNullOrEmpty(selectedLetter) && string.Equals(vm.DriveLetter, selectedLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        newSelected = vm;
                    }
                    else if (!string.IsNullOrEmpty(selectedGuid) && string.Equals(vm.VolumeGuid, selectedGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        newSelected = vm;
                    }
                }
            }

            // Default to first drive if nothing previously selected or match lost
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
