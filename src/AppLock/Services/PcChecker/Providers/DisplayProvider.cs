using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using AppLock.Services.PcChecker.Parsers;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Displays: each monitor's name, current and native mode, refresh rate, physical size, pixel
/// density, scaling, position and primary state, read from the display configuration and EDID.
/// </summary>
public sealed class DisplayProvider : IInfoProvider
{
    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceAttachedToDesktop = 0x1;
    private const int DisplayDevicePrimaryDevice = 0x4;

    public string Id => "display";
    public string Category => "displays";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(5);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Displays", Category = Category };
        var group = section.Group("Monitors");
        const string source = "Source: EnumDisplayDevices / EnumDisplaySettings";

        var edids = EdidParser.ReadAllEdids();
        var scaling = GetSystemScalingPercent();

        int monitorIndex = 0;
        for (int adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, (uint)adapterIndex, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

            for (int monitor = 0; ; monitor++)
            {
                var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(adapter.DeviceName, (uint)monitor, ref device, 0)) break;
                if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

                var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
                bool hasMode = EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref mode);

                var edid = MatchEdid(device.DeviceID, edids);
                var suffix = monitorIndex.ToString();

                var name = !string.IsNullOrWhiteSpace(edid?.MonitorName)
                    ? edid!.MonitorName
                    : !string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceString : adapter.DeviceString;

                group.Items.Add(InfoItem.Of($"display.{suffix}.name", monitorIndex == 0 ? "Monitor" : $"Monitor {monitorIndex + 1}",
                    name, DetailLevel.Basic, "The monitor Windows sees on this display output.", source));

                if (hasMode)
                {
                    group.Items.Add(InfoItem.Of($"display.{suffix}.mode", "Current resolution",
                        $"{mode.dmPelsWidth} × {mode.dmPelsHeight}", DetailLevel.Basic,
                        "The resolution this monitor is running at right now.", source));
                    group.Items.Add(InfoItem.Of($"display.{suffix}.refresh", "Refresh rate",
                        $"{mode.dmDisplayFrequency} Hz", DetailLevel.Basic,
                        "How many times per second the screen updates. Higher feels smoother.", source));
                    if (mode.dmBitsPerPel > 0)
                    {
                        group.Items.Add(InfoItem.Of($"display.{suffix}.depth", "Colour depth",
                            $"{mode.dmBitsPerPel}-bit", DetailLevel.Detailed, null, source));
                    }

                    var position = $"{mode.dmPositionX}, {mode.dmPositionY}";
                    group.Items.Add(InfoItem.Of($"display.{suffix}.position", "Position", position, DetailLevel.Detailed,
                        "Where this monitor sits in the multi-monitor layout, in pixels.", source));
                }

                if (edid != null)
                {
                    if (edid.NativeWidth > 0 && edid.NativeHeight > 0)
                    {
                        group.Items.Add(InfoItem.Of($"display.{suffix}.native", "Native resolution",
                            $"{edid.NativeWidth} × {edid.NativeHeight}", DetailLevel.Basic,
                            "The resolution the panel is designed for. Anything else is scaled.", "Source: EDID"));
                    }

                    if (edid.DiagonalInches > 0)
                    {
                        group.Items.Add(InfoItem.Of($"display.{suffix}.size", "Screen size",
                            $"{edid.DiagonalInches:0.0}″ ({edid.PhysicalWidthMm / 10.0:0} × {edid.PhysicalHeightMm / 10.0:0} cm)",
                            DetailLevel.Detailed, "The physical size of the panel.", "Source: EDID"));
                    }

                    var pixels = edid.NativeWidth > 0 ? edid.NativeWidth : hasMode ? (int)mode.dmPelsWidth : 0;
                    var pixelsY = edid.NativeHeight > 0 ? edid.NativeHeight : hasMode ? (int)mode.dmPelsHeight : 0;
                    if (edid.DiagonalInches > 0 && pixels > 0 && pixelsY > 0)
                    {
                        var diagonalPixels = Math.Sqrt(pixels * (double)pixels + pixelsY * (double)pixelsY);
                        var ppi = diagonalPixels / edid.DiagonalInches;
                        group.Items.Add(InfoItem.Of($"display.{suffix}.ppi", "Pixel density", $"{ppi:0} PPI", DetailLevel.Detailed,
                            "Pixels per inch. Higher means sharper text at the same size.", "Computed from EDID and resolution"));
                    }

                    group.Items.Add(InfoItem.Of($"display.{suffix}.manufacturer", "Manufacturer",
                        $"{edid.ManufacturerName} ({edid.ManufacturerCode})", DetailLevel.Detailed,
                        "Who made the panel.", "Source: EDID"));
                    group.Items.Add(InfoItem.Of($"display.{suffix}.edid", "EDID version",
                        $"{edid.EdidMajor}.{edid.EdidMinor}", DetailLevel.Everything, null, "Source: EDID"));

                    if (edid.YearOfManufacture > 1990)
                    {
                        var week = edid.WeekOfManufacture is > 0 and < 54 ? $" week {edid.WeekOfManufacture}" : string.Empty;
                        group.Items.Add(InfoItem.Of($"display.{suffix}.manufactured", "Manufactured",
                            $"{edid.YearOfManufacture}{week}", DetailLevel.Everything, null, "Source: EDID"));
                    }

                    group.Items.Add(InfoItem.Of($"display.{suffix}.serial", "Serial", edid.SerialNumber == 0
                            ? null
                            : edid.SerialNumber.ToString(), DetailLevel.Everything, null, "Source: EDID",
                        sensitivity: InfoSensitivity.Identifier));
                }
                else
                {
                    group.Items.Add(InfoItem.MissingValue($"display.{suffix}.edid", "EDID details",
                        MissingReason.NotReported, DetailLevel.Detailed,
                        "The monitor's EDID block is not stored in the registry for this display."));
                }

                group.Items.Add(InfoItem.Of($"display.{suffix}.primary",
                    "Primary display", (device.StateFlags & DisplayDevicePrimaryDevice) != 0 ? "Yes" : "No", DetailLevel.Detailed,
                    "The primary display holds the taskbar and most apps open on it.", source));

                group.Items.Add(InfoItem.Of($"display.{suffix}.scaling", "Scaling", $"{scaling}%", DetailLevel.Detailed,
                    "How much Windows enlarges text and apps on this display.", "Source: GetDpiForSystem"));

                group.Items.Add(InfoItem.Of($"display.{suffix}.connection", "Connection type", null, DetailLevel.Detailed,
                    "The DisplayPort / HDMI / eDP output in use."));
                group.Items.Last().Missing = MissingReason.NotReported;

                monitorIndex++;
                ct.ThrowIfCancellationRequested();
            }
        }

        if (monitorIndex == 0)
        {
            group.Items.Add(InfoItem.MissingValue("display.none", "Monitor", MissingReason.NotReported, DetailLevel.Basic, source));
        }

        progress?.Report(1);
        return section;
    }

    private static EdidInfo? MatchEdid(string deviceId, List<(string DeviceKey, byte[] Edid)> edids)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        // DeviceID looks like MONITOR\LEN40A9\{4d36e96e-...}\00040A01
        var instance = deviceId.Split('\\').LastOrDefault();
        foreach (var (deviceKey, raw) in edids)
        {
            if (instance != null && deviceKey.Contains(instance, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = EdidParser.Parse(raw);
                if (parsed != null) return parsed;
            }
        }

        // Fall back to the first valid EDID so laptops with one panel still show their details.
        foreach (var (_, raw) in edids)
        {
            var parsed = EdidParser.Parse(raw);
            if (parsed is { NativeWidth: > 0 }) return parsed;
        }

        return null;
    }

    private static int GetSystemScalingPercent()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            return dpi == 0 ? 100 : (int)Math.Round(dpi / 96.0 * 100);
        }
        catch
        {
            return 100;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }
}
