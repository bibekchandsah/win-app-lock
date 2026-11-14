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

    /// <summary>Switch that tells the app it was launched by Windows, so it opens in the tray.</summary>
    private const string STARTUP_ARGUMENT = "--startup";

    private static string CommandLineFor(string exePath) => $"\"{exePath}\" {STARTUP_ARGUMENT}";

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
                // The value holds the full command line, so compare only its executable part
                // (and keep recognising entries written by older builds without arguments).
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                string registeredPath = ExtractExecutablePath(value);
                return !string.IsNullOrEmpty(registeredPath) &&
                       registeredPath.Equals(currentExePath, StringComparison.OrdinalIgnoreCase);
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

                // Pass the startup switch so a Windows sign-in launch goes straight to the tray.
                key.SetValue(APP_NAME, CommandLineFor(exePath), RegistryValueKind.String);
                System.Diagnostics.Debug.WriteLine($"Enabled startup: {exePath} {STARTUP_ARGUMENT}");
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
    /// Rewrites a Run entry created by an older build (bare path, no switch) so the next
    /// Windows sign-in starts hidden in the tray. Does nothing when startup is not registered.
    /// </summary>
    public void UpdateStartupCommandLine()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_RUN_KEY, false);
            var value = key?.GetValue(APP_NAME) as string;

            if (string.IsNullOrEmpty(value)) return;

            if (value.Contains(STARTUP_ARGUMENT, StringComparison.OrdinalIgnoreCase) ||
                value.Contains("--minimized", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            using var writableKey = Registry.CurrentUser.OpenSubKey(REGISTRY_RUN_KEY, true);
            writableKey?.SetValue(APP_NAME, CommandLineFor(exePath), RegistryValueKind.String);
            System.Diagnostics.Debug.WriteLine($"Migrated startup entry: {exePath} {STARTUP_ARGUMENT}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error migrating startup entry: {ex.Message}");
        }
    }

    /// <summary>Pulls the executable path out of a Run value (quoted path + optional arguments).</summary>
    private static string ExtractExecutablePath(string commandLine)
    {
        var value = commandLine.Trim();
        if (value.Length == 0) return string.Empty;

        if (value[0] == '"')
        {
            int endQuote = value.IndexOf('"', 1);
            if (endQuote > 1) return value.Substring(1, endQuote - 1);
        }

        // Unquoted values are either the bare legacy path (which may contain spaces) or a path
        // followed by arguments — in both cases the path ends at ".exe".
        int exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0) return value.Substring(0, exeIndex + 4);

        return value;
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
                    Arguments = $"/Create /SC ONLOGON /TN \"{APP_NAME}\" /TR \"\\\"{exePath}\\\" {STARTUP_ARGUMENT}\" /RL HIGHEST /F",
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
