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
            
            if (settings != null)
            {
                bool hadSelfDisciplineInJson = json.Contains("\"SelfDiscipline\"");
                MigrateSettings(settings, !hadSelfDisciplineInJson);
                return settings;
            }

            return new AppSettings { IsFirstRun = true };
        }
        catch (Exception ex)
        {
            LogError("Failed to load settings", ex);
            return new AppSettings { IsFirstRun = true };
        }
    }

    private void MigrateSettings(AppSettings settings, bool forceSave = false)
    {
        bool modified = forceSave;

        if (settings.LockRules == null)
        {
            settings.LockRules = new List<LockRule>();
        }

        if (settings.Clipboard == null)
        {
            settings.Clipboard = new ClipboardSettings();
            modified = true;
        }
        settings.Clipboard.Clamp();

        if (settings.RingLight == null)
        {
            settings.RingLight = new RingLightSettings();
            modified = true;
        }

        if (settings.VolumeMixer == null)
        {
            settings.VolumeMixer = new VolumeMixerSettings();
            modified = true;
        }
        settings.VolumeMixer.Clamp();

        // Migration: Change Win+V to Alt+V (Win+V conflicts with clipboard)
        if (settings.VolumeMixer.Hotkey == "Win+V")
        {
            settings.VolumeMixer.Hotkey = "Alt+V";
            modified = true;
            System.Diagnostics.Debug.WriteLine("[SettingsStore] Migrated Volume Mixer hotkey: Win+V → Alt+V");
        }

        if (settings.NotificationCustom == null)
        {
            settings.NotificationCustom = new NotificationCustomSettings();
            modified = true;
        }

        if (settings.NotificationCustom.ToggleHotkey == null || settings.NotificationCustom.ToggleHotkey.VirtualKey == 0)
        {
            settings.NotificationCustom.ToggleHotkey = new HotkeyBinding { Modifiers = 0x0001, VirtualKey = 0x4E }; // Alt+N
            modified = true;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var rule in settings.LockRules)
        {
            if (rule.SelfDiscipline == null)
            {
                rule.SelfDiscipline = new SelfDisciplineSettings();
                modified = true;
            }

            if (rule.SelfDiscipline.TimeBlocks == null)
            {
                rule.SelfDiscipline.TimeBlocks = new List<TimeBlock>();
                modified = true;
            }

            if (rule.SelfDiscipline.QuotaDate == default)
            {
                rule.SelfDiscipline.QuotaDate = today;
                modified = true;
            }

            if (rule.SelfDiscipline.DailyLimitMinutes <= 0)
            {
                rule.SelfDiscipline.DailyLimitMinutes = 60;
                modified = true;
            }

            if (rule.SelfDiscipline.IdleRelockMinutes <= 0)
            {
                rule.SelfDiscipline.IdleRelockMinutes = 15;
                modified = true;
            }

            foreach (var block in rule.SelfDiscipline.TimeBlocks)
            {
                if (block.Days == null || block.Days.Length != 7)
                {
                    block.Days = new bool[] { true, true, true, true, true, true, true };
                    modified = true;
                }
            }
        }

        if (modified)
        {
            try
            {
                Save(settings);
            }
            catch (Exception ex)
            {
                LogError("Failed to save migrated settings", ex);
            }
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
