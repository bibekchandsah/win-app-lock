using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AppLock.Services.Audio;
using NAudio.CoreAudioApi;

namespace AppLock.WindowIsland.Modules;

/// <summary>One playback device as the Home volume bar shows it.</summary>
public sealed record IslandAudioDevice(string Id, string Name, bool IsDefault);

/// <summary>One app audio session, reduced to what the Home volume bar needs.</summary>
public sealed record IslandAudioSession(
    string SessionId,
    int ProcessId,
    string ProcessName,
    int Volume,
    bool Muted,
    float Peak);

/// <summary>
/// Playback devices and per-app audio sessions for the Window Island volume bar.
///
/// Enumerating endpoints and reading each FriendlyName is by far the most expensive part of the
/// audio API (a COM property-store round trip per device, which some drivers answer slowly), so
/// the device list is scanned only every <see cref="DeviceScanIntervalMs"/> or on request. The
/// once-a-second tick then just reads the level and mute flag from the already-open default
/// endpoint, which is cheap.
/// </summary>
public sealed class WindowIslandAudio : IDisposable
{
    private const int DeviceScanIntervalMs = 30_000;

    /// <summary>Mixer rule for "this session is making sound".</summary>
    private const float AudiblePeak = 0.0005f;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _defaultDevice;
    private AudioSessionManager? _sessionManager;
    private List<IslandAudioDevice> _devices = new();
    private string? _defaultId;
    private int _lastScanTick;
    private bool _scanned;

    public IReadOnlyList<IslandAudioDevice> Devices => _devices;
    public bool HasDevices => _devices.Count > 0;
    public string? DefaultDeviceId => _defaultId;

    public string DefaultDeviceName =>
        _devices.FirstOrDefault(d => d.IsDefault)?.Name
        ?? _devices.FirstOrDefault()?.Name
        ?? "No output device";

    private MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();

    /// <summary>
    /// Brings the cached default endpoint up to date. The device list (names, default flag) is
    /// only re-read every <see cref="DeviceScanIntervalMs"/>; pass <paramref name="forceDeviceScan"/>
    /// when the picker opens or after the default device was changed here.
    /// </summary>
    public void Refresh(bool forceDeviceScan = false)
    {
        try
        {
            if (forceDeviceScan || !_scanned || Environment.TickCount - _lastScanTick >= DeviceScanIntervalMs)
            {
                ScanDevices();
                return;
            }

            EnsureDefaultDevice();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IslandVolume] Refresh error: {ex.Message}");
        }
    }

    /// <summary>The expensive pass: one property-store read per endpoint, plus the default lookup.</summary>
    private void ScanDevices()
    {
        _scanned = true;
        _lastScanTick = Environment.TickCount;

        string? defaultId = QueryDefaultId();

        var devices = new List<IslandAudioDevice>();
        try
        {
            foreach (var device in Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    devices.Add(new IslandAudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                }
                catch { }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IslandVolume] Device enumeration error: {ex.Message}");
        }

        _devices = devices;

        bool defaultChanged = !string.Equals(defaultId, _defaultId, StringComparison.Ordinal);
        _defaultId = defaultId;
        if (defaultChanged || _defaultDevice == null) OpenDefaultDevice();
    }

    /// <summary>Cheap check for a default-device switch made outside the island.</summary>
    private void EnsureDefaultDevice()
    {
        string? defaultId = QueryDefaultId();
        if (string.Equals(defaultId, _defaultId, StringComparison.Ordinal) && _defaultDevice != null) return;

        _defaultId = defaultId;
        OpenDefaultDevice();

        // The list is stale now: reflect the new default in the cached names/flags without a scan.
        if (_devices.Count > 0)
        {
            _devices = _devices.Select(d => d with { IsDefault = d.Id == defaultId }).ToList();
        }

        _scanned = false;
    }

    private string? QueryDefaultId()
    {
        try
        {
            var device = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string? id = device?.ID;
            device?.Dispose();
            return id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens (or re-opens) the default endpoint and its session manager.</summary>
    private void OpenDefaultDevice()
    {
        try { _sessionManager?.Dispose(); } catch { }
        _sessionManager = null;

        try { _defaultDevice?.Dispose(); } catch { }
        _defaultDevice = null;

        if (_defaultId == null) return;

        try
        {
            _defaultDevice = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _sessionManager = _defaultDevice?.AudioSessionManager;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IslandVolume] Default device open failed: {ex.Message}");
        }
    }

    public int GetDeviceVolume()
    {
        try
        {
            return _defaultDevice == null
                ? -1
                : Math.Clamp((int)Math.Round(_defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100), 0, 100);
        }
        catch { return -1; }
    }

    public bool IsDeviceMuted()
    {
        try { return _defaultDevice?.AudioEndpointVolume.Mute ?? false; }
        catch { return false; }
    }

    public void SetDeviceVolume(int percent)
    {
        try
        {
            if (_defaultDevice != null)
            {
                _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
            }
        }
        catch { }
    }

    public bool ToggleDeviceMute()
    {
        try
        {
            if (_defaultDevice == null) return false;
            bool mute = !_defaultDevice.AudioEndpointVolume.Mute;
            _defaultDevice.AudioEndpointVolume.Mute = mute;
            return mute;
        }
        catch { return false; }
    }

    /// <summary>Makes the device the Windows default for every role (same call the mixer uses).</summary>
    public bool SetDefaultDevice(string deviceId)
    {
        bool ok = PolicyConfig.SetDefaultEndpoint(
            deviceId, PolicyConfig.RoleConsole, PolicyConfig.RoleMultimedia, PolicyConfig.RoleCommunications);
        if (ok) Refresh(forceDeviceScan: true);
        return ok;
    }

    /// <summary>
    /// The app session currently producing audio: the named process when it has a session,
    /// otherwise the loudest one. Null when no app has an audio session at all.
    /// </summary>
    public IslandAudioSession? FindPlayingSession(string? preferredProcess = null)
    {
        var sessions = ReadSessions();
        if (sessions.Count == 0) return null;

        var choice = sessions[0];
        foreach (var session in sessions)
        {
            if (session.Peak > choice.Peak) choice = session;
        }

        if (!string.IsNullOrEmpty(preferredProcess))
        {
            foreach (var session in sessions)
            {
                if (string.Equals(ProcessNameFor(session.ProcessId), preferredProcess, StringComparison.OrdinalIgnoreCase))
                {
                    choice = session;
                    break;
                }
            }
        }

        return new IslandAudioSession(
            choice.SessionId, choice.ProcessId, ProcessNameFor(choice.ProcessId),
            choice.Volume, choice.Muted, choice.Peak);
    }

    private readonly record struct RawSession(
        string SessionId, int ProcessId, int Volume, bool Muted, float Peak);

    public IslandAudioSession? FindSession(string sessionId)
    {
        foreach (var session in ReadSessions())
        {
            if (session.SessionId == sessionId)
            {
                return new IslandAudioSession(
                    session.SessionId, session.ProcessId, string.Empty,
                    session.Volume, session.Muted, session.Peak);
            }
        }
        return null;
    }

    public bool IsAudible(IslandAudioSession? session) => session != null && session.Peak > AudiblePeak;

    public void SetSessionVolume(string sessionId, int percent)
        => ApplyToSession(sessionId, s => s.SimpleAudioVolume.Volume = Math.Clamp(percent, 0, 100) / 100f);

    public void SetSessionMute(string sessionId, bool mute)
        => ApplyToSession(sessionId, s => s.SimpleAudioVolume.Mute = mute);

    /// <summary>Sessions of the default endpoint; cheap enough for the 1 Hz tick.</summary>
    private List<RawSession> ReadSessions()
    {
        var list = new List<RawSession>();
        try
        {
            var manager = _sessionManager;
            if (manager == null) return list;

            try { manager.RefreshSessions(); } catch { }

            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    int pid = (int)session.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId) continue;
                    if ((int)session.State == 2) continue; // expired

                    float peak = 0f;
                    try { peak = session.AudioMeterInformation?.MasterPeakValue ?? 0f; } catch { }

                    list.Add(new RawSession(
                        session.GetSessionIdentifier,
                        pid,
                        Math.Clamp((int)Math.Round(session.SimpleAudioVolume.Volume * 100), 0, 100),
                        session.SimpleAudioVolume.Mute,
                        peak));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IslandVolume] Read sessions error: {ex.Message}");
        }
        return list;
    }

    /// <summary>Process name looked up once for the session the bar is showing, not for every one.</summary>
    private static string ProcessNameFor(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch { return string.Empty; }
    }

    private void ApplyToSession(string sessionId, Action<AudioSessionControl> apply)
    {
        try
        {
            var manager = _sessionManager;
            if (manager == null) return;

            try { manager.RefreshSessions(); } catch { }

            var sessions = manager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.GetSessionIdentifier == sessionId) apply(session);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IslandVolume] Session update error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _sessionManager?.Dispose(); } catch { }
        _sessionManager = null;

        try { _defaultDevice?.Dispose(); } catch { }
        _defaultDevice = null;

        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }
}
