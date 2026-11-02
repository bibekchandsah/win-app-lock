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

    public Action? OnCooldownStarted { get; set; }
    public Action? OnCooldownEnded { get; set; }

    public bool HasSelectedDrive => SelectedDrive != null;
    public bool CanLockSelected => SelectedDrive != null && SelectedDrive.CanLock && IsElevated;
    public bool CanUnlockSelected => SelectedDrive != null && SelectedDrive.CanUnlock && IsElevated;
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
        Action<AppSettings>? saveSettings = null)
    {
        _supportService = supportService;
        _bitLockerService = bitLockerService;
        _volumeWatcher = volumeWatcher;
        _settings = settings;
        _saveSettings = saveSettings;

        _isSupported = supportService.IsSupported;
        _isElevated = supportService.IsElevated;

        if (_settings != null)
        {
            _openDriveAfterUnlock = _settings.BitLockerOpenDriveAfterUnlock;
        }
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
