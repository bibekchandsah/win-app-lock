using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AppLock.Services;

/// <summary>
/// Replaces the locked window's taskbar thumbnail and Alt-Tab preview with a "Locked" image.
///
/// Uses AppLockHook.dll injected into the target process. Because DWM enforces that
/// DwmSetWindowAttribute(DWMWA_FORCE_ICONIC_REPRESENTATION) and DwmSetIconicThumbnail
/// can ONLY be called by the process that owns the window (cross-process calls fail with
/// E_ACCESSDENIED), the hook DLL executes inside the target process context:
///   - Installs subclass on target window
///   - Sets DWMWA_FORCE_ICONIC_REPRESENTATION and DWMWA_HAS_ICONIC_BITMAP in-process
///   - Handles WM_DWMSENDICONICTHUMBNAIL with a 32-bit ARGB DIBSection (alpha = 0xFF)
///   - Handles WM_DWMSENDICONICLIVEPREVIEWBITMAP for Alt-Tab / Aero Peek
///   - Restores original thumbnail cleanly on unlock
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    // Native hook DLL exports
    [DllImport("AppLockHook.dll", EntryPoint = "InstallLockedThumbnail",
        CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool NativeInstall(nint hwnd);

    [DllImport("AppLockHook.dll", EntryPoint = "UninstallLockedThumbnail",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeUninstall(nint hwnd);

    // Active locked targets
    private readonly HashSet<nint> _lockedTargets = new();
    private static bool? _dllAvailable;

    public void SetLockedThumbnail(HWND overlayHwnd, HWND targetHwnd)
    {
        nint tg = targetHwnd.Value;
        if (tg == 0) return;

        lock (_lockedTargets)
        {
            if (_lockedTargets.Contains(tg)) return;

            try
            {
                if (IsDllAvailable())
                {
                    bool ok = NativeInstall(tg);
                    if (ok)
                    {
                        _lockedTargets.Add(tg);
                        System.Diagnostics.Debug.WriteLine($"[ThumbnailService] Locked thumbnail installed for 0x{tg:X}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[ThumbnailService] NativeInstall returned false for 0x{tg:X}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThumbnailService] SetLockedThumbnail failed: {ex.Message}");
            }
        }
    }

    public void ClearLockedThumbnailByTarget(HWND targetHwnd)
    {
        nint tg = targetHwnd.Value;
        if (tg == 0) return;

        lock (_lockedTargets)
        {
            if (!_lockedTargets.Remove(tg)) return;

            try
            {
                if (IsDllAvailable())
                {
                    NativeUninstall(tg);
                    System.Diagnostics.Debug.WriteLine($"[ThumbnailService] Locked thumbnail cleared for 0x{tg:X}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThumbnailService] ClearLockedThumbnail failed: {ex.Message}");
            }
        }
    }

    // Overload for callers passing overlay HWND (OverlayManager now calls ClearLockedThumbnailByTarget directly)
    public void ClearLockedThumbnail(HWND overlayHwnd)
    {
    }

    private static bool IsDllAvailable()
    {
        if (_dllAvailable.HasValue) return _dllAvailable.Value;
        var path = Path.Combine(AppContext.BaseDirectory, "AppLockHook.dll");
        if (File.Exists(path))
        {
            _dllAvailable = true;
            return true;
        }

        // Search candidate paths if not directly in output dir
        string[] candidates = [
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\x64\Release\AppLockHook.dll"),
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\x64\Debug\AppLockHook.dll"),
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\AppLockHook\x64\Release\AppLockHook.dll"),
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\AppLockHook\x64\Debug\AppLockHook.dll"),
            @"C:\Users\Public\AppLock\AppLockHook.dll"
        ];

        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full))
                {
                    File.Copy(full, path, overwrite: true);
                    System.Diagnostics.Debug.WriteLine($"[ThumbnailService] Copied AppLockHook.dll from {full} to {path}");
                    _dllAvailable = true;
                    return true;
                }
            }
            catch { }
        }

        System.Diagnostics.Debug.WriteLine($"[ThumbnailService] AppLockHook.dll NOT found at {path}");
        _dllAvailable = false;
        return false;
    }

    public void Dispose()
    {
        lock (_lockedTargets)
        {
            foreach (var tg in _lockedTargets)
            {
                try
                {
                    if (IsDllAvailable())
                    {
                        NativeUninstall(tg);
                    }
                }
                catch { }
            }
            _lockedTargets.Clear();
        }
    }
}
