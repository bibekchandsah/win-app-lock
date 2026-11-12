using NAudio.CoreAudioApi;
using System;
using System.Linq;

namespace AppLock.Services.Audio
{
    /// <summary>
    /// Service for controlling microphone (input device) volume
    /// </summary>
    public class MicrophoneVolumeService : IDisposable
    {
        private MMDevice _microphoneDevice;
        private readonly MMDeviceEnumerator _deviceEnumerator;

        public MicrophoneVolumeService()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }

        /// <summary>
        /// Initialize and find the default microphone device
        /// </summary>
        public bool Initialize()
        {
            try
            {
                // Get default capture (microphone) device
                _microphoneDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                
                return _microphoneDevice != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Get current microphone volume (0-100)
        /// </summary>
        public float GetVolume()
        {
            if (_microphoneDevice == null)
                return 0;

            try
            {
                // Get volume as percentage (0.0 to 1.0)
                float volumeLevel = _microphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                return volumeLevel * 100f;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Set microphone volume (0-100)
        /// </summary>
        public void SetVolume(float volume)
        {
            if (_microphoneDevice == null)
                return;

            try
            {
                // Clamp to 0-100 range
                volume = Math.Max(0, Math.Min(100, volume));
                
                // Convert to 0.0-1.0 range
                float volumeLevel = volume / 100f;
                
                _microphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar = volumeLevel;
            }
            catch (Exception)
            {
                // Silently fail - device might be disconnected
            }
        }

        /// <summary>
        /// Get microphone mute state
        /// </summary>
        public bool IsMuted()
        {
            if (_microphoneDevice == null)
                return false;

            try
            {
                return _microphoneDevice.AudioEndpointVolume.Mute;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Set microphone mute state
        /// </summary>
        public void SetMute(bool mute)
        {
            if (_microphoneDevice == null)
                return;

            try
            {
                _microphoneDevice.AudioEndpointVolume.Mute = mute;
            }
            catch (Exception)
            {
                // Silently fail
            }
        }

        /// <summary>
        /// Get the friendly name of the microphone device
        /// </summary>
        public string GetDeviceName()
        {
            if (_microphoneDevice == null)
                return "Microphone";

            try
            {
                return _microphoneDevice.FriendlyName;
            }
            catch (Exception)
            {
                return "Microphone";
            }
        }

        public void Dispose()
        {
            _microphoneDevice?.Dispose();
            _deviceEnumerator?.Dispose();
        }
    }
}
