using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AppLock.Views;

public sealed partial class WifiPasswordsPage : Page
{
    public ObservableCollection<WifiProfileViewModel> Networks { get; } = new();

    public WifiPasswordsPage()
    {
        InitializeComponent();

        // Offer the elevation route whenever AppLock is running without administrator rights,
        // exactly like the BitLocker page's InfoBar.
        ElevationInfoBar.IsOpen = !KeyboardService.IsAdministrator();

        Loaded += async (s, e) => await LoadAsync();
    }

    /// <summary>
    /// Relaunches AppLock elevated so restricted profiles can be read, mirroring the Keyboard page.
    /// </summary>
    private async void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (KeyboardService.IsAdministrator()) return;

            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            // Mark this exit as authorized so the watchdog does not revive the non-elevated instance.
            ProcessProtectionService.SignalAuthorizedExit();
            ProcessProtectionService.UnprotectCurrentProcess();

            var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                // Give the elevated instance a moment to come up, then close this one.
                await Task.Delay(400);
                Environment.Exit(0);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user dismissed the UAC prompt - keep running unelevated.
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Could not restart as administrator: {ex.Message}";
            StatusBar.IsOpen = true;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(OthersPage));
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;

        try
        {
            var networks = await WifiPasswordService.GetSavedNetworksAsync();

            Networks.Clear();
            foreach (var network in networks)
            {
                Networks.Add(new WifiProfileViewModel(network));
            }

            CountText.Text = Networks.Count == 1 ? "1 network" : $"{Networks.Count} networks";
            EmptyPanel.Visibility = Networks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // A profile can genuinely have no key (open or enterprise network) even when elevated,
            // so only point at the admin restart when the process is actually unelevated.
            int unavailable = Networks.Count(n => n.IsPasswordUnavailable);
            if (unavailable > 0)
            {
                bool elevated = KeyboardService.IsAdministrator();
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Message = elevated
                    ? $"{unavailable} saved network(s) have no stored password (open or enterprise networks)."
                    : $"{unavailable} saved network(s) have no visible password. Restart AppLock as administrator to read the ones Windows hides from standard users.";
                StatusBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Could not read the saved Wi-Fi networks: {ex.Message}";
            StatusBar.IsOpen = true;
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is WifiProfileViewModel network)
        {
            network.IsRevealed = !network.IsRevealed;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not WifiProfileViewModel network) return;

        try
        {
            var package = new DataPackage();
            package.SetText(network.Password);
            Clipboard.SetContent(package);
            Clipboard.Flush();

            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"Password for \u201C{network.Ssid}\u201D copied to the clipboard.";
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Could not copy the password: {ex.Message}";
            StatusBar.IsOpen = true;
        }
    }
}

/// <summary>One saved Wi-Fi network, with the password hidden unless the user reveals it.</summary>
public sealed class WifiProfileViewModel : INotifyPropertyChanged
{
    private const string MaskedPassword = "\u25CF\u25CF\u25CF\u25CF\u25CF\u25CF\u25CF\u25CF";

    private bool _isRevealed;

    public WifiProfileViewModel(WifiPasswordService.WifiNetwork network)
    {
        Ssid = network.Ssid;
        Password = network.Password;
        IsOpen = network.IsOpen;
        IsPasswordUnavailable = !IsOpen && string.IsNullOrEmpty(Password);
    }

    public string Ssid { get; }
    public string Password { get; }
    public bool IsOpen { get; }
    public bool IsPasswordUnavailable { get; }

    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (_isRevealed == value) return;
            _isRevealed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPassword));
            OnPropertyChanged(nameof(RevealGlyph));
            OnPropertyChanged(nameof(RevealToolTip));
        }
    }

    public string DisplayPassword
    {
        get
        {
            if (IsOpen) return "Open network - no password";
            if (IsPasswordUnavailable) return "Password not available";
            return IsRevealed ? Password : MaskedPassword;
        }
    }

    /// <summary>Eye / crossed-eye glyph for the reveal button.</summary>
    public string RevealGlyph => IsRevealed ? "\uED1A" : "\uE7B3";

    public string RevealToolTip => IsRevealed ? "Hide password" : "Show password";

    /// <summary>Reveal and copy only make sense for a network that actually has a key.</summary>
    public Visibility ActionsVisibility =>
        IsOpen || IsPasswordUnavailable ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
