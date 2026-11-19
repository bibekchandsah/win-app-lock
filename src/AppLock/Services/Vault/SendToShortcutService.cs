using System;
using System.IO;

namespace AppLock.Services.Vault;

/// <summary>
/// Manages the "Add to AppLock Vault" shortcut in the Windows SendTo folder.
/// </summary>
public static class SendToShortcutService
{
    private const string ShortcutFileName = "Add to AppLock Vault.lnk";

    public static string GetShortcutPath()
    {
        var sendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        return Path.Combine(sendTo, ShortcutFileName);
    }

    public static bool IsEnabled()
    {
        return File.Exists(GetShortcutPath());
    }

    public static void SetEnabled(bool enable)
    {
        var path = GetShortcutPath();
        if (enable)
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath = exePath;
                shortcut.Arguments = "--vault-add";
                shortcut.Description = "Add selected files or folders to an AppLock encrypted vault";
                shortcut.IconLocation = $"{exePath},0";
                shortcut.Save();

                LogService.LogAction("Vault", "SendTo shortcut created.");
            }
            catch (Exception ex)
            {
                LogService.LogAction("Vault", $"Failed to create SendTo shortcut: {ex.Message}");
            }
        }
        else
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    LogService.LogAction("Vault", "SendTo shortcut removed.");
                }
            }
            catch (Exception ex)
            {
                LogService.LogAction("Vault", $"Failed to remove SendTo shortcut: {ex.Message}");
            }
        }
    }
}
