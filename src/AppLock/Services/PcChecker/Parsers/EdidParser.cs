using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace AppLock.Services.PcChecker.Parsers;

/// <summary>The parts of a monitor's EDID that matter to a system information page.</summary>
public sealed class EdidInfo
{
    public string ManufacturerCode { get; init; } = string.Empty;
    public string ManufacturerName { get; init; } = string.Empty;
    public int ProductCode { get; init; }
    public int SerialNumber { get; init; }
    public int WeekOfManufacture { get; init; }
    public int YearOfManufacture { get; init; }
    public byte EdidMajor { get; init; }
    public byte EdidMinor { get; init; }
    public int NativeWidth { get; init; }
    public int NativeHeight { get; init; }
    public int PhysicalWidthMm { get; init; }
    public int PhysicalHeightMm { get; init; }
    public string MonitorName { get; init; } = string.Empty;

    public double DiagonalInches
    {
        get
        {
            if (PhysicalWidthMm <= 0 || PhysicalHeightMm <= 0) return 0;
            var diagonalMm = Math.Sqrt(PhysicalWidthMm * (double)PhysicalWidthMm + PhysicalHeightMm * (double)PhysicalHeightMm);
            return diagonalMm / 25.4;
        }
    }
}

/// <summary>Reads EDID blocks from the registry where Windows stores them per monitor.</summary>
public static class EdidParser
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    private static Dictionary<string, string>? _manufacturerNames;

    /// <summary>Every EDID block Windows has stored for a monitor, keyed by the monitor's instance key.</summary>
    public static List<(string DeviceKey, byte[] Edid)> ReadAllEdids()
    {
        var result = new List<(string, byte[])>();

        try
        {
            using var displayKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (displayKey == null) return result;

            foreach (var deviceName in displayKey.GetSubKeyNames())
            {
                using var deviceKey = displayKey.OpenSubKey(deviceName);
                if (deviceKey == null) continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceName + @"\Device Parameters");
                    if (instanceKey?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                    {
                        result.Add(($@"{deviceName}\{instanceName}", edid));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] EDID registry read failed: {ex.Message}");
        }

        return result;
    }

    public static EdidInfo? Parse(byte[] edid)
    {
        if (edid == null || edid.Length < 128) return null;

        // Header 00 FF FF FF FF FF FF 00
        if (!(edid[0] == 0x00 && edid[1] == 0xFF && edid[2] == 0xFF && edid[3] == 0xFF)) return null;

        ushort manufacturerRaw = (ushort)((edid[8] << 8) | edid[9]);
        var manufacturer = new string(new[]
        {
            (char)('A' + ((manufacturerRaw >> 10) & 0x1F) - 1),
            (char)('A' + ((manufacturerRaw >> 5) & 0x1F) - 1),
            (char)('A' + (manufacturerRaw & 0x1F) - 1)
        });

        int productCode = edid[10] | (edid[11] << 8);
        int serial = edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24);

        int week = edid[16];
        int year = edid[17] + 1990;

        int nativeWidth = 0, nativeHeight = 0, physWidth = 0, physHeight = 0;
        string monitorName = string.Empty;

        // Four 18-byte descriptor blocks starting at 54.
        for (int i = 0; i < 4; i++)
        {
            int offset = 54 + i * 18;
            if (offset + 18 > edid.Length) break;

            bool isMonitorDescriptor = edid[offset] == 0 && edid[offset + 1] == 0;

            if (!isMonitorDescriptor)
            {
                // Detailed timing descriptor: pixels and millimetres live here.
                if (nativeWidth == 0)
                {
                    nativeWidth = edid[offset + 2] | ((edid[offset + 4] >> 4) << 8);
                    nativeHeight = edid[offset + 5] | ((edid[offset + 7] >> 4) << 8);
                    physWidth = edid[offset + 12] | ((edid[offset + 14] >> 4) << 8);
                    physHeight = edid[offset + 13] | ((edid[offset + 14] & 0x0F) << 8);
                }
                continue;
            }

            byte tag = edid[offset + 3];
            if (tag == 0xFC)
            {
                monitorName = ReadDescriptorText(edid, offset).Trim();
            }
        }

        // Range limits descriptor (0xFD) carries no size; some panels only report size there in 0.1 cm.

        return new EdidInfo
        {
            ManufacturerCode = manufacturer,
            ManufacturerName = ResolveManufacturer(manufacturer),
            ProductCode = productCode,
            SerialNumber = serial,
            WeekOfManufacture = week,
            YearOfManufacture = year,
            EdidMajor = edid[18],
            EdidMinor = edid[19],
            NativeWidth = nativeWidth,
            NativeHeight = nativeHeight,
            PhysicalWidthMm = physWidth,
            PhysicalHeightMm = physHeight,
            MonitorName = monitorName
        };
    }

    private static string ReadDescriptorText(byte[] edid, int offset)
    {
        var chars = new List<char>();
        for (int i = 5; i < 18; i++)
        {
            byte value = edid[offset + i];
            if (value == 0x0A || value == 0x00) break;
            chars.Add((char)value);
        }
        return new string(chars.ToArray());
    }

    private static string ResolveManufacturer(string code)
    {
        _manufacturerNames ??= BuildManufacturerTable();
        return _manufacturerNames.TryGetValue(code, out var name) ? name : code;
    }

    private static Dictionary<string, string> BuildManufacturerTable() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACR"] = "Acer",
        ["ALI"] = "Alienware",
        ["APP"] = "Apple",
        ["AUO"] = "AU Optronics",
        ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ",
        ["BOE"] = "BOE",
        ["CMN"] = "Chi Mei / Innolux",
        ["DEL"] = "Dell",
        ["DPC"] = "Delta",
        ["ELE"] = "Element",
        ["ENC"] = "Eizo",
        ["GSM"] = "LG",
        ["HWP"] = "HP",
        ["HPN"] = "HP",
        ["HSP"] = "Hanns.G",
        ["IVM"] = "Iiyama",
        ["LEN"] = "Lenovo",
        ["LGD"] = "LG Display",
        ["MSI"] = "MSI",
        ["PHL"] = "Philips",
        ["SAM"] = "Samsung",
        ["SDC"] = "Samsung Display",
        ["SEC"] = "Samsung",
        ["SNY"] = "Sony",
        ["VSC"] = "ViewSonic",
        ["AOC"] = "AOC",
        ["GBT"] = "Gigabyte",
        ["MED"] = "Medion",
        ["NEC"] = "NEC"
    };

    /// <summary>True when the EDID looks like a real panel rather than a placeholder.</summary>
    public static bool LooksValid(byte[] edid) => Parse(edid) is { NativeWidth: > 0, NativeHeight: > 0 };
}
