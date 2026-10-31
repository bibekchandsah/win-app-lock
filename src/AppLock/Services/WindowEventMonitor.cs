using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace AppLock.Services;

/// <summary>
/// Monitors system-wide window events (creation, destruction, title changes)
/// and notifies subscribers when windows change.
///
/// Two event channels:
///   TitleChanged   — fires immediately (no debounce) with the specific HWND whose
///                    title changed. Callers do a cheap in-place title update.
///   WindowsChanged — fires after a short debounce for structural changes (new window,
///                    closed window, show/hide). Callers do a full re-enumeration.
/// </summary>
public class WindowEventMonitor : IDisposable
{
    private HWINEVENTHOOK _eventHook;
    private WINEVENTPROC? _eventDelegate; // Keep delegate alive to prevent GC
    private bool _isMonitoring;

    /// <summary>
    /// Fired immediately when a top-level window's title changes.
    /// EventArgs carry the HWND that changed — use it for an O(1) in-place update.
    /// </summary>
    public event EventHandler<WindowTitleChangedEventArgs>? TitleChanged;

    /// <summary>
    /// Fired (debounced) when windows are created, destroyed, shown, or hidden.
    /// </summary>
    public event EventHandler? WindowsChanged;

    private readonly object _lock = new();
    private System.Threading.Timer? _debounceTimer;
    private DateTime _lastStructuralEventTime = DateTime.MinValue;
    private const int DEBOUNCE_MS = 750; // Structural change debounce (was 1500ms)

    // Windows Event Constants
    private const uint EVENT_OBJECT_CREATE     = 0x8000;
    private const uint EVENT_OBJECT_DESTROY    = 0x8001;
    private const uint EVENT_OBJECT_SHOW       = 0x8002;
    private const uint EVENT_OBJECT_HIDE       = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const int  OBJID_WINDOW            = 0;

    // Hook Flags
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public bool IsMonitoring => _isMonitoring;

    /// <summary>Start monitoring window events system-wide.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isMonitoring)
                return;

            _eventDelegate = WinEventCallback;

            unsafe
            {
                // Single hook spanning all events we care about
                _eventHook = PInvoke.SetWinEventHook(
                    EVENT_OBJECT_CREATE,      // Min: window created
                    EVENT_OBJECT_NAMECHANGE,  // Max: covers create, destroy, show, hide, namechange
                    HMODULE.Null,
                    _eventDelegate,
                    0,    // All processes
                    0,    // All threads
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            }

            _isMonitoring = !_eventHook.IsNull;
            System.Diagnostics.Debug.WriteLine($"[WindowEventMonitor] Started: {_isMonitoring}");
        }
    }

    /// <summary>Stop monitoring window events.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isMonitoring)
                return;

            if (!_eventHook.IsNull)
            {
                PInvoke.UnhookWinEvent(_eventHook);
                _eventHook = default;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;

            _isMonitoring = false;
            _eventDelegate = null;
            System.Diagnostics.Debug.WriteLine("[WindowEventMonitor] Stopped");
        }
    }

    private void WinEventCallback(
        HWINEVENTHOOK hWinEventHook,
        uint eventType,
        HWND hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        // Only care about window-object events (not child controls, menus, etc.)
        if (idObject != OBJID_WINDOW || hwnd.IsNull)
            return;

        switch (eventType)
        {
            case EVENT_OBJECT_NAMECHANGE:
                // Title changed on a specific window — fire immediately with no debounce.
                // This is a targeted fast-path so the UI title updates without any delay.
                System.Diagnostics.Debug.WriteLine(
                    $"[WindowEventMonitor] NAMECHANGE 0x{hwnd.Value:X} — firing TitleChanged immediately");

                Task.Run(() =>
                {
                    try { TitleChanged?.Invoke(this, new WindowTitleChangedEventArgs((nint)hwnd.Value)); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[WindowEventMonitor] TitleChanged error: {ex.Message}");
                    }
                });
                break;

            case EVENT_OBJECT_CREATE:
            case EVENT_OBJECT_DESTROY:
            case EVENT_OBJECT_SHOW:
            case EVENT_OBJECT_HIDE:
                // Structural changes — debounce and fire WindowsChanged for full re-enum
                ScheduleStructuralRefresh();
                break;
        }
    }

    private void ScheduleStructuralRefresh()
    {
        lock (_lock)
        {
            _lastStructuralEventTime = DateTime.UtcNow;

            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    var elapsed = (DateTime.UtcNow - _lastStructuralEventTime).TotalMilliseconds;
                    if (elapsed >= DEBOUNCE_MS - 50) // 50ms tolerance
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[WindowEventMonitor] Structural debounce fired after {elapsed:F0}ms idle");
                        Task.Run(() =>
                        {
                            try { WindowsChanged?.Invoke(this, EventArgs.Empty); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[WindowEventMonitor] WindowsChanged error: {ex.Message}");
                            }
                        });
                    }
                }
            }, null, DEBOUNCE_MS, System.Threading.Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    ~WindowEventMonitor() => Stop();
}

/// <summary>Event args for targeted window-title changes.</summary>
public sealed class WindowTitleChangedEventArgs : EventArgs
{
    /// <summary>Handle of the top-level window whose title changed.</summary>
    public nint WindowHandle { get; }

    public WindowTitleChangedEventArgs(nint hwnd) => WindowHandle = hwnd;
}
