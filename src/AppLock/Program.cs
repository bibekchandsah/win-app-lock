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

        // 3. If running headless DNS restoration (--restore-dns)
        if (args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--restore-dns", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var store = new SettingsStore();
                var settings = store.Load();
                AppLock.Services.WebFilter.DnsConfigurator.RestoreDnsHeadless(settings.WebFilter);
                settings.WebFilter.Enabled = false;
                store.Save(settings);
                Console.WriteLine("[AppLock] Original DNS settings restored successfully.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AppLock] Failed to restore DNS: {ex.Message}");
            }
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

        // 3b. Island rendering spike (milestone 1): open only the island window, without the main app.
        if (args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--island-spike", StringComparison.OrdinalIgnoreCase)))
        {
            var spikeLog = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "AppLock", "Logs", "island-spike.log");
            void SpikeLog(string message)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(spikeLog)!);
                    System.IO.File.AppendAllText(spikeLog, $"[{System.DateTime.Now:HH:mm:ss}] {message}{System.Environment.NewLine}");
                }
                catch { }
            }

            SpikeLog("island spike requested");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => SpikeLog("unhandled: " + e.ExceptionObject);

            try
            {
                XamlCheckProcessRequirements();
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    try
                    {
                        SpikeLog("application started");
                        var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                        System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                        _ = new App();
                        SpikeLog("app created");
                    }
                    catch (Exception ex)
                    {
                        SpikeLog("startup failed: " + ex);
                    }
                });
            }
            catch (Exception ex)
            {
                SpikeLog("spike failed: " + ex);
            }

            return;
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
