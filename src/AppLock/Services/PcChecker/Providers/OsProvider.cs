using System;
using System.Collections.Generic;
using System.Globalization;
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

        progress?.Report(1);
        return section;
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
