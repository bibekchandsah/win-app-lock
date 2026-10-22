using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Reflection;

namespace AppLock;

public sealed partial class TrayIconPage : Page
{
    public event EventHandler? OpenRequested;
    public event EventHandler? ProtectionToggleRequested;
    public event EventHandler? EmergencyLockRequested;
    public event EventHandler? ExitRequested;

    public TrayIconPage()
    {
        InitializeComponent();
        LoadIcon();
    }

    private void LoadIcon()
    {
        try
        {
            var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var iconPath = Path.Combine(assemblyLocation ?? "", "icons", "icon.ico");
            
            if (File.Exists(iconPath))
            {
                TrayIcon.Icon = new System.Drawing.Icon(iconPath);
                System.Diagnostics.Debug.WriteLine($"Tray icon loaded from: {iconPath}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Tray icon not found at: {iconPath}");
                var exePath = Assembly.GetExecutingAssembly().Location;
                TrayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading tray icon: {ex.Message}");
        }
    }

    public void UpdateProtectionMenuItem(bool isProtectionEnabled)
    {
        ProtectionMenuItem.Text = isProtectionEnabled ? "Disable Protection" : "Enable Protection";
    }

    public void UpdateTooltip(string tooltip)
    {
        TrayIcon.ToolTipText = tooltip;
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ProtectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ProtectionToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void EmergencyLockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        EmergencyLockRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
