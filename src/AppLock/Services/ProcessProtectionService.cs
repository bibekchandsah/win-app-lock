using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;

namespace AppLock.Services;

/// <summary>
/// Provides process tamper protection by denying PROCESS_TERMINATE in the process DACL,
/// and runs an independent mutual watchdog (AppLockGuard.exe) to immediately revive
/// the process if it is terminated by Task Manager or external tools.
/// </summary>
public static class ProcessProtectionService
{
    private const int DACL_SECURITY_INFORMATION = 4;
    private const string GuardExeName = "AppLockGuard.exe";
    private const string MainExeName = "AppLock.exe";

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(
        IntPtr handle,
        int securityInformation,
        [In] byte[] pSecurityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static EventWaitHandle? _authorizedExitEvent;
    private static bool _isProtected = false;

    public static bool IsProtected => _isProtected;

    /// <summary>
    /// Modifies the process DACL to deny termination, thread creation, suspension,
    /// and memory writes to all users including standard Task Manager.
    /// </summary>
    public static bool ProtectCurrentProcess()
    {
        try
        {
            // Deny:
            //   PROCESS_TERMINATE        (0x0001)
            //   PROCESS_VM_OPERATION     (0x0008)
            //   PROCESS_VM_WRITE         (0x0020)
            //   PROCESS_SUSPEND_RESUME   (0x0800)
            //   WRITE_DAC                (0x40000)
            //   WRITE_OWNER              (0x80000)
            // Combined Deny Mask = 0xC0829
            //
            // Allow:
            //   SYNCHRONIZE | READ_CONTROL | QUERY_LIMITED | QUERY | VM_READ
            // Combined Allow Mask = 0x121411
            string sddl = "D:(D;;0xC0829;;;WD)(A;;0x121411;;;WD)";
            var raw = new RawSecurityDescriptor(sddl);
            byte[] binary = new byte[raw.BinaryLength];
            raw.GetBinaryForm(binary, 0);

            bool success = SetKernelObjectSecurity(GetCurrentProcess(), DACL_SECURITY_INFORMATION, binary);
            _isProtected = success;
            Debug.WriteLine($"[ProcessProtection] ProtectCurrentProcess result: {success}");
            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessProtection] ProtectCurrentProcess failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restores standard DACL so the process can exit cleanly when authorized.
    /// </summary>
    public static bool UnprotectCurrentProcess()
    {
        try
        {
            string sddl = "D:(A;;0x1FFFFF;;;WD)";
            var raw = new RawSecurityDescriptor(sddl);
            byte[] binary = new byte[raw.BinaryLength];
            raw.GetBinaryForm(binary, 0);

            bool success = SetKernelObjectSecurity(GetCurrentProcess(), DACL_SECURITY_INFORMATION, binary);
            _isProtected = !success;
            Debug.WriteLine($"[ProcessProtection] UnprotectCurrentProcess result: {success}");
            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessProtection] UnprotectCurrentProcess failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Initializes the named event used to signal to the watchdog that exit was authorized with password.
    /// </summary>
    public static void InitializeAuthorizedExitEvent(int pid)
    {
        try
        {
            string eventName = $"Local\\AppLock_AuthExit_{pid}";
            _authorizedExitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessProtection] Failed to initialize exit event: {ex.Message}");
        }
    }

    /// <summary>
    /// Signals to the watchdog that the user authenticated and authorized process exit.
    /// </summary>
    public static void SignalAuthorizedExit()
    {
        try
        {
            _authorizedExitEvent?.Set();
            Debug.WriteLine("[ProcessProtection] Authorized exit signaled to watchdog");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessProtection] Error signaling authorized exit: {ex.Message}");
        }
    }

    /// <summary>
    /// Spawns an independent background watchdog instance (AppLockGuard.exe) that monitors this process.
    /// Uses UseShellExecute = true to break away from the main process tree so Task Manager tree kills cannot hit both.
    /// </summary>
    public static void StartWatchdog()
    {
        try
        {
            int myPid = Environment.ProcessId;
            InitializeAuthorizedExitEvent(myPid);

            var baseDir = AppContext.BaseDirectory;
            var mainExePath = Path.Combine(baseDir, MainExeName);
            var guardExePath = Path.Combine(baseDir, GuardExeName);

            // Ensure AppLockGuard.exe exists
            if (!File.Exists(guardExePath) && File.Exists(mainExePath))
            {
                try
                {
                    File.Copy(mainExePath, guardExePath, true);
                }
                catch { }
            }

            var targetExe = File.Exists(guardExePath) ? guardExePath : mainExePath;

            var startInfo = new ProcessStartInfo
            {
                FileName = targetExe,
                Arguments = $"--watchdog {myPid}",
                UseShellExecute = true, // Critical: breaks away from AppLock.exe job/tree
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var guardProc = Process.Start(startInfo);
            Debug.WriteLine($"[ProcessProtection] Watchdog process launched: {targetExe} (PID {guardProc?.Id}) for parent {myPid}");

            // Start mutual monitor thread: revive guard if guard is killed unexpectedly
            if (guardProc != null)
            {
                var guardThread = new Thread(() =>
                {
                    try
                    {
                        guardProc.WaitForExit();
                        // If guard died but authorized exit was NOT signaled, respawn guard!
                        if (_authorizedExitEvent != null && !_authorizedExitEvent.WaitOne(0))
                        {
                            Thread.Sleep(300);
                            StartWatchdog();
                        }
                    }
                    catch { }
                })
                {
                    IsBackground = true,
                    Name = "AppLockMutualGuardMonitor"
                };
                guardThread.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessProtection] Failed to launch watchdog: {ex.Message}");
        }
    }

    /// <summary>
    /// Entry point for watchdog mode (runs inside AppLockGuard.exe):
    /// Protects itself and monitors parent process.
    /// If parent exits without authorized exit signal, immediately relaunches AppLock.
    /// </summary>
    public static void RunWatchdog(int parentPid)
    {
        try
        {
            // Protect watchdog itself from task kill
            ProtectCurrentProcess();

            Process? parentProcess = null;
            try
            {
                parentProcess = Process.GetProcessById(parentPid);
            }
            catch
            {
                // Parent already gone
                return;
            }

            // Connect to parent's authorized exit event
            string eventName = $"Local\\AppLock_AuthExit_{parentPid}";
            EventWaitHandle? authExitEvent = null;
            for (int i = 0; i < 30 && authExitEvent == null; i++)
            {
                try
                {
                    authExitEvent = EventWaitHandle.OpenExisting(eventName);
                }
                catch
                {
                    Thread.Sleep(50);
                }
            }

            // Wait for parent process to terminate
            parentProcess.WaitForExit();

            // Check if the exit was authorized (password entered)
            bool isAuthorized = false;
            if (authExitEvent != null)
            {
                isAuthorized = authExitEvent.WaitOne(0);
            }

            if (!isAuthorized)
            {
                // Unauthorized kill detected! Revive AppLock immediately.
                var baseDir = AppContext.BaseDirectory;
                var mainExePath = Path.Combine(baseDir, MainExeName);
                if (File.Exists(mainExePath))
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = mainExePath,
                        UseShellExecute = true
                    };
                    var revivedProcess = Process.Start(startInfo);
                    Debug.WriteLine($"[Watchdog] Relaunched AppLock.exe (PID {revivedProcess?.Id})");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watchdog] Error in watchdog loop: {ex.Message}");
        }
        finally
        {
            Environment.Exit(0);
        }
    }
}
