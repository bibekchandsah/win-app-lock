using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AppLock.Views;
using AppLock.Services;

namespace AppLock;

public sealed partial class MainWindow : Window
{
    private SystemBackdropConfiguration? _backdropConfiguration;
    private MicaController? _micaController;
    private bool _loginShown = false;

    public MainWindow()
    {
        InitializeComponent();
        
        // Set window size
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        
        // Set up Mica backdrop
        SetupMica();
        
        // Set dark theme
        if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = ElementTheme.Dark;
        }
        
        // Check first run
        if (ServiceContainer.AuthService.IsFirstRun)
        {
            ShowFirstRun();
        }
        else
        {
            // Show login immediately after loading completes
            ContentFrame.Loaded += async (s, e) =>
            {
                if (!_loginShown)
                {
                    _loginShown = true;
                    await ShowLogin();
                }
            };
        }
    }

    private void SetupMica()
    {
        if (MicaController.IsSupported())
        {
            _backdropConfiguration = new SystemBackdropConfiguration();
            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
            
            SystemBackdrop = new MicaBackdrop() { Kind = MicaKind.Base };
        }
    }

    private void ShowFirstRun()
    {
        var firstRunPage = new FirstRunPage(ServiceContainer.AuthService);
        firstRunPage.FirstRunCompleted += (s, e) =>
        {
            OnFirstRunComplete();
        };
        ContentFrame.Content = firstRunPage;
        
        // Hide navigation rail during first run
        ContentFrame.SetValue(Grid.ColumnSpanProperty, 2);
    }

    public void OnFirstRunComplete()
    {
        // Restore navigation rail
        ContentFrame.ClearValue(Grid.ColumnSpanProperty);
        
        // Navigate to protection page
        ContentFrame.Navigate(typeof(ProtectionPage));
    }

    private async Task ShowLogin()
    {
        int attemptCount = 0;
        while (true)
        {
            attemptCount++;
            System.Diagnostics.Debug.WriteLine($"Login attempt #{attemptCount}");
            
            var dialog = new AuthDialog(ServiceContainer.AuthService)
            {
                XamlRoot = ContentFrame.XamlRoot
            };

            await dialog.ShowAsync();
            
            System.Diagnostics.Debug.WriteLine($"IsAuthenticated: {dialog.IsAuthenticated}");

            // Check IsAuthenticated - this is set to true only when password is correct
            if (dialog.IsAuthenticated)
            {
                System.Diagnostics.Debug.WriteLine("Authentication successful, navigating to ProtectionPage");
                
                try
                {
                    ContentFrame.Navigate(typeof(ProtectionPage));
                    
                    // Start overlay manager if protection is enabled
                    if (ServiceContainer.Settings.IsProtectionEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine("Starting OverlayManager...");
                        ServiceContainer.OverlayManager.Start();
                        System.Diagnostics.Debug.WriteLine("OverlayManager started successfully");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR during initialization: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                    
                    // Show error to user
                    var errorDialog = new ContentDialog
                    {
                        Title = "Initialization Error",
                        Content = $"Failed to start AppLock:\n{ex.Message}",
                        CloseButtonText = "OK",
                        XamlRoot = ContentFrame.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                    
                    // Exit app
                    Application.Current.Exit();
                    return;
                }
                
                break;
            }
            else
            {
                // Password was wrong or user cancelled - try again
                System.Diagnostics.Debug.WriteLine("Authentication failed, showing dialog again");
            }
            
            // Add small delay to prevent rapid re-showing
            await Task.Delay(100);
        }
    }

    private void ProtectionNavButton_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(typeof(ProtectionPage));
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(typeof(SettingsPage));
    }
}
