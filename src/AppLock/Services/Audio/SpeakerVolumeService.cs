using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AppLock.Services.Audio
{
    /// <summary>One playback (speaker) device with its volume control interface.</summary>
    public class SpeakerDevice
    {
        public string DeviceId { get; set; }
        public string FriendlyName { get; set; }
        public MMDevice Device { get; set; }
        public bool IsDefault { get; set; }

        public float GetVolume()
        {
            try { return Device?.AudioEndpointVolume.MasterVolumeLevelScalar * 100f ?? 0f; }
            catch { return 0f; }
        }

        public void SetVolume(float volume)
        {
            try
            {
                if (Device != null)
                {
                    volume = Math.Max(0, Math.Min(100, volume));
                    Device.AudioEndpointVolume.MasterVolumeLevelScalar = volume / 100f;
                }
            }
            catch { }
        }

        public bool IsMuted()
        {
            try { return Device?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }

        public void SetMute(bool mute)
        {
            try
            {
                if (Device != null) Device.AudioEndpointVolume.Mute = mute;
            }
            catch { }
        }
    }

    /// <summary>
    /// Enumerates all active playback devices and keeps track of which one is the Windows default,
    /// so the overlay can show one row per speaker and let the user switch between them.
    /// </summary>
    public class SpeakerVolumeService : IDisposable
    {
        private readonly MMDeviceEnumerator _deviceEnumerator;
        private Dictionary<string, SpeakerDevice> _speakers = new();
        private string? _defaultDeviceId;

        public SpeakerVolumeService()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }

        public bool Initialize()
        {
            var (map, defaultId) = EnumerateDevices();
            AdoptDevices(map, defaultId);
            return _speakers.Count > 0;
        }

        /// <summary>Re-enumerates; true when devices or the default changed and the UI must rebuild.</summary>
        public bool RefreshDevices()
        {
            var (map, defaultId) = EnumerateDevices();
            if (Fingerprint(map, defaultId) == Fingerprint(_speakers, _defaultDeviceId))
            {
                DisposeDevices(map);
                return false;
            }

            AdoptDevices(map, defaultId);
            return true;
        }

        /// <summary>Makes the given playback device the Windows default for every role.</summary>
        public bool SetDefaultSpeaker(string deviceId)
        {
            bool ok = PolicyConfig.SetDefaultEndpoint(deviceId,
                PolicyConfig.RoleConsole, PolicyConfig.RoleMultimedia, PolicyConfig.RoleCommunications);

            if (ok) RefreshDevices();
            return ok;
        }

        public IReadOnlyList<SpeakerDevice> GetSpeakers()
        {
            return _speakers.Values
                .OrderByDescending(s => s.IsDefault)
                .ThenBy(s => s.FriendlyName)
                .ToList();
        }

        public SpeakerDevice? GetSpeaker(string deviceId)
        {
            return _speakers.TryGetValue(deviceId, out var speaker) ? speaker : null;
        }

        public void Refresh() => Initialize();

        // ── Internals ─────────────────────────────────────────────────────────────

        private (Dictionary<string, SpeakerDevice> Map, string? DefaultId) EnumerateDevices()
        {
            var map = new Dictionary<string, SpeakerDevice>();
            string? defaultId = null;

            try
            {
                try
                {
                    var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = defaultDevice?.ID;
                    defaultDevice?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SpeakerVolume] No default playback device: {ex.Message}");
                }

                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    try
                    {
                        map[device.ID] = new SpeakerDevice
                        {
                            DeviceId = device.ID,
                            FriendlyName = device.FriendlyName,
                            Device = device,
                            IsDefault = device.ID == defaultId
                        };
                    }
                    catch
                    {
                        try { device.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SpeakerVolume] Device enumeration error: {ex.Message}");
            }

            return (map, defaultId);
        }

        private void AdoptDevices(Dictionary<string, SpeakerDevice> map, string? defaultId)
        {
            DisposeDevices(_speakers);
            _speakers = map;
            _defaultDeviceId = defaultId;
        }

        private static void DisposeDevices(Dictionary<string, SpeakerDevice> map)
        {
            foreach (var speaker in map.Values)
            {
                try { speaker.Device?.Dispose(); } catch { }
            }
            map.Clear();
        }

        private static string Fingerprint(Dictionary<string, SpeakerDevice> map, string? defaultId)
        {
            var parts = map.Values
                .OrderBy(s => s.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(s => $"{s.DeviceId}|{s.FriendlyName}|{(s.IsDefault ? 1 : 0)}");
            return (defaultId ?? string.Empty) + "::" + string.Join(";", parts);
        }

        public void Dispose()
        {
            DisposeDevices(_speakers);
            _deviceEnumerator?.Dispose();
        }
    }
}
