using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AppLock.Services
{
    public class KeyboardService
    {
        [DllImport("user32.dll")]
        private static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool fBlockIt);

        private bool _isKeyboardBlocked = false;

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
            // Return opposite of blocked state (blocked = disabled)
            return !_isKeyboardBlocked;
        }

        public bool SetKeyboardState(bool enable)
        {
            if (!IsAdministrator())
            {
                Debug.WriteLine("[KeyboardService] Admin rights required");
                return false;
            }

            try
            {
                // enable = true means unblock (allow input)
                // enable = false means block (disable input)
                bool shouldBlock = !enable;
                
                bool success = BlockInput(shouldBlock);
                
                if (success)
                {
                    _isKeyboardBlocked = shouldBlock;
                    Debug.WriteLine($"[KeyboardService] Keyboard {(enable ? "enabled" : "disabled")} - instant effect");
                    LogService.LogAction("keyboard", $"Set to {(enable ? "enabled" : "disabled")} (instant)");
                    return true;
                }
                else
                {
                    Debug.WriteLine("[KeyboardService] BlockInput API call failed");
                    return false;
                }
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
    }
}
