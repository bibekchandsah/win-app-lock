using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppLock.Models;

namespace AppLock.Services;

public class SettingsStore
{
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppLock");
    
    private static readonly string SettingsFilePath = Path.Combine(AppDataPath, "settings.json");
    private static readonly string LogsPath = Path.Combine(AppDataPath, "logs");

    public SettingsStore()
    {
        EnsureDirectoriesExist();
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(LogsPath);
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings { IsFirstRun = true };
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            
            return settings ?? new AppSettings { IsFirstRun = true };
        }
        catch (Exception ex)
        {
            LogError("Failed to load settings", ex);
            return new AppSettings { IsFirstRun = true };
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            LogError("Failed to save settings", ex);
            throw;
        }
    }

    public byte[] ProtectData(byte[] data)
    {
        try
        {
            return System.Security.Cryptography.ProtectedData.Protect(
                data, 
                null, 
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            LogError("Failed to protect data", ex);
            throw;
        }
    }

    public byte[] UnprotectData(byte[] protectedData)
    {
        try
        {
            return System.Security.Cryptography.ProtectedData.Unprotect(
                protectedData, 
                null, 
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            LogError("Failed to unprotect data", ex);
            throw;
        }
    }

    private void LogError(string message, Exception ex)
    {
        try
        {
            var logFile = Path.Combine(LogsPath, $"error_{DateTime.Now:yyyy-MM-dd}.log");
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(logFile, logEntry);
        }
        catch
        {
            // Ignore logging errors
        }
    }
}
