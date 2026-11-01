using Microsoft.UI.Xaml;

namespace AppLock;

public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        this.UnhandledException += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[CRITICAL] Unhandled Exception: {e.Message}");
            System.Diagnostics.Debug.WriteLine(e.Exception);
            e.Handled = true;
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();

        // Apply process tamper protection & launch independent watchdog after window is up
        AppLock.Services.ProcessProtectionService.ProtectCurrentProcess();
        AppLock.Services.ProcessProtectionService.StartWatchdog();
    }

    public Window? FindMainWindow()
    {
        return _mainWindow;
    }
}
