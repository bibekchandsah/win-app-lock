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
            return "\uE72E"; // Padlock (Locked)
        }
        return "\uE785"; // Lock (Unlocked)
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

// Converts bool to Visibility
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool invert = parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        bool b = value is bool bv && bv;
        return (b ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

// Converts bool HasCustomPassword → "Set custom password" / "Change custom password"
public class CustomPasswordMenuTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? "Change custom password" : "Set custom password";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public sealed partial class ProtectionPage : Page
{
    public RunningProgramsViewModel RunningViewModel { get; }
    public LockedProgramsViewModel LockedViewModel { get; }
    
    private readonly LockManager _lockManager;
    private readonly AuthService _authService;
    private readonly WindowPicker _windowPicker;
    private bool _isUpdatingUI;
    // Tracks which RunningWindow opened the running-tab flyout most recently.
    private RunningWindow? _runningMenuContext;

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
        
        // Subscribe to auto-refresh changes to update icon
        RunningViewModel.PropertyChanged += RunningViewModel_PropertyChanged;
        
        UpdateStatusUI();
        UpdateRefreshIcon();
        CheckAutostartWarning();

        ServiceContainer.LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        Loaded += (s, e) =>
        {
            CheckAutostartWarning();
            ApplyLocalization();
            RunningViewModel.StartAutoRefresh();
        };
        Unloaded += (s, e) =>
        {
            ServiceContainer.LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
            RunningViewModel.StopAutoRefresh();
        };
        ApplyLocalization();
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ApplyLocalization());
    }

    private void ApplyLocalization()
    {
        var loc = ServiceContainer.LocalizationService;
        if (AutostartWarningBar != null)
        {
            AutostartWarningBar.Title = loc["AutostartWarningTitle"];
            AutostartWarningBar.Message = loc["AutostartWarningMessage"];
        }
        if (EnableAutostartButton != null) EnableAutostartButton.Content = loc["AutostartWarningAction"];
        if (SearchBox != null) SearchBox.PlaceholderText = loc["Search"];
        if (LockedSearchBox != null) LockedSearchBox.PlaceholderText = loc["Search"];
        if (RunningProgramsTab != null) RunningProgramsTab.Header = loc["RunningApps"];
        if (LockedProgramsTab != null) LockedProgramsTab.Header = loc["ProtectedApps"];
        if (CancelPickButton != null) CancelPickButton.Content = loc["Cancel"];
        UpdateStatusUI();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        CheckAutostartWarning();
        ApplyLocalization();
    }

    private void CheckAutostartWarning()
    {
        try
        {
            bool isStartup = ServiceContainer.StartupService.IsStartupEnabled();
            AutostartWarningBar.IsOpen = !isStartup;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking autostart warning: {ex.Message}");
        }
    }

    private void EnableAutostartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool success = ServiceContainer.StartupService.SetStartupEnabled(true);
            if (success)
            {
                AutostartWarningBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error enabling autostart: {ex.Message}");
        }
    }

    private void RunningViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunningViewModel.AutoRefreshEnabled))
        {
            UpdateRefreshIcon();
        }
    }

    private void UpdateRefreshIcon()
    {
        // Change icon based on auto-refresh state
        if (RefreshIcon != null)
        {
            RefreshIcon.Glyph = RunningViewModel.AutoRefreshEnabled ? "\uE895" : "\uE72C";
        }
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
                StatusText.Text = ServiceContainer.LocalizationService["MasterProtection"] + " - " + ServiceContainer.LocalizationService["Active"];
                if (StatusSubText != null)
                    StatusSubText.Text = ServiceContainer.LocalizationService["MasterProtectionDesc"];
            }
            else
            {
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Gray);
                StatusText.Text = ServiceContainer.LocalizationService["MasterProtection"] + " - " + ServiceContainer.LocalizationService["Inactive"];
                if (StatusSubText != null)
                    StatusSubText.Text = ServiceContainer.LocalizationService["MasterProtectionDesc"];
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
                    ServiceContainer.NotificationService.ShowSuccess(
                        "Protection Enabled",
                        "Master protection is now active. All locked apps are protected.");
                }
                else
                {
                    _lockManager.DisableProtection();
                    ServiceContainer.OverlayManager.Stop();
                    ServiceContainer.NotificationService.ShowSuccess(
                        "Protection Disabled",
                        "Master protection is now inactive.");
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
                ServiceContainer.NotificationService.ShowError(
                    "Action Cancelled",
                    "Password verification was not completed. Master protection remains unchanged.");
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
            ServiceContainer.NotificationService.ShowError(
                "Protection Toggle Failed",
                $"An error occurred: {ex.Message}");
        }
    }

    private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Handle tab changes if needed
    }

    private void BrowseRunningPrograms_Click(object sender, RoutedEventArgs e)
    {
        MainTabView.SelectedItem = RunningProgramsTab;
    }

    private void LockButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (sender.Tag is RunningWindow window)
        {
            if (!window.IsLocked)
            {
                try
                {
                    RunningViewModel.LockWindowCommand.Execute(window);
                    string title = !string.IsNullOrWhiteSpace(window.WindowTitle)
                        ? window.WindowTitle
                        : window.ProcessName;
                    string message = $"({window.ProcessName}) has been locked.";
                    ServiceContainer.NotificationService.ShowSuccess(title, message);
                }
                catch (Exception ex)
                {
                    ServiceContainer.NotificationService.ShowError(
                        "Lock Failed",
                        $"Failed to lock \"{window.ProcessName}\": {ex.Message}");
                }
            }
        }
    }

    // --- Running Programs tab flyout context tracking ---

    /// <summary>Finds the LockRule that matches a running window, or null if not locked.</summary>
    private LockRule? FindLockRuleForWindow(RunningWindow window)
        => _lockManager.GetMatchingRule(window);

    private void RunningMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;

        // Grab RunningWindow from the SplitButton that owns this flyout
        _runningMenuContext = (flyout.Target as SplitButton)?.Tag as RunningWindow;

        if (_runningMenuContext == null) return;

        var rule = FindLockRuleForWindow(_runningMenuContext);
        bool isLocked = _runningMenuContext.IsLocked || rule != null;
        bool hasCustomPassword = rule?.HasCustomPassword ?? false;

        // Keep model in sync
        _runningMenuContext.HasCustomPassword = hasCustomPassword;

        // Update Unlock item — disabled when the app isn't locked
        if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem unlockItem)
            unlockItem.IsEnabled = isLocked;

        // Update Set/Change password text and enabled state
        if (flyout.Items.Count > 2 && flyout.Items[2] is MenuFlyoutItem pwItem)
        {
            pwItem.IsEnabled = true; // Always enabled so user can directly set custom password without locking first
            pwItem.Text = (isLocked && hasCustomPassword)
                ? "Change custom password"
                : "Set custom password";
        }

        // Update Remove custom password item — only enabled if app is locked AND has custom password
        if (flyout.Items.Count > 3 && flyout.Items[3] is MenuFlyoutItem removeItem)
        {
            removeItem.IsEnabled = isLocked && hasCustomPassword;
        }

        if (flyout.Items.Count > 5 && flyout.Items[5] is MenuFlyoutItem selfDiscItem)
        {
            selfDiscItem.IsEnabled = true;
        }
    }

    private void LockedMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        var rule = (flyout.Target as SplitButton)?.Tag as LockRule;
        if (rule == null) return;

        bool hasCustomPassword = rule.HasCustomPassword;
        if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem pwItem)
        {
            pwItem.Text = hasCustomPassword
                ? "Change custom password"
                : "Set custom password";
        }
        if (flyout.Items.Count > 1 && flyout.Items[1] is MenuFlyoutItem removeItem)
        {
            removeItem.IsEnabled = hasCustomPassword;
        }
        if (flyout.Items.Count > 3 && flyout.Items[3] is MenuFlyoutItem lockedSelfDiscItem)
        {
            lockedSelfDiscItem.IsEnabled = true;
        }
    }

    private void RunningUnlockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = _runningMenuContext;
        if (window == null) return;

        var rule = FindLockRuleForWindow(window);
        if (rule == null)
        {
            ServiceContainer.NotificationService.ShowWarning(
                "Not Locked",
                $"{window.ProcessName} is not currently locked.");
            return;
        }

        try
        {
            LockedViewModel.UnlockProgramCommand.Execute(rule);
            string title = !string.IsNullOrWhiteSpace(window.WindowTitle)
                ? window.WindowTitle
                : window.ProcessName;
            ServiceContainer.NotificationService.ShowSuccess(title,
                $"({window.ProcessName}) has been unlocked.");
            RunningViewModel.RefreshWindowsCommand.Execute(null);
        }
        catch (Exception ex)
        {
            ServiceContainer.NotificationService.ShowError(
                "Unlock Failed",
                $"Failed to unlock \"{window.ProcessName}\": {ex.Message}");
        }
    }

    private async void RunningSetAppPasswordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = _runningMenuContext;
        if (window == null) return;

        var rule = FindLockRuleForWindow(window);
        if (rule == null)
        {
            // App is not locked yet - create new rule and allow setting custom password directly
            rule = new LockRule
            {
                Type = LockRuleType.ProcessName,
                ProcessName = window.ProcessName,
                ProcessPath = window.ProcessPath,
                DisplayName = !string.IsNullOrWhiteSpace(window.WindowTitle)
                    ? "{window.ProcessName} - {window.WindowTitle}"
                    : window.ProcessName,
                Icon = window.Icon
            };
            await ShowSetAppPasswordDialogAsync(rule, window);
            return;
        }
        await ShowSetAppPasswordDialogAsync(rule);
    }

    private async void RunningRemoveAppPasswordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = _runningMenuContext;
        if (window == null) return;

        var rule = FindLockRuleForWindow(window);
        if (rule == null)
        {
            ServiceContainer.NotificationService.ShowWarning(
                "Not Locked",
                $"\"{window.ProcessName}\" is not currently locked.");
            return;
        }
        if (!rule.HasCustomPassword)
        {
            ServiceContainer.NotificationService.ShowWarning(
                "No Custom Password",
                $"\"{window.ProcessName}\" is already using the admin password.");
            return;
        }
        await ShowRemoveAppPasswordDialogAsync(rule);
    }

    private void UnlockButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (sender.Tag is LockRule rule)
        {
            try
            {
                LockedViewModel.UnlockProgramCommand.Execute(rule);
                string title = !string.IsNullOrWhiteSpace(rule.DisplayName)
                    ? rule.DisplayName
                    : rule.ProcessName;
                string message = $"({rule.ProcessName}) has been unlocked.";
                ServiceContainer.NotificationService.ShowSuccess(title, message);
            }
            catch (Exception ex)
            {
                ServiceContainer.NotificationService.ShowError(
                    "Unlock Failed",
                    $"Failed to unlock \"{rule.DisplayName}\": {ex.Message}");
            }
        }
    }

    private async void SetAppPasswordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is LockRule rule)
        {
            await ShowSetAppPasswordDialogAsync(rule);
        }
    }

    private async void RemoveAppPasswordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is LockRule rule)
        {
            if (!rule.HasCustomPassword)
            {
                ServiceContainer.NotificationService.ShowWarning(
                    "No Custom Password",
                    $"{rule.DisplayName} is using the admin password.");
                return;
            }
            await ShowRemoveAppPasswordDialogAsync(rule);
        }
    }

    private async void SelfDisciplineMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is LockRule rule)
        {
            await ShowSelfDisciplineDialogAsync(rule);
        }
    }

    private async void RunningSelfDisciplineMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = _runningMenuContext;
        if (window == null) return;

        var rule = FindLockRuleForWindow(window);
        bool isNewRule = false;
        if (rule == null)
        {
            isNewRule = true;
            rule = new LockRule
            {
                Type = LockRuleType.ProcessName,
                ProcessName = window.ProcessName,
                ProcessPath = window.ProcessPath,
                DisplayName = !string.IsNullOrWhiteSpace(window.WindowTitle)
                    ? $"{window.ProcessName} - {window.WindowTitle}"
                    : window.ProcessName,
                Icon = window.Icon
            };
        }

        bool saved = await ShowSelfDisciplineDialogAsync(rule);
        if (saved && isNewRule)
        {
            ServiceContainer.LockManager.AddRule(rule);
            window.IsLocked = true;
            RunningViewModel.RefreshWindowsCommand.Execute(null);
        }
    }

    private async Task<bool> ShowSelfDisciplineDialogAsync(LockRule rule)
    {
        var xamlRoot = this.XamlRoot;
        if (xamlRoot == null) return false;

        var dialog = new SelfDisciplineDialog(rule)
        {
            XamlRoot = xamlRoot,
            RequestedTheme = this.ActualTheme
        };

        var result = await dialog.ShowAsync();
        return dialog.IsSaved || result == ContentDialogResult.Primary;
    }

    private async Task ShowSetAppPasswordDialogAsync(LockRule rule, RunningWindow? newWindowContext = null)
    {
        var xamlRoot = this.XamlRoot;
        if (xamlRoot == null) return;

        // Build dialog content
        var newPwBox = new PasswordBox
        {
            PlaceholderText = "New app password (min. 4 characters)",
            Margin = new Thickness(0, 0, 0, 8)
        };
        var confirmPwBox = new PasswordBox
        {
            PlaceholderText = "Confirm new app password",
            Margin = new Thickness(0, 0, 0, 4)
        };
        var errorText = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        var hintText = new TextBlock
        {
            Text = rule.HasCustomPassword
                ? $"Update the custom password for \"{rule.DisplayName}\"."
                : $"Set a custom password for \"{rule.DisplayName}\". This password will be used to unlock the application.",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(hintText);
        panel.Children.Add(newPwBox);
        panel.Children.Add(confirmPwBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = rule.HasCustomPassword ? "Change App Password" : "Set App Password",
            Content = panel,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        // Validate on Save
        bool saved = false;
        dialog.PrimaryButtonClick += (d, args) =>
        {
            args.Cancel = true; // keep open until valid
            string pw = newPwBox.Password;
            string confirm = confirmPwBox.Password;

            if (string.IsNullOrWhiteSpace(pw))
            {
                errorText.Text = "Password cannot be empty.";
                errorText.Visibility = Visibility.Visible;
                return;
            }
            if (pw.Length < 4)
            {
                errorText.Text = "Password must be at least 4 characters.";
                errorText.Visibility = Visibility.Visible;
                return;
            }
            if (pw != confirm)
            {
                errorText.Text = "Passwords do not match.";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                ServiceContainer.AuthService.SetAppPassword(rule, pw);

                if (newWindowContext != null)
                {
                    _lockManager.AddRule(rule);
                    newWindowContext.IsLocked = true;
                    newWindowContext.HasCustomPassword = true;
                }
                else
                {
                    ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                }

                saved = true;
                args.Cancel = false;
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
                errorText.Visibility = Visibility.Visible;
            }
        };

        await dialog.ShowAsync();

        if (saved)
        {
            // Notify the UI about the property change
            rule.NotifyCustomPasswordChanged();
            LockedViewModel.RefreshLockedCommand.Execute(null);
            RunningViewModel.UpdateLockStatesCommand?.Execute(null);
            ServiceContainer.NotificationService.ShowSuccess(
                "App Password Set",
                newWindowContext != null
                    ? $"\"{rule.DisplayName}\" is now locked with a custom password."
                    : $"Custom password saved for \"{rule.DisplayName}\".");
        }
    }

    private async Task ShowRemoveAppPasswordDialogAsync(LockRule rule)
    {
        var xamlRoot = this.XamlRoot;
        if (xamlRoot == null) return;

        // Confirm with admin password before removing
        var pwBox = new PasswordBox
        {
            PlaceholderText = "Enter admin password to confirm",
            Margin = new Thickness(0, 8, 0, 4)
        };
        var errorText = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            FontSize = 12,
            Visibility = Visibility.Collapsed
        };
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Remove the custom password from \"{rule.DisplayName}\"? " +
                   "It will revert to the admin password. Confirm with your admin password.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        panel.Children.Add(pwBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Remove App Password",
            Content = panel,
            PrimaryButtonText = "Remove",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = xamlRoot
        };

        bool removed = false;
        dialog.PrimaryButtonClick += (d, args) =>
        {
            args.Cancel = true;
            if (!ServiceContainer.AuthService.VerifyPassword(pwBox.Password))
            {
                errorText.Text = "Incorrect admin password.";
                errorText.Visibility = Visibility.Visible;
                return;
            }
            AuthService.RemoveAppPassword(rule);
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            removed = true;
            args.Cancel = false;
        };

        await dialog.ShowAsync();

        if (removed)
        {
            rule.NotifyCustomPasswordChanged();
            LockedViewModel.RefreshLockedCommand.Execute(null);
            RunningViewModel.UpdateLockStatesCommand?.Execute(null);
            ServiceContainer.NotificationService.ShowSuccess(
                "App Password Removed",
                $"\"{rule.DisplayName}\" will now use the admin password.");
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

    private void RefreshButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        RefreshRunningPrograms(showFeedback: true);
    }

    private void RefreshNowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshRunningPrograms(showFeedback: true);
    }

    /// <summary>
    /// Re-reads the running windows. Only rows that actually changed are touched, so the list updates
    /// in place; the icon spins briefly so the click is acknowledged even when nothing changed.
    /// </summary>
    private void RefreshRunningPrograms(bool showFeedback)
    {
        if (showFeedback)
        {
            try { RefreshSpinStoryboard.Begin(); } catch { }
        }

        RunningViewModel.RefreshWindowsCommand.Execute(null);
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
                // Match against existing running windows to reuse exact process info and icon
                var matchedWindow = RunningViewModel.Windows.FirstOrDefault(w => w.Handle == (nint)result.Hwnd.Value)
                    ?? RunningViewModel.Windows.FirstOrDefault(w => !string.IsNullOrEmpty(w.ProcessPath) && w.ProcessPath.Equals(result.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    ?? RunningViewModel.Windows.FirstOrDefault(w => !string.IsNullOrEmpty(w.WindowTitle) && w.WindowTitle.Equals(result.WindowTitle, StringComparison.OrdinalIgnoreCase));

                if (matchedWindow != null)
                {
                    RunningViewModel.LockWindowCommand.Execute(matchedWindow);
                }
                else
                {
                    string processName = System.IO.Path.GetFileNameWithoutExtension(result.ProcessPath);
                    if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    {
                        processName = result.WindowTitle.Split(new[] { " - " }, StringSplitOptions.None).FirstOrDefault() ?? processName;
                    }

                    var icon = ServiceContainer.WindowEnumerator.GetIcon(result.ProcessPath, processName);
                    string displayName = string.IsNullOrWhiteSpace(result.WindowTitle)
                        ? processName
                        : $"{processName} - {result.WindowTitle}";

                    var rule = new LockRule
                    {
                        Id = Guid.NewGuid(),
                        Type = LockRuleType.ProcessName,
                        ProcessName = processName,
                        ProcessPath = result.ProcessPath,
                        DisplayName = displayName,
                        Icon = icon,
                        CreatedAt = DateTime.Now
                    };

                    _lockManager.AddRule(rule);
                }
                
                // Refresh the lists
                RunningViewModel.RefreshWindowsCommand.Execute(null);
                LockedViewModel.RefreshLockedCommand.Execute(null);
                
                string title = !string.IsNullOrWhiteSpace(result.WindowTitle)
                    ? result.WindowTitle
                    : System.IO.Path.GetFileNameWithoutExtension(result.ProcessPath);
                string procName = System.IO.Path.GetFileName(result.ProcessPath);
                string msg = $"({procName}) has been locked.";

                ServiceContainer.NotificationService.ShowSuccess(title, msg);
            }
            else
            {
                ServiceContainer.NotificationService.ShowWarning(
                    "Selection Cancelled",
                    "Window selection was cancelled.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pick window failed: {ex.Message}");
            ServiceContainer.NotificationService.ShowError(
                "Lock Failed",
                $"Failed to pick window: {ex.Message}");
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
