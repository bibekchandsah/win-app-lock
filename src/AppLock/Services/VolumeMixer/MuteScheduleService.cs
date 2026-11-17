using System;
using System.Linq;
using System.Diagnostics;
using AppLock.Models;
using AppLock.Services.Audio;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Applies the auto-mute schedule to the Windows default playback device and, when enabled, the default
/// recording device. It is a watcher, not a driver: every 15 seconds it checks whether the current time is
/// inside one of the ranges and only changes a device when it has to, remembering the mute state the user had
/// before so leaving a range restores it. Speakers and microphone are tracked independently.
/// </summary>
public sealed class MuteScheduleService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly DispatcherQueueTimer _timer;

    private SpeakerVolumeService? _speakers;
    private MicrophoneVolumeService? _microphones;

    private bool _speakersMutedBySchedule;
    private bool _microphoneMutedBySchedule;
    private bool _previousSpeakerMute;
    private bool _previousMicrophoneMute;

    private string? _activeWindowText;
    private bool _started;

    public MuteScheduleService(DispatcherQueue dispatcherQueue)
    {
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = PollInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Evaluate();
    }

    /// <summary>Raised with the active range text when the schedule starts or stops muting (null when idle).</summary>
    public event EventHandler<string?>? StateChanged;

    /// <summary>True while the schedule is the reason the speakers are muted.</summary>
    public bool IsSpeakersMutedBySchedule => _speakersMutedBySchedule;

    /// <summary>True while the schedule is the reason the microphone is muted.</summary>
    public bool IsMicrophoneMutedBySchedule => _microphoneMutedBySchedule;

    public bool IsMutedBySchedule => _speakersMutedBySchedule || _microphoneMutedBySchedule;

    /// <summary>Text of the range currently muting, or null.</summary>
    public string? ActiveWindowText => _activeWindowText;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Start();
        Evaluate();
        Debug.WriteLine("[MuteSchedule] Started");
    }

    /// <summary>Stops watching and gives the audio devices back to the user.</summary>
    public void Stop()
    {
        _timer.Stop();
        _started = false;
        ReleaseSpeakers();
        ReleaseMicrophone();
        _activeWindowText = null;
        Debug.WriteLine("[MuteSchedule] Stopped");
    }

    /// <summary>Re-reads settings and re-applies immediately (called when the schedule is edited).</summary>
    public void Refresh() => Evaluate();

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

            var wantedSpeakers = activeNow && settings.MuteSpeakersOnSchedule;
            var wantedMicrophone = activeNow && settings.MuteMicrophoneOnSchedule;

            var changed = false;

            if (wantedSpeakers) changed |= ApplySpeakerMute(text);
            else changed |= ReleaseSpeakers();

            if (wantedMicrophone) changed |= ApplyMicrophoneMute(text);
            else changed |= ReleaseMicrophone();

            if (_activeWindowText != text)
            {
                _activeWindowText = text;
                changed = true;
            }

            if (changed) StateChanged?.Invoke(this, _activeWindowText);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] Evaluate failed: {ex.Message}");
        }
    }

    // ── Speakers ──────────────────────────────────────────────────────────────

    private bool ApplySpeakerMute(string? text)
    {
        if (_speakersMutedBySchedule) return false;

        var speaker = EnsureSpeaker();
        if (speaker == null) return false;

        _previousSpeakerMute = speaker.IsMuted();
        speaker.SetMute(true);
        _speakersMutedBySchedule = true;
        Debug.WriteLine($"[MuteSchedule] Speakers muted for {text}");
        return true;
    }

    /// <summary>
    /// Restores the state from before the schedule took over. If the user unmuted manually during the range we
    /// leave their choice alone instead of forcing the old state back.
    /// </summary>
    private bool ReleaseSpeakers()
    {
        if (!_speakersMutedBySchedule) return false;

        _speakersMutedBySchedule = false;

        try
        {
            var speaker = EnsureSpeaker();
            if (speaker == null) return true;
            if (!speaker.IsMuted()) return true;

            speaker.SetMute(_previousSpeakerMute);
            Debug.WriteLine($"[MuteSchedule] Speaker mute restored to {_previousSpeakerMute}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] Speaker release failed: {ex.Message}");
        }

        return true;
    }

    private SpeakerDevice? EnsureSpeaker()
    {
        try
        {
            _speakers ??= new SpeakerVolumeService();

            var speaker = _speakers.GetSpeakers().FirstOrDefault();
            if (speaker != null) return speaker;

            // No devices known yet (e.g. the default device changed): enumerate again.
            _speakers.Initialize();
            return _speakers.GetSpeakers().FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] No speaker available: {ex.Message}");
            return null;
        }
    }

    // ── Microphone ────────────────────────────────────────────────────────────

    private bool ApplyMicrophoneMute(string? text)
    {
        if (_microphoneMutedBySchedule) return false;

        var microphone = EnsureMicrophone();
        if (microphone == null) return false;

        _previousMicrophoneMute = microphone.IsMuted();
        microphone.SetMute(true);
        _microphoneMutedBySchedule = true;
        Debug.WriteLine($"[MuteSchedule] Microphone muted for {text}");
        return true;
    }

    private bool ReleaseMicrophone()
    {
        if (!_microphoneMutedBySchedule) return false;

        _microphoneMutedBySchedule = false;

        try
        {
            var microphone = EnsureMicrophone();
            if (microphone == null) return true;
            if (!microphone.IsMuted()) return true;

            microphone.SetMute(_previousMicrophoneMute);
            Debug.WriteLine($"[MuteSchedule] Microphone mute restored to {_previousMicrophoneMute}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] Microphone release failed: {ex.Message}");
        }

        return true;
    }

    private MicrophoneDevice? EnsureMicrophone()
    {
        try
        {
            _microphones ??= new MicrophoneVolumeService();

            var microphone = _microphones.GetMicrophones().FirstOrDefault();
            if (microphone != null) return microphone;

            _microphones.Initialize();
            return _microphones.GetMicrophones().FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] No microphone available: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _speakers?.Dispose(); } catch { }
        try { _microphones?.Dispose(); } catch { }
        _speakers = null;
        _microphones = null;
    }
}
