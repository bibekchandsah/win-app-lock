using AppLock.Models;
using AppLock.Views;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.Graphics.Dwm;

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
    private readonly UwpWindowResolver _uwpResolver;
    private nint _lastForegroundHwnd = 0;
    private long _lastToggleTick = 0;
    // Track last time overlay was shown per target (for toggle detection)
    private readonly Dictionary<nint, long> _overlayShownTick = new();

    public OverlayManager(LockManager lockManager, AuthService authService)
    {
        _lockManager = lockManager;
        _authService = authService;
        _windowEnumerator = new WindowEnumerator();
        _winEventHook = new WinEventHookService();
        _thumbnailService = ServiceContainer.ThumbnailService;
        _displayHelper = ServiceContainer.DisplayHelper;
        _uwpResolver = ServiceContainer.UwpWindowResolver;
        
        // Get the dispatcher queue from the main UI thread
        // Note: This will be null if not called on UI thread, we'll get it lazily in Start()
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        
        // Check for relock and cleanup dead windows every 2 seconds
        _relockTimer = new System.Threading.Timer(CheckRelockTimers, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        
        // Subscribe to lock manager events
        _lockManager.ProtectionStateChanged += OnProtectionStateChanged;
        _lockManager.RuleAdded += (s, e) => CheckAllWindows();
        
        // Subscribe to window events
        _winEventHook.WindowShown += OnWindowEvent;
        _winEventHook.WindowFocused += OnWindowFocused;
        _winEventHook.WindowMoved += OnWindowMoved;
        _winEventHook.WindowDestroyed += OnWindowDestroyed;
        _winEventHook.WindowRestored += OnWindowRestored;
        _winEventHook.WindowMinimized += OnWindowMinimized;
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
                var targetHwnd = new HWND(e.WindowHandle);
                if (PInvoke.IsIconic(targetHwnd))
                {
                    overlay.HideOverlay();
                    return;
                }

                overlay.PositionOverTarget();
                overlay.Activate();
                _lastForegroundHwnd = overlay.WindowHandle;
                return;
            }

            CheckWindow(e.WindowHandle);
        });
    }

    private static bool IsShellWindow(HWND hwnd)
    {
        if (hwnd.IsNull) return false;
        string cls = GetWindowClassName(hwnd);
        return cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("TaskListOverlayWnd", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase) ||
               cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
    }

    private void OnWindowFocused(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            nint targetHandle = e.WindowHandle;
            var targetHwnd = new HWND(targetHandle);

            // If focus briefly shifted to Windows Shell / Taskbar / Thumbnail preview,
            // DO NOT overwrite _lastForegroundHwnd! When clicking the taskbar, Windows
            // Shell momentarily takes focus to handle the click.
            if (IsShellWindow(targetHwnd))
            {
                return;
            }

            nint previousForeground = _lastForegroundHwnd;
            _lastForegroundHwnd = targetHandle;

            if (_activeOverlays.TryGetValue(targetHandle, out var overlay))
            {
                // If target is currently minimized, hide overlay and return
                if (PInvoke.IsIconic(targetHwnd))
                {
                    overlay.HideOverlay();
                    return;
                }

                nint overlayHwnd = overlay.WindowHandle;
                long now = Environment.TickCount64;

                // TOGGLE MINIMIZE:
                // User clicked the target window's taskbar button while the overlay was already visible.
                // Criteria:
                // 1. Overlay is currently visible on screen.
                // 2. The overlay was shown recently (within 3 seconds — meaning it was already up).
                // 3. At least 350ms elapsed since last toggle (debounce).
                // 4. The focus is coming back to the target itself (not from a different app)
                //    i.e. previousForeground was the overlay OR was a shell window (taskbar stole focus)
                //    OR was 0 (we just cleared it after a toggle).
                bool overlayWasFocused = previousForeground == overlayHwnd || previousForeground == 0;
                bool shellIntercepted = previousForeground != overlayHwnd && previousForeground != 0 &&
                                        IsShellWindow(new HWND(previousForeground));
                bool shouldToggle = overlay.IsOverlayVisible &&
                    (overlayWasFocused || shellIntercepted) &&
                    _overlayShownTick.TryGetValue(targetHandle, out long shownAt) &&
                    (now - shownAt > 100) &&    // Overlay was up for at least 100ms (not just shown)
                    (now - _lastToggleTick > 350);

                if (shouldToggle)
                {
                    _lastToggleTick = now;
                    // Save window placement before minimizing to preserve size/position
                    SaveAndMinimize(targetHwnd);
                    overlay.HideOverlay();
                    _lastForegroundHwnd = 0;
                    return;
                }

                // User is activating or restoring this window
                _lastToggleTick = now;
                _overlayShownTick[targetHandle] = now;
                overlay.PositionOverTarget();
                overlay.Activate();
                _lastForegroundHwnd = overlayHwnd;
                return;
            }

            CheckWindow(targetHandle);
        });
    }

    // Manual P/Invoke for WINDOWPLACEMENT (not generated by CsWin32 in this project config)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WP_POINT { public int X; public int Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WP_RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public WP_POINT ptMinPosition;
        public WP_POINT ptMaxPosition;
        public WP_RECT rcNormalPosition;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    /// <summary>
    /// Saves WINDOWPLACEMENT and minimizes. This preserves the window's normal size/position
    /// so that when the user later restores, it comes back at the right size (fixes Excel/Explorer resize).
    /// </summary>
    private static void SaveAndMinimize(HWND targetHwnd)
    {
        var wp = new WINDOWPLACEMENT();
        wp.length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>();

        if (GetWindowPlacement(targetHwnd, ref wp))
        {
            // Snap the normalPosition (restored size) before SW_MINIMIZE changes it
            var savedNormal = wp.rcNormalPosition;

            // Minimize
            PInvoke.ShowWindow(targetHwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);

            // Re-read placement (showCmd is now SW_SHOWMINIMIZED) and fix normal bounds
            if (GetWindowPlacement(targetHwnd, ref wp))
            {
                wp.rcNormalPosition = savedNormal;
                SetWindowPlacement(targetHwnd, ref wp);
            }
        }
        else
        {
            // Fallback: plain minimize
            PInvoke.ShowWindow(targetHwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
        }
    }

    private void OnWindowRestored(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_activeOverlays.TryGetValue(e.WindowHandle, out var overlay))
            {
                var targetHwnd = new HWND(e.WindowHandle);
                if (!PInvoke.IsIconic(targetHwnd))
                {
                    long now = Environment.TickCount64;
                    _lastToggleTick = now;
                    overlay.PositionOverTarget();
                    overlay.Activate();
                    _lastForegroundHwnd = overlay.WindowHandle;
                }
            }
            else
            {
                CheckWindow(e.WindowHandle);
            }
        });
    }

    private void OnWindowMoved(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            // Update overlay position if it exists
            if (_activeOverlays.TryGetValue(e.WindowHandle, out var overlay))
            {
                var targetHwnd = new HWND(e.WindowHandle);
                if (PInvoke.IsIconic(targetHwnd))
                {
                    overlay.HideOverlay();
                    return;
                }

                overlay.PositionOverTarget();
            }
        });
    }

    private void OnWindowMinimized(object? sender, WindowEventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_activeOverlays.TryGetValue(e.WindowHandle, out var overlay))
            {
                overlay.HideOverlay();
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

    private static string GetWindowClassName(HWND hwnd)
    {
        unsafe
        {
            var buffer = new char[256];
            fixed (char* p = buffer)
            {
                int len = PInvoke.GetClassName(hwnd, new Windows.Win32.Foundation.PWSTR(p), buffer.Length);
                return len > 0 ? new string(buffer, 0, len) : string.Empty;
            }
        }
    }

    private static bool IsValidTargetWindow(HWND hwnd, string title, string processName)
    {
        // 1. Skip system popups, flyouts, and context menu titles
        if (title.Equals("PopupHost", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("PopupHost", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Taskbar", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Windows Shell Experience Host", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Desktop Window", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Cortana", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Class name checks (menus, popups, taskbars, thumbnail previews)
        string className = GetWindowClassName(hwnd);
        if (className.Equals("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("TaskListOverlayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("ToolTips_Class32", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
            className == "#32768")
        {
            return false;
        }

        // 3. For explorer.exe specifically: ONLY lock actual File Explorer folder windows (CabinetWClass)
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (!className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 4. Skip tool windows (WS_EX_TOOLWINDOW = 0x00000080)
        int exStyle = (int)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((exStyle & 0x00000080) != 0)
        {
            return false;
        }

        // 5. Must be a top-level root window
        HWND root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (root.Value != 0 && root != hwnd)
        {
            return false;
        }

        // 6. Skip owned popup windows without WS_EX_APPWINDOW (0x00040000)
        nint owner = PInvoke.GetWindowLongPtr(hwnd, (WINDOW_LONG_PTR_INDEX)(-8));
        if (owner != 0 && (exStyle & 0x00040000) == 0)
        {
            return false;
        }

        // 6. Check if cloaked (DWMWA_CLOAKED = 14)
        int cloaked = 0;
        unsafe
        {
            var hr = PInvoke.DwmGetWindowAttribute(
                hwnd,
                (DWMWINDOWATTRIBUTE)14,
                &cloaked,
                sizeof(int));
            if (hr.Succeeded && cloaked != 0)
            {
                return false;
            }
        }

        return true;
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
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
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

            if (string.IsNullOrWhiteSpace(title))
                return;

            // Quick check on title
            if (title.Equals("PopupHost", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith("PopupHost", StringComparison.OrdinalIgnoreCase))
                return;

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

                        // Validate window before proceeding
                        if (!IsValidTargetWindow(hwnd, title, processName))
                            return;

                        // Handle UWP apps (ApplicationFrameHost)
                        if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                            _uwpResolver.IsApplicationFrameHost(processPath))
                        {
                            var (actualTitle, actualPath) = _uwpResolver.ResolveUwpWindow(hwnd);
                            if (!string.IsNullOrEmpty(actualTitle))
                            {
                                title = actualTitle;
                            }
                            if (!string.IsNullOrEmpty(actualPath))
                            {
                                processPath = actualPath;
                                processName = Path.GetFileNameWithoutExtension(actualPath);
                            }
                            else
                            {
                                // Could not resolve actual UWP process path.
                                // Try AUMID as a more reliable identifier (e.g. "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
                                var aumid = _uwpResolver.GetAppUserModelId(hwnd);
                                if (!string.IsNullOrEmpty(aumid))
                                {
                                    // Use AUMID as processPath for matching purposes; extract a human-readable name
                                    processPath = aumid;
                                    // Extract package name part: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" -> "WindowsCalculator"
                                    var pkgName = aumid.Split('_')[0]; // "Microsoft.WindowsCalculator"
                                    var shortName = pkgName.Contains('.') ? pkgName.Substring(pkgName.LastIndexOf('.') + 1) : pkgName;
                                    processName = shortName; // "WindowsCalculator"
                                }
                                // else: keep processName = "ApplicationFrameHost" so it only matches explicit rules for it
                            }
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
                            // Always install locked thumbnail immediately, even if window is currently minimized!
                            _thumbnailService.SetLockedThumbnail(HWND.Null, hwnd);

                            if (!PInvoke.IsIconic(hwnd))
                            {
                                ShowOverlay(window);
                            }
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

        var targetHwnd = new HWND(window.Handle);

        // Do not show overlay if target window is minimized or not a valid top-level window
        if (PInvoke.IsIconic(targetHwnd) || !IsValidTargetWindow(targetHwnd, window.WindowTitle, window.ProcessName))
            return;

        try
        {
            var overlay = new LockOverlayWindow(window.Handle, window.WindowTitle);
            _activeOverlays[window.Handle] = overlay;
            _overlayShownTick[window.Handle] = Environment.TickCount64;

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

                    // Only clear locked thumbnail when the window is actually unlocked!
                    _thumbnailService.ClearLockedThumbnailByTarget(targetHwnd);
                }

                _activeOverlays.Remove(window.Handle);
                _overlayShownTick.Remove(window.Handle);
            };

            overlay.Activate();

            // Force correct theme immediately after activate (visual tree is now live)
            overlay.UpdateTheme(ServiceContainer.CurrentTheme);

            // Inject hook DLL into target window's thread for "Locked" thumbnail
            var overlayHwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(overlay));
            _thumbnailService.SetLockedThumbnail(overlayHwnd, targetHwnd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing overlay for window '{window.WindowTitle}': {ex.Message}");
        }
    }

    private void RemoveOverlay(nint windowHandle)
    {
        // Unconditionally clear locked thumbnail and detach hooks from target
        try
        {
            _thumbnailService.ClearLockedThumbnailByTarget(new HWND(windowHandle));
        }
        catch { }

        if (_activeOverlays.TryGetValue(windowHandle, out var overlay))
        {
            overlay.Close();
            _activeOverlays.Remove(windowHandle);
        }

        _unlockTimers.Remove(windowHandle);
        _indefinitelyUnlocked.Remove(windowHandle);
        _overlayShownTick.Remove(windowHandle);
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

    /// <summary>
    /// Dynamically updates the theme across all active overlays.
    /// </summary>
    public void UpdateTheme(Microsoft.UI.Xaml.ElementTheme theme)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            foreach (var overlay in _activeOverlays.Values)
            {
                overlay.UpdateTheme(theme);
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
        _dispatcherQueue?.TryEnqueue(() =>
        {
            // 1. Clean up dead overlays whose target windows were closed or terminated
            var deadHandles = _activeOverlays.Keys.Where(h => !PInvoke.IsWindow(new HWND(h))).ToList();
            foreach (var h in deadHandles)
            {
                RemoveOverlay(h);
            }

            // 2. Clean up any dead hook targets in ThumbnailService
            _thumbnailService.CleanupDeadTargets();

            // 3. Process expired unlock timers
            var now = DateTime.Now;
            var expiredTimers = _unlockTimers.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList();
            foreach (var windowHandle in expiredTimers)
            {
                _unlockTimers.Remove(windowHandle);
                CheckWindow(windowHandle);
            }
        });
    }

    public void Dispose()
    {
        _relockTimer?.Dispose();
        _winEventHook?.Dispose();
        CloseAllOverlays();
    }
}
