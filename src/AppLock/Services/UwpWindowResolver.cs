using System;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Services;

/// <summary>
/// Resolves UWP/Store app windows that run inside ApplicationFrameHost.exe.
/// Finds the actual app process and title by examining child windows.
/// </summary>
public sealed class UwpWindowResolver
{
    private const string ApplicationFrameHost = "ApplicationFrameHost.exe";
    
    /// <summary>
    /// Check if a process path is ApplicationFrameHost.
    /// </summary>
    public bool IsApplicationFrameHost(string processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        return processPath.EndsWith(ApplicationFrameHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve a UWP window to get the real app information.
    /// Returns the actual app title and process path if found.
    /// </summary>
    public (string actualTitle, string actualProcessPath) ResolveUwpWindow(HWND hwnd)
    {
        if (hwnd.IsNull)
            return (string.Empty, string.Empty);

        try
        {
            // Find the child CoreWindow which contains the actual UWP app
            var coreWindowHwnd = FindCoreWindow(hwnd);
            
            if (coreWindowHwnd.IsNull)
            {
                // Fallback: use the frame's title
                return (GetWindowTitle(hwnd), string.Empty);
            }

            // Get the actual process from the CoreWindow
            uint processId = 0;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(coreWindowHwnd, &processId);
            }

            if (processId == 0)
                return (GetWindowTitle(hwnd), string.Empty);

            // Get process path
            var processPath = GetProcessPath(processId);
            
            // Get actual window title (usually from the frame is good enough)
            var title = GetWindowTitle(hwnd);
            
            return (title, processPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error resolving UWP window: {ex.Message}");
            return (GetWindowTitle(hwnd), string.Empty);
        }
    }

    /// <summary>
    /// Find the CoreWindow child of an ApplicationFrameHost window.
    /// </summary>
    private HWND FindCoreWindow(HWND frameWindow)
    {
        HWND coreWindow = default;
        
        unsafe
        {
            PInvoke.EnumChildWindows(frameWindow, (hwnd, lParam) =>
            {
                var className = GetClassName(hwnd);
                
                // Look for Windows.UI.Core.CoreWindow or ApplicationFrameInputSinkWindow
                if (className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                    className.Contains("InputSink", StringComparison.OrdinalIgnoreCase))
                {
                    coreWindow = hwnd;
                    return false; // Stop enumeration
                }
                
                return true; // Continue
            }, default);
        }

        return coreWindow;
    }

    /// <summary>
    /// Get the class name of a window.
    /// </summary>
    private string GetClassName(HWND hwnd)
    {
        if (hwnd.IsNull) return string.Empty;

        unsafe
        {
            var buffer = stackalloc char[256];
            var length = PInvoke.GetClassName(hwnd, buffer, 256);
            
            if (length > 0)
            {
                return new string(buffer, 0, length);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Get window title.
    /// </summary>
    private string GetWindowTitle(HWND hwnd)
    {
        if (hwnd.IsNull) return string.Empty;

        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;

        unsafe
        {
            var buffer = stackalloc char[length + 1];
            var actualLength = PInvoke.GetWindowText(hwnd, buffer, length + 1);
            
            if (actualLength > 0)
            {
                return new string(buffer, 0, actualLength);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Get process path from process ID.
    /// </summary>
    private string GetProcessPath(uint processId)
    {
        try
        {
            unsafe
            {
                var handle = PInvoke.OpenProcess(
                    Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    processId);

                if (handle.Value == IntPtr.Zero) return string.Empty;

                try
                {
                    var buffer = stackalloc char[1024];
                    uint size = 1024;
                    
                    if (PInvoke.QueryFullProcessImageName(handle, 0, buffer, &size))
                    {
                        return new string(buffer, 0, (int)size);
                    }
                }
                finally
                {
                    PInvoke.CloseHandle(handle);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting process path: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// Get the Application User Model ID (AUMID) for a UWP app window.
    /// Can be used for more precise identification.
    /// </summary>
    public string GetAppUserModelId(HWND hwnd)
    {
        if (hwnd.IsNull) return string.Empty;

        try
        {
            // Get process ID
            uint processId = 0;
            unsafe
            {
                PInvoke.GetWindowThreadProcessId(hwnd, &processId);
            }

            if (processId == 0) return string.Empty;

            // Open process handle
            unsafe
            {
                var handle = PInvoke.OpenProcess(
                    Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    processId);

                if (handle.Value == IntPtr.Zero) return string.Empty;

                try
                {
                    // Get AUMID length
                    uint length = 0;
                    Windows.Win32.PInvoke.GetApplicationUserModelId(handle, &length, null);
                    
                    if (length == 0) return string.Empty;

                    // Get AUMID
                    var buffer = stackalloc char[(int)length];
                    var result = Windows.Win32.PInvoke.GetApplicationUserModelId(handle, &length, buffer);
                    if (result == 0)
                    {
                        return new string(buffer, 0, (int)length - 1); // -1 to exclude null terminator
                    }
                }
                finally
                {
                    PInvoke.CloseHandle(handle);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting AUMID: {ex.Message}");
        }

        return string.Empty;
    }
}
