using System;
using System.Linq;
using System.Diagnostics;
using AppLock.Models;
using AppLock.Services.Audio;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Applies the auto-mute schedule to the Windows default playback device. It is a watcher, not a driver:
/// every 15 seconds it checks whether the current time is inside one of the ranges and only changes the
/// device when it has to, remembering the mute state the user had before so leaving a range restores it.
/// </summary>
public sealed class MuteScheduleService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly DispatcherQueueTimer _timer;
    private SpeakerVolumeService? _speakers;
    private bool _mutedBySchedule;
    private bool _previousMuteState;
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

    /// <summary>True while the schedule is the reason the device is muted.</summary>
    public bool IsMutedBySchedule => _mutedBySchedule;

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

    /// <summary>Stops watching and gives the audio back to the user.</summary>
    public void Stop()
    {
        _timer.Stop();
        _started = false;
        ReleaseMute();
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

            if (activeNow)
            {
                var text = active == null ? null : MuteSchedule.Describe(active);

                if (!_mutedBySchedule)
                {
                    var speaker = EnsureSpeaker();
                    if (speaker == null) return;

                    _previousMuteState = speaker.IsMuted();
                    speaker.SetMute(true);
                    _mutedBySchedule = true;
                    _activeWindowText = text;
                    Debug.WriteLine($"[MuteSchedule] Muted for {text}");
                    StateChanged?.Invoke(this, _activeWindowText);
                }
                else if (_activeWindowText != text)
                {
                    _activeWindowText = text;
                    StateChanged?.Invoke(this, _activeWindowText);
                }

                return;
            }

            if (_mutedBySchedule)
            {
                ReleaseMute();
                StateChanged?.Invoke(this, null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] Evaluate failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the mute state from before the schedule took over. If the user unmuted manually during the
    /// range we leave their choice alone instead of forcing the old state back.
    /// </summary>
    private void ReleaseMute()
    {
        try
        {
            if (!_mutedBySchedule) return;

            var speaker = EnsureSpeaker();
            _mutedBySchedule = false;
            _activeWindowText = null;

            if (speaker == null) return;
            if (!speaker.IsMuted()) return;

            speaker.SetMute(_previousMuteState);
            Debug.WriteLine($"[MuteSchedule] Restored mute state to {_previousMuteState}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuteSchedule] Release failed: {ex.Message}");
            _mutedBySchedule = false;
            _activeWindowText = null;
        }
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

    public void Dispose()
    {
        _timer.Stop();
        try { _speakers?.Dispose(); } catch { }
        _speakers = null;
    }
}
