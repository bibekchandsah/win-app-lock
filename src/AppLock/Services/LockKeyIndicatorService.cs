using AppLock.Views;
using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppLock.Services
{
    public class LockKeyIndicatorService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private const int VK_CAPITAL = 0x14; // Caps Lock
        private const int VK_NUMLOCK = 0x90; // Num Lock
        private const int VK_SCROLL = 0x91;  // Scroll Lock

        private readonly DispatcherQueue _dispatcherQueue;
        private readonly LockKeyHookService _hookService;
        private LockKeyOverlayWindow? _overlayWindow;
        private bool _isEnabled;
        private bool _disposed;

        // Events for key state changes
        public event EventHandler<LockKeyStateEventArgs>? CapsLockChanged;
        public event EventHandler<LockKeyStateEventArgs>? NumLockChanged;
        public event EventHandler<LockKeyStateEventArgs>? ScrollLockChanged;

        public LockKeyIndicatorService(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;
            _hookService = new LockKeyHookService();
            _hookService.LockKeyPressed += OnLockKeyPressed;
        }

        private void OnLockKeyPressed(object? sender, int virtualKeyCode)
        {
            Debug.WriteLine($"[LockKeyIndicator] Key pressed: {virtualKeyCode}, Enabled: {_isEnabled}, Window exists: {_overlayWindow != null}");
            
            if (!_isEnabled) return;
            
            CheckAndShowIndicator(virtualKeyCode);
        }

        public void Start()
        {
            if (_isEnabled) return;

            Debug.WriteLine("[LockKeyIndicator] Starting lock key monitoring");
            _isEnabled = true;

            // Create overlay window on UI thread
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _overlayWindow = new LockKeyOverlayWindow();
                    _overlayWindow.Activate(); // Make sure window is activated
                    Debug.WriteLine("[LockKeyIndicator] Overlay window created and activated");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LockKeyIndicator] Failed to create overlay window: {ex.Message}");
                }
            });

            // Start keyboard hook
            _hookService.Start();
            Debug.WriteLine("[LockKeyIndicator] Keyboard hook started");
        }

        public void Stop()
        {
            if (!_isEnabled) return;

            Debug.WriteLine("[LockKeyIndicator] Stopping lock key monitoring");
            _isEnabled = false;

            // Stop keyboard hook
            _hookService.Stop();

            // Close overlay window
            _dispatcherQueue.TryEnqueue(() =>
            {
                _overlayWindow?.Close();
                _overlayWindow = null;
            });
        }

        public static bool IsCapsLockOn()
        {
            return (GetKeyState(VK_CAPITAL) & 0x0001) != 0;
        }

        public static bool IsNumLockOn()
        {
            return (GetKeyState(VK_NUMLOCK) & 0x0001) != 0;
        }

        public static bool IsScrollLockOn()
        {
            return (GetKeyState(VK_SCROLL) & 0x0001) != 0;
        }

        public void CheckAndShowIndicator(int virtualKeyCode)
        {
            Debug.WriteLine($"[LockKeyIndicator] CheckAndShowIndicator called: VK={virtualKeyCode}, Enabled={_isEnabled}, Window={_overlayWindow != null}");
            
            if (!_isEnabled || _overlayWindow == null)
            {
                Debug.WriteLine($"[LockKeyIndicator] Skipping - Enabled: {_isEnabled}, Window exists: {_overlayWindow != null}");
                return;
            }

            // Use normal priority for more responsive detection
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    string keyName = "";
                    bool isOn = false;

                    if (virtualKeyCode == VK_CAPITAL)
                    {
                        isOn = IsCapsLockOn();
                        keyName = "Caps Lock";
                        CapsLockChanged?.Invoke(this, new LockKeyStateEventArgs(keyName, isOn));
                    }
                    else if (virtualKeyCode == VK_NUMLOCK)
                    {
                        isOn = IsNumLockOn();
                        keyName = "Num Lock";
                        NumLockChanged?.Invoke(this, new LockKeyStateEventArgs(keyName, isOn));
                    }
                    else if (virtualKeyCode == VK_SCROLL)
                    {
                        isOn = IsScrollLockOn();
                        keyName = "Scroll Lock";
                        ScrollLockChanged?.Invoke(this, new LockKeyStateEventArgs(keyName, isOn));
                    }

                    if (!string.IsNullOrEmpty(keyName))
                    {
                        Debug.WriteLine($"[LockKeyIndicator] Showing indicator: {keyName} = {(isOn ? "ON" : "OFF")}");
                        _overlayWindow?.ShowIndicator(keyName, isOn);
                    }
                    else
                    {
                        Debug.WriteLine($"[LockKeyIndicator] Unknown key code: {virtualKeyCode}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LockKeyIndicator] Error showing indicator: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _hookService.Dispose();
        }
    }

    public class LockKeyStateEventArgs : EventArgs
    {
        public string KeyName { get; }
        public bool IsOn { get; }

        public LockKeyStateEventArgs(string keyName, bool isOn)
        {
            KeyName = keyName;
            IsOn = isOn;
        }
    }
}
