using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppLock.Views;

public sealed partial class ChangeHotkeyDialog : ContentDialog
{
    private bool _isRecording = false;
    private bool _isManualMode = false;
    private readonly HashSet<VirtualKey> _pressedKeys = new();
    public string? NewHotkey { get; private set; }
    public bool HotkeyChanged { get; private set; }

    public ChangeHotkeyDialog(string currentHotkey)
    {
        InitializeComponent();
        CurrentHotkeyText.Text = currentHotkey;
        NewHotkeyText.Text = "";

        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            this.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            this.RequestedTheme = ServiceContainer.CurrentTheme;
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isManualMode) return; // ignore in manual mode

        if (!_isRecording)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }
    }

    private void ToggleModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isManualMode = !_isManualMode;

        if (_isManualMode)
        {
            // Switch to manual text entry mode
            if (_isRecording) StopRecording();
            RecordedHotkeyDisplay.Visibility = Visibility.Collapsed;
            ManualHotkeyBox.Visibility = Visibility.Visible;
            RecordModePanel.Visibility = Visibility.Collapsed;
            ToggleModeButton.Content = "← Switch back to key recording";
            // Pre-fill with current recorded value if any
            if (!string.IsNullOrEmpty(NewHotkeyText.Text) && NewHotkeyText.Text != "Recording...")
                ManualHotkeyBox.Text = NewHotkeyText.Text;
            ManualHotkeyBox.Focus(FocusState.Programmatic);
        }
        else
        {
            // Switch back to record mode
            RecordedHotkeyDisplay.Visibility = Visibility.Visible;
            ManualHotkeyBox.Visibility = Visibility.Collapsed;
            RecordModePanel.Visibility = Visibility.Visible;
            ToggleModeButton.Content = "Can't capture Ctrl+Alt combos? Type it manually →";
        }
    }

    private void ManualHotkeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = ManualHotkeyBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            IsPrimaryButtonEnabled = false;
            return;
        }

        // Validate: must contain a modifier and a key that we can parse
        bool hasModifier = text.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("Control", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("Alt", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("Shift", StringComparison.OrdinalIgnoreCase) ||
                           text.Contains("Win", StringComparison.OrdinalIgnoreCase);
        var (_, key) = AppLock.Services.HotkeyService.ParseHotkey(text);
        bool hasKey = key != 0;

        IsPrimaryButtonEnabled = hasModifier && hasKey;
        ErrorText.Visibility = (hasModifier && !hasKey && text.Length > 2)
            ? Visibility.Visible : Visibility.Collapsed;
        if (!hasKey && text.Length > 2)
            ErrorText.Text = "Key not recognized. Use format: Ctrl+Alt+L, Ctrl+Shift+F5, etc.";
    }

    private void StartRecording()
    {
        _isRecording = true;
        _pressedKeys.Clear();
        RecordButton.Content = "Stop Recording";
        InstructionText.Text = "Press your desired key combination...";
        NewHotkeyText.Text = "Recording...";
        
        // Set focus to capture key
        CaptureElement.Focus(FocusState.Programmatic);
    }

    private void StopRecording()
    {
        _isRecording = false;
        RecordButton.Content = "Record Hotkey";
        InstructionText.Text = "Click 'Record Hotkey' to set a new emergency hotkey";
        
        if (_pressedKeys.Count > 0)
        {
            string hotkey = BuildHotkeyString();
            NewHotkeyText.Text = hotkey;
            IsPrimaryButtonEnabled = true;
        }
        else
        {
            NewHotkeyText.Text = "No keys captured";
        }
    }

    private void CaptureElement_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecording) return;

        e.Handled = true;
        
        var key = e.Key;
        
        // Add to pressed keys
        if (!_pressedKeys.Contains(key))
        {
            _pressedKeys.Add(key);
        }

        // Update display
        string currentCombo = BuildHotkeyString();
        NewHotkeyText.Text = string.IsNullOrEmpty(currentCombo) ? "..." : currentCombo;
    }

    private void CaptureElement_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecording) return;

        e.Handled = true;
        
        // When key is released, finalize if we have at least a modifier + key
        if (_pressedKeys.Count >= 2)
        {
            StopRecording();
        }
    }

    private string BuildHotkeyString()
    {
        List<string> parts = new();

        // Modifiers
        if (_pressedKeys.Contains(VirtualKey.Control) || _pressedKeys.Contains(VirtualKey.LeftControl) || _pressedKeys.Contains(VirtualKey.RightControl))
            parts.Add("Ctrl");
        
        if (_pressedKeys.Contains(VirtualKey.Menu) || _pressedKeys.Contains(VirtualKey.LeftMenu) || _pressedKeys.Contains(VirtualKey.RightMenu))
            parts.Add("Alt");
        
        if (_pressedKeys.Contains(VirtualKey.Shift) || _pressedKeys.Contains(VirtualKey.LeftShift) || _pressedKeys.Contains(VirtualKey.RightShift))
            parts.Add("Shift");
        
        if (_pressedKeys.Contains(VirtualKey.LeftWindows) || _pressedKeys.Contains(VirtualKey.RightWindows))
            parts.Add("Win");

        // Regular keys
        foreach (var key in _pressedKeys)
        {
            if (IsModifierKey(key))
                continue;

            string keyName = key.ToString();
            
            // Handle number keys
            if (keyName.StartsWith("Number"))
                keyName = keyName.Replace("Number", "");
            
            parts.Add(keyName);
        }

        return string.Join("+", parts);
    }

    private bool IsModifierKey(VirtualKey key)
    {
        return key == VirtualKey.Control || key == VirtualKey.LeftControl || key == VirtualKey.RightControl ||
               key == VirtualKey.Menu || key == VirtualKey.LeftMenu || key == VirtualKey.RightMenu ||
               key == VirtualKey.Shift || key == VirtualKey.LeftShift || key == VirtualKey.RightShift ||
               key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        string hotkeyText;

        if (_isManualMode)
        {
            hotkeyText = ManualHotkeyBox.Text?.Trim() ?? string.Empty;
        }
        else
        {
            hotkeyText = NewHotkeyText.Text;
        }

        if (string.IsNullOrEmpty(hotkeyText) || hotkeyText == "Recording..." || hotkeyText == "No keys captured")
        {
            ErrorText.Text = "Please record or type a valid hotkey.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Validate hotkey has at least one modifier
        if (!hotkeyText.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) &&
            !hotkeyText.Contains("Alt", StringComparison.OrdinalIgnoreCase) &&
            !hotkeyText.Contains("Shift", StringComparison.OrdinalIgnoreCase) &&
            !hotkeyText.Contains("Win", StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = "Hotkey must include at least one modifier key (Ctrl, Alt, Shift, or Win).";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Validate we can parse a non-zero key code
        var (_, key) = AppLock.Services.HotkeyService.ParseHotkey(hotkeyText);
        if (key == 0)
        {
            ErrorText.Text = "Could not parse the key. Use format: Ctrl+Alt+L or Ctrl+Shift+F5.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        NewHotkey = hotkeyText;
        HotkeyChanged = true;
        Hide();
    }
}
