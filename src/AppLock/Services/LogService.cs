using System;
using System.IO;
using System.Text;

namespace AppLock.Services;

/// <summary>
/// Simple logging service for AppLock actions (no sensitive data)
/// Logs to %APPDATA%\AppLock\logs\
/// </summary>
public class LogService
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppLock",
        "logs");

    private static readonly object _lock = new();

    static LogService()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // Silently fail if we can't create log directory
        }
    }

    public static void LogFirewallAction(string action, string appName, string? details = null)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logFile = Path.Combine(LogDirectory, $"firewall-{DateTime.Now:yyyy-MM}.log");
        var message = $"[{timestamp}] {action}: {appName}";
        if (!string.IsNullOrEmpty(details))
        {
            message += $" - {details}";
        }

        try
        {
            lock (_lock)
            {
                File.AppendAllText(logFile, message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Silently fail logging
        }
    }

    public static void LogAction(string category, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logFile = Path.Combine(LogDirectory, $"{category}-{DateTime.Now:yyyy-MM}.log");
        var logMessage = $"[{timestamp}] {message}";

        try
        {
            lock (_lock)
            {
                File.AppendAllText(logFile, logMessage + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Silently fail logging
        }
    }
}
