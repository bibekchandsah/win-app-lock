using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AppLock.Services;

/// <summary>
/// Service for managing application startup with Windows
/// </summary>
public class StartupService
{
    private const string APP_NAME = "AppLock";
    private const string REGISTRY_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Check if the application is set to start with Windows
    /// </summary>
    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_RUN_KEY, false);
            var value = key?.GetValue(APP_NAME) as string;
            
            if (!string.IsNullOrEmpty(value))
            {
                // Verify the path still exists and matches current executable
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                return value.Equals(currentExePath, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking startup status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enable or disable application startup with Windows
    /// </summary>
    public bool SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_RUN_KEY, true);
            
            if (key == null)
            {
                System.Diagnostics.Debug.WriteLine("Could not open registry key for startup");
                return false;
            }

            if (enabled)
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    System.Diagnostics.Debug.WriteLine("Executable path not found");
                    return false;
                }

                key.SetValue(APP_NAME, exePath, RegistryValueKind.String);
                System.Diagnostics.Debug.WriteLine($"Enabled startup: {exePath}");
                return true;
            }
            else
            {
                key.DeleteValue(APP_NAME, false);
                System.Diagnostics.Debug.WriteLine("Disabled startup");
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting startup: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Create a scheduled task for startup (alternative to registry, supports elevation)
    /// </summary>
    public bool CreateScheduledTask(bool enabled)
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            
            if (string.IsNullOrEmpty(exePath))
                return false;

            if (enabled)
            {
                // Create scheduled task using schtasks.exe
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /SC ONLOGON /TN \"{APP_NAME}\" /TR \"\\\"{exePath}\\\"\" /RL HIGHEST /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                process?.WaitForExit();

                bool success = process?.ExitCode == 0;
                System.Diagnostics.Debug.WriteLine($"Scheduled task creation: {(success ? "success" : "failed")}");
                return success;
            }
            else
            {
                // Delete scheduled task
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /TN \"{APP_NAME}\" /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                process?.WaitForExit();

                bool success = process?.ExitCode == 0;
                System.Diagnostics.Debug.WriteLine($"Scheduled task deletion: {(success ? "success" : "failed")}");
                return success;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error managing scheduled task: {ex.Message}");
            return false;
        }
    }
}
