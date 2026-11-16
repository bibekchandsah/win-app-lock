using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
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

    /// <summary>
    /// Title Windows gives a toast banner window. Localized builds use a translated string, which
    /// is why <see cref="IsAtNativeToastPosition"/> is checked as well.
    /// </summary>
    private const string ToastWindowTitle = "New notification";

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

            if (width  < MinToastWidth  || width  > MaxToastWidth ||
                height < MinToastHeight || height > MaxToastHeight)
            {
                // Shell popups that reuse the toast host window (taskbar jumplists, flyouts) are
                // expected here — record them so "why was my menu not moved" is answerable.
                if (width >= 100 && height >= 40)
                    LogIgnoredShellPopup(hwnd, width, height, className, processName);
                return false;
            }

            // The banner window is reused by the shell for other popups (taskbar jumplists,
            // flyouts), so class + process + size are not enough: a 318×157 jumplist matches all
            // three. Only a real banner is titled "New notification" (localized) and/or sits in the
            // native bottom-right toast spot.
            bool titleMatches = GetTitle(hwnd).Equals(ToastWindowTitle, StringComparison.OrdinalIgnoreCase);
            if (!titleMatches && !IsAtNativeToastPosition(hwnd, rect))
            {
                if (width >= 100 && height >= 40)
                    LogIgnoredShellPopup(hwnd, width, height, className, processName);
                return false;
            }

            LogCandidate(hwnd, width, height, className, processName, rect);
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

    /// <summary>
    /// Quick check: is this HWND owned by a toast host process with the right class?
    /// Does NOT check size or visibility — used for the retry path before final sizing.
    /// </summary>
    public static bool IsLikelyToastHost(HWND hwnd)
    {
        try
        {
            if (!PInvoke.IsWindow(hwnd)) return false;
            var className = GetClassName(hwnd);
            if (!className.Equals("Windows.UI.Core.CoreWindow", StringComparison.Ordinal)) return false;
            uint processId = 0;
            unsafe { PInvoke.GetWindowThreadProcessId(hwnd, &processId); }
            var processName = GetProcessName((int)processId);
            return IsToastHostProcess(processName);
        }
        catch { return false; }
    }

    /// <param name="skip">
    /// Optional filter called with each top-level HWND before it is validated. The service uses it
    /// to skip windows it already manages, so polling never re-inspects a live toast.
    /// </param>
    public static List<HWND> FindToastWindows(Func<nint, bool>? skip = null)
    {
        var toasts = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (skip != null && skip(hwnd.Value))
                return true;

            if (IsToastWindow(hwnd))
                toasts.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return toasts;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, [System.Runtime.InteropServices.Out] char[] text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    private static readonly System.Collections.Generic.Dictionary<nint, (string Text, long At)> _rejects = new();
    private static readonly System.Collections.Generic.Dictionary<nint, (string Text, long At)> _candidates = new();

    /// <summary>
    /// Logs a confirmed toast candidate, but only when its geometry changed or every few seconds:
    /// the service polls for untracked toasts, and repeating the same line twice a second is noise.
    /// </summary>
    private static void LogCandidate(
        HWND hwnd, int width, int height, string className, string processName, RECT rect)
    {
        var text = $"{width}×{height} {rect.left},{rect.top}";
        var now = Environment.TickCount64;
        lock (_candidates)
        {
            // Time-throttled per window: during an animation the rect changes every frame, so
            // keying on the text alone would log each frame.
            if (_candidates.TryGetValue(hwnd.Value, out var prev) && now - prev.At < 5000)
                return;
            if (_candidates.Count > 64)
                _candidates.Clear();
            _candidates[hwnd.Value] = (text, now);
        }

        LogService.LogAction("notifications",
            $"  ✓ Toast candidate: 0x{hwnd.Value:X} {width}×{height} class='{className}' proc='{processName}'" +
            $" rect={rect.left},{rect.top} title='{GetTitle(hwnd)}' owner='{GetOwnerClass(hwnd)}'" +
            $" dpi={PInvoke.GetDpiForWindow(hwnd)} style=0x{GetStyle(hwnd):X}");
    }

    private static void LogIgnoredShellPopup(HWND hwnd, int width, int height, string className, string processName)
    {
        var text = $"{width}×{height} '{GetTitle(hwnd)}'";
        var now = Environment.TickCount64;
        lock (_rejects)
        {
            if (_rejects.TryGetValue(hwnd.Value, out var prev) && prev.Text == text && now - prev.At < 3000)
                return;
            if (_rejects.Count > 256)
                _rejects.Clear();
            _rejects[hwnd.Value] = (text, now);
        }

        LogService.LogAction("notifications",
            $"  · ignored shell popup 0x{hwnd.Value:X} {width}×{height} class='{className}' proc='{processName}'" +
            $" rect={GetRect(hwnd)} title='{GetTitle(hwnd)}' owner='{GetOwnerClass(hwnd)}'");
    }

    /// <summary>
    /// Windows creates a toast banner flush with the bottom-right corner of the monitor work area
    /// (12 px in) and slides it in from below. Any other position means this is another shell
    /// popup that happens to share the toast host window.
    /// </summary>
    private static bool IsAtNativeToastPosition(HWND hwnd, RECT rect)
    {
        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>()
        };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
            return false;

        int rightGap = Math.Abs(rect.right - info.rcWork.right);
        int bottomOffset = rect.bottom - info.rcWork.bottom;

        return rightGap <= 48 && bottomOffset >= -64 && bottomOffset <= 500;
    }

    private static string GetRect(HWND hwnd)
    {
        RECT rect = default;
        unsafe { PInvoke.GetWindowRect(hwnd, &rect); }
        return $"{rect.left},{rect.top},{rect.right},{rect.bottom}";
    }

    private static string GetTitle(HWND hwnd)
    {
        var buffer = new char[256];
        int length = GetWindowTextW(hwnd.Value, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static string GetOwnerClass(HWND hwnd)
    {
        var owner = GetWindow(hwnd.Value, 4 /* GW_OWNER */);
        if (owner == 0) return "-";
        var name = new char[256];
        int length = GetClassNameW(owner, name, name.Length);
        return new string(name, 0, length);
    }

    private static long GetStyle(HWND hwnd) => GetWindowLongPtr(hwnd.Value, -16 /* GWL_STYLE */).ToInt64();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, [System.Runtime.InteropServices.Out] char[] name, int maxCount);

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
