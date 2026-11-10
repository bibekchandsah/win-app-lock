using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Manages Windows Core Audio using NAudio.
///
/// THREADING MODEL
/// ───────────────
/// All NAudio / COM audio objects (_deviceEnumerator, _defaultDevice, AudioSessionManager, …)
/// are created on and must only ever be accessed from _audioThread.  Every public method that
/// needs to touch those objects posts a delegate onto _commandQueue; the audio thread drains
/// that queue in its run-loop.
///
/// UI-thread callers that need a return value use a TaskCompletionSource so they can await the
/// result without blocking the audio thread.
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    // ── Threading infrastructure ──────────────────────────────────────────────

    private readonly DispatcherQueue _dispatcherQueue;
    private Thread? _audioThread;
    private volatile bool _isRunning;
    private volatile bool _reinitRequired;
    private readonly ConcurrentQueue<Action> _commandQueue = new();
    private readonly object _startStopLock = new();

    // ── COM objects – accessed ONLY from _audioThread ─────────────────────────

    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _defaultDevice;
    
    // Keep strong references to prevent GC while COM has pointers to these callbacks
    private AudioEndpointVolumeNotificationDelegate? _volumeCallbackDelegate;
    private DeviceNotificationClient? _deviceNotificationClient;

    // ── Cached state (written on audio thread, read on any thread safely) ─────

    private volatile float _cachedVolume = 0.5f;
    private volatile bool  _cachedMute   = false;
    private volatile bool  _hasDevice    = false;
    private string? _cachedDeviceName = null;  // Only updated on audio thread

    // ── Public events (always fired on the UI DispatcherQueue) ────────────────

    public event EventHandler<float>? MasterVolumeChanged;
    public event EventHandler<bool>?  MasterMuteChanged;
    public event EventHandler?        DefaultDeviceChanged;

    // ─────────────────────────────────────────────────────────────────────────

    public AudioMixerService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_startStopLock)
        {
            if (_isRunning) return;

            _isRunning = true;
            _audioThread = new Thread(AudioThreadProc)
            {
                Name       = "AudioMixerThread",
                IsBackground = true
            };
            // CRITICAL: Set apartment state to STA before starting the thread.
            // NAudio's COM objects (MMDevice, AudioEndpointVolume) require STA.
            _audioThread.SetApartmentState(ApartmentState.STA);
            _audioThread.Start();
            System.Diagnostics.Debug.WriteLine("[AudioMixer] Started");
        }
    }

    public void Stop()
    {
        lock (_startStopLock)
        {
            if (!_isRunning) return;

            _isRunning = false;
            // Audio thread's finally block performs cleanup; just wait for it.
            _audioThread?.Join(3000);
            _audioThread = null;
            System.Diagnostics.Debug.WriteLine("[AudioMixer] Stopped");
        }
    }

    public void Dispose() => Stop();

    // ── Audio thread ──────────────────────────────────────────────────────────

    private void AudioThreadProc()
    {
        try
        {
            InitializeAudioObjects();

            while (_isRunning)
            {
                // Handle device-change reinit requested by the notification callback
                if (_reinitRequired)
                {
                    _reinitRequired = false;
                    CleanupAudioObjects();
                    InitializeAudioObjects();

                    _dispatcherQueue.TryEnqueue(() =>
                        DefaultDeviceChanged?.Invoke(this, EventArgs.Empty));
                }

                // Drain command queue — this is the ONLY place COM objects are touched
                while (_commandQueue.TryDequeue(out var cmd))
                {
                    try { cmd(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AudioMixer] Command error: {ex.Message}");
                    }
                }

                Thread.Sleep(15); // ~60 Hz drain rate; keeps latency low
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioMixer] Thread fatal: {ex.Message}");
        }
        finally
        {
            // Drain any remaining commands so queued lambdas don't hold references
            while (_commandQueue.TryDequeue(out _)) { }
            CleanupAudioObjects();
        }
    }

    // ── COM init / cleanup (audio thread only) ────────────────────────────────

    private void InitializeAudioObjects()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            
            // CRITICAL: Keep strong reference to DeviceNotificationClient
            // COM holds a pointer to this; if GC collects it, we get ExecutionEngineException
            _deviceNotificationClient = new DeviceNotificationClient(this);
            _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceNotificationClient);

            _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(
                DataFlow.Render, Role.Multimedia);

            if (_defaultDevice != null)
            {
                _cachedDeviceName = _defaultDevice.FriendlyName;
                _hasDevice = true;
                
                System.Diagnostics.Debug.WriteLine(
                    $"[AudioMixer] Default device: {_cachedDeviceName}");

                // CRITICAL: Keep strong reference to callback delegate to prevent GC
                // The += operator creates a COM callback wrapper that holds a function pointer.
                // If the delegate is GC'd, COM calls freed memory → ExecutionEngineException
                _volumeCallbackDelegate = OnVolumeNotification;
                _defaultDevice.AudioEndpointVolume.OnVolumeNotification += _volumeCallbackDelegate;

                // Capture initial state and push to UI
                var vol  = _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                var mute = _defaultDevice.AudioEndpointVolume.Mute;

                _cachedVolume = vol;
                _cachedMute   = mute;

                _dispatcherQueue.TryEnqueue(() =>
                {
                    MasterVolumeChanged?.Invoke(this, vol);
                    MasterMuteChanged?.Invoke(this, mute);
                });
            }
            else
            {
                _cachedDeviceName = null;
                _hasDevice = false;
                System.Diagnostics.Debug.WriteLine("[AudioMixer] No default audio device");
            }
        }
        catch (Exception ex)
        {
            _cachedDeviceName = null;
            _hasDevice = false;
            System.Diagnostics.Debug.WriteLine($"[AudioMixer] InitializeAudioObjects error: {ex.Message}");
        }
    }

    private void CleanupAudioObjects()
    {
        try
        {
            if (_defaultDevice != null)
            {
                try 
                { 
                    if (_volumeCallbackDelegate != null)
                    {
                        _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= _volumeCallbackDelegate;
                        _volumeCallbackDelegate = null;
                    }
                }
                catch { /* best-effort */ }
                _defaultDevice.Dispose();
                _defaultDevice = null;
            }

            if (_deviceEnumerator != null)
            {
                try
                {
                    if (_deviceNotificationClient != null)
                    {
                        _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceNotificationClient);
                        _deviceNotificationClient = null;
                    }
                }
                catch { /* best-effort */ }
                _deviceEnumerator.Dispose();
                _deviceEnumerator = null;
            }

            _cachedDeviceName = null;
            _hasDevice = false;

            System.Diagnostics.Debug.WriteLine("[AudioMixer] Audio objects cleaned up");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioMixer] CleanupAudioObjects error: {ex.Message}");
        }
    }

    // ── Volume notification (called by NAudio on a Windows callback thread) ───

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        // We just update the cache here; actual COM interaction is not needed.
        _cachedVolume = data.MasterVolume;
        _cachedMute   = data.Muted;

        _dispatcherQueue.TryEnqueue(() =>
        {
            MasterVolumeChanged?.Invoke(this, data.MasterVolume);
            MasterMuteChanged?.Invoke(this, data.Muted);
        });
    }

    // ── Public API — synchronous via cache (safe from any thread) ─────────────

    /// <summary>Returns the last-known master volume (0–1). Never touches COM directly.</summary>
    public float GetMasterVolume() => _cachedVolume;

    /// <summary>Returns the last-known mute state. Never touches COM directly.</summary>
    public bool GetMasterMute() => _cachedMute;

    /// <summary>Returns whether a default audio device is present (cached state).</summary>
    public bool HasDefaultDevice => _hasDevice;

    /// <summary>Returns the cached device name. Never touches COM directly.</summary>
    public string? GetDefaultDeviceName() => _cachedDeviceName;

    // ── Public API — write operations (posted to audio thread) ────────────────

    public void SetMasterVolume(float volume)
    {
        if (volume < 0f || volume > 1f)
            throw new ArgumentOutOfRangeException(nameof(volume));

        _commandQueue.Enqueue(() =>
        {
            if (_defaultDevice?.AudioEndpointVolume != null)
            {
                _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                System.Diagnostics.Debug.WriteLine($"[AudioMixer] SetMasterVolume: {volume:P0}");
            }
        });
    }

    public void SetMasterMute(bool mute)
    {
        _commandQueue.Enqueue(() =>
        {
            if (_defaultDevice?.AudioEndpointVolume != null)
            {
                _defaultDevice.AudioEndpointVolume.Mute = mute;
                System.Diagnostics.Debug.WriteLine($"[AudioMixer] SetMasterMute: {mute}");
            }
        });
    }

    // ── Per-app session management (async, results posted to UI thread) ────────

    /// <summary>
    /// Enqueues a session snapshot operation on the audio thread and delivers
    /// the result to <paramref name="onResult"/> on the UI DispatcherQueue.
    /// </summary>
    public void GetAudioSessionsAsync(Action<List<SessionInfo>> onResult)
    {
        _commandQueue.Enqueue(() =>
        {
            var sessions = ReadSessionsOnAudioThread();
            _dispatcherQueue.TryEnqueue(() => onResult(sessions));
        });
    }

    private List<SessionInfo> ReadSessionsOnAudioThread()
    {
        var sessions = new List<SessionInfo>();
        try
        {
            if (_defaultDevice?.AudioSessionManager == null)
                return sessions;

            var col = _defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0; i < col.Count; i++)
            {
                try
                {
                    var s = col[i];
                    var pid = (int)s.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId)
                        continue;

                    sessions.Add(new SessionInfo
                    {
                        ProcessId         = pid,
                        DisplayName       = s.DisplayName ?? string.Empty,
                        IconPath          = s.IconPath    ?? string.Empty,
                        State             = (int)s.State,
                        Volume            = s.SimpleAudioVolume.Volume,
                        IsMuted           = s.SimpleAudioVolume.Mute,
                        SessionIdentifier = s.GetSessionIdentifier
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AudioMixer] Error reading session {i}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioMixer] GetAudioSessions error: {ex.Message}");
        }
        System.Diagnostics.Debug.WriteLine($"[AudioMixer] Found {sessions.Count} sessions");
        return sessions;
    }

    public void SetSessionVolume(string sessionIdentifier, float volume)
    {
        if (volume < 0f || volume > 1f)
            throw new ArgumentOutOfRangeException(nameof(volume));

        _commandQueue.Enqueue(() =>
        {
            try
            {
                if (_defaultDevice?.AudioSessionManager == null) return;
                var col = _defaultDevice.AudioSessionManager.Sessions;
                for (int i = 0; i < col.Count; i++)
                {
                    try
                    {
                        var s = col[i];
                        if (s.GetSessionIdentifier == sessionIdentifier)
                            s.SimpleAudioVolume.Volume = volume;
                    }
                    catch { /* session may have ended */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioMixer] SetSessionVolume error: {ex.Message}");
            }
        });
    }

    public void SetSessionMute(string sessionIdentifier, bool mute)
    {
        _commandQueue.Enqueue(() =>
        {
            try
            {
                if (_defaultDevice?.AudioSessionManager == null) return;
                var col = _defaultDevice.AudioSessionManager.Sessions;
                for (int i = 0; i < col.Count; i++)
                {
                    try
                    {
                        var s = col[i];
                        if (s.GetSessionIdentifier == sessionIdentifier)
                            s.SimpleAudioVolume.Mute = mute;
                    }
                    catch { /* session may have ended */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioMixer] SetSessionMute error: {ex.Message}");
            }
        });
    }

    // ── Device change notification (fires on Windows MMNotification thread) ───

    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioMixerService _service;
        public DeviceNotificationClient(AudioMixerService service) => _service = service;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AudioMixer] Default device changed: {defaultDeviceId}");

                // Only set a flag — do NOT touch any COM object here.
                // This callback fires on the MMNotificationClient thread (a different COM
                // apartment from _audioThread). The audio thread will see the flag on its
                // next loop iteration and perform the reinit there.
                _service._reinitRequired = true;
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string pwstrDeviceId) { }
        public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

// ── Data model ───────────────────────────────────────────────────────────────

/// <summary>Snapshot of a single audio session captured on the audio thread.</summary>
public sealed class SessionInfo
{
    public int    ProcessId         { get; set; }
    public string DisplayName       { get; set; } = string.Empty;
    public string IconPath          { get; set; } = string.Empty;
    public int    State             { get; set; } // 0=Inactive 1=Active 2=Expired
    public float  Volume            { get; set; }
    public bool   IsMuted           { get; set; }
    public string SessionIdentifier { get; set; } = string.Empty;
}
