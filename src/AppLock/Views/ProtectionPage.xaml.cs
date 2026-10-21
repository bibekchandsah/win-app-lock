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

public sealed partial class ProtectionPage : Page
{
    public RunningProgramsViewModel RunningViewModel { get; }
    public LockedProgramsViewModel LockedViewModel { get; }
    
    private readonly LockManager _lockManager;
    private readonly AuthService _authService;

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

    private async void MasterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        var targetState = toggle.IsOn;

        // Verify password before changing state
        var dialog = new AuthDialog(ServiceContainer.AuthService)
        {
            XamlRoot = this.XamlRoot
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
            toggle.Toggled -= MasterToggle_Toggled;
            toggle.IsOn = _lockManager.IsProtectionEnabled;
            toggle.Toggled += MasterToggle_Toggled;
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
                UpdateButtonState(button, true);
            }
        }
    }

    private void RunningProgramsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is RunningWindow window)
        {
            // Schedule update after the item is rendered
            var container = args.ItemContainer;
            container.Loaded += (s, e) =>
            {
                var button = FindDescendant<Button>(container, "LockBtn");
                if (button != null)
                {
                    UpdateButtonState(button, window.IsLocked);
                }
            };
        }
    }

    private void UpdateButtonState(Button button, bool isLocked)
    {
        var content = button.Content as StackPanel;
        if (content != null)
        {
            var icon = content.Children[0] as FontIcon;
            var text = content.Children[1] as TextBlock;

            if (icon != null && text != null)
            {
                if (isLocked)
                {
                    icon.Glyph = "\uE785"; // Padlock
                    text.Text = "Locked";
                    button.IsEnabled = false;
                }
                else
                {
                    icon.Glyph = "\uE72E"; // Lock
                    text.Text = "Lock";
                    button.IsEnabled = true;
                }
            }
        }
    }

    private T? FindDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            if (child is T typedChild && typedChild.Name == name)
            {
                return typedChild;
            }

            var result = FindDescendant<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LockRule rule)
        {
            LockedViewModel.UnlockProgramCommand.Execute(rule);
        }
    }
}
