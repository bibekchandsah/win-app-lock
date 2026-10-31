using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace AppLock.Services;

/// <summary>
/// Monitors system-wide window events (creation, destruction, title changes)
/// and notifies subscribers when windows change.
/// </summary>
public class WindowEventMonitor : IDisposable
{
    private HWINEVENTHOOK _eventHook;
    private WINEVENTPROC? _eventDelegate; // Keep delegate alive to prevent GC
    private bool _isMonitoring;

    /// <summary>
    /// Fired when a new window is created, destroyed, shown, or its title changes.
    /// </summary>
    public event EventHandler? WindowsChanged;

    private readonly object _lock = new();
    private System.Threading.Timer? _debounceTimer;
    private DateTime _lastEventTime = DateTime.MinValue;
    private const int DEBOUNCE_MS = 1500; // Wait 1.5 seconds after last event before notifying

    // Windows Event Constants
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    
    // Hook Flags
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public bool IsMonitoring => _isMonitoring;

    /// <summary>
    /// Start monitoring window events system-wide.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isMonitoring)
                return;

            // Keep delegate alive
            _eventDelegate = WinEventCallback;

            unsafe
            {
                // Monitor window creation, destruction, show/hide, and title changes
                _eventHook = PInvoke.SetWinEventHook(
                    EVENT_OBJECT_CREATE,                    // Min event
                    EVENT_OBJECT_NAMECHANGE,                // Max event (covers create, destroy, show, hide, namechange)
                    HMODULE.Null,
                    _eventDelegate,
                    0,                                       // All processes
                    0,                                       // All threads
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
                );
            }

            _isMonitoring = !_eventHook.IsNull;
            System.Diagnostics.Debug.WriteLine($"WindowEventMonitor started: {_isMonitoring}");
        }
    }

    /// <summary>
    /// Stop monitoring window events.
    /// </summary>
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

            // Dispose debounce timer
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            _isMonitoring = false;
            _eventDelegate = null;
            System.Diagnostics.Debug.WriteLine("WindowEventMonitor stopped");
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
        // Only process window-level events (not child controls)
        const int OBJID_WINDOW = 0;
        if (idObject != OBJID_WINDOW)
            return;

        // Filter relevant events
        bool isRelevant = eventType switch
        {
            EVENT_OBJECT_CREATE => true,       // Window created
            EVENT_OBJECT_DESTROY => true,      // Window destroyed
            EVENT_OBJECT_SHOW => true,         // Window shown
            EVENT_OBJECT_HIDE => true,         // Window hidden
            EVENT_OBJECT_NAMECHANGE => true,   // Window title changed
            _ => false
        };

        if (!isRelevant)
            return;

        // Debounce: Reset timer on each event, only fire after events stop coming
        lock (_lock)
        {
            _lastEventTime = DateTime.UtcNow;
            
            // Dispose existing timer if any
            _debounceTimer?.Dispose();
            
            // Create new timer that fires after DEBOUNCE_MS of inactivity
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                lock (_lock)
                {
                    // Only fire if enough time has passed since last event
                    var timeSinceLastEvent = (DateTime.UtcNow - _lastEventTime).TotalMilliseconds;
                    if (timeSinceLastEvent >= DEBOUNCE_MS - 50) // 50ms tolerance
                    {
                        System.Diagnostics.Debug.WriteLine($"[WindowEventMonitor] Debounced notification triggered after {timeSinceLastEvent:F0}ms idle");
                        
                        // Notify subscribers on background thread
                        Task.Run(() =>
                        {
                            try
                            {
                                WindowsChanged?.Invoke(this, EventArgs.Empty);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error in WindowsChanged handler: {ex.Message}");
                            }
                        });
                    }
                }
            }, null, DEBOUNCE_MS, System.Threading.Timeout.Infinite);
        }

        System.Diagnostics.Debug.WriteLine($"[WindowEventMonitor] Event: {GetEventName(eventType)} HWND=0x{hwnd.Value:X} (debounce timer reset)");
    }

    private static string GetEventName(uint eventType)
    {
        return eventType switch
        {
            EVENT_OBJECT_CREATE => "CREATE",
            EVENT_OBJECT_DESTROY => "DESTROY",
            EVENT_OBJECT_SHOW => "SHOW",
            EVENT_OBJECT_HIDE => "HIDE",
            EVENT_OBJECT_NAMECHANGE => "NAMECHANGE",
            _ => $"0x{eventType:X}"
        };
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    ~WindowEventMonitor()
    {
        Stop();
    }
}
