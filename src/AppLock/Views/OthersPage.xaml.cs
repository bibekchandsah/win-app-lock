using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
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

    public OthersPage()
    {
        LoadUtilityCards();
        this.InitializeComponent();
        this.Loaded += OthersPage_Loaded;
        this.Unloaded += OthersPage_Unloaded;
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
        UpdateClipboardCardState();
        UpdateEyeCareCardState();
        UpdateRingLightCardState();
    }

    private void OthersPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
        ServiceContainer.EyeCareEnabledChanged -= OnEyeCareEnabledChanged;
        ServiceContainer.RingLightEnabledChanged -= OnRingLightEnabledChanged;
        ServiceContainer.RingLightService.EnabledChanged -= OnRingLightServiceChanged;
    }

    private void OnClipboardEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateClipboardCardState);
    private void OnClipboardPauseStateChanged(object? sender, EventArgs e) => DispatcherQueue?.TryEnqueue(UpdateClipboardCardState);
    private void OnEyeCareEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateEyeCareCardState);
    private void OnRingLightEnabledChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateRingLightCardState);
    private void OnRingLightServiceChanged(object? sender, bool enabled) => DispatcherQueue?.TryEnqueue(UpdateRingLightCardState);

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
        var svc = ServiceContainer.RingLightService;
        var s   = ServiceContainer.Settings.RingLight;
        card.RingLightEnabled = svc.IsOn;
        card.Status = svc.IsOn
            ? $"On · {s.Kelvin}K · {s.BrightnessPercent}%"
            : "Off";
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
                Status = ServiceContainer.Settings.RingLight.Enabled
                    ? $"On · {ServiceContainer.Settings.RingLight.Kelvin}K · {ServiceContainer.Settings.RingLight.BrightnessPercent}%"
                    : "Off",
                StatusVisibility = Visibility.Visible,
                BrightnessHotkeysEnabled = false,
                BlurHotkeyEnabled = false,
                EyeCareEnabled = false,
                ClipboardEnabled = false,
                RingLightEnabled = ServiceContainer.Settings.RingLight.Enabled
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

    private void CardExpander_Loaded(object sender, RoutedEventArgs e)
    {
        // Show expander for brightness, eyecare, and clipboard cards
        if (sender is Expander expander && expander.DataContext is UtilityCard card)
        {
            expander.Visibility = (card.Id == "brightness" || card.Id == "eyecare" || card.Id == "clipboard")
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void CardExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        // Elevate the card when expanding (bring to front with z-index and shadow)
        var border = GetParentBorder(sender);
        if (border != null)
        {
            // Set high z-index to appear on top
            Canvas.SetZIndex(border, 10);
            
            // Add elevation with translation and shadow
            border.Translation = new System.Numerics.Vector3(0, 0, 16);
            
            // Force layout update
            border.InvalidateMeasure();
            border.InvalidateArrange();
            border.UpdateLayout();
        }
    }

    private void CardExpander_Collapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        // Reset the card elevation when collapsing
        var border = GetParentBorder(sender);
        if (border != null)
        {
            // Reset z-index to normal
            Canvas.SetZIndex(border, 0);
            
            // Remove elevation
            border.Translation = new System.Numerics.Vector3(0, 0, 0);
            
            // Force layout update
            border.InvalidateMeasure();
            border.InvalidateArrange();
            border.UpdateLayout();
        }
    }

    private Border? GetParentBorder(DependencyObject child)
    {
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is Border border)
                return border;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
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
    
    public Visibility BrightnessContentVisibility => IsBrightnessCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EyeCareContentVisibility => IsEyeCareCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClipboardContentVisibility => IsClipboardCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RingLightContentVisibility => IsRingLightCard ? Visibility.Visible : Visibility.Collapsed;
    
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

