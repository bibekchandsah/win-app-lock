using System;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace AppLock.Services.EyeCare;

public sealed class EyeCareScheduler : IDisposable
{
    private readonly EyeCareOverlayService _overlayService;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<EyeCareSettings> _getSettings;
    private readonly Action<AppSettings> _saveSettings;
    
    private Timer? _timer;
    private bool _isOverlayVisible;
    private bool _isSessionLocked;
    private DateTime _nextFireTime;

    public EyeCareScheduler(
        EyeCareOverlayService overlayService,
        DispatcherQueue dispatcher,
        Func<EyeCareSettings> getSettings,
        Action<AppSettings> saveSettings)
    {
        _overlayService = overlayService;
        _dispatcher = dispatcher;
        _getSettings = getSettings;
        _saveSettings = saveSettings;

        // Monitor system events
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Failed to subscribe to system events: {ex.Message}");
        }
    }

    public void Start()
    {
        Stop();

        var settings = _getSettings();
        if (!settings.Enabled)
            return;

        _nextFireTime = DateTime.Now.AddMinutes(settings.IntervalMinutes);
        var delay = TimeSpan.FromMinutes(settings.IntervalMinutes);

        _timer = new Timer(OnTimerTick, null, delay, Timeout.InfiniteTimeSpan);
        
        System.Diagnostics.Debug.WriteLine($"[EyeCare] Scheduler started, next fire at {_nextFireTime:HH:mm:ss}");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        System.Diagnostics.Debug.WriteLine("[EyeCare] Scheduler stopped");
    }

    private void OnTimerTick(object? state)
    {
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                var settings = _getSettings();

                // Skip if disabled
                if (!settings.Enabled)
                {
                    Stop();
                    return;
                }

                // Skip if session is locked
                if (_isSessionLocked)
                {
                    System.Diagnostics.Debug.WriteLine("[EyeCare] Skipping reminder: session locked");
                    RearmTimer();
                    return;
                }

                // Check schedule
                var now = DateTime.Now.TimeOfDay;
                if (!settings.IsWithinSchedule(now))
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Skipping reminder: outside schedule window");
                    RearmTimer();
                    return;
                }

                // Skip if overlay already visible
                if (_isOverlayVisible || _overlayService.IsVisible)
                {
                    System.Diagnostics.Debug.WriteLine("[EyeCare] Skipping reminder: overlay already visible");
                    RearmTimer();
                    return;
                }

                // Show overlay
                await ShowOverlayAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EyeCare] Timer tick error: {ex.Message}");
                RearmTimer();
            }
        });
    }

    private async Task ShowOverlayAsync()
    {
        try
        {
            _isOverlayVisible = true;
            await _overlayService.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Show overlay error: {ex.Message}");
        }
        finally
        {
            _isOverlayVisible = false;
            // Re-arm timer AFTER overlay closes, so intervals don't stack
            RearmTimer();
        }
    }

    private void RearmTimer()
    {
        var settings = _getSettings();
        if (!settings.Enabled)
            return;

        _nextFireTime = DateTime.Now.AddMinutes(settings.IntervalMinutes);
        var delay = TimeSpan.FromMinutes(settings.IntervalMinutes);

        _timer?.Dispose();
        _timer = new Timer(OnTimerTick, null, delay, Timeout.InfiniteTimeSpan);
        
        System.Diagnostics.Debug.WriteLine($"[EyeCare] Timer re-armed, next fire at {_nextFireTime:HH:mm:ss}");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            System.Diagnostics.Debug.WriteLine("[EyeCare] System resumed from sleep, re-arming timer");
            _dispatcher.TryEnqueue(() =>
            {
                if (_getSettings().Enabled)
                    Start();
            });
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _isSessionLocked = true;
                System.Diagnostics.Debug.WriteLine("[EyeCare] Session locked");
                break;
            case SessionSwitchReason.SessionUnlock:
                _isSessionLocked = false;
                System.Diagnostics.Debug.WriteLine("[EyeCare] Session unlocked");
                break;
        }
    }

    public void Dispose()
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch { }

        Stop();
    }
}
