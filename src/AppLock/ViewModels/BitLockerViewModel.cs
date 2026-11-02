using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Services.BitLocker;

namespace AppLock.ViewModels;

public partial class BitLockerViewModel : ObservableObject
{
    private readonly BitLockerSupportService _supportService;

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    private bool _isSupported;

    public BitLockerViewModel(BitLockerSupportService supportService)
    {
        _supportService = supportService;
        _isSupported = supportService.IsSupported;
        _isElevated = supportService.IsElevated;
    }
}
