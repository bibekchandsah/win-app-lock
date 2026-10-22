using Microsoft.UI.Xaml;

namespace AppLock;

public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }

    public Window? FindMainWindow()
    {
        return _mainWindow;
    }
}
