using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AppLock.Services.BitLocker;

public static class BitLockerLogger
{
    private static readonly object _lock = new();
    private static readonly LinkedList<string> _recentEntries = new();
    private const int MaxEntries = 200;

    // Pattern to detect and redact potential 48-digit numerical recovery passwords
    private static readonly Regex RecoveryKeyRegex = new(
        @"\b(?:\d{6}[-\s]?){7}\d{6}\b|\b\d{48}\b",
        RegexOptions.Compiled);

    public static string LogsDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, "AppLock", "Logs");
            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch { }
            }
            return dir;
        }
    }

    public static string LogFilePath => Path.Combine(LogsDirectory, "bitlocker.log");

    public static void Log(string message, string? driveLetter = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var sanitized = Sanitize(message);
        var prefix = string.IsNullOrWhiteSpace(driveLetter) ? "" : $"[{driveLetter}] ";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logLine = $"[{timestamp}] {prefix}{sanitized}";

        lock (_lock)
        {
            _recentEntries.AddLast(logLine);
            while (_recentEntries.Count > MaxEntries)
            {
                _recentEntries.RemoveFirst();
            }

            try
            {
                File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write to BitLocker log file: {ex.Message}");
            }
        }
    }

    public static IReadOnlyList<string> GetRecentLogs()
    {
        lock (_lock)
        {
            return _recentEntries.ToList();
        }
    }

    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Redact any full numerical recovery keys
        var redacted = RecoveryKeyRegex.Replace(input, "[REDACTED-KEY]");

        // Redact any password parameters if accidentally logged
        redacted = Regex.Replace(redacted, @"(?i)(password|passphrase)\s*[:=]\s*[^\s,;]+", "$1: [REDACTED]");

        return redacted;
    }
}
