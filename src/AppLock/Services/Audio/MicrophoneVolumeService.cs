using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;

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
        private readonly Dictionary<string, MicrophoneDevice> _microphones = new();

        public MicrophoneVolumeService()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }

        /// <summary>
        /// Initialize and enumerate all connected microphone devices
        /// </summary>
        public bool Initialize()
        {
            try
            {
                _microphones.Clear();
                
                // Get all active capture devices
                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                
                if (devices == null || devices.Count == 0)
                    return false;

                // Get default device ID for marking
                string defaultDeviceId = null;
                try
                {
                    var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    defaultDeviceId = defaultDevice?.ID;
                }
                catch { }

                foreach (var device in devices)
                {
                    try
                    {
                        var micDevice = new MicrophoneDevice
                        {
                            DeviceId = device.ID,
                            FriendlyName = device.FriendlyName,
                            Device = device,
                            IsDefault = device.ID == defaultDeviceId
                        };
                        
                        _microphones[device.ID] = micDevice;
                    }
                    catch
                    {
                        // Skip devices we can't access
                    }
                }
                
                return _microphones.Count > 0;
            }
            catch (Exception)
            {
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

        public void Dispose()
        {
            foreach (var mic in _microphones.Values)
            {
                try
                {
                    mic.Device?.Dispose();
                }
                catch { }
            }
            _microphones.Clear();
            _deviceEnumerator?.Dispose();
        }
    }
}
