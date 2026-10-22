using AppLock.Models;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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

// Formats process name with count suffix when multiple windows share the same process
// Usage: Text="{x:Bind local:ProtectionPage.FormatProcessName(ProcessName, WindowCount), Mode=OneWay}"
public class ProcessCountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // value = WindowCount (int), parameter = ProcessName (string)
        int count = value is int c ? c : 1;
        string name = parameter as string ?? string.Empty;
        return count > 1 ? $"{name} (x{count})" : name;
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
    private readonly WindowPicker _windowPicker;
    private bool _isUpdatingUI;

    public ProtectionPage()
    {
        // Use shared service instances
        _authService = ServiceContainer.AuthService;
        _lockManager = ServiceContainer.LockManager;
        _windowPicker = ServiceContainer.WindowPicker;
        
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
                if (StatusSubText != null)
                    StatusSubText.Text = "AppLock is protecting your apps.";
            }
            else
            {
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray);
                StatusText.Text = "AppLock protection is ready (currently OFF)";
                if (StatusSubText != null)
                    StatusSubText.Text = "Turn on protection to enforce locked windows.";
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
                    ServiceContainer.OverlayManager.Start();
                }
                else
                {
                    _lockManager.DisableProtection();
                    ServiceContainer.OverlayManager.Stop();
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

    // --- List item hover visual states ---

    private SolidColorBrush GetHoverBrush()
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        return new SolidColorBrush(isDark
            ? Color.FromArgb(20, 255, 255, 255)   // ~8% white overlay for dark theme
            : Color.FromArgb(13, 0, 0, 0));        // ~5% black overlay for light theme
    }

    private SolidColorBrush GetPressedBrush()
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        return new SolidColorBrush(isDark
            ? Color.FromArgb(35, 255, 255, 255)   // ~14% white overlay for dark theme
            : Color.FromArgb(22, 0, 0, 0));        // ~9% black overlay for light theme
    }

    private void ListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = GetHoverBrush();
    }

    private void ListItem_PointerExited(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void ListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
            border.Background = GetPressedBrush();
    }

    private void ListItem_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Return to hover state after release
        if (sender is Border border)
            border.Background = GetHoverBrush();
    }

    private void CancelPickButton_Click(object sender, RoutedEventArgs e)
    {
        _windowPicker.CancelPick();
    }

    private async void PickWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Get main window HWND
            var mainWindow = (Application.Current as App)?.FindMainWindow();
            if (mainWindow == null)
                return;

            var mainHwnd = new Windows.Win32.Foundation.HWND(
                WinRT.Interop.WindowNative.GetWindowHandle(mainWindow)
            );

            // Switch master card to picking mode
            NormalStatusPanel.Visibility = Visibility.Collapsed;
            PickingStatusPanel.Visibility = Visibility.Visible;
            CancelPickButton.Visibility = Visibility.Visible;
            if (PickWindowButton != null)
                PickWindowButton.IsEnabled = false;

            var result = await _windowPicker.PickWindowAsync(mainHwnd);
            
            if (result != null)
            {
                // Create a lock rule for the picked window
                var rule = new LockRule
                {
                    Id = Guid.NewGuid(),
                    Type = LockRuleType.PickedWindow,
                    ProcessPath = result.ProcessPath,
                    WindowTitlePattern = result.WindowTitle,
                    DisplayName = $"Picked: {result.WindowTitle}",
                    CreatedAt = DateTime.Now
                };

                _lockManager.AddRule(rule);
                
                // Refresh the lists
                RunningViewModel.RefreshWindowsCommand.Execute(null);
                LockedViewModel.RefreshLockedCommand.Execute(null);
                
                // Show confirmation
                var infoBar = new InfoBar
                {
                    Title = "Window Locked",
                    Message = $"Successfully locked: {result.WindowTitle}",
                    Severity = InfoBarSeverity.Success,
                    IsOpen = true
                };
                
                // Find the root grid to add the InfoBar
                if (Content is Grid rootGrid)
                {
                    rootGrid.Children.Insert(0, infoBar);
                    
                    // Auto-close after 3 seconds
                    var timer = new System.Threading.Timer(_ =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            infoBar.IsOpen = false;
                            rootGrid.Children.Remove(infoBar);
                        });
                    }, null, 3000, System.Threading.Timeout.Infinite);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pick window failed: {ex.Message}");
        }
        finally
        {
            // Restore master card to normal status mode
            NormalStatusPanel.Visibility = Visibility.Visible;
            PickingStatusPanel.Visibility = Visibility.Collapsed;
            CancelPickButton.Visibility = Visibility.Collapsed;
            if (PickWindowButton != null)
                PickWindowButton.IsEnabled = true;
        }
    }
}
