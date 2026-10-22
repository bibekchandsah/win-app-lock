using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AppLock.Services;

/// <summary>
/// Service for picking a window by hovering over it (showing glowing rainbow border)
/// and left-clicking to confirm or pressing ESC to cancel.
/// Does not obscure the screen with a full-screen card; instructions are shown in the master card.
/// </summary>
public class WindowPicker
{
    private GlowBorderOverlay? _glowOverlay;
    private TaskCompletionSource<PickedWindowInfo?> _pickTaskSource = null!;
    private HWND _currentHighlightedWindow;
    private HWND _mainWindowHwnd;
    private nint _mouseHookHandle;
    private HOOKPROC? _mouseHookProc;
    private nint _keyboardHookHandle;
    private HOOKPROC? _keyboardHookProc;
    private bool _isActive;

    public bool IsActive => _isActive;

    public async Task<PickedWindowInfo?> PickWindowAsync(HWND mainWindowHwnd)
    {
        if (_isActive)
        {
            return null;
        }

        _isActive = true;
        _pickTaskSource = new TaskCompletionSource<PickedWindowInfo?>();
        _currentHighlightedWindow = HWND.Null;
        _mainWindowHwnd = mainWindowHwnd;

        try
        {
            // Initialize glow border overlay
            _glowOverlay = new GlowBorderOverlay();

            // Install low-level hooks
            InstallHooks();

            // Wait for user to click a window or cancel (via ESC or AppLock click)
            var result = await _pickTaskSource.Task;
            return result;
        }
        finally
        {
            Cleanup();
            _isActive = false;

            // Ensure main window is foreground
            if (_mainWindowHwnd != HWND.Null)
            {
                PInvoke.SetForegroundWindow(_mainWindowHwnd);
            }
        }
    }

    private void InstallHooks()
    {
        _mouseHookProc = MouseHookCallback;
        _mouseHookHandle = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_MOUSE_LL,
            _mouseHookProc,
            HINSTANCE.Null,
            0
        );

        _keyboardHookProc = KeyboardHookCallback;
        _keyboardHookHandle = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
            _keyboardHookProc,
            HINSTANCE.Null,
            0
        );
    }

    private LRESULT KeyboardHookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            const uint WM_KEYDOWN = 0x0100;
            const uint WM_SYSKEYDOWN = 0x0104;
            uint msg = (uint)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                const int VK_ESCAPE = 0x1B;
                if (kbd.vkCode == VK_ESCAPE)
                {
                    CancelPick();
                    return new LRESULT(1); // Swallow ESC
                }
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private LRESULT MouseHookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            const uint WM_MOUSEMOVE = 0x0200;
            const uint WM_LBUTTONDOWN = 0x0201;

            if ((uint)wParam == WM_MOUSEMOVE)
            {
                HandleMouseMove(new System.Drawing.Point(hookStruct.X, hookStruct.Y));
            }
            else if ((uint)wParam == WM_LBUTTONDOWN)
            {
                var pt = new System.Drawing.Point(hookStruct.X, hookStruct.Y);

                // If user clicks on AppLock main window (e.g. Cancel button), cancel pick and allow click
                var hwnd = PInvoke.WindowFromPoint(pt);
                var rootHwnd = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
                if (rootHwnd == _mainWindowHwnd)
                {
                    CancelPick();
                    return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
                }

                HandleMouseClick(pt);
                return new LRESULT(1); // Block the click from reaching the target window
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private void HandleMouseMove(System.Drawing.Point point)
    {
        if (_glowOverlay == null) return;

        var hwnd = PInvoke.WindowFromPoint(point);
        if (hwnd == HWND.Null)
        {
            _glowOverlay.Hide();
            _currentHighlightedWindow = HWND.Null;
            return;
        }

        var rootHwnd = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (rootHwnd == HWND.Null)
        {
            _glowOverlay.Hide();
            _currentHighlightedWindow = HWND.Null;
            return;
        }

        // Don't highlight AppLock's own main window
        if (rootHwnd == _mainWindowHwnd)
        {
            _glowOverlay.Hide();
            _currentHighlightedWindow = HWND.Null;
            return;
        }

        // Don't highlight the glow overlay window itself
        if (_glowOverlay != null && rootHwnd.Value == _glowOverlay.Handle)
        {
            return;
        }

        // Don't highlight Windows Shell / Desktop / Taskbar
        var shell = GetShellWindow();
        if (rootHwnd == shell)
        {
            _glowOverlay?.Hide();
            _currentHighlightedWindow = HWND.Null;
            return;
        }

        if (!PInvoke.IsWindowVisible(rootHwnd) || PInvoke.IsIconic(rootHwnd))
        {
            _glowOverlay?.Hide();
            _currentHighlightedWindow = HWND.Null;
            return;
        }

        if (rootHwnd != _currentHighlightedWindow)
        {
            _currentHighlightedWindow = rootHwnd;
            HighlightWindow(rootHwnd);
        }
    }

    [DllImport("user32.dll")]
    private static extern HWND GetShellWindow();

    private void HandleMouseClick(System.Drawing.Point point)
    {
        if (_currentHighlightedWindow == HWND.Null)
        {
            return;
        }

        try
        {
            unsafe
            {
                uint processId = 0;
                PInvoke.GetWindowThreadProcessId(_currentHighlightedWindow, &processId);

                var hProcess = PInvoke.OpenProcess(
                    Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    processId
                );

                if (hProcess == HANDLE.Null)
                {
                    CancelPick();
                    return;
                }

                try
                {
                    char* pathBuffer = stackalloc char[1024];
                    uint size = 1024;

                    var result = PInvoke.QueryFullProcessImageName(
                        new SafeHandle(hProcess.Value, true),
                        Windows.Win32.System.Threading.PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32,
                        new PWSTR(pathBuffer),
                        ref size
                    );

                    if (!result)
                    {
                        CancelPick();
                        return;
                    }

                    var processPath = new string(pathBuffer, 0, (int)size);

                    var titleLength = PInvoke.GetWindowTextLength(_currentHighlightedWindow);
                    char* titleBuffer = stackalloc char[titleLength + 1];
                    PInvoke.GetWindowText(_currentHighlightedWindow, new PWSTR(titleBuffer), titleLength + 1);
                    var windowTitle = new string(titleBuffer, 0, titleLength);

                    _glowOverlay?.Hide();
                    _pickTaskSource.TrySetResult(new PickedWindowInfo(
                        _currentHighlightedWindow,
                        processPath,
                        windowTitle
                    ));
                }
                finally
                {
                    PInvoke.CloseHandle(hProcess);
                }
            }
        }
        catch
        {
            CancelPick();
        }
    }

    private void HighlightWindow(HWND hwnd)
    {
        if (_glowOverlay == null) return;

        if (GlowBorderOverlay.GetVisibleWindowRect(hwnd.Value, out var rect))
        {
            _glowOverlay.ShowBorder(rect);
        }
    }

    public void CancelPick()
    {
        _glowOverlay?.Hide();
        _pickTaskSource?.TrySetResult(null);
    }

    private void Cleanup()
    {
        if (_mouseHookHandle != 0)
        {
            PInvoke.UnhookWindowsHookEx(new HHOOK(_mouseHookHandle));
            _mouseHookHandle = 0;
        }
        _mouseHookProc = null;

        if (_keyboardHookHandle != 0)
        {
            PInvoke.UnhookWindowsHookEx(new HHOOK(_keyboardHookHandle));
            _keyboardHookHandle = 0;
        }
        _keyboardHookProc = null;

        _currentHighlightedWindow = HWND.Null;

        if (_glowOverlay != null)
        {
            _glowOverlay.Dispose();
            _glowOverlay = null;
        }
    }
}

public record PickedWindowInfo(HWND Hwnd, string ProcessPath, string WindowTitle);

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public int X;
    public int Y;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

// SafeHandle wrapper for HANDLE
internal class SafeHandle : System.Runtime.InteropServices.SafeHandle
{
    public SafeHandle(nint handle, bool ownsHandle) : base(nint.Zero, ownsHandle)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        return true; // Handle is closed by CloseHandle elsewhere
    }
}
