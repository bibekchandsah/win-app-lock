using System;
using System.Linq;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.Controls;

public sealed partial class EyeCareCard : UserControl
{
    private EyeCareViewModel? _viewModel;

    public EyeCareCard()
    {
        this.InitializeComponent();
    }

    public void SetViewModel(EyeCareViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        UpdateIntervalDisplay();
    }

    private void UpdateIntervalDisplay()
    {
        if (_viewModel == null) return;

        // Clear all checks
        Interval1Item.IsChecked = false;
        Interval20Item.IsChecked = false;
        Interval25Item.IsChecked = false;
        Interval30Item.IsChecked = false;
        Interval60Item.IsChecked = false;

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

                // Check the corresponding item
                switch (_viewModel.SelectedIntervalIndex)
                {
                    case 0: Interval1Item.IsChecked = true; break;
                    case 1: Interval20Item.IsChecked = true; break;
                    case 2: Interval25Item.IsChecked = true; break;
                    case 3: Interval30Item.IsChecked = true; break;
                    case 4: Interval60Item.IsChecked = true; break;
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
        if (_viewModel != null)
        {
            await _viewModel.BrowseForTemplateAsync();
        }
    }

    private void OpenTemplatesFolder_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.OpenTemplatesFolder();
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.PreviewAsync();
        }
    }
}
