using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Services.Notifications;

/// <summary>
/// Finds and validates Windows toast notification banner windows.
///
/// Real toasts live in one of these processes:
///   • ShellExperienceHost.exe  – the classic toast host (Windows 10 / early 11)
///   • svchost.exe              – used on some Windows 11 builds
///   • (rarely) explorer.exe    – legacy path
///
/// The shell window that the hook sees is always class
///   Windows.UI.Core.CoreWindow
/// and is owned by one of the above host processes.
///
/// Known false-positive windows we must EXCLUDE:
///   • TextInputHost.exe / TextInputHost CoreWindow  – touch keyboard / IME
///   • ApplicationFrameWindow                         – UWP frame, NOT a toast
///   • Windows.UI.Core.CoreWindow owned by any process
///     other than ShellExperienceHost / svchost       – unrelated shell UIs
/// </summary>
public class ToastWindowFinder
{
    // Toast banners on a 100%-DPI primary monitor are ~396 px wide.
    // Allow a wide range to handle DPI scaling (150% → ~594) and variability.
    private const int MinToastWidth  = 300;
    private const int MaxToastWidth  = 700;
    private const int MinToastHeight = 60;
    private const int MaxToastHeight = 300;

    private const int DWMWA_CLOAKED = 14;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// Returns true if <paramref name="hwnd"/> looks like a Windows toast banner.
    /// This is intentionally strict to avoid repositioning unrelated shell windows.
    /// </summary>
    public static bool IsToastWindow(HWND hwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd))
                return false;

            // Reject cloaked windows (cloaked by DWM, not visible to user)
            int cloaked = 0;
            int hr = DwmGetWindowAttribute(hwnd.Value, DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (hr == 0 && cloaked != 0)
                return false;

            if (!PInvoke.IsWindowVisible(hwnd))
                return false;

            var className = GetClassName(hwnd);

            // Must be the CoreWindow class used by the notification host.
            // Reject ApplicationFrameWindow – that is a UWP app chrome, not a toast.
            if (!className.Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal))
                return false;

            // Must be owned by the toast host process
            uint processId = 0;
            unsafe { PInvoke.GetWindowThreadProcessId(hwnd, &processId); }
            var processName = GetProcessName((int)processId);

            if (!IsToastHostProcess(processName))
                return false;

            // Size guard – eliminates the TextInputHost CoreWindow (usually ~314×73 or
            // very small) and any full-screen shell overlays.
            RECT rect = default;
            unsafe { PInvoke.GetWindowRect(hwnd, &rect); }
            int width  = rect.right  - rect.left;
            int height = rect.bottom - rect.top;

            if (width  < MinToastWidth  || width  > MaxToastWidth)  return false;
            if (height < MinToastHeight || height > MaxToastHeight)  return false;

            LogService.LogAction("notifications",
                $"  ✓ Toast candidate: 0x{hwnd.Value:X} {width}×{height} class='{className}' proc='{processName}'");
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"  IsToastWindow 0x{hwnd.Value:X} ex: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The processes that host Windows toast notification banners.
    /// Extend this list if a future Windows update moves toasts to a new host.
    /// </summary>
    private static bool IsToastHostProcess(string processName) =>
        processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("svchost",             StringComparison.OrdinalIgnoreCase)  ||
        processName.Equals("explorer",            StringComparison.OrdinalIgnoreCase);

    public static List<HWND> FindToastWindows()
    {
        var toasts = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (IsToastWindow(hwnd))
                toasts.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return toasts;
    }

    private static unsafe string GetClassName(HWND hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = PInvoke.GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, length);
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch { return string.Empty; }
    }
}
