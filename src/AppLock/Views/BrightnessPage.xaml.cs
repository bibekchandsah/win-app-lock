using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using AppLock.Services.Brightness;
using AppLock.ViewModels;
using AppLock.Views.Dialogs;
using AppLock.Services;
using System;
using System.Linq;

namespace AppLock.Views;

public sealed partial class BrightnessPage : Page
{
    private BrightnessViewModel? _viewModel;
    private DispatcherTimer? _pollingTimer;
    private OsdService? _osdService;
    private BlurOverlayService? _blurOverlayService;
    private bool _isLoadingSettings;

    public BrightnessPage()
    {
        this.InitializeComponent();
        Loaded += BrightnessPage_Loaded;
        Unloaded += BrightnessPage_Unloaded;
    }

    private void BrightnessPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize services
        var brightnessService = new BrightnessService(DispatcherQueue);
        _osdService = new OsdService(DispatcherQueue);
        _blurOverlayService = new BlurOverlayService(DispatcherQueue);
        
        // Initialize view model
        _viewModel = new BrightnessViewModel(brightnessService, DispatcherQueue);
        DataContext = _viewModel;

        // Note: Hotkey manager is now initialized globally in ServiceContainer
        // and persists even when navigating away from this page

        // Load settings into UI (this will trigger slider events which update preview)
        LoadSettings();

        // Check if we need to show software dimming warning
        CheckSoftwareDimmingWarning();

        // Start polling for external brightness changes
        StartPolling();
    }

    private void BrightnessPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopPolling();
        // Don't dispose hotkey manager here - it's now managed globally
        _blurOverlayService?.Dispose();
        _blurOverlayService = null;
        _osdService?.Dispose();
        _osdService = null;
        _viewModel?.Dispose();
        _viewModel = null;
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        
        var settings = ServiceContainer.Settings.Brightness;
        ShowOsdCheckBox.IsChecked = settings.ShowOsd;
        PressAgainCheckBox.IsChecked = settings.PressAgainRestoresPrevious;
        RestoreOnExitCheckBox.IsChecked = settings.RestoreOnExit;
        RememberBrightnessCheckBox.IsChecked = settings.RememberAndApplyAtStartup;
        MinBrightnessNumberBox.Value = settings.MinSoftwareBrightness;
        
        // Load hotkey toggles
        BrightnessHotkeysToggle.IsOn = settings.HotkeysEnabled;
        BlurHotkeyToggle.IsOn = settings.Blur.HotkeyEnabled;
        
        // Load blur settings (setting slider values will trigger events that update preview)
        BlurLevelSlider.Value = settings.Blur.LevelPercent;
        BlurLevelText.Text = $"{settings.Blur.LevelPercent}%";
        DimAmountSlider.Value = settings.Blur.DimPercent;
        DimAmountText.Text = $"{settings.Blur.DimPercent}%";
        KeepOnAfterRestartCheckBox.IsChecked = settings.Blur.KeepOnAfterRestart;

        // Load diagnostics
        RefreshDiagnostics();

        _isLoadingSettings = false;
    }

    private void StartPolling()
    {
        _pollingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _pollingTimer.Tick += async (s, e) =>
        {
            if (_viewModel != null)
            {
                await _viewModel.RefreshCurrentBrightnessAsync();
            }
        };
        _pollingTimer.Start();
    }

    private void StopPolling()
    {
        _pollingTimer?.Stop();
        _pollingTimer = null;
    }

    private void CheckSoftwareDimmingWarning()
    {
        if (_viewModel == null) return;

        // Check if any display is using software dimming
        var hasSoftwareDimming = _viewModel.Displays.Any(d => 
            d.ControlMethod == "Software dimming" && d.Id != "All");

        if (SoftwareDimmingInfo != null)
        {
            SoftwareDimmingInfo.IsOpen = hasSoftwareDimming;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void EditHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not BrightnessPresetViewModel presetVm || _viewModel == null)
            return;

        var validator = new Func<Models.HotkeyBinding?, ValidationResult>(binding =>
        {
            var settings = ServiceContainer.Settings.Brightness;
            return HotkeyValidator.ValidateHotkey(
                binding,
                settings.Presets,
                presetVm.Id,
                ServiceContainer.Settings.EmergencyHotkey,
                settings.IncreaseHotkey,
                settings.DecreaseHotkey,
                settings.Blur.ToggleHotkey);
        });

        var dialog = new HotkeyCaptureDialog(presetVm.GetModel().Hotkey, validator)
        {
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            presetVm.UpdateHotkey(dialog.CapturedHotkey);
            
            // Re-register hotkeys
            ServiceContainer.BrightnessHotkeyManager?.RegisterAllHotkeys();
        }
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not BrightnessPresetViewModel presetVm || _viewModel == null)
            return;

        _viewModel.RemovePreset(presetVm);
        
        // Re-register hotkeys
        ServiceContainer.BrightnessHotkeyManager?.RegisterAllHotkeys();
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.AddPreset();
    }

    private async void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset to Defaults",
            Content = "This will reset all brightness presets to the default shortcuts (Ctrl+Shift+0-9, Ctrl+Shift+F). Continue?",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8)
        };

        // Apply theme synchronization
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            dialog.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            dialog.RequestedTheme = ServiceContainer.CurrentTheme;
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _viewModel?.ResetToDefaults();
            
            // Re-register hotkeys
            ServiceContainer.BrightnessHotkeyManager?.RegisterAllHotkeys();
        }
    }

    private void ShowOsd_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        ServiceContainer.Settings.Brightness.ShowOsd = ShowOsdCheckBox.IsChecked ?? true;
        _viewModel?.SaveSettings();
    }

    private void PressAgain_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        ServiceContainer.Settings.Brightness.PressAgainRestoresPrevious = PressAgainCheckBox.IsChecked ?? false;
        _viewModel?.SaveSettings();
    }

    private void RestoreOnExit_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        ServiceContainer.Settings.Brightness.RestoreOnExit = RestoreOnExitCheckBox.IsChecked ?? false;
        _viewModel?.SaveSettings();
    }

    private void RememberBrightness_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        ServiceContainer.Settings.Brightness.RememberAndApplyAtStartup = RememberBrightnessCheckBox.IsChecked ?? false;
        _viewModel?.SaveSettings();
    }

    private void MinBrightness_Changed(object sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs e)
    {
        if (_isLoadingSettings) return;
        ServiceContainer.Settings.Brightness.MinSoftwareBrightness = (int)(MinBrightnessNumberBox.Value);
        _viewModel?.SaveSettings();
    }

    private async void ResetDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        
        // Set to 100%
        _viewModel.CurrentBrightness = 100;
        
        // Show OSD
        if (ServiceContainer.Settings.Brightness.ShowOsd && _osdService != null)
        {
            _osdService.Show(100);
        }
    }

    private void ToggleBlur_Click(object sender, RoutedEventArgs e)
    {
        _blurOverlayService?.Toggle();
    }

    private void BrightnessHotkeysToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        
        ServiceContainer.Settings.Brightness.HotkeysEnabled = BrightnessHotkeysToggle.IsOn;
        _viewModel?.SaveSettings();
        
        System.Diagnostics.Debug.WriteLine($"Brightness hotkeys {(BrightnessHotkeysToggle.IsOn ? "enabled" : "disabled")}");
    }

    private void BlurHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        
        ServiceContainer.Settings.Brightness.Blur.HotkeyEnabled = BlurHotkeyToggle.IsOn;
        _viewModel?.SaveSettings();
        
        System.Diagnostics.Debug.WriteLine($"Blur hotkey {(BlurHotkeyToggle.IsOn ? "enabled" : "disabled")}");
    }

    private void BlurLevel_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int level = (int)BlurLevelSlider.Value;
        BlurLevelText.Text = $"{level}%";
        
        // Update live preview
        BlurPreview?.SetBlurLevel(level);
        
        // Update overlay if active (only if not loading)
        if (!_isLoadingSettings)
        {
            _blurOverlayService?.SetBlurLevel(level);
        }
        
        // Save setting (only if not loading)
        if (!_isLoadingSettings)
        {
            ServiceContainer.Settings.Brightness.Blur.LevelPercent = level;
            _viewModel?.SaveSettings();
        }
    }

    private void DimAmount_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int dimAmount = (int)DimAmountSlider.Value;
        
        // Update text display
        if (DimAmountText != null)
        {
            DimAmountText.Text = $"{dimAmount}%";
        }
        
        // Update preview
        BlurPreview?.SetDimAmount(dimAmount);
        
        // Update overlay if active (only if not loading)
        if (!_isLoadingSettings)
        {
            _blurOverlayService?.SetDimAmount(dimAmount);
        }
        
        // Save setting (only if not loading)
        if (!_isLoadingSettings)
        {
            ServiceContainer.Settings.Brightness.Blur.DimPercent = dimAmount;
            _viewModel?.SaveSettings();
        }
    }

    private void KeepOnAfterRestart_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        
        ServiceContainer.Settings.Brightness.Blur.KeepOnAfterRestart = KeepOnAfterRestartCheckBox.IsChecked ?? false;
        _viewModel?.SaveSettings();
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        if (DiagnosticsText == null) return;

        var settings = ServiceContainer.Settings.Brightness;
        var diagnostics = new System.Text.StringBuilder();

        diagnostics.AppendLine("Registered Brightness Hotkeys:");
        diagnostics.AppendLine();

        // Preset hotkeys
        foreach (var preset in settings.Presets)
        {
            if (preset.Hotkey != null)
            {
                var hotkeyStr = HotkeyValidator.FormatHotkey(preset.Hotkey);
                diagnostics.AppendLine($"  {hotkeyStr} → Set to {preset.Percent}%");
            }
        }

        // Blur toggle
        if (settings.Blur.ToggleHotkey != null)
        {
            var hotkeyStr = HotkeyValidator.FormatHotkey(settings.Blur.ToggleHotkey);
            diagnostics.AppendLine($"  {hotkeyStr} → Toggle Blur Overlay");
            
            // Update UI
            if (BlurHotkeyText != null)
            {
                BlurHotkeyText.Text = hotkeyStr;
            }
        }
        else
        {
            diagnostics.AppendLine("  Ctrl+Shift+B → Toggle Blur Overlay");
        }

        // Reset display
        diagnostics.AppendLine("  Ctrl+Alt+Shift+R → Reset Display");

        diagnostics.AppendLine();
        diagnostics.AppendLine($"Total presets: {settings.Presets.Count}");
        diagnostics.AppendLine($"OSD enabled: {settings.ShowOsd}");
        diagnostics.AppendLine($"Blur level: {settings.Blur.LevelPercent}%");

        DiagnosticsText.Text = diagnostics.ToString();
    }

    private async void ChangeBlurHotkey_Click(object sender, RoutedEventArgs e)
    {
        var settings = ServiceContainer.Settings.Brightness;
        
        var validator = new Func<Models.HotkeyBinding?, ValidationResult>(binding =>
        {
            return HotkeyValidator.ValidateHotkey(
                binding,
                settings.Presets,
                null,
                ServiceContainer.Settings.EmergencyHotkey,
                settings.IncreaseHotkey,
                settings.DecreaseHotkey,
                null); // Don't check against itself
        });

        var dialog = new HotkeyCaptureDialog(settings.Blur.ToggleHotkey, validator)
        {
            XamlRoot = this.XamlRoot,
            Title = "Change Blur Toggle Hotkey"
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            settings.Blur.ToggleHotkey = dialog.CapturedHotkey;
            _viewModel?.SaveSettings();
            
            // Update UI
            if (settings.Blur.ToggleHotkey != null)
            {
                BlurHotkeyText.Text = HotkeyValidator.FormatHotkey(settings.Blur.ToggleHotkey);
            }
            else
            {
                BlurHotkeyText.Text = "Not set";
            }
            
            // Re-register hotkeys
            ServiceContainer.BrightnessHotkeyManager?.RegisterAllHotkeys();
            
            // Refresh diagnostics
            RefreshDiagnostics();
        }
    }
}
