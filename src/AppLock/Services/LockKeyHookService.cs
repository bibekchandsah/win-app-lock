using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AppLock.Services
{
    public class LockKeyHookService : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_CAPITAL = 0x14;
        private const int VK_NUMLOCK = 0x90;
        private const int VK_SCROLL = 0x91;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookID = IntPtr.Zero;
        private bool _disposed;

        public event EventHandler<int>? LockKeyPressed;

        public LockKeyHookService()
        {
            _proc = HookCallback;
        }

        public void Start()
        {
            if (_hookID != IntPtr.Zero) return;

            _hookID = SetHook(_proc);
            Debug.WriteLine("[LockKeyHook] Keyboard hook installed");
        }

        public void Stop()
        {
            if (_hookID == IntPtr.Zero) return;

            UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
            Debug.WriteLine("[LockKeyHook] Keyboard hook removed");
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                if (curModule != null)
                {
                    return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            return IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP;

                if (isKeyDown || isKeyUp)
                {
                    int vkCode = Marshal.ReadInt32(lParam);

                    // Check if it's a lock key - trigger on key UP for more reliable state detection
                    if ((vkCode == VK_CAPITAL || vkCode == VK_NUMLOCK || vkCode == VK_SCROLL) && isKeyUp)
                    {
                        Debug.WriteLine($"[LockKeyHook] Lock key detected (UP): VK={vkCode}");
                        
                        // Small delay to ensure key state has changed
                        Task.Delay(10).ContinueWith(_ =>
                        {
                            LockKeyPressed?.Invoke(this, vkCode);
                        });
                    }
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
        }
    }
}
