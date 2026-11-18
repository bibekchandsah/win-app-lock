using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AppLock.Services.Audio;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Holds every microphone at the level the user chose. Windows, a driver's automatic gain control, or a
/// communication app can move the capture level behind the user's back; this watcher reads the level a few times
/// a second and puts it back when something else changed it.
/// The level to hold is captured when the feature is switched on (or when the slider is moved in the mixer), so
/// the user's own changes are never fought.
/// </summary>
public sealed class MicrophoneVolumeLockService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(15);

    // A difference smaller than this is quantization noise, not somebody changing the level.
    private const double TolerancePercent = 0.75;

    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private MicrophoneVolumeService? _microphones;
    private DateTime _lastDeviceRefresh = DateTime.MinValue;
    private int _restoredCount;
    private bool _savePending;
    private bool _started;

    public MicrophoneVolumeLockService(DispatcherQueue? dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _timer = new Timer(_ => EvaluateOnUiThread(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Number of microphones currently being held.</summary>
    public int HeldDeviceCount
    {
        get
        {
            lock (_gate) return ServiceContainer.Settings.VolumeMixer.LockedMicrophoneVolumes?.Count ?? 0;
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        var settings = ServiceContainer.Settings.VolumeMixer;
        Log($"Started: lock={settings.LockMicrophoneVolume}, held={settings.LockedMicrophoneVolumes?.Count ?? 0}");
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _started = false;
        Log("Stopped");
    }

    /// <summary>Re-reads settings and checks the levels immediately.</summary>
    public void Refresh() => EvaluateOnUiThread();

    /// <summary>
    /// Remembers the current level of every microphone as the level to hold. Called when the feature is switched
    /// on, so the levels the user has right now are the ones that stick.
    /// </summary>
    public void CaptureCurrentVolumes()
    {
        try
        {
            var microphones = CurrentMicrophones(forceRefresh: true);
            if (microphones.Count == 0)
            {
                Log("Capture: no recording devices found");
                return;
            }

            var settings = ServiceContainer.Settings.VolumeMixer;
            settings.LockedMicrophoneVolumes ??= new Dictionary<string, float>(StringComparer.Ordinal);
            settings.LockedMicrophoneVolumes.Clear();

            foreach (var microphone in microphones)
            {
                var id = microphone.DeviceId;
                if (string.IsNullOrEmpty(id)) continue;

                settings.LockedMicrophoneVolumes[id] = Round(microphone.GetVolume());
            }

            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
            Log($"Captured {settings.LockedMicrophoneVolumes.Count} recording device(s): " +
                string.Join(", ", settings.LockedMicrophoneVolumes.Values.Select(v => $"{v:0}%")));
        }
        catch (Exception ex)
        {
            Log($"Capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records a level the user just chose on the mixer slider, so the watcher holds the new level instead of
    /// putting the old one back.
    /// </summary>
    public void NoteUserVolume(string deviceId, double volume)
    {
        if (string.IsNullOrEmpty(deviceId)) return;

        lock (_gate)
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            if (!settings.LockMicrophoneVolume) return;

            settings.LockedMicrophoneVolumes ??= new Dictionary<string, float>(StringComparer.Ordinal);
            settings.LockedMicrophoneVolumes[deviceId] = Round(volume);

            // A slider drag fires this many times a second; the next poll writes the result once.
            _savePending = true;
        }
    }

    private void EvaluateOnUiThread()
    {
        var queue = _dispatcherQueue;
        if (queue != null && queue.TryEnqueue(Evaluate)) return;

        Evaluate();
    }

    private void Evaluate()
    {
        try
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            if (!settings.LockMicrophoneVolume)
            {
                FlushPendingSave();
                return;
            }

            var microphones = CurrentMicrophones(forceRefresh: false);
            if (microphones.Count == 0) return;

            settings.LockedMicrophoneVolumes ??= new Dictionary<string, float>(StringComparer.Ordinal);
            var targets = settings.LockedMicrophoneVolumes;

            var captured = 0;
            var restored = 0;
            string? example = null;

            lock (_gate)
            {
                foreach (var microphone in microphones)
                {
                    var id = microphone.DeviceId;
                    if (string.IsNullOrEmpty(id)) continue;

                    if (!targets.TryGetValue(id, out var target))
                    {
                        // A device that appeared after the lock was switched on keeps whatever level it has now.
                        targets[id] = Round(microphone.GetVolume());
                        captured++;
                        continue;
                    }

                    var current = microphone.GetVolume();
                    if (Math.Abs(current - target) <= TolerancePercent) continue;

                    microphone.SetVolume(target);
                    restored++;
                    example ??= $"\"{microphone.FriendlyName}\" {current:0}% -> {target:0}%";
                }
            }

            if (captured > 0)
            {
                ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
                Log($"Remembered {captured} new recording device(s)");
            }

            if (restored > 0)
            {
                _restoredCount += restored;
                Log($"Put back {restored} level(s), e.g. {example} ({_restoredCount} total)");
            }

            FlushPendingSave();
        }
        catch (Exception ex)
        {
            Log($"Evaluate failed: {ex.Message}");
            Debug.WriteLine($"[MicVolumeLock] Evaluate failed: {ex.Message}");
        }
    }

    private IReadOnlyList<MicrophoneDevice> CurrentMicrophones(bool forceRefresh)
    {
        try
        {
            _microphones ??= new MicrophoneVolumeService();

            var due = forceRefresh || DateTime.Now - _lastDeviceRefresh >= DeviceRefreshInterval;
            if (due)
            {
                // Re-enumerating finds a headset plugged in later, but it costs a COM round-trip, so it is done
                // occasionally rather than on every poll.
                _microphones.RefreshDevices();
                _lastDeviceRefresh = DateTime.Now;
            }

            var microphones = _microphones.GetMicrophones();
            if (microphones.Count == 0 && !due)
            {
                _microphones.Initialize();
                _lastDeviceRefresh = DateTime.Now;
                microphones = _microphones.GetMicrophones();
            }

            return microphones;
        }
        catch (Exception ex)
        {
            Log($"Microphone enumeration failed: {ex.Message}");
            return Array.Empty<MicrophoneDevice>();
        }
    }

    private void FlushPendingSave()
    {
        lock (_gate)
        {
            if (!_savePending) return;
            _savePending = false;
        }

        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private static float Round(double percent) => (float)Math.Round(Math.Clamp(percent, 0, 100), 1);

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "mic-volume-lock.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 256 * 1024) File.Delete(path);

            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        _timer.Dispose();
        try { _microphones?.Dispose(); } catch { }
        _microphones = null;
    }
}
