using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Resolves application names and exe paths from process IDs.
/// Simplified: returns exe path for icon loading, caches names.
/// </summary>
public sealed class AppIconResolver
{
    private readonly Dictionary<string, string> _nameCache = new();

    /// <summary>
    /// Resolves app name and exe path for icon loading.
    /// </summary>
    public (string Name, string? ExePath) ResolveApp(int processId, string displayName, string iconPath)
    {
        try
        {
            // Try to get process path
            var process = Process.GetProcessById(processId);
            var exePath = GetProcessPath(process);

            if (string.IsNullOrEmpty(exePath))
            {
                return (displayName, null);
            }

            // Check cache for name
            if (_nameCache.TryGetValue(exePath, out var cachedName))
            {
                return (cachedName, exePath);
            }

            // Resolve name
            var name = ResolveName(exePath, displayName);

            // Cache name
            _nameCache[exePath] = name;

            return (name, exePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppIconResolver] ResolveApp error for PID {processId}: {ex.Message}");
            return (displayName.Length > 0 ? displayName : "Unknown App", null);
        }
    }

    private static string? GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // Access denied or process exited
            return null;
        }
    }

    private static string ResolveName(string exePath, string displayName)
    {
        // 1. Try display name if not a resource string
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@"))
        {
            return displayName;
        }

        // Special handling for packaged apps (e.g. WhatsApp, Store apps)
        if (exePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    var manifestPath = Path.Combine(dir, "AppxManifest.xml");
                    if (File.Exists(manifestPath))
                    {
                        var doc = System.Xml.Linq.XDocument.Load(manifestPath);
                        var desc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                        var uwpName = desc?.Attribute("DisplayName")?.Value;
                        if (!string.IsNullOrWhiteSpace(uwpName) && !uwpName.StartsWith("ms-resource:"))
                        {
                            return uwpName;
                        }
                    }
                }
            }
            catch { }
        }

        // 2. Try FileVersionInfo
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            
            if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription) && !versionInfo.FileDescription.Equals("WhatsApp.Root", StringComparison.OrdinalIgnoreCase))
                return versionInfo.FileDescription;

            if (!string.IsNullOrWhiteSpace(versionInfo.ProductName) && !versionInfo.ProductName.Equals("WhatsApp.Root", StringComparison.OrdinalIgnoreCase))
                return versionInfo.ProductName;
        }
        catch
        {
            // Ignore
        }

        // 3. Fallback to friendly name if known
        var fileBase = Path.GetFileNameWithoutExtension(exePath);
        if (fileBase.Equals("WhatsApp.Root", StringComparison.OrdinalIgnoreCase))
            return "WhatsApp";

        return fileBase;
    }

    public void ClearCache()
    {
        _nameCache.Clear();
    }
}
