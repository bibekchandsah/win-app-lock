using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

namespace AppLock.Services.PcChecker;

/// <summary>
/// Thin, defensive wrappers around WMI/CIM and the registry. Every call is wrapped so a missing
/// class (virtual machines, N editions) returns nothing instead of throwing.
/// </summary>
internal static class Wmi
{
    public const string CimV2 = @"root\cimv2";

    public static List<Dictionary<string, object?>> Query(string wql, string scope = CimV2)
    {
        var rows = new List<Dictionary<string, object?>>();

        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            try { searcher.Options.Timeout = TimeSpan.FromSeconds(8); } catch { }

            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in mo.Properties)
                    {
                        try { row[property.Name] = property.Value; } catch { }
                    }
                    rows.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] WMI query failed ({wql}): {ex.Message}");
        }

        return rows;
    }

    public static string? Str(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return null;
        var text = value.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static ulong? ULong(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return null;

        return value switch
        {
            ulong u => u,
            long l => l < 0 ? null : (ulong)l,
            int i => i < 0 ? null : (ulong)i,
            uint ui => ui,
            string s when ulong.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    public static int? Int(this Dictionary<string, object?> row, string key)
    {
        var number = row.ULong(key);
        return number is null or > int.MaxValue ? null : (int)number.Value;
    }

    public static DateTime? Date(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value == null) return null;

        if (value is DateTime date) return date;
        return DateTime.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}

/// <summary>Read-only registry helpers with graceful failure.</summary>
internal static class PcRegistry
{
    public static string? ReadString(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(path);
            var value = key?.GetValue(name);
            if (value == null) return null;

            var text = value switch
            {
                string s => s,
                string[] parts => string.Join(", ", parts),
                _ => value.ToString()
            };

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }

    public static long? ReadLong(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(path);
            var value = key?.GetValue(name);

            return value switch
            {
                int i => i,
                long l => l,
                string s when long.TryParse(s, out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static string? MachineString(string path, string name)
        => ReadString(RegistryHive.LocalMachine, RegistryView.Registry64, path, name);
}
