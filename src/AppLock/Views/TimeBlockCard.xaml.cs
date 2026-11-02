using System;
using System.Linq;
using AppLock.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AppLock.Views;

public sealed partial class TimeBlockCard : UserControl
{
    private static readonly string[] Hours = Enumerable.Range(0, 24).Select(h => h.ToString("D2")).ToArray();
    private static readonly string[] Minutes = Enumerable.Range(0, 12).Select(m => (m * 5).ToString("D2")).ToArray();

    private static readonly SolidColorBrush SelectedDayBg = new(Color.FromArgb(255, 0xD9, 0xA0, 0xE8)); // Lilac
    private static readonly SolidColorBrush SelectedDayFg = new(Color.FromArgb(255, 0x1A, 0x1A, 0x1A)); // Dark text
    private static readonly SolidColorBrush SelectedDayBorder = new(Color.FromArgb(255, 0xD9, 0xA0, 0xE8));

    private readonly ToggleButton[] _dayButtons;
    private bool _isInitializing = true;

    public TimeBlock Block { get; }

    public event EventHandler? DeleteRequested;
    public event EventHandler? Changed;

    public TimeBlockCard(TimeBlock block)
    {
        InitializeComponent();
        Block = block;

        _dayButtons = new[] { DayBtn0, DayBtn1, DayBtn2, DayBtn3, DayBtn4, DayBtn5, DayBtn6 };

        StartHourCombo.ItemsSource = Hours;
        EndHourCombo.ItemsSource = Hours;
        StartMinuteCombo.ItemsSource = Minutes;
        EndMinuteCombo.ItemsSource = Minutes;

        ActualThemeChanged += (s, e) =>
        {
            for (int i = 0; i < 7; i++)
            {
                UpdateDayButtonStyle(_dayButtons[i], _dayButtons[i].IsChecked == true);
            }
        };

        LoadFromBlock();
        _isInitializing = false;
    }

    private void LoadFromBlock()
    {
        StartHourCombo.SelectedItem = Block.Start.Hour.ToString("D2");
        StartMinuteCombo.SelectedItem = RoundToFiveMinutes(Block.Start.Minute).ToString("D2");

        EndHourCombo.SelectedItem = Block.End.Hour.ToString("D2");
        EndMinuteCombo.SelectedItem = RoundToFiveMinutes(Block.End.Minute).ToString("D2");

        for (int i = 0; i < 7; i++)
        {
            bool isDaySelected = Block.Days != null && i < Block.Days.Length && Block.Days[i];
            _dayButtons[i].IsChecked = isDaySelected;
            UpdateDayButtonStyle(_dayButtons[i], isDaySelected);
        }
    }

    private static int RoundToFiveMinutes(int minute)
    {
        int rounded = (int)Math.Round(minute / 5.0) * 5;
        return rounded >= 60 ? 55 : rounded;
    }

    private void UpdateDayButtonStyle(ToggleButton btn, bool isChecked)
    {
        if (isChecked)
        {
            btn.Background = SelectedDayBg;
            btn.Foreground = SelectedDayFg;
            btn.BorderBrush = SelectedDayBorder;
            btn.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        }
        else
        {
            btn.ClearValue(ToggleButton.BackgroundProperty);
            btn.ClearValue(ToggleButton.ForegroundProperty);
            btn.ClearValue(ToggleButton.BorderBrushProperty);
            btn.ClearValue(ToggleButton.FontWeightProperty);
        }
    }

    private void DayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;

        int index = Array.IndexOf(_dayButtons, btn);
        if (index < 0 || index >= 7) return;

        // Verify not deselecting the last selected day
        int currentSelectedCount = _dayButtons.Count(b => b.IsChecked == true);
        if (btn.IsChecked != true && currentSelectedCount == 0)
        {
            // Disallow deselecting the only active day
            btn.IsChecked = true;
            UpdateDayButtonStyle(btn, true);
            BlockErrorText.Text = "At least one day must be selected.";
            BlockErrorText.Visibility = Visibility.Visible;
            return;
        }

        BlockErrorText.Visibility = Visibility.Collapsed;
        bool isChecked = btn.IsChecked == true;
        Block.Days[index] = isChecked;
        UpdateDayButtonStyle(btn, isChecked);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (StartHourCombo.SelectedItem is string sh && int.TryParse(sh, out int startH) &&
            StartMinuteCombo.SelectedItem is string sm && int.TryParse(sm, out int startM))
        {
            Block.Start = new TimeOnly(startH, startM);
        }

        if (EndHourCombo.SelectedItem is string eh && int.TryParse(eh, out int endH) &&
            EndMinuteCombo.SelectedItem is string em && int.TryParse(em, out int endM))
        {
            Block.End = new TimeOnly(endH, endM);
        }

        Validate(out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool Validate(out string errorMessage)
    {
        errorMessage = string.Empty;

        if (Block.Start == Block.End)
        {
            errorMessage = "Start and end times cannot be the same.";
            BlockErrorText.Text = errorMessage;
            BlockErrorText.Visibility = Visibility.Visible;
            return false;
        }

        if (Block.Days == null || !Block.Days.Any(d => d))
        {
            errorMessage = "At least one day must be selected.";
            BlockErrorText.Text = errorMessage;
            BlockErrorText.Visibility = Visibility.Visible;
            return false;
        }

        BlockErrorText.Visibility = Visibility.Collapsed;
        return true;
    }
}

