using AppLock.Models;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AppLock.Views;

// Converter to get first letter of process name
public class FirstLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string str && !string.IsNullOrEmpty(str))
        {
            return str[0].ToString().ToUpper();
        }
        return "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

// Inverts boolean for IsEnabled
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return !b;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return !b;
        }
        return false;
    }
}

// Converts IsLocked boolean to lock icon glyph
public class LockGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isLocked && isLocked)
        {
            return "\uE785"; // Padlock (Locked)
        }
        return "\uE72E"; // Lock (Unlocked)
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

// Converts IsLocked boolean to lock button text
public class LockTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isLocked && isLocked)
        {
            return "Locked";
        }
        return "Lock";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

// Converts null/not-null to Visibility
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isNull = value == null;
        if (parameter is string param && param.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }
        return isNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public sealed partial class ProtectionPage : Page
{
    public RunningProgramsViewModel RunningViewModel { get; }
    public LockedProgramsViewModel LockedViewModel { get; }
    
    private readonly LockManager _lockManager;
    private readonly AuthService _authService;
    private bool _isUpdatingUI;

    public ProtectionPage()
    {
        // Use shared service instances
        _authService = ServiceContainer.AuthService;
        _lockManager = ServiceContainer.LockManager;
        
        RunningViewModel = new RunningProgramsViewModel(_lockManager);
        LockedViewModel = new LockedProgramsViewModel(_lockManager);
        
        InitializeComponent();
        
        _lockManager.ProtectionStateChanged += OnProtectionStateChanged;
        UpdateStatusUI();
    }

    private void OnProtectionStateChanged(object? sender, bool isEnabled)
    {
        UpdateStatusUI();
    }

    private void UpdateStatusUI()
    {
        _isUpdatingUI = true;
        try
        {
            MasterToggle.IsOn = _lockManager.IsProtectionEnabled;
            
            if (_lockManager.IsProtectionEnabled)
            {
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.LimeGreen);
                StatusText.Text = "AppLock protection is active";
            }
            else
            {
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray);
                StatusText.Text = "AppLock protection is ready (currently OFF)";
            }
        }
        finally
        {
            _isUpdatingUI = false;
        }
    }

    private async void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUI)
            return;

        var toggle = (ToggleSwitch)sender;
        var targetState = toggle.IsOn;

        // If the toggle state already matches the current manager state, do nothing
        if (targetState == _lockManager.IsProtectionEnabled)
            return;

        var xamlRoot = this.XamlRoot ?? toggle.XamlRoot;
        if (xamlRoot == null)
        {
            _isUpdatingUI = true;
            try
            {
                toggle.IsOn = _lockManager.IsProtectionEnabled;
            }
            finally
            {
                _isUpdatingUI = false;
            }
            return;
        }

        try
        {
            // Verify password before changing state
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                XamlRoot = xamlRoot
            };

            await dialog.ShowAsync();

            if (dialog.IsAuthenticated)
            {
                // Password verified, apply the change
                if (targetState)
                {
                    _lockManager.EnableProtection();
                }
                else
                {
                    _lockManager.DisableProtection();
                }
            }
            else
            {
                // Password not verified, revert toggle
                _isUpdatingUI = true;
                try
                {
                    toggle.IsOn = _lockManager.IsProtectionEnabled;
                }
                finally
                {
                    _isUpdatingUI = false;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to verify auth for master toggle: {ex.Message}");
            _isUpdatingUI = true;
            try
            {
                toggle.IsOn = _lockManager.IsProtectionEnabled;
            }
            finally
            {
                _isUpdatingUI = false;
            }
        }
    }

    private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Handle tab changes if needed
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is RunningWindow window)
        {
            if (!window.IsLocked)
            {
                RunningViewModel.LockWindowCommand.Execute(window);
            }
        }
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LockRule rule)
        {
            LockedViewModel.UnlockProgramCommand.Execute(rule);
        }
    }
}
