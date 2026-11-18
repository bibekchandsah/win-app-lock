using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AppLock.Services
{
    /// <summary>
    /// Turns keyboard input off and on for the whole desktop.
    ///
    /// A WH_KEYBOARD_LL hook swallows keystrokes while disabled. The hook belongs to this process, so it
    /// needs no elevation, leaves the mouse working (the switch can always be turned back on), and dies
    /// with the process — a crash can never leave the keyboard dead. BlockInput was none of those things:
    /// it required administrator rights (the "BlockInput API call failed" error), blocked the mouse as
    /// well, and only lasted while the calling thread lived.
    /// </summary>
    public class KeyboardService
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // Held in a field so the delegate is never collected while Windows is still calling it.
        private readonly LowLevelKeyboardProc _hookProc;
        private IntPtr _hookId = IntPtr.Zero;

        // Written from the UI thread, read from the hook callback.
        private volatile bool _isKeyboardBlocked;

        public KeyboardService()
        {
            _hookProc = HookCallback;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public bool IsKeyboardEnabled()
        {
            // Blocked = disabled.
            return !_isKeyboardBlocked;
        }

        public bool SetKeyboardState(bool enable)
        {
            try
            {
                // The hook has to exist before the flag can matter; installing it also proves this
                // process can intercept input, so a failure here is reported as one.
                if (!enable && !EnsureHookInstalled())
                {
                    Debug.WriteLine("[KeyboardService] Keyboard hook could not be installed");
                    LogService.LogAction("keyboard", "Error: keyboard hook could not be installed");
                    return false;
                }

                bool shouldBlock = !enable;
                if (_isKeyboardBlocked == shouldBlock)
                {
                    return true;
                }

                _isKeyboardBlocked = shouldBlock;

                Debug.WriteLine($"[KeyboardService] Keyboard {(enable ? "enabled" : "disabled")}");
                LogService.LogAction("keyboard", $"Set to {(enable ? "enabled" : "disabled")}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyboardService] Error setting state: {ex.Message}");
                LogService.LogAction("keyboard", $"Error: {ex.Message}");
                return false;
            }
        }

        public bool ToggleKeyboardState()
        {
            bool newState = !IsKeyboardEnabled();
            return SetKeyboardState(newState);
        }

        /// <summary>
        /// Installs the keyboard hook once. It must be installed on a thread with a message loop
        /// (the UI thread), which is where the page and card switches call from.
        /// </summary>
        private bool EnsureHookInstalled()
        {
            if (_hookId != IntPtr.Zero) return true;

            try
            {
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                if (module == null) return false;

                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module.ModuleName), 0);
                if (_hookId == IntPtr.Zero)
                {
                    Debug.WriteLine($"[KeyboardService] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                Debug.WriteLine("[KeyboardService] Keyboard hook installed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyboardService] Hook install error: {ex.Message}");
                return false;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isKeyboardBlocked)
            {
                int message = wParam.ToInt32();
                if (message == WM_KEYDOWN || message == WM_KEYUP ||
                    message == WM_SYSKEYDOWN || message == WM_SYSKEYUP)
                {
                    // Swallow the key: no application sees it. Windows' own secure sequences
                    // (Ctrl+Alt+Del) are handled below any hook, so they still work.
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
    }
}
