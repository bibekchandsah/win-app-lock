using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using AppLock.Models;
using AppLock.Services.Audio;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Applies the auto-mute schedule to every active playback device and, when enabled, every active recording
/// device. It is a watcher, not a driver: a background timer looks every 20 seconds at the clock, and the work
/// itself runs on the UI thread (the same thread the Volume Mixer uses for audio), so the schedule keeps
/// working whether or not the user ever opens the Volume Mixer page or its overlay.
/// Each device's mute state before the range is remembered per device, so leaving the range restores speakers
/// and microphones exactly as they were.
/// </summary>
public sealed class MuteScheduleService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private SpeakerVolumeService? _speakers;
    private MicrophoneVolumeService? _microphones;

    // Device id -> mute state before the schedule touched it.
    private readonly Dictionary<string, bool> _speakerMuteBefore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _microphoneMuteBefore = new(StringComparer.Ordinal);

    private string? _activeWindowText;
    private bool _started;

    public MuteScheduleService(DispatcherQueue? dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _timer = new Timer(_ => EvaluateOnUiThread(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised with the active range text when the schedule starts or stops muting (null when idle).</summary>
    public event EventHandler<string?>? StateChanged;

    /// <summary>True while the schedule is the reason the speakers are muted.</summary>
    public bool IsSpeakersMutedBySchedule => _speakerMuteBefore.Count > 0;

    /// <summary>True while the schedule is the reason the microphone is muted.</summary>
    public bool IsMicrophoneMutedBySchedule => _microphoneMuteBefore.Count > 0;

    public bool IsMutedBySchedule => IsSpeakersMutedBySchedule || IsMicrophoneMutedBySchedule;

    /// <summary>Text of the range currently muting, or null.</summary>
    public string? ActiveWindowText => _activeWindowText;

    public void Start()
    {
        if (_started) return;
        _started = true;

        var settings = ServiceContainer.Settings.VolumeMixer;
        Log($"Started: enabled={settings.MuteScheduleEnabled}, ranges={settings.MuteWindows.Count}, " +
            $"speakers={settings.MuteSpeakersOnSchedule}, microphone={settings.MuteMicrophoneOnSchedule}");

        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    /// <summary>Stops watching and gives every audio device back to the user.</summary>
    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _started = false;
        ReleaseSpeakers();
        ReleaseMicrophones();
        _activeWindowText = null;
        Log("Stopped, devices restored");
    }

    /// <summary>Re-reads settings and re-applies immediately (called when the schedule is edited).</summary>
    public void Refresh() => EvaluateOnUiThread();

    /// <summary>The timer runs on a background thread; the audio work belongs on the UI thread.</summary>
    private void EvaluateOnUiThread()
    {
        var queue = _dispatcherQueue;
        if (queue != null && queue.TryEnqueue(Evaluate)) return;

        // No dispatcher (or it is gone): the NAudio calls are still safe off the UI thread.
        Evaluate();
    }

    private void Evaluate()
    {
        try
        {
            var settings = ServiceContainer.Settings.VolumeMixer;
            var windows = settings.MuteWindows;
            var enabled = settings.MuteScheduleEnabled && windows.Count > 0;

            MuteWindow? active = null;
            var activeNow = enabled && MuteSchedule.IsActive(DateTime.Now, windows, out active);
            var text = activeNow && active != null ? MuteSchedule.Describe(active) : null;

            var changed = false;

            if (activeNow && settings.MuteSpeakersOnSchedule) changed |= MuteAllSpeakers();
            else changed |= ReleaseSpeakers();

            if (activeNow && settings.MuteMicrophoneOnSchedule) changed |= MuteAllMicrophones();
            else changed |= ReleaseMicrophones();

            if (_activeWindowText != text)
            {
                _activeWindowText = text;
                changed = true;
                Log(text == null ? "No range active" : $"Range active: {text}");
            }

            if (changed) StateChanged?.Invoke(this, _activeWindowText);
        }
        catch (Exception ex)
        {
            Log($"Evaluate failed: {ex.Message}");
            Debug.WriteLine($"[MuteSchedule] Evaluate failed: {ex.Message}");
        }
    }

    // ── Speakers (all active playback devices) ────────────────────────────────

    /// <summary>
    /// Mutes every playback device that is not muted yet, remembering what each one had. Devices that appear
    /// while a range is active are picked up on the next poll, so "all speakers" stays true.
    /// </summary>
    private bool MuteAllSpeakers()
    {
        var speakers = EnumerateSpeakers();
        if (speakers.Count == 0)
        {
            return false;
        }

        var changed = false;

        lock (_gate)
        {
            foreach (var speaker in speakers)
            {
                var id = speaker.DeviceId;
                if (string.IsNullOrEmpty(id)) continue;
                if (_speakerMuteBefore.ContainsKey(id)) continue;

                _speakerMuteBefore[id] = speaker.IsMuted();
                speaker.SetMute(true);
                changed = true;
            }
        }

        if (changed) Log($"Muted {_speakerMuteBefore.Count} playback device(s)");
        return changed;
    }

    /// <summary>
    /// Restores the remembered state of every device. A device the user unmuted by hand during the range keeps
    /// their choice instead of being forced back; devices that disappeared are simply forgotten.
    /// </summary>
    private bool ReleaseSpeakers()
    {
        if (_speakerMuteBefore.Count == 0) return false;

        var byId = EnumerateSpeakers()
            .Where(s => !string.IsNullOrEmpty(s.DeviceId))
            .GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var restored = 0;

        lock (_gate)
        {
            foreach (var (id, wasMuted) in _speakerMuteBefore)
            {
                if (!byId.TryGetValue(id, out var speaker)) continue;
                if (!speaker.IsMuted()) continue;

                speaker.SetMute(wasMuted);
                restored++;
            }

            _speakerMuteBefore.Clear();
        }

        Log($"Restored mute state of {restored} playback device(s)");
        return true;
    }

    private IReadOnlyList<SpeakerDevice> EnumerateSpeakers()
    {
        try
        {
            _speakers ??= new SpeakerVolumeService();

            // Re-enumerate so newly connected devices (docks, headsets, monitors) are included.
            _speakers.RefreshDevices();
            var speakers = _speakers.GetSpeakers();

            if (speakers.Count == 0)
            {
                _speakers.Initialize();
                speakers = _speakers.GetSpeakers();
            }

            return speakers;
        }
        catch (Exception ex)
        {
            Log($"Speaker enumeration failed: {ex.Message}");
            return Array.Empty<SpeakerDevice>();
        }
    }

    // ── Microphones (all active capture devices) ──────────────────────────────

    private bool MuteAllMicrophones()
    {
        var microphones = EnumerateMicrophones();
        if (microphones.Count == 0) return false;

        var changed = false;

        lock (_gate)
        {
            foreach (var microphone in microphones)
            {
                var id = microphone.DeviceId;
                if (string.IsNullOrEmpty(id)) continue;
                if (_microphoneMuteBefore.ContainsKey(id)) continue;

                _microphoneMuteBefore[id] = microphone.IsMuted();
                microphone.SetMute(true);
                changed = true;
            }
        }

        if (changed) Log($"Muted {_microphoneMuteBefore.Count} recording device(s)");
        return changed;
    }

    private bool ReleaseMicrophones()
    {
        if (_microphoneMuteBefore.Count == 0) return false;

        var byId = EnumerateMicrophones()
            .Where(m => !string.IsNullOrEmpty(m.DeviceId))
            .GroupBy(m => m.DeviceId)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var restored = 0;

        lock (_gate)
        {
            foreach (var (id, wasMuted) in _microphoneMuteBefore)
            {
                if (!byId.TryGetValue(id, out var microphone)) continue;
                if (!microphone.IsMuted()) continue;

                microphone.SetMute(wasMuted);
                restored++;
            }

            _microphoneMuteBefore.Clear();
        }

        Log($"Restored mute state of {restored} recording device(s)");
        return true;
    }

    private IReadOnlyList<MicrophoneDevice> EnumerateMicrophones()
    {
        try
        {
            _microphones ??= new MicrophoneVolumeService();

            _microphones.RefreshDevices();
            var microphones = _microphones.GetMicrophones();

            if (microphones.Count == 0)
            {
                _microphones.Initialize();
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

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Small rolling log so a user can tell why a range did or did not mute. Lives next to the other AppLock
    /// logs; failures here are ignored on purpose.
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "mute-schedule.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 256 * 1024)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        _timer.Dispose();
        try { _speakers?.Dispose(); } catch { }
        try { _microphones?.Dispose(); } catch { }
        _speakers = null;
        _microphones = null;
        _speakerMuteBefore.Clear();
        _microphoneMuteBefore.Clear();
    }
}
