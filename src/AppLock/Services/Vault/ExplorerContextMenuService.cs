using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppLock.Services.Vault;

/// <summary>
/// Registers and unregisters Windows Explorer context menu ("Add to AppLock Vault")
/// for files and directories under HKCU (no administrator privileges required).
/// </summary>
public static class ExplorerContextMenuService
{
    public const string DefaultMenuKeyPathFiles = @"Software\Classes\*\shell\AppLock.AddVault";
    public const string DefaultMenuKeyPathFolders = @"Software\Classes\Directory\shell\AppLock.AddVault";

    internal static string MenuKeyPathFiles = DefaultMenuKeyPathFiles;
    internal static string MenuKeyPathFolders = DefaultMenuKeyPathFolders;
    private const string VerbText = "Add to AppLock Vault";

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

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
            var exePath = GetExecutablePath();
            if (string.IsNullOrEmpty(exePath)) return;

            string commandStr = $"\"{exePath}\" --vault-add \"%1\"";
            string iconStr = exePath;

            // Register for files: HKCU\Software\Classes\*\shell\AppLock.AddVault
            RegisterKey(MenuKeyPathFiles, VerbText, iconStr, commandStr);

            // Register for directories: HKCU\Software\Classes\Directory\shell\AppLock.AddVault
            RegisterKey(MenuKeyPathFolders, VerbText, iconStr, commandStr);

            NotifyShellChanged();
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
            NotifyShellChanged();
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
        key.SetValue("Position", "Top");

        using var cmdKey = key.CreateSubKey("command", writable: true);
        cmdKey?.SetValue("", command);
    }

    private static void NotifyShellChanged()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static string? GetExecutablePath()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && !exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exePath))
        {
            return exePath;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "AppLock.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return exePath;
    }
}
