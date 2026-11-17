using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker;

/// <summary>
/// Builds the Compatibility category from data that is already collected: Windows 11 readiness (TPM, Secure
/// Boot, firmware mode, memory, boot drive, DirectX, 64-bit) and upgrade advice (free slots, drive type,
/// headroom for the next OS).
/// </summary>
public static class CompatibilityService
{
    public static InfoSection Evaluate(IReadOnlyDictionary<string, InfoSection> sections)
    {
        var section = new InfoSection { Id = "compatibility", Title = "Compatibility", Category = "compatibility" };

        string? Value(string sectionId, string key)
            => sections.TryGetValue(sectionId, out var s) ? s.Find(key)?.Value : null;

        var readiness = section.Group("Windows 11 readiness");
        var blocking = 0;

        void Check(string key, string label, string? value, Func<string?, bool?> verdict, string hint)
        {
            var item = InfoItem.Of(key, label, value, DetailLevel.Basic, hint,
                "Checked from the values on the other categories of this page.");

            switch (verdict(value))
            {
                case true:
                    item.Status = InfoStatus.Ok;
                    break;
                case false:
                    item.Status = InfoStatus.Problem;
                    blocking++;
                    break;
                default:
                    item.Status = InfoStatus.Info;
                    break;
            }

            readiness.Items.Add(item);
        }

        Check("compatibility.tpm", "TPM 2.0", Value("firmware", "tpm.version") ?? Value("firmware", "tpm.present"),
            v => v == null ? null : v.Contains("2.0", StringComparison.OrdinalIgnoreCase) || v.Contains("Present", StringComparison.OrdinalIgnoreCase),
            "Windows 11 requires TPM 2.0. Enable the firmware TPM (PTT / fTPM) if it is switched off.");

        Check("compatibility.secureBoot", "Secure Boot", Value("firmware", "firmware.secureBoot"),
            v => v == null ? null : v.Contains("On", StringComparison.OrdinalIgnoreCase) || v.Contains("Enabled", StringComparison.OrdinalIgnoreCase),
            "Windows 11 wants Secure Boot capable firmware with it switched on.");

        Check("compatibility.firmwareMode", "UEFI firmware", Value("firmware", "firmware.type"),
            v => v == null ? null : v.Contains("UEFI", StringComparison.OrdinalIgnoreCase),
            "Windows 11 needs UEFI, not legacy BIOS boot.");

        Check("compatibility.memory", "At least 4 GB of memory", Value("memory", "memory.installed"),
            v => v == null ? null : ParseGb(v) >= 4,
            "Windows 11 needs 4 GB or more.");

        Check("compatibility.systemDisk", "System drive of 64 GB or more", Value("storage", "storage.total"),
            v => v == null ? null : ParseGb(v) >= 64,
            "Windows 11 needs a system drive of at least 64 GB.");

        Check("compatibility.directx", "DirectX 12 / WDDM 2.0", Value("gpu", "gpu.directx"),
            v => v == null ? null : !v.Contains("11", StringComparison.OrdinalIgnoreCase) && !v.Contains("10", StringComparison.OrdinalIgnoreCase),
            "Windows 11 needs a DirectX 12 capable graphics driver.");

        Check("compatibility.arch", "64-bit processor", Value("cpu", "cpu.addressWidth"),
            v => v == null ? null : v.Contains("64", StringComparison.Ordinal),
            "Windows 11 is 64-bit only.");

        var summary = blocking == 0
            ? "This PC meets the checks that can be verified here. Microsoft's supported CPU list is the one item this page cannot check."
            : $"{blocking} check(s) would block a Windows 11 upgrade.";

        section.Groups[0].Items.Insert(0, InfoItem.Of("compatibility.summary", "Result", summary, DetailLevel.Basic,
            "A quick reading of the checks below; anything uncertain is listed as unknown rather than passed.",
            "Source: values collected by the other categories",
            blocking == 0 ? InfoStatus.Ok : InfoStatus.Problem));

        // ── Upgrade advice ──────────────────────────────────────────────────────
        var advice = section.Group("Upgrade advice");

        var freeSlots = Value("memory", "memory.freeSlots");
        if (freeSlots is { } slots && int.TryParse(slots.Split(' ')[0], out var free) && free > 0)
        {
            advice.Items.Add(InfoItem.Of("compatibility.freeSlots", "Free memory slots", freeSlots, DetailLevel.Basic,
                "Memory is the cheapest upgrade for most machines; adding a matching module uses these slots.",
                "Source: SMBIOS memory devices"));
        }

        var driveType = Value("storage", "storage.disk.1");
        if (driveType != null && driveType.Contains("HDD", StringComparison.OrdinalIgnoreCase))
        {
            advice.Items.Add(InfoItem.Of("compatibility.hdd", "System drive is a hard disk",
                driveType, DetailLevel.Basic,
                "Moving Windows to an SSD is the single biggest responsiveness upgrade available to this PC.",
                "Source: storage details", InfoStatus.Info));
        }

        var freeSpace = Value("storage", "storage.systemFree");
        if (freeSpace != null && ParseGb(freeSpace) < 100)
        {
            advice.Items.Add(InfoItem.Of("compatibility.headroom", "Little headroom for a feature update",
                $"{freeSpace} free on the system drive", DetailLevel.Basic,
                "A Windows feature update typically wants 20–30 GB free, plus room for the old installation.",
                "Source: storage details", InfoStatus.Info));
        }

        var cores = Value("cpu", "cpu.cores");
        if (cores != null && int.TryParse(cores, out var coreCount) && coreCount <= 2)
        {
            advice.Items.Add(InfoItem.Of("compatibility.cores", "Two cores or fewer",
                cores, DetailLevel.Basic,
                "Modern browsers and Windows updates assume four or more threads; this machine will feel slow under load.",
                "Source: processor details", InfoStatus.Info));
        }

        if (advice.Items.Count == 0)
        {
            advice.Items.Add(InfoItem.Of("compatibility.advice.none", "Upgrades", "Nothing obvious", DetailLevel.Basic,
                "No cheap upgrade stands out from the collected data.", "Source: collected values", InfoStatus.Ok));
        }

        return section;
    }

    /// <summary>Reads a formatted size like "16.0 GB" or "476 GB" as gigabytes.</summary>
    private static double ParseGb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',').ToArray())
            .Replace(',', '.');
        if (!double.TryParse(digits, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number)) return 0;

        if (text.Contains("TB", StringComparison.OrdinalIgnoreCase)) return number * 1024;
        if (text.Contains("MB", StringComparison.OrdinalIgnoreCase)) return number / 1024;
        if (text.Contains("KB", StringComparison.OrdinalIgnoreCase)) return number / (1024 * 1024);
        return number;
    }
}
