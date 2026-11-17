using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Health;

/// <summary>
/// Turns collected sections into plain-language findings. Rules are small, independent checks so a new
/// one only has to be added to the list; each finding carries severity, what it means, what to do and the
/// category it belongs to.
/// </summary>
public static class HealthRuleEngine
{
    public static List<Finding> Evaluate(IReadOnlyDictionary<string, InfoSection> sections,
        PcCheckerSettings settings, IEnumerable<string>? dismissedIds = null)
    {
        var dismissed = new HashSet<string>(dismissedIds ?? settings.DismissedFindings, StringComparer.OrdinalIgnoreCase);
        var findings = new List<Finding>();

        void Add(Finding finding)
        {
            if (dismissed.Contains(finding.Id)) return;
            findings.Add(finding);
        }

        // ── Storage ─────────────────────────────────────────────────────────────
        if (sections.TryGetValue("storage", out var storage))
        {
            var free = storage.Find("storage.systemFree");
            if (free != null)
            {
                if (free.Status == InfoStatus.Problem)
                {
                    Add(new Finding
                    {
                        Id = "storage.systemDriveCritical",
                        Severity = InfoStatus.Problem,
                        Title = "The system drive is nearly full",
                        Explanation = $"Only {free.Value} is free. Windows needs room for updates and temporary files.",
                        Action = "Free space by moving large files or uninstalling unused apps, then run Disk Cleanup.",
                        CategoryId = "storage"
                    });
                }
                else if (free.Status == InfoStatus.Warning)
                {
                    Add(new Finding
                    {
                        Id = "storage.systemDriveLow",
                        Severity = InfoStatus.Warning,
                        Title = "The system drive is running low",
                        Explanation = $"{free.Value} is left on the drive Windows is installed on.",
                        Action = "Clear temporary files or move large folders to another drive before it gets tighter.",
                        CategoryId = "storage"
                    });
                }
            }

            foreach (var item in storage.AllItems().Where(i => i.Key.StartsWith("storage.health.", StringComparison.OrdinalIgnoreCase)))
            {
                if (item.Key.EndsWith(".wear", StringComparison.OrdinalIgnoreCase) && item.Status == InfoStatus.Warning)
                {
                    Add(new Finding
                    {
                        Id = $"storage.wear.{item.Label}",
                        Severity = InfoStatus.Warning,
                        Title = "A drive is nearing its write endurance",
                        Explanation = item.Value ?? "The drive reports high wear.",
                        Action = "Back up important data and plan a replacement; SSDs keep working but can fail suddenly at the end.",
                        CategoryId = "storage"
                    });
                }

                if (item.Key.EndsWith(".errors", StringComparison.OrdinalIgnoreCase) && item.Status == InfoStatus.Warning)
                {
                    Add(new Finding
                    {
                        Id = $"storage.errors.{item.Label}",
                        Severity = InfoStatus.Warning,
                        Title = "A drive reported uncorrected errors",
                        Explanation = item.Value ?? "The drive recorded errors it could not correct.",
                        Action = "Check the drive's cabling and back up data now; run the manufacturer's diagnostic tool.",
                        CategoryId = "storage"
                    });
                }

                if (item.Status == InfoStatus.Problem && item.Key.EndsWith(".overall", StringComparison.OrdinalIgnoreCase))
                {
                    Add(new Finding
                    {
                        Id = $"storage.unhealthy.{item.Label}",
                        Severity = InfoStatus.Problem,
                        Title = "Windows reports a drive as unhealthy",
                        Explanation = item.Value ?? "The drive's own health check failed.",
                        Action = "Back up immediately and replace the drive.",
                        CategoryId = "storage"
                    });
                }
            }
        }

        // ── Memory ──────────────────────────────────────────────────────────────
        if (sections.TryGetValue("memory", out var memory))
        {
            var inUse = memory.Find("memory.inUse");
            if (inUse?.Status == InfoStatus.Warning)
            {
                Add(new Finding
                {
                    Id = "memory.pressure",
                    Severity = InfoStatus.Warning,
                    Title = "Memory is almost full",
                    Explanation = $"{inUse.Value} of the usable memory is in use, so Windows is paging to disk.",
                    Action = "Close unused apps, or add memory if this happens often.",
                    CategoryId = "memory"
                });
            }

            var channels = memory.Find("memory.channels");
            if (channels?.Status == InfoStatus.Warning)
            {
                Add(new Finding
                {
                    Id = "memory.singleChannel",
                    Severity = InfoStatus.Info,
                    Title = "Memory may be running in single channel",
                    Explanation = channels.Value ?? "The modules look like they are not paired.",
                    Action = "Fit matching modules in the paired slots to get the extra bandwidth.",
                    CategoryId = "memory"
                });
            }

            var profile = memory.Find("memory.profile");
            if (profile != null)
            {
                Add(new Finding
                {
                    Id = "memory.profile",
                    Severity = InfoStatus.Info,
                    Title = "Memory runs below its rated speed",
                    Explanation = profile.Value ?? "Configured speed is lower than the module's rating.",
                    Action = "On a desktop, enable the XMP / EXPO profile in the BIOS if the board supports it.",
                    CategoryId = "memory"
                });
            }
        }

        // ── Devices ─────────────────────────────────────────────────────────────
        if (sections.TryGetValue("devices", out var devices))
        {
            var problems = devices.Find("devices.problems");
            if (problems != null && problems.Value is { } count && count != "0")
            {
                Add(new Finding
                {
                    Id = "devices.problemDevices",
                    Severity = InfoStatus.Warning,
                    Title = "Devices with a problem",
                    Explanation = $"{count} device(s) have a problem code. They may not work, or may be missing drivers.",
                    Action = "Open Devices & peripherals above to see the codes, then check Windows Update or the vendor's driver page.",
                    CategoryId = "devices"
                });
            }
        }

        // ── Battery ─────────────────────────────────────────────────────────────
        if (sections.TryGetValue("battery", out var battery))
        {
            var wear = battery.Find("battery.wear");
            if (wear != null && wear.Value is { } wearText)
            {
                if (wear.Status == InfoStatus.Warning)
                {
                    Add(new Finding
                    {
                        Id = "battery.worn",
                        Severity = InfoStatus.Warning,
                        Title = "The battery has lost a lot of capacity",
                        Explanation = wearText,
                        Action = "Expect short runtimes on battery; a replacement battery restores them.",
                        CategoryId = "battery"
                    });
                }
                else if (wearText.StartsWith("4", StringComparison.Ordinal) || wearText.StartsWith("5", StringComparison.Ordinal))
                {
                    Add(new Finding
                    {
                        Id = "battery.aging",
                        Severity = InfoStatus.Info,
                        Title = "The battery is ageing",
                        Explanation = wearText,
                        Action = "Nothing is wrong yet; keep an eye on runtime if it matters to you.",
                        CategoryId = "battery"
                    });
                }
            }
        }

        // ── Firmware ────────────────────────────────────────────────────────────
        if (sections.TryGetValue("firmware", out var firmware))
        {
            var secureBoot = firmware.Find("firmware.secureBoot");
            if (secureBoot?.Status == InfoStatus.Warning)
            {
                Add(new Finding
                {
                    Id = "firmware.secureBootOff",
                    Severity = InfoStatus.Warning,
                    Title = "Secure Boot is off",
                    Explanation = "Secure Boot only lets signed boot software run, which blocks some boot-level malware.",
                    Action = "Turn Secure Boot on in the UEFI settings if this PC supports it.",
                    CategoryId = "firmware"
                });
            }

            var tpm = firmware.Find("tpm.present");
            if (tpm is { Value: "Not present or disabled" })
            {
                Add(new Finding
                {
                    Id = "firmware.tpmMissing",
                    Severity = InfoStatus.Info,
                    Title = "No TPM detected",
                    Explanation = "BitLocker, Windows Hello and Windows 11 want a TPM 2.0 security chip.",
                    Action = "Enable the firmware TPM (PTT / fTPM) in the UEFI settings if you need those features.",
                    CategoryId = "firmware"
                });
            }

            var biosDate = firmware.Find("bios.date");
            if (bioDate(biosDate) is { } released && (DateTime.Now - released).TotalDays > 365 * 3)
            {
                Add(new Finding
                {
                    Id = "firmware.biosOld",
                    Severity = InfoStatus.Info,
                    Title = "The firmware is more than three years old",
                    Explanation = $"The BIOS/UEFI was released on {released:d}.",
                    Action = "Check the manufacturer's support site for a newer firmware; do not flash unless the update fixes something you need.",
                    CategoryId = "firmware"
                });
            }
        }

        // ── Security ────────────────────────────────────────────────────────────
        if (sections.TryGetValue("security", out var security))
        {
            void Check(string key, InfoStatus severity, string id, string title, string explanation, string action)
            {
                var item = security.Find(key);
                if (item == null) return;
                if (item.Value == null) return;
                if (item.Status != InfoStatus.Warning && item.Status != InfoStatus.Problem) return;

                Add(new Finding
                {
                    Id = id,
                    Severity = severity,
                    Title = title,
                    Explanation = $"{item.Label}: {item.Value}. {explanation}",
                    Action = action,
                    CategoryId = "security"
                });
            }

            Check("security.defender.rtp", InfoStatus.Problem, "security.defenderOff",
                "Defender real-time protection is off",
                "New malware will not be stopped as it arrives.",
                "Turn real-time protection back on in Windows Security.");

            Check("security.defender.signatureAge", InfoStatus.Warning, "security.signaturesOld",
                "Defender definitions are out of date",
                "Definitions older than a week miss recent malware.",
                "Let Windows Update run, or press Check for updates in Windows Security.");

            Check("security.uac", InfoStatus.Problem, "security.uacOff",
                "User Account Control is disabled",
                "Apps can make system-wide changes without asking.",
                "Set UAC back to the default level in Control Panel → User Accounts.");

            Check("security.smb1", InfoStatus.Warning, "security.smb1",
                "SMBv1 is enabled",
                "This old file-sharing protocol has known weaknesses.",
                "Turn SMBv1 off in Windows Features unless a device you own needs it.");

            Check("security.rdp", InfoStatus.Warning, "security.rdp",
                "Remote Desktop is enabled",
                "A public network with RDP open is a common attack target.",
                "Keep Remote Desktop off unless you use it, and never expose it directly to the internet.");

            foreach (var firewall in security.AllItems().Where(i => i.Key.StartsWith("security.firewall.", StringComparison.OrdinalIgnoreCase)))
            {
                if (firewall.Status == InfoStatus.Warning)
                {
                    Add(new Finding
                    {
                        Id = $"security.firewall.{firewall.Key}",
                        Severity = InfoStatus.Warning,
                        Title = "A firewall profile is off",
                        Explanation = $"{firewall.Label} firewall is off.",
                        Action = "Turn it back on in Windows Security → Firewall & network protection.",
                        CategoryId = "security"
                    });
                }
            }
        }

        // ── Updates ─────────────────────────────────────────────────────────────
        if (sections.TryGetValue("os", out var os))
        {
            var reboot = os.Find("os.pendingReboot");
            if (reboot?.Value is { } rebootText && rebootText.StartsWith("Yes", StringComparison.OrdinalIgnoreCase))
            {
                Add(new Finding
                {
                    Id = "os.pendingReboot",
                    Severity = InfoStatus.Info,
                    Title = "Windows is waiting for a restart",
                    Explanation = rebootText,
                    Action = "Restart when convenient so the pending update can finish.",
                    CategoryId = "os"
                });
            }
        }

        // ── Stability ───────────────────────────────────────────────────────────
        if (sections.TryGetValue("stability", out var stability))
        {
            var bugChecks = stability.Find("stability.bugchecks");
            if (bugChecks?.Value is { } crashes && crashes != "0" && crashes != "Needs administrator rights")
            {
                Add(new Finding
                {
                    Id = "stability.bugChecks",
                    Severity = InfoStatus.Problem,
                    Title = "Blue screens in the last 30 days",
                    Explanation = $"{crashes} blue screen(s) are recorded. They usually come from a driver or failing hardware.",
                    Action = "Check Stability & events for the codes, update the graphics and chipset drivers, then run a memory test.",
                    CategoryId = "stability"
                });
            }

            var hardware = stability.Find("stability.hardware");
            if (hardware?.Value is { } whea && whea != "0" && whea != "Needs administrator rights")
            {
                Add(new Finding
                {
                    Id = "stability.whea",
                    Severity = InfoStatus.Warning,
                    Title = "Hardware errors were corrected",
                    Explanation = $"{whea} WHEA hardware error(s) are recorded.",
                    Action = "Repeated hardware errors point at failing memory, a CPU or a PCIe device; check cooling and run a memory test.",
                    CategoryId = "stability"
                });
            }

            var shutdowns = stability.Find("stability.powerLoss");
            if (shutdowns?.Value is { } power && power != "0" && power != "Needs administrator rights")
            {
                Add(new Finding
                {
                    Id = "stability.unexpectedShutdowns",
                    Severity = InfoStatus.Warning,
                    Title = "Unexpected shutdowns",
                    Explanation = $"{power} unexpected shutdown(s) are recorded, which usually means power loss or a hang.",
                    Action = "Check the power cable and battery, and look for overheating before assuming software.",
                    CategoryId = "stability"
                });
            }
        }

        // ── Software ────────────────────────────────────────────────────────────
        if (sections.TryGetValue("software", out var software))
        {
            var startup = software.Find("software.startup");
            if (startup?.Value is { } startupText && int.TryParse(startupText, out var startupCount) && startupCount > 20)
            {
                Add(new Finding
                {
                    Id = "software.manyStartup",
                    Severity = InfoStatus.Info,
                    Title = "Many programs start with Windows",
                    Explanation = $"{startupCount} startup items are registered, which can slow sign-in.",
                    Action = "Disable the ones you do not need in Task Manager → Startup apps.",
                    CategoryId = "software"
                });
            }

            var missingPath = software.Find("software.path.missing");
            if (missingPath?.Status == InfoStatus.Info)
            {
                Add(new Finding
                {
                    Id = "software.pathMissing",
                    Severity = InfoStatus.Info,
                    Title = "PATH contains folders that do not exist",
                    Explanation = missingPath.Value ?? "Some PATH entries point at missing folders.",
                    Action = "Clean up the PATH environment variable so command lookups stay fast.",
                    CategoryId = "software"
                });
            }
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static DateTime? bioDate(InfoItem? item)
    {
        if (item?.Value == null) return null;
        return DateTime.TryParse(item.Value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
