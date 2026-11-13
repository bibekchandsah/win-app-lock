using AppLock.Models;
using AppLock.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels
{
    public partial class KeyboardViewModel : ObservableObject
    {
        private readonly KeyboardService _keyboardService;
        private readonly AppSettings _settings;

        [ObservableProperty]
        private bool _enabled;

        [ObservableProperty]
        private string _statusText = "Loading...";

        [ObservableProperty]
        private bool _isAdmin;

        public KeyboardViewModel(KeyboardService keyboardService, AppSettings settings)
        {
            _keyboardService = keyboardService;
            _settings = settings;
            _isAdmin = KeyboardService.IsAdministrator();
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            Enabled = _keyboardService.IsKeyboardEnabled();
            StatusText = $"Input is currently: {(Enabled ? "Enabled" : "Disabled")}";
            StatusText += $"\nAdministrator rights: {(IsAdmin ? "Yes ✓" : "No ✗")}";
        }
    }
}
