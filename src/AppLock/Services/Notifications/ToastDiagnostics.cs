using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Services.Notifications;

public record ToastCandidate(
    nint Hwnd,
    string ClassName,
    string WindowTitle,
    string ProcessName,
    int ProcessId,
    RECT WindowRect,
    int Width,
    int Height,
    bool IsVisible,
    uint Styles,
    uint ExStyles
);

public class ToastDiagnostics
{
    public static List<ToastCandidate> FindCandidates()
    {
        var candidates = new List<ToastCandidate>();
        int totalWindows = 0;
        int visibleWindows = 0;

        PInvoke.EnumWindows((hwnd, lParam) =>
        {
            try
            {
                totalWindows++;
                
                if (!PInvoke.IsWindowVisible((HWND)hwnd))
                    return true;

                visibleWindows++;

                var className = GetClassName(hwnd);
                var title = GetWindowText(hwnd);
                
                // Get process info
                uint processId = 0;
                unsafe
                {
                    PInvoke.GetWindowThreadProcessId((HWND)hwnd, &processId);
                }
                var processName = GetProcessName((int)processId);

                // Look for ANY window that might be a toast - broaden the search
                bool isCandidate = 
                    // Original heuristics
                    className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("Windows.UI", StringComparison.OrdinalIgnoreCase) ||
                    // Also look for notification-related
                    className.Contains("Notification", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
                    // Shell processes
                    processName.Contains("Shell", StringComparison.OrdinalIgnoreCase) ||
                    // Any ApplicationFrameWindow (UWP host)
                    className.Contains("ApplicationFrame", StringComparison.OrdinalIgnoreCase);

                if (!isCandidate)
                    return true;

                // Get window rect
                RECT rect = default;
                unsafe
                {
                    PInvoke.GetWindowRect((HWND)hwnd, &rect);
                }
                int width = rect.right - rect.left;
                int height = rect.bottom - rect.top;

                // Get styles
                var styles = PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
                var exStyles = PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

                candidates.Add(new ToastCandidate(
                    hwnd,
                    className,
                    title,
                    processName,
                    (int)processId,
                    rect,
                    width,
                    height,
                    true,
                    (uint)styles,
                    (uint)exStyles
                ));
            }
            catch (Exception ex)
            {
                LogService.LogAction("notifications", $"Error enumerating window: {ex.Message}");
            }

            return true;
        }, IntPtr.Zero);

        LogService.LogAction("notifications", $"EnumWindows completed: {totalWindows} total, {visibleWindows} visible, {candidates.Count} candidates");
        return candidates;
    }

    private static unsafe string GetClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = PInvoke.GetClassName((HWND)hwnd, buffer, 256);
        return new string(buffer, 0, length);
    }

    private static unsafe string GetWindowText(nint hwnd)
    {
        var length = PInvoke.GetWindowTextLength((HWND)hwnd);
        if (length == 0) return string.Empty;

        char* buffer = stackalloc char[length + 1];
        PInvoke.GetWindowText((HWND)hwnd, buffer, length + 1);
        return new string(buffer);
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
