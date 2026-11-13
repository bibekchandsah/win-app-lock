using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AppLock.ViewModels;
using AppLock.Services;

namespace AppLock.Views;

public sealed partial class NotificationsPage : Page
{
    public NotificationsViewModel ViewModel { get; }

    // True while the page applies saved settings to its controls. The XAML defaults
    // (IsSelected="True", Value="300", ...) fire SelectionChanged/ValueChanged while
    // InitializeComponent runs, which would otherwise write those defaults over the settings
    // the user saved earlier.
    private bool _isLoading;

    public NotificationsPage()
    {
        _isLoading = true;
        this.InitializeComponent();
        ViewModel = new NotificationsViewModel();
        LoadAnimationSettings();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadAnimationSettings();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    private void LoadAnimationSettings()
    {
        _isLoading = true;
        try
        {
            ApplySettingsToControls();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ApplySettingsToControls()
    {
        var settings = ServiceContainer.Settings.NotificationCustom;

        // Check reduce motion and show InfoBar if enabled
        if (ReduceMotionInfoBar != null)
        {
            bool reduceMotion = Services.Notifications.ReduceMotionDetector.IsReduceMotionEnabled();
            ReduceMotionInfoBar.IsOpen = reduceMotion;
            
            if (reduceMotion)
            {
                LogService.LogAction("notifications", "Reduce motion enabled, user may want to use fade animations");
            }
        }

        // Enter from
        SelectComboBoxItemByTag(EnterFromComboBox, settings.EnterFrom);

        // Distance mode
        SelectRadioButtonByTag(DistanceModeRadio, settings.DistanceMode);
        UpdateShortDistanceVisibility();

        // Short distance
        ShortDistanceSlider.Value = settings.ShortDistancePx;
        ShortDistanceText.Text = $"{settings.ShortDistancePx} px";

        // Enter duration
        EnterDurationSlider.Value = settings.EnterMs;
        EnterDurationText.Text = $"{settings.EnterMs} ms";

        // Easing
        SelectComboBoxItemByTag(EasingComboBox, settings.Easing);

        // Exit to
        SelectComboBoxItemByTag(ExitToComboBox, settings.ExitTo);

        // Exit duration
        ExitDurationSlider.Value = settings.ExitMs;
        ExitDurationText.Text = $"{settings.ExitMs} ms";

        // Click-through
        if (ClickThroughCheckBox != null)
            ClickThroughCheckBox.IsChecked = settings.ClickThrough;

        if (AltToInteractCheckBox != null)
            AltToInteractCheckBox.IsChecked = settings.AltToInteract;
    }

    private void EnterFromComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        if (EnterFromComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ServiceContainer.Settings.NotificationCustom.EnterFrom = tag;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void DistanceModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        if (DistanceModeRadio.SelectedItem is RadioButton rb && rb.Tag is string tag)
        {
            ServiceContainer.Settings.NotificationCustom.DistanceMode = tag;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            UpdateShortDistanceVisibility();
        }
    }

    private void UpdateShortDistanceVisibility()
    {
        if (ShortDistancePanel != null)
        {
            ShortDistancePanel.Visibility = ServiceContainer.Settings.NotificationCustom.DistanceMode == "Short"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ShortDistanceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading) return;

        int value = (int)e.NewValue;
        ServiceContainer.Settings.NotificationCustom.ShortDistancePx = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        
        if (ShortDistanceText != null)
            ShortDistanceText.Text = $"{value} px";
    }

    private void EnterDurationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading) return;

        int value = (int)e.NewValue;
        ServiceContainer.Settings.NotificationCustom.EnterMs = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        
        if (EnterDurationText != null)
            EnterDurationText.Text = $"{value} ms";
    }

    private void EasingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        if (EasingComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ServiceContainer.Settings.NotificationCustom.Easing = tag;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void ExitToComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        if (ExitToComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            ServiceContainer.Settings.NotificationCustom.ExitTo = tag;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void ExitDurationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading) return;

        int value = (int)e.NewValue;
        ServiceContainer.Settings.NotificationCustom.ExitMs = value;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        
        if (ExitDurationText != null)
            ExitDurationText.Text = $"{value} ms";
    }

    private void ClickThroughCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        if (ClickThroughCheckBox != null)
        {
            ServiceContainer.Settings.NotificationCustom.ClickThrough = ClickThroughCheckBox.IsChecked == true;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void AltToInteractCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        if (AltToInteractCheckBox != null)
        {
            ServiceContainer.Settings.NotificationCustom.AltToInteract = AltToInteractCheckBox.IsChecked == true;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }

    private void SelectComboBoxItemByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag as string == tag)
            {
                comboBox.SelectedItem = item;
                break;
            }
        }
    }

    private void SelectRadioButtonByTag(RadioButtons radioButtons, string tag)
    {
        foreach (var item in radioButtons.Items)
        {
            if (item is RadioButton rb && rb.Tag as string == tag)
            {
                radioButtons.SelectedItem = item;
                break;
            }
        }
    }

    private void AnchorRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        if (sender is RadioButton rb && rb.Tag is string anchor && !string.IsNullOrEmpty(anchor))
        {
            if (ViewModel.SelectedAnchor != anchor)
            {
                ViewModel.SelectedAnchor = anchor;
            }
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.GoBack();
    }
}
