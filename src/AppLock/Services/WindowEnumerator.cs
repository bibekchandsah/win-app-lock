using System.Runtime.InteropServices;
using AppLock.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Shell;

namespace AppLock.Services;

public class WindowEnumerator
{
    private const int DWMWA_CLOAKED = 14;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public List<RunningWindow> EnumerateWindows()
    {
        var windows = new List<RunningWindow>();
        var currentProcessId = PInvoke.GetCurrentProcessId();

        unsafe
        {
            PInvoke.EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    // Skip if not visible
                    if (!PInvoke.IsWindowVisible(hwnd))
                        return true;

                    // Get window title
                    int titleLength = PInvoke.GetWindowTextLength(hwnd);
                    if (titleLength == 0)
                        return true;

                    var titleBuffer = new char[titleLength + 1];
                    fixed (char* pTitle = titleBuffer)
                    {
                        PInvoke.GetWindowText(hwnd, new PWSTR(pTitle), titleBuffer.Length);
                    }
                    string title = new string(titleBuffer).TrimEnd('\0');

                    if (string.IsNullOrWhiteSpace(title))
                        return true;

                    // Check if cloaked (hidden by Windows, like UWP suspended apps)
                    int cloaked = 0;
                    var hr = PInvoke.DwmGetWindowAttribute(
                        hwnd,
                        (DWMWINDOWATTRIBUTE)DWMWA_CLOAKED,
                        &cloaked,
                        sizeof(int));

                    if (hr.Succeeded && cloaked != 0)
                        return true;

                    // Skip tool windows
                    int exStyle = (int)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                        return true;

                    // Get process info
                    uint processId = 0;
                    PInvoke.GetWindowThreadProcessId(hwnd, &processId);

                    // Skip our own process
                    if (processId == currentProcessId)
                        return true;

                    var hProcess = PInvoke.OpenProcess_SafeHandle(
                        Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
                        false,
                        processId);

                    if (hProcess.IsInvalid)
                        return true;

                    using (hProcess)
                    {
                        // Get process path
                        var pathBuffer = new char[260];
                        uint size = (uint)pathBuffer.Length;
                        
                        fixed (char* pPath = pathBuffer)
                        {
                            if (PInvoke.QueryFullProcessImageName(hProcess, 0, new PWSTR(pPath), ref size))
                            {
                                string processPath = new string(pathBuffer, 0, (int)size);
                                string processName = Path.GetFileNameWithoutExtension(processPath);

                                // Skip AppLock itself
                                if (processName.Equals("AppLock", StringComparison.OrdinalIgnoreCase))
                                    return true;

                                // Handle UWP apps (ApplicationFrameHost)
                                if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Try to get the actual UWP app name from the window title
                                    // For UWP apps, the title usually contains the app name
                                    processName = title.Split(new[] { " - " }, StringSplitOptions.None).FirstOrDefault() ?? processName;
                                }

                                var window = new RunningWindow
                                {
                                    Handle = (nint)hwnd.Value,
                                    WindowTitle = title,
                                    ProcessName = processName,
                                    ProcessPath = processPath,
                                    ProcessId = (int)processId,
                                    Icon = ExtractIcon(processPath)
                                };

                                windows.Add(window);
                            }
                        }
                    }
                }
                catch
                {
                    // Skip windows we can't access
                }

                return true;
            }, IntPtr.Zero);
        }

        return windows.OrderBy(w => w.ProcessName).ToList();
    }

    private BitmapImage? ExtractIcon(string path)
    {
        try
        {
            // Use SHGetFileInfo to get the icon
            SHFILEINFOW fileInfo = default;
            unsafe
            {
                fixed (char* pPath = path)
                {
                    var result = PInvoke.SHGetFileInfo(
                        pPath,
                        0,
                        &fileInfo,
                        (uint)sizeof(SHFILEINFOW),
                        SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_SMALLICON);

                    if (result != 0 && !fileInfo.hIcon.IsNull)
                    {
                        // TODO: Convert HICON to BitmapImage
                        // For now, destroy the icon and return null
                        // We'll implement proper icon conversion in the next iteration
                        PInvoke.DestroyIcon(fileInfo.hIcon);
                        return null;
                    }
                }
            }
        }
        catch
        {
            // Ignore icon extraction errors
        }

        return null;
    }
}
