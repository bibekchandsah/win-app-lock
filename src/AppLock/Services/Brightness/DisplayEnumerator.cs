using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AppLock.Services.Brightness;

public class DisplayEnumerator
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    public static List<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();

        try
        {
            uint deviceNum = 0;
            DISPLAY_DEVICE displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = Marshal.SizeOf(displayDevice);

            while (EnumDisplayDevices(null, deviceNum, ref displayDevice, 0))
            {
                // Skip if not attached to desktop
                if ((displayDevice.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                {
                    deviceNum++;
                    continue;
                }

                string deviceName = displayDevice.DeviceName;
                string deviceString = displayDevice.DeviceString;

                // Get monitor info for resolution
                DEVMODE devMode = new DEVMODE();
                devMode.dmSize = (ushort)Marshal.SizeOf(devMode);
                
                string resolution = "Unknown";
                if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    resolution = $"{devMode.dmPelsWidth}×{devMode.dmPelsHeight}";
                }

                var display = new DisplayInfo
                {
                    Id = deviceName,
                    Name = deviceString,
                    Resolution = resolution,
                    IsBuiltIn = false,
                    ControlMethod = "Unknown"
                };

                // Check if it's the primary display
                if ((displayDevice.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
                {
                    display.IsBuiltIn = true; // Assume primary is built-in for now
                }

                displays.Add(display);
                deviceNum++;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Display enumeration error: {ex.Message}");
        }

        return displays;
    }
}
