using System;
using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views;

public sealed partial class EyeCarePage : Page
{
    private EyeCareViewModel? _viewModel;

    public EyeCarePage()
    {
        this.InitializeComponent();
        InitializeViewModel();
    }

    private void InitializeViewModel()
    {
        if (ServiceContainer.EyeCareScheduler != null && ServiceContainer.EyeCareOverlayService != null)
        {
            _viewModel = new EyeCareViewModel(
                ServiceContainer.EyeCareScheduler,
                ServiceContainer.EyeCareOverlayService,
                DispatcherQueue);
            
            DataContext = _viewModel;
            UpdateIntervalDisplay();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.GoBack();
    }

    private void Button_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[EyeCarePage] Button PointerPressed detected!");
        if (sender is Button button)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCarePage] Button content: {button.Content}");
        }
    }

    private void UpdateIntervalDisplay()
    {
        if (_viewModel == null) return;

        // Clear all checks
        Interval1Check.Visibility = Visibility.Collapsed;
        Interval20Check.Visibility = Visibility.Collapsed;
        Interval25Check.Visibility = Visibility.Collapsed;
        Interval30Check.Visibility = Visibility.Collapsed;
        Interval60Check.Visibility = Visibility.Collapsed;

        if (_viewModel.IsCustomIntervalMode)
        {
            IntervalText.Text = $"{_viewModel.CustomInterval} minutes";
            CustomIntervalHint.Visibility = Visibility.Visible;
        }
        else
        {
            var presets = _viewModel.IntervalPresets;
            if (_viewModel.SelectedIntervalIndex >= 0 && _viewModel.SelectedIntervalIndex < presets.Count)
            {
                var interval = presets[_viewModel.SelectedIntervalIndex];
                IntervalText.Text = $"{interval} minute{(interval == 1 ? "" : "s")}";
                CustomIntervalHint.Visibility = Visibility.Collapsed;

                // Show check mark on the corresponding item
                switch (_viewModel.SelectedIntervalIndex)
                {
                    case 0: Interval1Check.Visibility = Visibility.Visible; break;
                    case 1: Interval20Check.Visibility = Visibility.Visible; break;
                    case 2: Interval25Check.Visibility = Visibility.Visible; break;
                    case 3: Interval30Check.Visibility = Visibility.Visible; break;
                    case 4: Interval60Check.Visibility = Visibility.Visible; break;
                }
            }
        }
    }

    private void Interval1_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SelectedIntervalIndex = 0;
            UpdateIntervalDisplay();
        }
    }

    private void Interval20_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SelectedIntervalIndex = 1;
            UpdateIntervalDisplay();
        }
    }

    private void Interval25_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SelectedIntervalIndex = 2;
            UpdateIntervalDisplay();
        }
    }

    private void Interval30_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SelectedIntervalIndex = 3;
            UpdateIntervalDisplay();
        }
    }

    private void Interval60_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SelectedIntervalIndex = 4;
            UpdateIntervalDisplay();
        }
    }

    private async void IntervalCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var dialog = new ContentDialog
        {
            Title = "Custom Interval",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var numberBox = new NumberBox
        {
            Header = "Enter the interval in minutes:",
            Value = _viewModel.CustomInterval,
            Minimum = 1,
            Maximum = 1440,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 250
        };

        dialog.Content = numberBox;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var value = (int)numberBox.Value;
            if (value >= 1 && value <= 1440)
            {
                _viewModel.SetCustomInterval(value);
                UpdateIntervalDisplay();
            }
        }
    }

    private async void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[EyeCarePage] Browse button clicked");
            
            if (_viewModel == null)
            {
                System.Diagnostics.Debug.WriteLine("[EyeCarePage] ViewModel is null!");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine("[EyeCarePage] Calling BrowseForTemplateAsync");
            await _viewModel.BrowseForTemplateAsync();
            System.Diagnostics.Debug.WriteLine("[EyeCarePage] BrowseForTemplateAsync completed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCarePage] Browse error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[EyeCarePage] Stack trace: {ex.StackTrace}");
            
            // Show error to user
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Failed to browse for template: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private void OpenTemplatesFolder_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.OpenTemplatesFolder();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[EyeCarePage] Preview button clicked");
            
            if (_viewModel == null)
            {
                System.Diagnostics.Debug.WriteLine("[EyeCarePage] ViewModel is null!");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine("[EyeCarePage] Calling PreviewAsync");
            await _viewModel.PreviewAsync();
            System.Diagnostics.Debug.WriteLine("[EyeCarePage] PreviewAsync completed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCarePage] Preview error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[EyeCarePage] Stack trace: {ex.StackTrace}");
            
            // Show error to user
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Failed to show preview: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
