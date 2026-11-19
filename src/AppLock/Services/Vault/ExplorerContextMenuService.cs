using System;
using System.IO;
using Microsoft.Win32;

namespace AppLock.Services.Vault;

/// <summary>
/// Registers and unregisters Windows Explorer context menu ("Add to AppLock Vault")
/// for files and directories under HKCU (no administrator privileges required).
/// </summary>
public static class ExplorerContextMenuService
{
    private const string MenuKeyPathFiles = @"Software\Classes\*\shell\AppLock.AddVault";
    private const string MenuKeyPathFolders = @"Software\Classes\Directory\shell\AppLock.AddVault";
    private const string VerbText = "Add to AppLock Vault";

    public static bool IsEnabled()
    {
        try
        {
            using var fileKey = Registry.CurrentUser.OpenSubKey(MenuKeyPathFiles);
            using var folderKey = Registry.CurrentUser.OpenSubKey(MenuKeyPathFolders);
            return fileKey != null && folderKey != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enable)
    {
        if (enable)
        {
            Register();
        }
        else
        {
            Unregister();
        }
    }

    public static void Register()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            string commandStr = $"\"{exePath}\" --vault-add \"%1\"";
            string iconStr = $"\"{exePath}\",0";

            // Register for files: HKCU\Software\Classes\*\shell\AppLock.AddVault
            RegisterKey(MenuKeyPathFiles, VerbText, iconStr, commandStr);

            // Register for directories: HKCU\Software\Classes\Directory\shell\AppLock.AddVault
            RegisterKey(MenuKeyPathFolders, VerbText, iconStr, commandStr);

            LogService.LogAction("Vault", "Explorer context menu registered.");
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Failed to register Explorer context menu: {ex.Message}");
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(MenuKeyPathFiles, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(MenuKeyPathFolders, throwOnMissingSubKey: false);
            LogService.LogAction("Vault", "Explorer context menu unregistered.");
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Failed to unregister Explorer context menu: {ex.Message}");
        }
    }

    private static void RegisterKey(string subKeyPath, string text, string icon, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        if (key == null) return;

        key.SetValue("", text);
        key.SetValue("Icon", icon);

        using var cmdKey = key.CreateSubKey("command", writable: true);
        cmdKey?.SetValue("", command);
    }
}
