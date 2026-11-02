using System.Collections.ObjectModel;
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
    [NotifyPropertyChangedFor(nameof(CanTurnOnSelected))]
    [NotifyPropertyChangedFor(nameof(CanTurnOffSelected))]
    [NotifyPropertyChangedFor(nameof(CanSuspendSelected))]
    [NotifyPropertyChangedFor(nameof(CanBackupSelected))]
    private DriveItemViewModel? _selectedDrive;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _lastRefreshedText = "";

    [ObservableProperty]
    private bool _hasDrives;

    [ObservableProperty]
    private bool _isEmpty;

    public bool HasSelectedDrive => SelectedDrive != null;
    public bool CanLockSelected => SelectedDrive != null && SelectedDrive.CanLock && IsElevated;
    public bool CanUnlockSelected => SelectedDrive != null && SelectedDrive.CanUnlock && IsElevated;
    public bool CanTurnOnSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus == ProtectionStatus.Off && IsElevated;
    public bool CanTurnOffSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus != ProtectionStatus.Off && IsElevated;
    public bool CanSuspendSelected => SelectedDrive != null && SelectedDrive.ProtectionStatus == ProtectionStatus.On && IsElevated;
    public bool CanBackupSelected => SelectedDrive != null && SelectedDrive.HasProtectors && IsElevated;

    public BitLockerViewModel(
        BitLockerSupportService supportService,
        IBitLockerService bitLockerService,
        VolumeWatcher volumeWatcher)
    {
        _supportService = supportService;
        _bitLockerService = bitLockerService;
        _volumeWatcher = volumeWatcher;

        _isSupported = supportService.IsSupported;
        _isElevated = supportService.IsElevated;
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
            StatusMessage = Drives.Count == 0
                ? "No BitLocker-capable drives found."
                : $"Ready • {Drives.Count} drive{(Drives.Count == 1 ? "" : "s")} detected.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to refresh BitLocker drives: {ex.Message}");
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _isRefreshing = false;
        }
    }
}
