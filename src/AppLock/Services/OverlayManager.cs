using AppLock.Models;
using AppLock.Views;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AppLock.Services;

public class OverlayManager
{
    private readonly LockManager _lockManager;
    private readonly AuthService _authService;
    private readonly WinEventHookService _winEventHook;
    private readonly WindowEnumerator _windowEnumerator;
    private readonly ThumbnailService _thumbnailService;
    private readonly DisplayHelper _displayHelper;
    private DispatcherQueue? _dispatcherQueue;
    
    private readonly Dictionary<nint, LockOverlayWindow> _activeOverlays = new();
    private readonly Dictionary<nint, DateTime> _unlockTimers = new();
    private readonly HashSet<nint> _indefinitelyUnlocked = new(); // Windows unlocked with "No limit"
    private readonly System.Threading.Timer _relockTimer;

    public OverlayManager(LockManager lockManager, AuthService authService)
    {
        _lockManager = lockManager;
        _authService = authService;
        _windowEnumerator = new WindowEnumerator();
        _winEventHook = new WinEventHookService();
        _thumbnailService = ServiceContainer.ThumbnailService;
        _displayHelper = ServiceContainer.DisplayHelper;
        
        // Get the dispatcher queue from the main UI thread
        // Note: This will be null if not called on UI thread, we'll get it lazily in Start()
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        
        // Check for relock every 10 seconds
        _relockTimer = new System.Threading.Timer(CheckRelockTimers, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        
        // Subscribe to lock manager events
        _lockManager.ProtectionStateChanged += OnProtectionStateChanged;
        _lockManager.RuleAdded += (s, e) => CheckAllWindows();
        
        // Subscribe to window events
        _winEventHook.WindowShown += OnWindowEvent;
        _winEventHook.WindowFocused += OnWindowEvent;
        _winEventHook.WindowMoved += OnWindowMoved;
        _winEventHook.WindowDestroyed += OnWindowDestroyed;
        _winEventHook.WindowRestored += OnWindowEvent;
    }

    public void Start()
    {
        // Ensure we have dispatcher queue (must be called on UI thread)
        if (_dispatcherQueue == null)
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            if (_dispatcherQueue == null)
            {
                throw new InvalidOperationException("OverlayManager.Start() must be called on the UI thread");
            }
        }
        
        if (_lockManager.IsProtectionEnabled)
        {
            _winEventHook.Start();
            CheckAllWindows();
        }
    }

    public void Stop()
    {
        _winEventHook.Stop();
        CloseAllOverlays();
    }

    private void OnProtectionStateChanged(object? sender, bool isEnabled)
    {
        if (isEnabled)
        {
            _winEventHook.Start();
            CheckAllWindows();
        }
        else
        {
            _winEventHook.Stop();
            CloseAllOverlays();
        }
    }

    private void OnWindowEvent(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_activeOverlays.TryGetValue(e.WindowHandle, out var overlay))
            {
                overlay.PositionOverTarget();
                overlay.Activate();
                return;
            }

            CheckWindow(e.WindowHandle);
        });
    }

    private void OnWindowMoved(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            // Update overlay position if it exists
            if (_activeOverlays.TryGetValue(e.WindowHandle, out var overlay))
            {
                overlay.PositionOverTarget();
            }
        });
    }

    private void OnWindowDestroyed(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            RemoveOverlay(e.WindowHandle);
        });
    }

    private void CheckAllWindows()
    {
        if (!_lockManager.IsProtectionEnabled)
            return;

        var windows = _windowEnumerator.EnumerateWindows();
        
        foreach (var window in windows)
        {
            CheckWindow(window.Handle);
        }
    }

    private void CheckWindow(nint windowHandle)
    {
        if (!_lockManager.IsProtectionEnabled)
            return;

        // Check if window is indefinitely unlocked
        if (_indefinitelyUnlocked.Contains(windowHandle))
            return;

        // Check if unlock timer is active
        if (_unlockTimers.ContainsKey(windowHandle))
            return;

        // Get window info
        unsafe
        {
            var hwnd = new HWND(windowHandle);
            if (!PInvoke.IsWindowVisible(hwnd))
                return;

            int titleLength = PInvoke.GetWindowTextLength(hwnd);
            if (titleLength == 0)
                return;

            var titleBuffer = new char[titleLength + 1];
            fixed (char* pTitle = titleBuffer)
            {
                PInvoke.GetWindowText(hwnd, new Windows.Win32.Foundation.PWSTR(pTitle), titleBuffer.Length);
            }
            string title = new string(titleBuffer).TrimEnd('\0');

            // Get process info
            uint processId = 0;
            PInvoke.GetWindowThreadProcessId(hwnd, &processId);

            var hProcess = PInvoke.OpenProcess_SafeHandle(
                Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId);

            if (hProcess.IsInvalid)
                return;

            using (hProcess)
            {
                var pathBuffer = new char[260];
                uint size = (uint)pathBuffer.Length;
                
                fixed (char* pPath = pathBuffer)
                {
                    if (PInvoke.QueryFullProcessImageName(hProcess, 0, new Windows.Win32.Foundation.PWSTR(pPath), ref size))
                    {
                        string processPath = new string(pathBuffer, 0, (int)size);
                        string processName = Path.GetFileNameWithoutExtension(processPath);

                        // Handle UWP apps
                        if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                        {
                            processName = title.Split(new[] { " - " }, StringSplitOptions.None).FirstOrDefault() ?? processName;
                        }

                        var window = new RunningWindow
                        {
                            Handle = windowHandle,
                            WindowTitle = title,
                            ProcessName = processName,
                            ProcessPath = processPath,
                            ProcessId = (int)processId
                        };

                        if (_lockManager.IsWindowLocked(window))
                        {
                            ShowOverlay(window);
                        }
                    }
                }
            }
        }
    }

    private void ShowOverlay(RunningWindow window)
    {
        // Check if overlay already exists
        if (_activeOverlays.ContainsKey(window.Handle))
            return;

        // Set locked thumbnail for taskbar preview
        var hwnd = new HWND(window.Handle);
        _thumbnailService.SetLockedThumbnail(hwnd);

        var overlay = new LockOverlayWindow(window.Handle, window.WindowTitle);
        _activeOverlays[window.Handle] = overlay;
        
        overlay.Closed += (s, e) =>
        {
            if (overlay.IsUnlocked)
            {
                if (overlay.UnlockDuration == TimeSpan.Zero)
                {
                    // No limit - unlock indefinitely until app restart
                    _indefinitelyUnlocked.Add(window.Handle);
                }
                else
                {
                    // Set unlock timer with expiration
                    _unlockTimers[window.Handle] = DateTime.Now.Add(overlay.UnlockDuration);
                }
            }
            
            // Clear locked thumbnail when overlay is removed
            _thumbnailService.ClearLockedThumbnail(hwnd);
            
            _activeOverlays.Remove(window.Handle);
        };
        
        overlay.Activate();
    }

    private void RemoveOverlay(nint windowHandle)
    {
        if (_activeOverlays.TryGetValue(windowHandle, out var overlay))
        {
            overlay.Close();
            _activeOverlays.Remove(windowHandle);
        }
        
        // Clear thumbnail
        var hwnd = new HWND(windowHandle);
        _thumbnailService.ClearLockedThumbnail(hwnd);
        
        _unlockTimers.Remove(windowHandle);
    }

    /// <summary>
    /// Dynamically updates the "keep lock screen on top" setting across all currently active overlays.
    /// </summary>
    public void UpdateKeepOnTop(bool keepOnTop)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            foreach (var overlay in _activeOverlays.Values)
            {
                overlay.UpdateKeepOnTop(keepOnTop);
            }
        });
    }

    private void CloseAllOverlays()
    {
        foreach (var overlay in _activeOverlays.Values.ToList())
        {
            overlay.Close();
        }
        _activeOverlays.Clear();
        _unlockTimers.Clear();
        _indefinitelyUnlocked.Clear();
    }

    private void CheckRelockTimers(object? state)
    {
        var now = DateTime.Now;
        var expiredTimers = _unlockTimers.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList();

        if (expiredTimers.Any())
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                foreach (var windowHandle in expiredTimers)
                {
                    _unlockTimers.Remove(windowHandle);
                    CheckWindow(windowHandle);
                }
            });
        }
    }

    public void Dispose()
    {
        _relockTimer?.Dispose();
        _winEventHook?.Dispose();
        CloseAllOverlays();
    }
}
