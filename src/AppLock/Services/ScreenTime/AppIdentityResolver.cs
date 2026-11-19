using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppLock.Services.ScreenTime;

/// <summary>Stable identity of a foreground app (spec 5.3).</summary>
public sealed record AppIdentity(
    string Key,
    string DisplayName,
    string? ExePath,
    string? Aumid,
    string ProcessName);

/// <summary>
/// Resolves the foreground window to the same key no matter how many windows or processes the app
/// uses: <c>exe:path</c> for Win32 apps and <c>aumid:...</c> for packaged/UWP apps. Never throws:
/// a protected process falls back to <c>proc:name</c> so its time is still counted.
/// </summary>
public sealed class AppIdentityResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly object _lock = new();
    private readonly Dictionary<string, AppIdentity> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]?> _icons = new(StringComparer.OrdinalIgnoreCase);

    private nint _cachedHwnd;
    private AppIdentity? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TrayNotifyWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "TopLevelWindowForOverflowXamlIsland"
    };

    public AppIdentity? Resolve(nint hwnd)
    {
        if (hwnd == 0) return null;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (hwnd == _cachedHwnd && _cached != null && now - _cachedAt < TimeSpan.FromSeconds(5))
                return _cached;

            try
            {
                _cached = ResolveCore(hwnd);
                _cachedHwnd = hwnd;
                _cachedAt = now;

                if (_cached != null)
                    _byKey[_cached.Key] = _cached;

                return _cached;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppIdentity] resolve failed: {ex.Message}");
                return null;
            }
        }
    }

    public AppIdentity? GetKnown(string appKey)
    {
        lock (_lock)
        {
            return _byKey.TryGetValue(appKey, out var identity) ? identity : null;
        }
    }

    public static string GetWindowClass(nint hwnd)
    {
        if (hwnd == 0) return string.Empty;
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>Default exclusions from spec 5.4, plus AppLock itself and the bare shell.</summary>
    public static bool IsDefaultExcluded(string processName, string? exePath, string? windowClass)
    {
        string name = (processName ?? string.Empty).Trim();
        if (name.Length == 0) return true;

        if (!string.IsNullOrEmpty(exePath) &&
            !string.IsNullOrEmpty(Environment.ProcessPath) &&
            exePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("AppLock", StringComparison.OrdinalIgnoreCase)) return true;

        if (name.Equals("LockApp", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("LogonUI", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Explorer windows are real apps; only the desktop, taskbar and Start flyouts are shell.
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(windowClass) &&
            ShellWindowClasses.Contains(windowClass))
        {
            return true;
        }

        return false;
    }

    /// <summary>Extracts a small PNG icon once per executable; null when Windows has no icon.</summary>
    public byte[]? GetIconPng(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;

        return _icons.GetOrAdd(exePath, path =>
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;

                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return stream.ToArray();
            }
            catch
            {
                return null;
            }
        });
    }

    private static AppIdentity? ResolveCore(nint hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0)
            return null;

        var process = GetProcessInfo(pid);
        string processName = process?.Name ?? "Unknown";
        string? exePath = process?.Path;

        // UWP: ApplicationFrameHost owns the visible window; the real app is a child window with
        // another process id. Resolve that process and its AUMID.
        string? aumid = null;
        if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            uint childPid = FindChildProcessId(hwnd, pid);
            if (childPid != 0)
            {
                var child = GetProcessInfo(childPid);
                if (child != null)
                {
                    processName = child.Value.Name;
                    exePath = child.Value.Path;
                    aumid = TryGetAumid(childPid);
                }
            }
        }
        else if (!string.IsNullOrEmpty(exePath) &&
                 exePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
        {
            aumid = TryGetAumid(pid);
        }

        string key;
        if (!string.IsNullOrEmpty(aumid))
            key = "aumid:" + aumid.ToLowerInvariant();
        else if (!string.IsNullOrEmpty(exePath))
            key = "exe:" + exePath.ToLowerInvariant();
        else
            key = "proc:" + processName.ToLowerInvariant();

        string display = DisplayNameFor(exePath, processName);
        return new AppIdentity(key, display, exePath, aumid, processName);
    }

    private static (string Name, string? Path)? GetProcessInfo(uint pid)
    {
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                try
                {
                    var fallback = Process.GetProcessById((int)pid);
                    return (fallback.ProcessName, null);
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                var buffer = new StringBuilder(1024);
                uint size = 1024;
                string? path = QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                    ? buffer.ToString()
                    : null;

                string name = !string.IsNullOrEmpty(path)
                    ? Path.GetFileNameWithoutExtension(path)
                    : TryGetProcessName(pid);

                return (name, path);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string? TryGetAumid(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            uint length = 512;
            var buffer = new StringBuilder(512);
            return GetApplicationUserModelId(handle, ref length, buffer) == 0
                ? buffer.ToString()
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static uint FindChildProcessId(nint parent, uint hostPid)
    {
        uint found = 0;

        EnumChildWindows(parent, (child, _) =>
        {
            if (GetWindowThreadProcessId(child, out uint childPid) == 0 || childPid == 0 || childPid == hostPid)
                return true;

            found = childPid;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static string DisplayNameFor(string? exePath, string processName)
    {
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
            }
            catch
            {
                // Fall through to the process name.
            }
        }

        return string.IsNullOrWhiteSpace(processName) ? "Unknown app" : processName;
    }

    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc callback, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(nint hProcess, ref uint appModelIdLength, StringBuilder appModelId);
}
