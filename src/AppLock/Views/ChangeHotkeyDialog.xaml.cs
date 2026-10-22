using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AppLock.Views;

public sealed partial class ChangeHotkeyDialog : ContentDialog
{
    private bool _isRecording = false;
    private readonly HashSet<VirtualKey> _pressedKeys = new();
    public string? NewHotkey { get; private set; }
    public bool HotkeyChanged { get; private set; }

    public ChangeHotkeyDialog(string currentHotkey)
    {
        InitializeComponent();
        CurrentHotkeyText.Text = currentHotkey;
        NewHotkeyText.Text = "";
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

        if (string.IsNullOrEmpty(NewHotkeyText.Text) || NewHotkeyText.Text == "Recording..." || NewHotkeyText.Text == "No keys captured")
        {
            ErrorText.Text = "Please record a valid hotkey";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Validate hotkey has at least one modifier
        if (!NewHotkeyText.Text.Contains("Ctrl") && !NewHotkeyText.Text.Contains("Alt") && 
            !NewHotkeyText.Text.Contains("Shift") && !NewHotkeyText.Text.Contains("Win"))
        {
            ErrorText.Text = "Hotkey must include at least one modifier key (Ctrl, Alt, Shift, or Win)";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        NewHotkey = NewHotkeyText.Text;
        HotkeyChanged = true;
        Hide();
    }
}
