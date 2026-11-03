using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.Services.Firewall;

public class InstalledApp
{
    public string DisplayName { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? PackageFamilyName { get; set; }
    public string? Publisher { get; set; }
    public BitmapImage? Icon { get; set; }
    public bool IsUwp => !string.IsNullOrEmpty(PackageFamilyName);
}

public class InstalledAppsService
{
    // System executables that should show warnings before blocking
    private static readonly HashSet<string> SystemCriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe",
        "lsass.exe",
        "services.exe",
        "System",
        "wininit.exe",
        "csrss.exe",
        "winlogon.exe",
        "explorer.exe",
        "MsMpEng.exe",
        "TrustedInstaller.exe",
        "SearchHost.exe",
        "RuntimeBroker.exe",
        "dwm.exe",
        "smss.exe",
        "AppLock.exe",
        "AppLockGuard.exe"
    };

    private static readonly HashSet<string> SystemServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wuauserv",
        "MpsSvc",
        "mpssvc",
        "WinDefend",
        "wscsvc"
    };

    public static bool IsSystemCritical(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        var fileName = Path.GetFileName(exePath);
        return SystemCriticalProcesses.Contains(fileName);
    }

    public static bool IsSystemService(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return false;

        return SystemServiceNames.Contains(serviceName);
    }

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken ct = default)
    {
        var apps = new List<InstalledApp>();

        // Get desktop applications from Start Menu
        var desktopApps = await GetDesktopAppsAsync(ct);
        apps.AddRange(desktopApps);

        // Get UWP/Store apps
        var uwpApps = await GetUwpAppsAsync(ct);
        apps.AddRange(uwpApps);

        // Icons are loaded asynchronously by the ViewModel for each app
        return apps.OrderBy(a => a.DisplayName).ToList();
    }

    private async Task<List<InstalledApp>> GetDesktopAppsAsync(CancellationToken ct)
    {
        var apps = new List<InstalledApp>();

        await Task.Run(() =>
        {
            // Scan common Start Menu locations for .lnk files
            var startMenuPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            };

            foreach (var basePath in startMenuPaths)
            {
                if (!Directory.Exists(basePath))
                    continue;

                var shortcuts = Directory.GetFiles(basePath, "*.lnk", SearchOption.AllDirectories);

                foreach (var shortcut in shortcuts)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        var target = ResolveShortcut(shortcut);
                        if (string.IsNullOrEmpty(target) || !File.Exists(target))
                            continue;

                        var ext = Path.GetExtension(target).ToLowerInvariant();
                        if (ext != ".exe" && ext != ".com")
                            continue;

                        // Skip if already added
                        if (apps.Any(a => string.Equals(a.ExecutablePath, target, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var displayName = Path.GetFileNameWithoutExtension(shortcut);
                        apps.Add(new InstalledApp
                        {
                            DisplayName = displayName,
                            ExecutablePath = target,
                            Publisher = GetFilePublisher(target),
                            Icon = null // Icon loaded asynchronously for performance
                        });
                    }
                    catch
                    {
                        // Skip invalid shortcuts
                    }
                }
            }
        }, ct);

        return apps;
    }

    private async Task<List<InstalledApp>> GetUwpAppsAsync(CancellationToken ct)
    {
        var apps = new List<InstalledApp>();

        try
        {
            await Task.Run(() =>
            {
                var packageManager = new PackageManager();
                var packages = packageManager.FindPackagesForUser("");

                foreach (var package in packages)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    try
                    {
                        // Skip framework packages
                        if (package.IsFramework || package.IsResourcePackage)
                            continue;

                        // Get display name
                        var displayName = package.DisplayName;
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = package.Id.Name;

                        apps.Add(new InstalledApp
                        {
                            DisplayName = displayName,
                            PackageFamilyName = package.Id.FamilyName,
                            Publisher = package.Id.Publisher,
                            Icon = null // UWP icons not loaded yet
                        });
                    }
                    catch
                    {
                        // Skip packages we can't access
                    }
                }
            }, ct);
        }
        catch
        {
            // PackageManager may fail in some environments
        }

        return apps;
    }

    private string? ResolveShortcut(string shortcutPath)
    {
        try
        {
            // Use WScript.Shell COM object to resolve shortcuts (simpler than IShellLink)
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return null;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string targetPath = shortcut.TargetPath;
            
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);

            return string.IsNullOrWhiteSpace(targetPath) ? null : targetPath;
        }
        catch
        {
            return null;
        }
    }

    private string? GetFilePublisher(string filePath)
    {
        try
        {
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
            return versionInfo.CompanyName;
        }
        catch
        {
            return null;
        }
    }
}
