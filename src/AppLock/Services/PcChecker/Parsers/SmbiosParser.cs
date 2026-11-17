using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AppLock.Services.PcChecker.Parsers;

/// <summary>One SMBIOS structure: its formatted area plus the string table that follows it.</summary>
public sealed class SmbiosStructure
{
    /// <summary>The raw structure, including the 4-byte header, so spec offsets can be used directly.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    public IReadOnlyList<string> Strings { get; init; } = Array.Empty<string>();

    public byte Type => ByteAt(0);
    public byte Length => ByteAt(1);
    public ushort Handle => WordAt(2);

    public byte ByteAt(int offset) => offset >= 0 && offset < Data.Length ? Data[offset] : (byte)0;

    public ushort WordAt(int offset)
        => offset >= 0 && offset + 1 < Data.Length ? (ushort)(Data[offset] | (Data[offset + 1] << 8)) : (ushort)0;

    public uint DwordAt(int offset)
        => offset >= 0 && offset + 3 < Data.Length
            ? (uint)(Data[offset] | (Data[offset + 1] << 8) | (Data[offset + 2] << 16) | (Data[offset + 3] << 24))
            : 0u;

    /// <summary>1-based string reference, as SMBIOS stores it.</summary>
    public string String(int index)
        => index >= 1 && index <= Strings.Count ? Strings[index - 1] : string.Empty;

    /// <summary>A string reference stored at the given offset.</summary>
    public string StringAt(int offset) => String(ByteAt(offset));

    public byte[] BytesAt(int offset, int count)
    {
        if (offset < 0 || offset >= Data.Length) return Array.Empty<byte>();
        count = Math.Min(count, Data.Length - offset);
        var result = new byte[count];
        Array.Copy(Data, offset, result, 0, count);
        return result;
    }
}

/// <summary>The whole SMBIOS table. The format version comes from <see cref="SmbiosParser.ReadVersion"/>.</summary>
public sealed class SmbiosTable
{
    public IReadOnlyList<SmbiosStructure> Structures { get; init; } = Array.Empty<SmbiosStructure>();

    public IEnumerable<SmbiosStructure> OfType(byte type)
    {
        foreach (var structure in Structures)
        {
            if (structure.Type == type) yield return structure;
        }
    }

    public SmbiosStructure? First(byte type)
    {
        foreach (var structure in Structures)
        {
            if (structure.Type == type) return structure;
        }
        return null;
    }
}

/// <summary>Reads and parses the SMBIOS firmware table Windows exposes through GetSystemFirmwareTable.</summary>
public static class SmbiosParser
{
    private const uint RsmbSignature = 0x52534D42; // 'RSMB' little-endian

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetSystemFirmwareTable")]
    private static extern uint GetSystemFirmwareTableNative(uint firmwareTableProviderSignature, uint firmwareTableId, byte[]? buffer, uint bufferSize);

    private static SmbiosTable? _cached;

    /// <summary>Parsed table for this PC, cached for the life of the process (the firmware never changes).</summary>
    public static SmbiosTable? GetTable() => _cached ??= TryRead();

    public static SmbiosTable? TryRead()
    {
        try
        {
            var raw = ReadRaw();
            return raw == null ? null : Parse(raw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] SMBIOS read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The raw firmware table without the 8-byte provider header, or null when unavailable.</summary>
    public static byte[]? ReadRaw()
    {
        try
        {
            uint size = GetSystemFirmwareTableNative(RsmbSignature, 0, null, 0);
            if (size == 0) return null;

            var buffer = new byte[size];
            uint written = GetSystemFirmwareTableNative(RsmbSignature, 0, buffer, size);
            if (written == 0) return null;

            // Header: calling method (1), major (1), minor (1), DMI revision (1), length (4).
            if (buffer.Length <= 8) return null;

            int tableLength = BitConverter.ToInt32(buffer, 4);
            if (tableLength <= 0) return null;

            int available = Math.Min(tableLength, buffer.Length - 8);
            var table = new byte[available];
            Array.Copy(buffer, 8, table, 0, available);
            return table;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] GetSystemFirmwareTable failed: {ex.Message}");
            return null;
        }
    }

    public static SmbiosTable Parse(byte[] raw)
    {
        var structures = new List<SmbiosStructure>();
        int i = 0;

        while (i + 4 <= raw.Length)
        {
            byte type = raw[i];
            byte length = raw[i + 1];
            if (length < 4 || i + length > raw.Length) break;

            var data = new byte[length];
            Array.Copy(raw, i, data, 0, length);

            // String table: NUL-terminated strings, ending with an extra NUL (or the end of the table).
            var strings = new List<string>();
            int cursor = i + length;

            while (cursor < raw.Length && raw[cursor] != 0)
            {
                int start = cursor;
                while (cursor < raw.Length && raw[cursor] != 0) cursor++;

                int count = cursor - start;
                strings.Add(Encoding.ASCII.GetString(raw, start, count));
                if (cursor < raw.Length && raw[cursor] == 0) cursor++;
            }

            // Step over the double NUL that terminates the string set.
            while (cursor < raw.Length && raw[cursor] == 0) cursor++;

            structures.Add(new SmbiosStructure { Data = data, Strings = strings });
            i = cursor;

            // Type 127 is the end-of-table marker.
            if (type == 127) break;
        }

        return new SmbiosTable { Structures = structures };
    }

    /// <summary>Reads the SMBIOS version from the firmware table header bytes.</summary>
    public static (byte Major, byte Minor) ReadVersion()
    {
        try
        {
            uint size = GetSystemFirmwareTableNative(RsmbSignature, 0, null, 0);
            if (size < 8) return (3, 0);

            var buffer = new byte[size];
            if (GetSystemFirmwareTableNative(RsmbSignature, 0, buffer, size) == 0) return (3, 0);

            return (buffer[1], buffer[2]);
        }
        catch
        {
            return (3, 0);
        }
    }

    /// <summary>SMBIOS memory type codes.</summary>
    public static string MemoryTypeName(byte code) => code switch
    {
        0x01 => "Other",
        0x02 => "Unknown",
        0x03 => "DRAM",
        0x0F => "SDRAM",
        0x11 => "SDRAM",
        0x12 => "SGRAM",
        0x13 => "RDRAM",
        0x14 => "DDR",
        0x15 => "DDR2",
        0x16 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x19 => "FBD2",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x1F => "Logical non-volatile",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => "Unknown"
    };

    /// <summary>SMBIOS form factor codes.</summary>
    public static string FormFactorName(byte code) => code switch
    {
        0x01 => "Other",
        0x02 => "Unknown",
        0x03 => "SIMM",
        0x04 => "SIP",
        0x05 => "Chip",
        0x06 => "DIP",
        0x07 => "ZIP",
        0x08 => "Proprietary card",
        0x09 => "DIMM",
        0x0A => "TSOP",
        0x0B => "Row of chips",
        0x0C => "RIMM",
        0x0D => "SODIMM",
        0x0E => "SRIMM",
        0x0F => "FB-DIMM",
        0x10 => "Die",
        _ => "Unknown"
    };

    /// <summary>SMBIOS chassis type codes (lower 7 bits of the chassis type byte).</summary>
    public static string ChassisTypeName(byte code) => (code & 0x7F) switch
    {
        0x01 => "Other",
        0x02 => "Unknown",
        0x03 => "Desktop",
        0x04 => "Low-profile desktop",
        0x05 => "Pizza box",
        0x06 => "Mini tower",
        0x07 => "Tower",
        0x08 => "Portable",
        0x09 => "Laptop",
        0x0A => "Notebook",
        0x0B => "Hand-held",
        0x0C => "Docking station",
        0x0D => "All-in-one",
        0x0E => "Sub-notebook",
        0x0F => "Space-saving",
        0x10 => "Lunch box",
        0x11 => "Main server chassis",
        0x12 => "Expansion chassis",
        0x13 => "Sub-chassis",
        0x14 => "Bus expansion chassis",
        0x15 => "Peripheral chassis",
        0x16 => "RAID chassis",
        0x17 => "Rack-mount chassis",
        0x18 => "Sealed-case PC",
        0x19 => "Multi-system chassis",
        0x1A => "Compact PCI",
        0x1B => "Advanced TCA",
        0x1C => "Blade",
        0x1D => "Blade enclosure",
        0x1E => "Tablet",
        0x1F => "Convertible",
        0x20 => "Detachable",
        0x21 => "IoT gateway",
        0x22 => "Embedded PC",
        0x23 => "Mini PC",
        0x24 => "Stick PC",
        _ => "Unknown"
    };
}
