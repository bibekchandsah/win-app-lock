using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace AppLock.Services.Notifications;

/// <summary>
/// Event-driven toast window detection using SetWinEventHook
/// </summary>
public class ToastWinEventHook : IDisposable
{
    private HWINEVENTHOOK _eventHook;
    private WINEVENTPROC? _eventDelegate;
    private bool _disposed;

    // Tracks toasts that have been shown (and not yet hidden). The delayed retries below run on
    // thread-pool threads, so every access goes through _seenLock.
    private readonly HashSet<nint> _seenToasts = new();
    // Windows that must not be treated as a new toast for a while (see MuteToast).
    private readonly System.Collections.Generic.Dictionary<nint, long> _mutedUntil = new();
    private readonly object _seenLock = new();

    public event EventHandler<HWND>? ToastShown;
    public event EventHandler<HWND>? ToastMoved;
    public event EventHandler<HWND>? ToastHidden;

    /// <summary>
    /// Set this to true while AppLock is performing a SetWindowPos call so that
    /// the resulting LOCATIONCHANGE event is not echoed back as a ToastMoved.
    /// </summary>
    public bool SuppressNextLocationChange { get; set; }

    public void Start()
    {
        if (_eventHook.Value != IntPtr.Zero)
            return;

        _eventDelegate = OnWinEvent;

        const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        // Hook spanning CREATE to CLOAKED (0x8000 to 0x8017)
        // This covers:
        //   0x8000 EVENT_OBJECT_CREATE
        //   0x8001 EVENT_OBJECT_DESTROY
        //   0x8002 EVENT_OBJECT_SHOW
        //   0x8003 EVENT_OBJECT_HIDE
        //   0x800B EVENT_OBJECT_LOCATIONCHANGE
        //   0x800C EVENT_OBJECT_NAMECHANGE
        //   0x8017 EVENT_OBJECT_CLOAKED
        //   0x8018 EVENT_OBJECT_UNCLOAKED
        _eventHook = PInvoke.SetWinEventHook(
            0x8000, // EVENT_OBJECT_CREATE
            0x8018, // EVENT_OBJECT_UNCLOAKED
            HMODULE.Null,
            _eventDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS
        );

        LogService.LogAction("notifications", $"WinEventHook started (0x8000-0x8018): handle=0x{_eventHook.Value:X}");
    }

    public void Stop()
    {
        if (_eventHook.Value != IntPtr.Zero)
        {
            PInvoke.UnhookWinEvent(_eventHook);
            _eventHook = default;
        }

        lock (_seenLock)
        {
            _seenToasts.Clear();
            _mutedUntil.Clear();
        }
        LogService.LogAction("notifications", "WinEventHook stopped");
    }

    /// <summary>
    /// Called by the positioner to clear a toast from the seen-set when it
    /// is hidden, so a re-used HWND is treated as a fresh toast next time.
    /// </summary>
    public void ForgetToast(HWND hwnd)
    {
        lock (_seenLock) { _seenToasts.Remove(hwnd.Value); }
    }

    /// <summary>
    /// Ignores this banner window for a short while. When we hand a toast back, Windows re-shows
    /// the same window for its own dismissal animation; without the mute that SHOW/LOCATIONCHANGE
    /// is managed as a brand-new toast, which then reports itself hidden a second later.
    /// </summary>
    public void MuteToast(HWND hwnd, int durationMs)
    {
        lock (_seenLock) { _mutedUntil[hwnd.Value] = Environment.TickCount64 + durationMs; }
    }

    private bool IsMuted(nint value)
    {
        lock (_seenLock) { return IsMutedNoLock(value); }
    }

    private bool IsMutedNoLock(nint value)
    {
        if (!_mutedUntil.TryGetValue(value, out var until)) return false;
        if (Environment.TickCount64 >= until)
        {
            _mutedUntil.Remove(value);
            return false;
        }
        return true;
    }

    private void OnWinEvent(
        HWINEVENTHOOK hWinEventHook,
        uint eventType,
        HWND hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        try
        {
            // idObject values for window-level events:
            //   0            = OBJID_WINDOW   (normal top-level windows)
            //  -1 (0xFFFFFF) = OBJID_WINDOW   (some immersive / shell windows, same meaning)
            // We allow both so we don't miss shell-hosted toast banners (WhatsApp, etc.)
            const int OBJID_WINDOW = 0;
            const int OBJID_WINDOW_IMMERSIVE = -1; // same semantic, different shell code path
            if (idObject != OBJID_WINDOW && idObject != OBJID_WINDOW_IMMERSIVE)
                return;
            if (hwnd.Value == IntPtr.Zero)
                return;

            const uint EVENT_OBJECT_CREATE        = 0x8000;
            const uint EVENT_OBJECT_DESTROY       = 0x8001;
            const uint EVENT_OBJECT_SHOW          = 0x8002;
            const uint EVENT_OBJECT_HIDE          = 0x8003;
            const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
            const uint EVENT_OBJECT_NAMECHANGE    = 0x800C;
            const uint EVENT_OBJECT_CLOAKED       = 0x8017;
            const uint EVENT_OBJECT_UNCLOAKED     = 0x8018;

            switch (eventType)
            {
                case EVENT_OBJECT_CREATE:
                case EVENT_OBJECT_SHOW:
                case EVENT_OBJECT_UNCLOAKED:
                case EVENT_OBJECT_NAMECHANGE:
                case EVENT_OBJECT_LOCATIONCHANGE:
                {
                    if (IsMuted(hwnd.Value)) break;

                    // For SHOW/CREATE: the window may not yet have its final size
                    // (shell sizes it asynchronously). If IsToastWindow fails due to
                    // bad size, schedule a single retry after 80ms.
                    bool isToast = ToastWindowFinder.IsToastWindow(hwnd);

                    if (!isToast &&
                        (eventType == EVENT_OBJECT_SHOW || eventType == EVENT_OBJECT_CREATE || eventType == EVENT_OBJECT_UNCLOAKED))
                    {
                        // Check class + process quickly (cheap); if likely a toast host, retry a
                        // few times while the shell sizes and shows the reused banner window.
                        if (ToastWindowFinder.IsLikelyToastHost(hwnd))
                        {
                            var capturedHwnd = hwnd;
                            var capturedEvent = eventType;
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                foreach (var delay in RetryDelaysMs)
                                {
                                    System.Threading.Thread.Sleep(delay);
                                    if (!ToastWindowFinder.IsToastWindow(capturedHwnd)) continue;

                                    lock (_seenLock)
                                    {
                                        if (_seenToasts.Contains(capturedHwnd.Value)) return;
                                        _seenToasts.Add(capturedHwnd.Value);
                                    }

                                    LogService.LogAction("notifications",
                                        $"✓ Toast appeared (delayed) via {GetEventName(capturedEvent)}: 0x{capturedHwnd.Value:X}");
                                    ToastShown?.Invoke(this, capturedHwnd);
                                    return;
                                }
                            });
                        }
                        break;
                    }

                    if (!isToast) break;

                    bool firstSighting;
                    lock (_seenLock)
                    {
                        firstSighting = _seenToasts.Add(hwnd.Value);
                    }

                    if (firstSighting)
                    {
                        // First time we see this toast window (or freshly uncloaked toast)
                        LogService.LogAction("notifications",
                            $"✓ Toast appeared via {GetEventName(eventType)}: 0x{hwnd.Value:X}");
                        ToastShown?.Invoke(this, hwnd);
                    }
                    else if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
                    {
                        // The shell is trying to move the toast.
                        // If WE caused this event, skip it to avoid an echo loop.
                        if (SuppressNextLocationChange)
                        {
                            SuppressNextLocationChange = false;
                            break;
                        }

                        ToastMoved?.Invoke(this, hwnd);
                    }
                    break;
                }

                case EVENT_OBJECT_CLOAKED:
                {
                    // A cloak can be a transient step of the shell's own show/hide transition, so
                    // confirm it before dropping the toast: a false positive erases a banner the
                    // user is still reading.
                    var captured = hwnd;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        System.Threading.Thread.Sleep(250);
                        if (ToastWindowFinder.IsToastWindow(captured)) return;

                        bool muted;
                        bool wasTracked;
                        lock (_seenLock)
                        {
                            muted = IsMutedNoLock(captured.Value);
                            wasTracked = _seenToasts.Remove(captured.Value);
                        }
                        if (muted || !wasTracked) return;

                        LogService.LogAction("notifications",
                            $"Toast hidden/closed via CLOAKED (confirmed): 0x{captured.Value:X}");
                        ToastHidden?.Invoke(this, captured);
                    });
                    break;
                }

                case EVENT_OBJECT_HIDE:
                case EVENT_OBJECT_DESTROY:
                    bool wasTracked;
                    lock (_seenLock) { wasTracked = _seenToasts.Remove(hwnd.Value); }
                    if (wasTracked)
                    {
                        LogService.LogAction("notifications",
                            $"Toast hidden/closed via {GetEventName(eventType)}: 0x{hwnd.Value:X}");
                        ToastHidden?.Invoke(this, hwnd);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Error in OnWinEvent: {ex.Message}");
        }
    }

    /// <summary>Delays tried after SHOW/CREATE when the window is not yet valid as a toast.</summary>
    private static readonly int[] RetryDelaysMs = { 80, 180, 400 };

    private static string GetEventName(uint eventType) => eventType switch
    {
        0x8000 => "CREATE",
        0x8001 => "DESTROY",
        0x8002 => "SHOW",
        0x8003 => "HIDE",
        0x800B => "LOCATIONCHANGE",
        0x800C => "NAMECHANGE",
        0x8017 => "CLOAKED",
        0x8018 => "UNCLOAKED",
        _ => $"0x{eventType:X}"
    };

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
