using System;
using System.Runtime.InteropServices;
using System.Threading;
using AppLock.Services;

namespace AppLock;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void XamlCheckProcessRequirements();

    /// <summary>
    /// Only one AppLock window may exist. The mutex is held for the life of the process, and a restarted
    /// instance waits here for the previous one to exit (the abandoned-mutex case) instead of opening a
    /// second window next to it.
    /// </summary>
    private const string SingleInstanceMutexName = @"Local\AppLock_MainInstance_Mutex";

    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static void Main(string[] args)
    {
        // 1. If running as Watchdog, execute pure headless monitoring loop
        if (args.Length > 1 && args[0] == "--watchdog" && int.TryParse(args[1], out int parentPid))
        {
            ProcessProtectionService.RunWatchdog(parentPid);
            return;
        }

        // 2. If running headless BitLocker auto-unlock (from scheduled task)
        if (args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--bitlocker-autounlock", StringComparison.OrdinalIgnoreCase)))
        {
            AppLock.Services.BitLocker.BitLockerAutoUnlockService.RunHeadlessAsync().GetAwaiter().GetResult();
            return;
        }

        // 3. If invoked with --vault-add to add files/folders to vault
        var vaultAddIdx = Array.FindIndex(args, a => string.Equals(a, "--vault-add", StringComparison.OrdinalIgnoreCase));
        if (vaultAddIdx >= 0)
        {
            var pathsToAdd = new System.Collections.Generic.List<string>();
            for (int i = vaultAddIdx + 1; i < args.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(args[i]))
                    pathsToAdd.Add(args[i]);
            }

            if (pathsToAdd.Count > 0)
            {
                // Try signaling running instance
                if (AppLock.Services.Vault.VaultIpcService.SignalPendingAdd(pathsToAdd))
                {
                    System.Diagnostics.Debug.WriteLine("[Program] Signaled running instance with --vault-add items.");
                    return;
                }
            }
        }

        // 4. Normal AppLock instance — but only one at a time. A restarted instance waits for the old one
        //    to finish shutting down; a stray second launch gives up quietly instead of adding a window.
        if (!TryAcquireSingleInstance(TimeSpan.FromSeconds(8)))
        {
            System.Diagnostics.Debug.WriteLine("[Program] Another AppLock instance is already running; exiting");
            return;
        }

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

    private static bool TryAcquireSingleInstance(TimeSpan timeout)
    {
        try
        {
            _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName);

            try
            {
                return _singleInstanceMutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous instance exited without releasing it - which is exactly a restart.
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Program] Single-instance check failed: {ex.Message}");
            return true;
        }
    }
}
