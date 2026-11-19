using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    /// <summary>Keyboard/mouse users get the same action as the eye button by clicking the row.</summary>
    private void NetworksList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WifiProfileViewModel network && network.ActionsVisibility == Visibility.Visible)
        {
            network.IsRevealed = !network.IsRevealed;
        }
    }

    /// <summary>
    /// Arrow keys already move the selection between rows; Enter/Space toggles the password and
    /// Ctrl+C copies it, matching the buttons at the end of each row.
    /// </summary>
    private void NetworksList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (NetworksList.SelectedItem is not WifiProfileViewModel network) return;
        if (network.ActionsVisibility != Visibility.Visible) return;

        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            network.IsRevealed = !network.IsRevealed;
            e.Handled = true;
            return;
        }

        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);

        if (e.Key == Windows.System.VirtualKey.C &&
            control.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            CopyPassword(network);
            e.Handled = true;
        }
    }

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

        CopyPassword(network);
    }

    /// <summary>
    /// Shows the standard `WIFI:` QR payload so a phone can join from a scan instead of typing the password.
    /// </summary>
    private async void QrButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not WifiProfileViewModel network) return;
        if (network.IsOpen || network.IsPasswordUnavailable) return;

        var payload = WifiQrCode.BuildPayload(network.Ssid, network.Password, network.IsOpen);

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(WifiQrCode.CreateVisual(payload));

        stack.Children.Add(new TextBlock
        {
            Text = network.Ssid,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Point your phone's camera at the code to join this network.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 280
        });

        var dialog = new ContentDialog
        {
            Title = "Wi-Fi QR code",
            Content = stack,
            PrimaryButtonText = "Copy password",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.ActualTheme ?? ServiceContainer.CurrentTheme
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            CopyPassword(network);
        }
    }

    private void CopyPassword(WifiProfileViewModel network)
    {
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
