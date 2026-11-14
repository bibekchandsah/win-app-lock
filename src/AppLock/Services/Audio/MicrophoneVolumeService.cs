using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace AppLock.Services.Audio
{
    /// <summary>
    /// Represents a single microphone device with its control interface
    /// </summary>
    public class MicrophoneDevice
    {
        public string DeviceId { get; set; }
        public string FriendlyName { get; set; }
        public MMDevice Device { get; set; }

        public float GetVolume()
        {
            try
            {
                return Device?.AudioEndpointVolume.MasterVolumeLevelScalar * 100f ?? 0f;
            }
            catch
            {
                return 0f;
            }
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
            catch
            {
                // Silently fail
            }
        }

        public bool IsMuted()
        {
            try
            {
                return Device?.AudioEndpointVolume.Mute ?? false;
            }
            catch
            {
                return false;
            }
        }

        public void SetMute(bool mute)
        {
            try
            {
                if (Device != null)
                {
                    Device.AudioEndpointVolume.Mute = mute;
                }
            }
            catch
            {
                // Silently fail
            }
        }

        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Service for controlling all connected microphone (input device) volumes
    /// </summary>
    public class MicrophoneVolumeService : IDisposable
    {
        private readonly MMDeviceEnumerator _deviceEnumerator;
        private Dictionary<string, MicrophoneDevice> _microphones = new();
        private string? _defaultDeviceId;

        public MicrophoneVolumeService()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }

        /// <summary>
        /// Initialize and enumerate all connected microphone devices
        /// </summary>
        public bool Initialize()
        {
            var (map, defaultId) = EnumerateDevices();
            AdoptDevices(map, defaultId);
            return _microphones.Count > 0;
        }

        /// <summary>
        /// Re-enumerates the devices and reports whether anything the UI shows changed:
        /// a device appeared or disappeared, a name changed, or the default device moved.
        /// Volume and mute changes are read separately and do not need a rebuild.
        /// </summary>
        public bool RefreshDevices()
        {
            var (map, defaultId) = EnumerateDevices();
            if (Fingerprint(map, defaultId) == Fingerprint(_microphones, _defaultDeviceId))
            {
                DisposeDevices(map);
                return false;
            }

            AdoptDevices(map, defaultId);
            return true;
        }

        /// <summary>
        /// Makes the given input device the default for every role (what "Set as default device"
        /// does in Windows sound settings). Returns true when the change was applied.
        /// </summary>
        public bool SetDefaultMicrophone(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            try
            {
                var client = (IPolicyConfig)new CPolicyConfigClient();
                try
                {
                    bool ok = false;
                    foreach (int role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                    {
                        try
                        {
                            int hr = client.SetDefaultEndpoint(deviceId, role);
                            if (hr == 0 && role == ERole.Communications) ok = true;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Microphone] SetDefaultEndpoint(role {role}) failed: {ex.Message}");
                        }
                    }

                    if (ok) RefreshDevices();
                    return ok;
                }
                finally
                {
                    Marshal.ReleaseComObject(client);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Microphone] Set default microphone error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get all connected microphone devices
        /// </summary>
        public IReadOnlyList<MicrophoneDevice> GetMicrophones()
        {
            // Return sorted: default first, then alphabetically
            return _microphones.Values
                .OrderByDescending(m => m.IsDefault)
                .ThenBy(m => m.FriendlyName)
                .ToList();
        }

        /// <summary>
        /// Get a specific microphone by device ID
        /// </summary>
        public MicrophoneDevice GetMicrophone(string deviceId)
        {
            return _microphones.TryGetValue(deviceId, out var mic) ? mic : null;
        }

        /// <summary>
        /// Refresh device list - call when devices might have changed
        /// </summary>
        public void Refresh()
        {
            Initialize();
        }

        // ── Internals ─────────────────────────────────────────────────────────────

        private (Dictionary<string, MicrophoneDevice> Map, string? DefaultId) EnumerateDevices()
        {
            var map = new Dictionary<string, MicrophoneDevice>();
            string? defaultId = null;

            try
            {
                try
                {
                    var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
                    defaultId = defaultDevice?.ID;
                    defaultDevice?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Microphone] No default capture device: {ex.Message}");
                }

                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in devices)
                {
                    try
                    {
                        map[device.ID] = new MicrophoneDevice
                        {
                            DeviceId = device.ID,
                            FriendlyName = device.FriendlyName,
                            Device = device,
                            IsDefault = device.ID == defaultId
                        };
                    }
                    catch
                    {
                        // Skip devices we can't access
                        try { device.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Microphone] Device enumeration error: {ex.Message}");
            }

            return (map, defaultId);
        }

        private void AdoptDevices(Dictionary<string, MicrophoneDevice> map, string? defaultId)
        {
            DisposeDevices(_microphones);
            _microphones = map;
            _defaultDeviceId = defaultId;
        }

        private static void DisposeDevices(Dictionary<string, MicrophoneDevice> map)
        {
            foreach (var mic in map.Values)
            {
                try { mic.Device?.Dispose(); } catch { }
            }
            map.Clear();
        }

        private static string Fingerprint(Dictionary<string, MicrophoneDevice> map, string? defaultId)
        {
            var parts = map.Values
                .OrderBy(m => m.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{m.DeviceId}|{m.FriendlyName}|{(m.IsDefault ? 1 : 0)}");
            return (defaultId ?? string.Empty) + "::" + string.Join(";", parts);
        }

        public void Dispose()
        {
            DisposeDevices(_microphones);
            _deviceEnumerator?.Dispose();
        }

        // ── Default device switching (undocumented but stable PolicyConfig COM API) ─

        /// <summary>EDataFlow role values used by IPolicyConfig.SetDefaultEndpoint.</summary>
        private static class ERole
        {
            public const int Console = 0;
            public const int Multimedia = 1;
            public const int Communications = 2;
        }

        [ComImport]
        [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient
        {
        }

        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
            int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
            int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
            int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
            int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
            int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
            int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
            int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr key, IntPtr pv);
            int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr key, IntPtr pv);
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
            int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
        }
    }
}
