using System;
using Microsoft.UI.Dispatching;
using AppLock.Views;

namespace AppLock.Services.VolumeMixer;

public sealed class VolumeOverlayService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AudioMixerService _audioService;
    private VolumeOverlayWindow? _overlayWindow;
    private bool _isRunning;

    public VolumeOverlayService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _audioService = new AudioMixerService(dispatcherQueue);
    }

    public void Start()
    {
        if (_isRunning)
        {
            System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Already running");
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Creating overlay window...");
                
                // Start audio service first
                _audioService.Start();

                // Create overlay window and activate it so the compositor paints the first frame
                _overlayWindow = new VolumeOverlayWindow(_audioService);
                System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Window created, calling Activate()...");
                
                _overlayWindow.Activate();
                System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Activate() complete, applying NoActivate style...");

                // Apply WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW AFTER the first Activate() call.
                // Setting these flags before Activate() prevents the compositor from painting the
                // initial frame, leaving the window present but invisible.
                _overlayWindow.ApplyNoActivateStyle();
                System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] NoActivate style applied");

                _isRunning = true;
                System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Started successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeOverlayService] Start error: {ex}");
                System.Diagnostics.Debug.WriteLine($"[VolumeOverlayService] Stack trace: {ex.StackTrace}");
            }
        });
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Not running");
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _overlayWindow?.Close();
                _overlayWindow = null;

                _audioService.Stop();

                _isRunning = false;
                System.Diagnostics.Debug.WriteLine("[VolumeOverlayService] Stopped");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VolumeOverlayService] Stop error: {ex.Message}");
            }
        });
    }

    public void RefreshSettings()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _overlayWindow?.RefreshSettings();
        });
    }

    public bool IsRunning => _isRunning;

    public void Dispose()
    {
        Stop();
        _audioService.Dispose();
    }
}
