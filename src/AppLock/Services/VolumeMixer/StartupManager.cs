using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Manages "Start with Windows" functionality for Volume Mixer.
/// Handles both packaged (MSIX) and unpackaged scenarios.
/// </summary>
public static class StartupManager
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "AppLockVolumeMixer";

    /// <summary>
    /// Enables or disables auto-start on Windows login.
    /// </summary>
    public static async Task<bool> SetStartWithWindows(bool enable)
    {
        try
        {
            // Try packaged app approach first (MSIX/Store)
            if (await TrySetPackagedStartup(enable))
            {
                System.Diagnostics.Debug.WriteLine($"[StartupManager] Packaged startup {(enable ? "enabled" : "disabled")}");
                return true;
            }

            // Fall back to registry approach (unpackaged)
            return SetUnpackagedStartup(enable);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupManager] Failed to set startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets current startup status.
    /// </summary>
    public static async Task<bool> GetStartWithWindows()
    {
        try
        {
            // Check packaged first
            var packagedStatus = await GetPackagedStartupStatus();
            if (packagedStatus.HasValue)
            {
                return packagedStatus.Value;
            }

            // Check registry
            return GetUnpackagedStartupStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupManager] Failed to get startup status: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> TrySetPackagedStartup(bool enable)
    {
        // Package.Current throws a native exception (0xc0000005) on unpackaged apps
        // Use the safe IsPackaged check first
        if (!IsRunningAsPackaged())
        {
            return false;
        }

        try
        {
            var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("AppLockVolumeMixerStartup");
            
            if (startupTask == null)
            {
                System.Diagnostics.Debug.WriteLine("[StartupManager] Startup task not found in manifest");
                return false;
            }

            if (enable)
            {
                var state = await startupTask.RequestEnableAsync();
                return state == Windows.ApplicationModel.StartupTaskState.Enabled;
            }
            else
            {
                startupTask.Disable();
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupManager] Packaged startup failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool?> GetPackagedStartupStatus()
    {
        if (!IsRunningAsPackaged())
        {
            return null;
        }

        try
        {
            var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("AppLockVolumeMixerStartup");
            if (startupTask == null)
            {
                return null;
            }

            return startupTask.State == Windows.ApplicationModel.StartupTaskState.Enabled;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely checks if the app is running as an MSIX packaged app.
    /// Package.Current throws on unpackaged apps, so we use GetCurrentPackageFullName instead.
    /// </summary>
    private static bool IsRunningAsPackaged()
    {
        try
        {
            // GetCurrentPackageFullName returns non-zero for packaged apps
            int length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            // ERROR_INSUFFICIENT_BUFFER (0x8007007A) means packaged (buffer too small but function found package)
            // APPMODEL_ERROR_NO_PACKAGE (0x80073D54) means not packaged
            return result == 0x8007007A || result == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

    private static bool SetUnpackagedStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key == null)
            {
                System.Diagnostics.Debug.WriteLine("[StartupManager] Failed to open registry key");
                return false;
            }

            if (enable)
            {
                // Get current executable path
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Debug.WriteLine("[StartupManager] Failed to get exe path");
                    return false;
                }

                // Set registry value with --minimized flag
                var command = $"\"{exePath}\" --minimized";
                key.SetValue(RegistryValueName, command, RegistryValueKind.String);
                
                System.Diagnostics.Debug.WriteLine($"[StartupManager] Registry startup enabled: {command}");
                return true;
            }
            else
            {
                // Remove registry value
                key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
                
                System.Diagnostics.Debug.WriteLine("[StartupManager] Registry startup disabled");
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupManager] Registry operation failed: {ex.Message}");
            return false;
        }
    }

    private static bool GetUnpackagedStartupStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            if (key == null)
            {
                return false;
            }

            var value = key.GetValue(RegistryValueName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }
}
