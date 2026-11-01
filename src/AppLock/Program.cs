using System;
using System.Runtime.InteropServices;
using AppLock.Services;

namespace AppLock;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    static void Main(string[] args)
    {
        // 1. If running as Watchdog, execute pure headless monitoring loop
        if (args.Length > 1 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            ProcessProtectionService.RunWatchdog(parentPid);
            return;
        }

        // 2. Normal AppLock instance
        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
