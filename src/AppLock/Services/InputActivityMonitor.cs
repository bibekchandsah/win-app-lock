using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AppLock.Services;

/// <summary>
/// Keeps AppLock's own "last real input" clock using low-level keyboard/mouse hooks.
///
/// Why: <c>GetLastInputInfo</c> cannot be trusted on every machine — on some systems something
/// resets it every second (a HID device, a driver, a shell bug), so the system idle time never
/// grows and the idle auto-lock never fires. The hooks only see genuine (non-injected) keyboard and
/// mouse activity, which is exactly what "without keyboard/mouse activity" means for auto-lock.
///
/// Must be started on a thread that pumps messages (the UI thread).
/// </summary>
public static class InputActivityMonitor
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint LLKHF_INJECTED = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint LowLevelProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    // Delegates must stay referenced or the GC frees the callback the OS is calling.
    private static LowLevelProc? _mouseProc;
    private static LowLevelProc? _keyboardProc;
    private static nint _mouseHook;
    private static nint _keyboardHook;

    private static long _lastInputTicks = Environment.TickCount64;

    /// <summary>True when at least one hook is installed.</summary>
    public static bool IsRunning => _mouseHook != nint.Zero || _keyboardHook != nint.Zero;

    /// <summary>Minutes since the last real keyboard/mouse input.</summary>
    public static double IdleMinutes
    {
        get
        {
            long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastInputTicks);
            return elapsed <= 0 ? 0 : elapsed / 60000.0;
        }
    }

    /// <summary>Installs the hooks. Safe to call more than once. Call on the UI thread.</summary>
    public static void Start()
    {
        try
        {
            if (_mouseHook != nint.Zero || _keyboardHook != nint.Zero) return;

            nint module = GetModuleHandleW(null);

            if (_mouseProc == null) _mouseProc = MouseCallback;
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);

            if (_keyboardProc == null) _keyboardProc = KeyboardCallback;
            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, module, 0);

            // Start counting from now, not from process start.
            Interlocked.Exchange(ref _lastInputTicks, Environment.TickCount64);

            IdleService.Log($"Input hooks: mouse={_mouseHook != nint.Zero}, keyboard={_keyboardHook != nint.Zero}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InputActivity] Start error: {ex.Message}");
        }
    }

    private static nint MouseCallback(int nCode, nint wParam, nint lParam)
    {
        // Runs for every mouse message (high-polling mice can send ~1000/s), so keep it minimal:
        // read the flags directly (MSLLHOOKSTRUCT.flags is at offset 12) and only touch the shared
        // clock when it actually moved.
        if (nCode >= 0)
        {
            try
            {
                uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                if ((flags & LLMHF_INJECTED) == 0) MarkInput();
            }
            catch { }
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private static nint KeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        // KBDLLHOOKSTRUCT.flags is at offset 8.
        if (nCode >= 0)
        {
            try
            {
                uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                if ((flags & LLKHF_INJECTED) == 0) MarkInput();
            }
            catch { }
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private static void MarkInput()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastInputTicks) < 200) return;
        Interlocked.Exchange(ref _lastInputTicks, now);
    }
}
