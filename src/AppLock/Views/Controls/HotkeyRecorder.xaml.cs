using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using AppLock.Services;

namespace AppLock.Views.Controls;

public sealed partial class HotkeyRecorder : UserControl
{
    private bool _isRecording;
    private string _currentHotkey = "Ctrl+Shift+V";

    public event EventHandler<string>? HotkeyChanged;

    public string Hotkey
    {
        get => _currentHotkey;
        set
        {
            _currentHotkey = value;
            UpdateDisplay();
        }
    }

    public HotkeyRecorder()
    {
        this.InitializeComponent();
        this.Loaded += HotkeyRecorder_Loaded;
    }

    private void HotkeyRecorder_Loaded(object sender, RoutedEventArgs e)
    {
        _currentHotkey = ServiceContainer.Settings.Clipboard.Hotkey;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (string.IsNullOrWhiteSpace(_currentHotkey))
        {
            HotkeyDisplayText.Text = "None (Click to record)";
            HotkeyDisplayText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        }
        else
        {
            HotkeyDisplayText.Text = _currentHotkey;
            HotkeyDisplayText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }
    }

    private void StartRecording()
    {
        _isRecording = true;
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        HotkeyDisplayText.Text = "Press shortcut keys...";
        HotkeyDisplayText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        RecordButton.Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        _isRecording = false;
        UpdateDisplay();
    }

    private void RecordButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecording) return;

        e.Handled = true;

        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }

        // Ignore modifier-only key events while waiting for the key
        if (key == VirtualKey.Control || key == VirtualKey.LeftControl || key == VirtualKey.RightControl ||
            key == VirtualKey.Shift || key == VirtualKey.LeftShift || key == VirtualKey.RightShift ||
            key == VirtualKey.Menu || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu ||
            key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows)
        {
            return;
        }

        // Check modifiers
        uint modifiers = 0;
        var ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (ctrlState.HasFlag(CoreVirtualKeyStates.Down)) modifiers |= HotkeyParser.MOD_CONTROL;

        var altState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        if (altState.HasFlag(CoreVirtualKeyStates.Down)) modifiers |= HotkeyParser.MOD_ALT;

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shiftState.HasFlag(CoreVirtualKeyStates.Down)) modifiers |= HotkeyParser.MOD_SHIFT;

        uint vk = (uint)key;
        bool isFunctionKey = vk >= (uint)VirtualKey.F1 && vk <= (uint)VirtualKey.F24;

        if (!isFunctionKey && modifiers == 0)
        {
            // Requires at least one modifier
            ErrorTextBlock.Text = "Shortcut requires at least one modifier (Ctrl, Alt, Shift, or Win), unless using F1–F24.";
            ErrorTextBlock.Visibility = Visibility.Visible;
            return;
        }

        string newHotkey = HotkeyParser.FormatHotkey(modifiers, vk);

        // Try registering to verify conflicts
        bool success = ServiceContainer.ClipboardMonitor.RegisterHotkey(newHotkey);
        if (success)
        {
            _currentHotkey = newHotkey;
            ServiceContainer.Settings.Clipboard.Hotkey = newHotkey;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            StopRecording();
            HotkeyChanged?.Invoke(this, newHotkey);
        }
        else
        {
            ErrorTextBlock.Text = "This shortcut is used by another app. Choose a different one.";
            ErrorTextBlock.Visibility = Visibility.Visible;
            StopRecording();
        }
    }

    private void RecordButton_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_isRecording)
        {
            e.Handled = true;
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        const string defaultHotkey = "Ctrl+Shift+V";
        bool success = ServiceContainer.ClipboardMonitor.RegisterHotkey(defaultHotkey);
        if (success)
        {
            _currentHotkey = defaultHotkey;
            ServiceContainer.Settings.Clipboard.Hotkey = defaultHotkey;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            UpdateDisplay();
            HotkeyChanged?.Invoke(this, defaultHotkey);
        }
        else
        {
            ErrorTextBlock.Text = "Default shortcut Ctrl+Shift+V is used by another application.";
            ErrorTextBlock.Visibility = Visibility.Visible;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardMonitor.RegisterHotkey(string.Empty);
        _currentHotkey = string.Empty;
        ServiceContainer.Settings.Clipboard.Hotkey = string.Empty;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        ErrorTextBlock.Visibility = Visibility.Collapsed;
        UpdateDisplay();
        HotkeyChanged?.Invoke(this, string.Empty);
    }
}

