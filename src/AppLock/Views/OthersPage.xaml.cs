using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AppLock.Services;

namespace AppLock.Views;

public sealed partial class OthersPage : Page
{
    public OthersPage()
    {
        this.InitializeComponent();
        LoadUtilityCards();
    }

    private void LoadUtilityCards()
    {
        var cards = new List<UtilityCard>
        {
            new UtilityCard
            {
                Id = "brightness",
                Icon = "\uE706", // Sun icon
                Title = "Brightness",
                Description = "Control screen brightness with shortcuts and a blur overlay",
                Status = "Ready to configure",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = ServiceContainer.Settings.Brightness.HotkeysEnabled,
                BlurHotkeyEnabled = ServiceContainer.Settings.Brightness.Blur.HotkeyEnabled
            }
        };

        UtilityCardsControl.ItemsSource = cards;
    }

    private void UtilityCard_Click(object sender, RoutedEventArgs e)
    {
        // Prevent navigation when clicking inside the expander
        if (sender is Button button && button.DataContext is UtilityCard card)
        {
            if (card.Id == "brightness")
            {
                // Navigate to BrightnessPage
                Frame.Navigate(typeof(BrightnessPage));
            }
        }
    }

    private void BrightnessHotkeysToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            ServiceContainer.Settings.Brightness.HotkeysEnabled = toggle.IsOn;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            card.BrightnessHotkeysEnabled = toggle.IsOn;
        }
    }

    private void BlurHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            ServiceContainer.Settings.Brightness.Blur.HotkeyEnabled = toggle.IsOn;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            card.BlurHotkeyEnabled = toggle.IsOn;
        }
    }

    private void CardExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        // Prevent card click when expander is toggled
        // Note: ExpanderExpandingEventArgs doesn't have Handled property, 
        // but the event structure prevents propagation automatically
    }
}

public class UtilityCard : INotifyPropertyChanged
{
    private bool _brightnessHotkeysEnabled;
    private bool _blurHotkeyEnabled;

    public string Id { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Visibility StatusVisibility { get; set; } = Visibility.Collapsed;

    public bool BrightnessHotkeysEnabled
    {
        get => _brightnessHotkeysEnabled;
        set
        {
            if (_brightnessHotkeysEnabled != value)
            {
                _brightnessHotkeysEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool BlurHotkeyEnabled
    {
        get => _blurHotkeyEnabled;
        set
        {
            if (_blurHotkeyEnabled != value)
            {
                _blurHotkeyEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

