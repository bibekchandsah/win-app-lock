using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>Operating system basics: edition, build, install date, uptime, machine and user.</summary>
public sealed class OsProvider : IInfoProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public string Id => "os";
    public string Category => "os";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(10);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var note = $"Source: registry {CurrentVersionKey}";
        var section = new InfoSection { Id = Id, Title = "Operating system", Category = Category };
        var summary = section.Group("Windows");

        string? productName = PcRegistry.MachineString(CurrentVersionKey, "ProductName");
        string? buildText = PcRegistry.MachineString(CurrentVersionKey, "CurrentBuild");
        if (string.IsNullOrEmpty(buildText))
            buildText = PcRegistry.MachineString(CurrentVersionKey, "CurrentBuildNumber");

        int build = int.TryParse(buildText, out var parsedBuild) ? parsedBuild : 0;
        long? ubr = PcRegistry.ReadLong(Microsoft.Win32.RegistryHive.LocalMachine, CurrentVersionKey, "UBR");
        string? displayVersion = PcRegistry.MachineString(CurrentVersionKey, "DisplayVersion");
        string? editionId = PcRegistry.MachineString(CurrentVersionKey, "EditionID");

        // Windows 11 still reports "Windows 10 …" in ProductName, the build number is authoritative.
        if (build >= 22000 && productName != null && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);

        string buildNumber = build > 0
            ? (ubr is > 0 ? $"{build}.{ubr}" : build.ToString(CultureInfo.InvariantCulture))
            : "Not reported";

        summary.Items.Add(InfoItem.Of("os.name", "Windows", productName, DetailLevel.Basic, "The Windows edition that is installed.", note));
        summary.Items.Add(InfoItem.Of("os.version", "Version", displayVersion, DetailLevel.Basic, "The yearly Windows release, for example 24H2.", note));
        summary.Items.Add(InfoItem.Of("os.build", "Build number", buildNumber, DetailLevel.Basic, "The exact build and revision (UBR) of this Windows installation.", note));
        summary.Items.Add(InfoItem.Of("os.editionId", "Edition ID", editionId, DetailLevel.Everything, "The internal edition name Windows uses.", note));
        summary.Items.Add(InfoItem.Of("os.arch", "Architecture", ArchitectureText(), DetailLevel.Basic, "The architecture of this copy of Windows."));

        long? installRaw = PcRegistry.ReadLong(Microsoft.Win32.RegistryHive.LocalMachine, CurrentVersionKey, "InstallDate");
        if (installRaw is > 0)
        {
            var installed = DateTimeOffset.FromUnixTimeSeconds(installRaw.Value).LocalDateTime;
            var installItem = InfoItem.Of("os.installDate", "Install date", PcFormat.DateText(installed), DetailLevel.Basic,
                "When this copy of Windows was installed.", note);
            installItem.RawValue = installRaw.Value.ToString(CultureInfo.InvariantCulture);
            summary.Items.Add(installItem);
        }
        else
        {
            summary.Items.Add(InfoItem.MissingValue("os.installDate", "Install date", MissingReason.NotReported, DetailLevel.Basic, note));
        }

        var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        summary.Items.Add(InfoItem.Of("os.lastBoot", "Last boot", PcFormat.DateTimeText(bootTime), DetailLevel.Basic,
            "When Windows was last started.", "Source: system uptime counter"));
        summary.Items.Add(InfoItem.Of("os.uptime", "Uptime", PcFormat.Uptime(DateTime.Now - bootTime), DetailLevel.Basic,
            "How long the PC has been running since that boot."));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        var machine = section.Group("This PC");
        machine.Items.Add(InfoItem.Of("os.computerName", "Computer name", Environment.MachineName, DetailLevel.Basic,
            "The name this PC uses on a network.", sensitivity: InfoSensitivity.Identifier));
        machine.Items.Add(InfoItem.Of("os.user", "Signed-in user", Environment.UserName, DetailLevel.Basic,
            "The Windows account that is currently signed in.", sensitivity: InfoSensitivity.Identifier));
        machine.Items.Add(InfoItem.Of("os.userAdmin", "User is an administrator",
            ElevationHelper.IsElevated ? "Yes (AppLock is running elevated)" : "No (standard user)",
            DetailLevel.Basic, "Whether this account has administrator rights."));
        machine.Items.Add(InfoItem.Of("os.domain", "Domain or workgroup", Environment.UserDomainName, DetailLevel.Detailed,
            "The domain or workgroup this PC belongs to.", sensitivity: InfoSensitivity.Identifier));
        machine.Items.Add(InfoItem.Of("os.timeZone", "Time zone", TimeZoneInfo.Local.DisplayName, DetailLevel.Detailed,
            "The time zone Windows is set to."));
        machine.Items.Add(InfoItem.Of("os.culture", "UI language", CultureInfo.CurrentUICulture.DisplayName, DetailLevel.Detailed,
            "The language the interface is displayed in."));
        machine.Items.Add(InfoItem.Of("os.systemDrive", "System drive", Environment.GetEnvironmentVariable("SystemDrive"), DetailLevel.Detailed,
            "The drive Windows is installed on."));
        machine.Items.Add(InfoItem.Of("os.windowsDir", "Windows folder", Environment.SystemDirectory, DetailLevel.Everything));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.8);

        // ── Windows status ──────────────────────────────────────────────────────
        var status = section.Group("Windows status");

        var activation = ReadActivation();
        status.Items.Add(activation.Text != null
            ? InfoItem.Of("os.activation", "Activation", activation.Text, DetailLevel.Basic,
                "Whether Windows is activated. Only the last five characters of the product key are read, never the full key.",
                "Source: WMI SoftwareLicensingProduct",
                activation.Licensed ? InfoStatus.Ok : InfoStatus.Warning)
            : InfoItem.MissingValue("os.activation", "Activation", MissingReason.NotReported, DetailLevel.Basic,
                "Source: WMI SoftwareLicensingProduct"));

        if (activation.PartialKey != null)
        {
            status.Items.Add(InfoItem.Of("os.partialKey", "Product key (last 5)", activation.PartialKey, DetailLevel.Detailed,
                "The last five characters of the installed product key, the same Windows' own licensing tools show.",
                "Source: WMI SoftwareLicensingProduct", sensitivity: InfoSensitivity.Identifier));
        }

        if (activation.Channel != null)
        {
            status.Items.Add(InfoItem.Of("os.licenseChannel", "License channel", activation.Channel, DetailLevel.Detailed, null,
                "Source: WMI SoftwareLicensingProduct"));
        }

        // The full installed key, decoded from the registry value Windows keeps for its own licensing. It stays
        // masked until someone asks for it, and the page warns about misuse before revealing it.
        var installedKey = ProductKeyReader.ReadInstalledKey();
        if (installedKey != null)
        {
            // The decoded key must end with the same five characters Windows reports for this licence;
            // otherwise it is a decoding mismatch and must not be presented as the real key.
            bool matchesWindows = ProductKeyReader.MatchesPartialKey(installedKey, activation.PartialKey);
            var productKeyItem = InfoItem.Of("os.productKey", "Product key (full)", installedKey, DetailLevel.Detailed,
                "The key installed on this PC, decoded from Windows' own DigitalProductId. Treat it like a password: " +
                "anyone who has it can try to activate Windows with it. A digital licence is tied to your Microsoft " +
                "account and this hardware instead." +
                (matchesWindows ? string.Empty
                    : $" Unconfirmed: this value does not end with the five characters Windows reports ({activation.PartialKey}), " +
                      "so do not rely on it."),
                "Source: registry HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
                matchesWindows ? InfoStatus.Info : InfoStatus.Warning, InfoSensitivity.Identifier);
            status.Items.Add(productKeyItem);
        }
        else
        {
            status.Items.Add(InfoItem.MissingValue("os.productKey", "Product key (full)", MissingReason.NotReported,
                DetailLevel.Detailed, "Source: registry HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"));
        }

        var rebootReasons = PendingRebootReasons();
        var rebootItem = InfoItem.Of("os.pendingReboot", "Pending reboot",
            rebootReasons.Count == 0 ? "No" : $"Yes ({string.Join(", ", rebootReasons)})", DetailLevel.Basic,
            "Some updates and app installs only finish after a restart.");
        rebootItem.Status = rebootReasons.Count == 0 ? InfoStatus.Ok : InfoStatus.Info;
        status.Items.Add(rebootItem);

        var hotfixes = Wmi.Query("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
        status.Items.Add(hotfixes.Count == 0
            ? InfoItem.MissingValue("os.hotfixes", "Installed updates", MissingReason.NotReported, DetailLevel.Detailed,
                "Source: WMI Win32_QuickFixEngineering")
            : InfoItem.Of("os.hotfixes", "Installed updates", hotfixes.Count.ToString(), DetailLevel.Detailed,
                "How many Windows updates are installed. The full list and update history come with the software milestone.",
                "Source: WMI Win32_QuickFixEngineering"));

        var newest = hotfixes
            .Select(row => new { Id = row.Str("HotFixID"), Date = row.Date("InstalledOn") })
            .Where(x => x.Date.HasValue)
            .OrderByDescending(x => x.Date)
            .FirstOrDefault();

        if (newest?.Date is { } newestDate)
        {
            status.Items.Add(InfoItem.Of("os.newestUpdate", "Newest update",
                $"{newest.Id} · {PcFormat.DateText(newestDate)}", DetailLevel.Detailed,
                "The most recently installed Windows update on this PC.", "Source: WMI Win32_QuickFixEngineering"));
        }

        progress?.Report(1);
        return section;
    }

    private sealed record ActivationInfo(string? Text, string? PartialKey, string? Channel, bool Licensed);

    private static ActivationInfo ReadActivation()
    {
        const string applicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";
        var rows = Wmi.Query(
            "SELECT LicenseStatus, PartialProductKey, ProductKeyChannel FROM SoftwareLicensingProduct " +
            $"WHERE PartialProductKey IS NOT NULL AND ApplicationID='{applicationId}'");

        if (rows.Count == 0) return new ActivationInfo(null, null, null, false);

        var row = rows[0];
        int? licenseStatus = row.Int("LicenseStatus");
        string? partialKey = row.Str("PartialProductKey");
        string? channel = row.Str("ProductKeyChannel");

        var text = licenseStatus switch
        {
            0 => "Not activated",
            1 => "Activated (licensed)",
            2 or 3 or 4 or 6 => "Activated temporarily",
            5 => "Not activated (notification mode)",
            _ => null
        };

        return new ActivationInfo(text, partialKey, channel, licenseStatus == 1);
    }

    /// <summary>Reasons Windows is waiting for a restart, in plain language.</summary>
    private static List<string> PendingRebootReasons()
    {
        var reasons = new List<string>();

        if (PcRegistry.KeyOrValueExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("an update finished installing");

        if (PcRegistry.KeyOrValueExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update needs a restart");

        if (PcRegistry.KeyOrValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"))
            reasons.Add("an installer is waiting to replace files");

        return reasons;
    }

    private static string ArchitectureText()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        return arch switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "64-bit (x64)",
            System.Runtime.InteropServices.Architecture.Arm64 => "64-bit (ARM64)",
            System.Runtime.InteropServices.Architecture.X86 => "32-bit (x86)",
            System.Runtime.InteropServices.Architecture.Arm => "32-bit (ARM)",
            _ => arch.ToString()
        };
    }
}
