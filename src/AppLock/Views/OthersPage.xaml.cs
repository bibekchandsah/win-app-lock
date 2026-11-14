using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AppLock.Services;

namespace AppLock.Views;

// Template selector to choose content based on card type
public class CardContentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BrightnessTemplate { get; set; }
    public DataTemplate? EyeCareTemplate { get; set; }
    public DataTemplate? ClipboardTemplate { get; set; }
    public DataTemplate? RingLightTemplate { get; set; }
    public DataTemplate? VolumeMixerTemplate { get; set; }
    public DataTemplate? NotificationsTemplate { get; set; }
    public DataTemplate? KeyboardTemplate { get; set; }
    public DataTemplate? WifiTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is UtilityCard card)
        {
            return card.Id switch
            {
                "brightness" => BrightnessTemplate,
                "eyecare" => EyeCareTemplate,
                "clipboard" => ClipboardTemplate,
                "ringlight" => RingLightTemplate,
                "volumemixer" => VolumeMixerTemplate,
                "notifications" => NotificationsTemplate,
                "keyboard" => KeyboardTemplate,
                "wifi" => WifiTemplate,
                _ => null
            };
        }
        return null;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

public sealed partial class OthersPage : Page
{
    public List<UtilityCard> Cards { get; } = new();

    /// <summary>The cards currently on screen; the search box filters this collection.</summary>
    public ObservableCollection<UtilityCard> VisibleCards { get; } = new();

    private string _searchText = string.Empty;

    // Guards the programmatic IsOn revert below from re-entering the Toggled handler.
    private bool _syncingKeyboardToggle;

    public OthersPage()
    {
        LoadUtilityCards();
        this.InitializeComponent();
        this.Loaded += OthersPage_Loaded;
        this.Unloaded += OthersPage_Unloaded;

        ApplyCardFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox?.Text?.Trim() ?? string.Empty;
        ApplyCardFilter();
    }

    /// <summary>
    /// Shows the cards whose title or description contains the search text. The collection is updated
    /// in place so cards that stay visible keep their expander state.
    /// </summary>
    private void ApplyCardFilter()
    {
        var matches = string.IsNullOrEmpty(_searchText)
            ? Cards
            : Cards.Where(c =>
                c.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                c.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        for (int i = VisibleCards.Count - 1; i >= 0; i--)
        {
            if (!matches.Contains(VisibleCards[i]))
            {
                VisibleCards.RemoveAt(i);
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var card = matches[i];
            int existingIndex = VisibleCards.IndexOf(card);

            if (existingIndex < 0)
            {
                if (i < VisibleCards.Count)
                    VisibleCards.Insert(i, card);
                else
                    VisibleCards.Add(card);
            }
            else if (existingIndex != i)
            {
                VisibleCards.Move(existingIndex, i);
            }
        }

        NoResultsText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OthersPage_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
        ServiceContainer.ClipboardEnabledChanged += OnClipboardEnabledChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged += OnClipboardPauseStateChanged;
        ServiceContainer.EyeCareEnabledChanged -= OnEyeCareEnabledChanged;
        ServiceContainer.EyeCareEnabledChanged += OnEyeCareEnabledChanged;
        ServiceContainer.RingLightEnabledChanged -= OnRingLightEnabledChanged;
        ServiceContainer.RingLightEnabledChanged += OnRingLightEnabledChanged;
        ServiceContainer.RingLightService.EnabledChanged -= OnRingLightServiceChanged;
        ServiceContainer.RingLightService.EnabledChanged += OnRingLightServiceChanged;
        ServiceContainer.VolumeMixerEnabledChanged -= OnVolumeMixerEnabledChanged;
        ServiceContainer.VolumeMixerEnabledChanged += OnVolumeMixerEnabledChanged;
        ServiceContainer.NotificationCustomizerEnabledChanged -= OnNotificationCustomizerEnabledChanged;
        ServiceContainer.NotificationCustomizerEnabledChanged += OnNotificationCustomizerEnabledChanged;
        UpdateClipboardCardState();
        UpdateEyeCareCardState();
        UpdateRingLightCardState();
        UpdateVolumeMixerCardState();
        UpdateNotificationCardState();
    }

    private void OthersPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
        ServiceContainer.EyeCareEnabledChanged -= OnEyeCareEnabledChanged;
        ServiceContainer.RingLightEnabledChanged -= OnRingLightEnabledChanged;
        ServiceContainer.RingLightService.EnabledChanged -= OnRingLightServiceChanged;
        ServiceContainer.VolumeMixerEnabledChanged -= OnVolumeMixerEnabledChanged;
        ServiceContainer.NotificationCustomizerEnabledChanged -= OnNotificationCustomizerEnabledChanged;
    }

    private void OnClipboardEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateClipboardCardState);
    private void OnClipboardPauseStateChanged(object? sender, EventArgs e) => DispatcherQueue?.TryEnqueue(UpdateClipboardCardState);
    private void OnEyeCareEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateEyeCareCardState);
    private void OnRingLightEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateRingLightCardState);
    private void OnRingLightServiceChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateRingLightCardState);
    private void OnVolumeMixerEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateVolumeMixerCardState);
    private void OnNotificationCustomizerEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateNotificationCardState);

    private void UpdateClipboardCardState()
    {
        var card = Cards.FirstOrDefault(c => c.Id == "clipboard");
        if (card != null)
        {
            card.ClipboardEnabled = ServiceContainer.Settings.Clipboard.Enabled;
        }
    }

    private void UpdateEyeCareCardState()
    {
        var card = Cards.FirstOrDefault(c => c.Id == "eyecare");
        if (card != null)
        {
            card.EyeCareEnabled = ServiceContainer.Settings.EyeCare.Enabled;
        }
    }

    private void UpdateRingLightCardState()
    {
        var card = Cards.FirstOrDefault(c => c.Id == "ringlight");
        if (card == null) return;
        card.RingLightEnabled = ServiceContainer.RingLightService.IsOn;
        // Status text is hidden for ring light card; toggle state is visible via quick settings
    }

    private void UpdateVolumeMixerCardState()
    {
        var card = Cards.FirstOrDefault(c => c.Id == "volumemixer");
        if (card != null)
        {
            card.VolumeMixerEnabled = ServiceContainer.Settings.VolumeMixer.Enabled;
        }
    }

    private void UpdateNotificationCardState()
    {
        var card = Cards.FirstOrDefault(c => c.Id == "notifications");
        if (card != null)
        {
            card.NotificationsEnabled = ServiceContainer.Settings.NotificationCustom.Enabled;
        }
    }

    private void LoadUtilityCards()
    {
        var allCards = new List<UtilityCard>
        {
            new UtilityCard
            {
                Id = "brightness",
                Order = 10,
                Icon = "\uE706", // Sun icon
                Title = "Brightness",
                Description = "Control screen brightness with shortcuts and a blur overlay",
                Status = "",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = ServiceContainer.Settings.Brightness.HotkeysEnabled,
                BlurHotkeyEnabled = ServiceContainer.Settings.Brightness.Blur.HotkeyEnabled,
                EyeCareEnabled = false
            },
            new UtilityCard
            {
                Id = "eyecare",
                Order = 20,
                Icon = "\uE7B3", // Eye icon
                Title = "Eye Care",
                Description = "Periodic reminders to rest your eyes with fullscreen overlays",
                Status = "",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = ServiceContainer.Settings.EyeCare.Enabled
            },
            new UtilityCard
            {
                Id = "clipboard",
                Order = 40,
                Icon = "\uE8C8", // Paste / Clipboard icon
                Title = "Clipboard",
                Description = "Persistent clipboard history with custom hotkey and search",
                Status = ServiceContainer.Settings.Clipboard.Enabled ? "Capturing" : "Disabled",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = false,
                ClipboardEnabled = ServiceContainer.Settings.Clipboard.Enabled
            },
            new UtilityCard
            {
                Id = "ringlight",
                Order = 50,
                Icon = "\uE781", // Light / Ring-light glyph
                Title = "Ring Light",
                Description = "Light your face during calls using your screen edges",
                Status = "",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = false,
                ClipboardEnabled = false,
                RingLightEnabled = ServiceContainer.Settings.RingLight.Enabled
            },
            new UtilityCard
            {
                Id = "volumemixer",
                Order = 60,
                Icon = "\uE767", // Volume icon
                Title = "Volume Mixer",
                Description = "Floating overlay with master volume and per-app mixer",
                Status = "",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = false,
                ClipboardEnabled = false,
                RingLightEnabled = false,
                VolumeMixerEnabled = ServiceContainer.Settings.VolumeMixer.Enabled
            },
            new UtilityCard
            {
                Id = "notifications",
                Order = 70,
                Icon = "\uE91C", // Notification icon
                Title = "Notifications",
                Description = "Choose where notifications appear and how they animate",
                Status = "",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = false,
                ClipboardEnabled = false,
                RingLightEnabled = false,
                VolumeMixerEnabled = false,
                NotificationsEnabled = ServiceContainer.Settings.NotificationCustom.Enabled
            },
            new UtilityCard
            {
                Id = "keyboard",
                Order = 80,
                Icon = "\uE92E", // Keyboard icon
                Title = "Keyboard",
                Description = "Enable or disable the integrated laptop keyboard",
                Status = "",
                StatusVisibility = Visibility.Collapsed,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = false,
                ClipboardEnabled = false,
                RingLightEnabled = false,
                VolumeMixerEnabled = false,
                NotificationsEnabled = false,
                KeyboardEnabled = ServiceContainer.Settings.Keyboard.Enabled
            },
            new UtilityCard
            {
                Id = "wifi",
                Order = 85,
                Icon = "\uE701", // Wi-Fi icon
                Title = "Wi-Fi Passwords",
                Description = "View and copy the passwords of the wireless networks saved on this PC",
                Status = "",
                StatusVisibility = Visibility.Collapsed
            }
        };

        // Sort by explicit order value so order is not file-order dependent
        allCards.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var card in allCards)
            Cards.Add(card);
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
            else if (card.Id == "eyecare")
            {
                // Navigate to EyeCarePage
                Frame.Navigate(typeof(EyeCarePage));
            }
            else if (card.Id == "clipboard")
            {
                // Navigate to ClipboardSettingsPage
                Frame.Navigate(typeof(ClipboardSettingsPage));
            }
            else if (card.Id == "ringlight")
            {
                Frame.Navigate(typeof(RingLightPage));
            }
            else if (card.Id == "volumemixer")
            {
                Frame.Navigate(typeof(VolumeMixerSettingsPage));
            }
            else if (card.Id == "notifications")
            {
                Frame.Navigate(typeof(NotificationsPage));
            }
            else if (card.Id == "keyboard")
            {
                Frame.Navigate(typeof(KeyboardPage));
            }
            else if (card.Id == "wifi")
            {
                Frame.Navigate(typeof(WifiPasswordsPage));
            }
        }
    }

    private void OpenWifiPasswords_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(WifiPasswordsPage));
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

    /// <summary>
    /// Shows the card's quick settings in a flyout anchored to its "Quick Settings" button. The
    /// panel floats above the grid, so neighbouring cards keep their position and size.
    /// </summary>
    private void QuickSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not UtilityCard card) return;

        var content = new ContentControl
        {
            Content = card,
            ContentTemplateSelector = (DataTemplateSelector)Resources["CardContentSelector"],
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinWidth = 300
        };

        var flyout = new Flyout
        {
            Content = content,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
        };

        flyout.ShowAt(button);
    }

    private void EyeCareToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            card.EyeCareEnabled = toggle.IsOn;
            ServiceContainer.SetEyeCareEnabled(toggle.IsOn);
        }
    }

    private void RingLightToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            card.RingLightEnabled = toggle.IsOn;
            ServiceContainer.SetRingLightEnabled(toggle.IsOn);
            UpdateRingLightCardState();
        }
    }

    private void VolumeMixerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            card.VolumeMixerEnabled = toggle.IsOn;
            ServiceContainer.SetVolumeMixerEnabled(toggle.IsOn);
        }
    }

    private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            card.NotificationsEnabled = toggle.IsOn;
            if (ServiceContainer.Settings.NotificationCustom.Enabled != toggle.IsOn)
            {
                ServiceContainer.SetNotificationCustomizerEnabled(toggle.IsOn);
            }
        }
    }

    private void KeyboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingKeyboardToggle) return;

        if (sender is ToggleSwitch toggle && toggle.DataContext is UtilityCard card)
        {
            bool enable = toggle.IsOn;

            // This switch used to only write a setting; the input state was never applied, so it
            // disagreed with the Keyboard page. Go through the same view model the page uses.
            bool applied = ServiceContainer.KeyboardViewModel.SetEnabled(enable);

            if (!applied)
            {
                // No admin rights / the API refused: put the switch back where it was.
                _syncingKeyboardToggle = true;
                toggle.IsOn = !enable;
                _syncingKeyboardToggle = false;
                card.KeyboardEnabled = !enable;
                return;
            }

            card.KeyboardEnabled = enable;
            ServiceContainer.Settings.Keyboard.Enabled = enable;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
    }
}

public class UtilityCard : INotifyPropertyChanged
{
    private bool _brightnessHotkeysEnabled;
    private bool _blurHotkeyEnabled;
    private bool _eyeCareEnabled;
    private bool _ringLightEnabled;
    private string _status = string.Empty;

    public string Id { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Icon { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    // Visibility helpers - computed based on Id
    public bool IsBrightnessCard => Id == "brightness";
    public bool IsEyeCareCard => Id == "eyecare";
    public bool IsClipboardCard => Id == "clipboard";
    public bool IsRingLightCard => Id == "ringlight";
    public bool IsVolumeMixerCard => Id == "volumemixer";
    public bool IsNotificationsCard => Id == "notifications";
    public bool IsKeyboardCard => Id == "keyboard";
    
    public Visibility BrightnessContentVisibility => IsBrightnessCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EyeCareContentVisibility => IsEyeCareCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClipboardContentVisibility => IsClipboardCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RingLightContentVisibility => IsRingLightCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VolumeMixerContentVisibility => IsVolumeMixerCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotificationsContentVisibility => IsNotificationsCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KeyboardContentVisibility => IsKeyboardCard ? Visibility.Visible : Visibility.Collapsed;
    
    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
            }
        }
    }
    
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

    public bool EyeCareEnabled
    {
        get => _eyeCareEnabled;
        set
        {
            if (_eyeCareEnabled != value)
            {
                _eyeCareEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _clipboardEnabled;
    public bool ClipboardEnabled
    {
        get => _clipboardEnabled;
        set
        {
            if (_clipboardEnabled != value)
            {
                _clipboardEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool RingLightEnabled
    {
        get => _ringLightEnabled;
        set
        {
            if (_ringLightEnabled != value)
            {
                _ringLightEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _volumeMixerEnabled;
    public bool VolumeMixerEnabled
    {
        get => _volumeMixerEnabled;
        set
        {
            if (_volumeMixerEnabled != value)
            {
                _volumeMixerEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _notificationsEnabled;
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (_notificationsEnabled != value)
            {
                _notificationsEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _keyboardEnabled;
    public bool KeyboardEnabled
    {
        get => _keyboardEnabled;
        set
        {
            if (_keyboardEnabled != value)
            {
                _keyboardEnabled = value;
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

