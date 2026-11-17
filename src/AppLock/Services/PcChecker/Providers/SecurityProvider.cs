using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using Microsoft.Win32;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Security posture: Defender state and signature age, firewall per profile, UAC, Secure Boot, memory
/// integrity / VBS, Credential Guard, RDP, SMBv1, AutoPlay, the guest account and BitLocker.
/// Read-only: nothing is changed.
/// </summary>
public sealed class SecurityProvider : IInfoProvider
{
    private const string DefenderScope = @"root\Microsoft\Windows\Defender";
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string FirewallRoot = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    public string Id => "security";
    public string Category => "security";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(2);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Security", Category = Category };

        // ── Defender ────────────────────────────────────────────────────────────
        var defenderGroup = section.Group("Microsoft Defender");
        var status = Wmi.Query(
            "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureVersion, AntivirusSignatureLastUpdated, " +
            "IsTamperProtected, AMServiceEnabled FROM MSFT_MpComputerStatus", DefenderScope);

        if (status.Count == 0)
        {
            defenderGroup.Items.Add(InfoItem.MissingValue("security.defender", "Defender status",
                ElevationHelper.IsElevated ? MissingReason.NotReported : MissingReason.NeedsAdmin, DetailLevel.Basic,
                "Source: WMI MSFT_MpComputerStatus"));
        }
        else
        {
            var row = status[0];
            var rtp = row.Str("RealTimeProtectionEnabled");
            var rtpItem = InfoItem.Of("security.defender.rtp", "Real-time protection",
                rtp is null ? null : IsTrue(rtp) ? "On" : "Off", DetailLevel.Basic,
                "Watches files as they are opened and stops known malware.", "Source: WMI MSFT_MpComputerStatus");
            rtpItem.Status = rtp is null ? InfoStatus.None : IsTrue(rtp) ? InfoStatus.Ok : InfoStatus.Warning;
            defenderGroup.Items.Add(rtpItem);

            var signatureVersion = row.Str("AntivirusSignatureVersion");
            defenderGroup.Items.Add(InfoItem.Of("security.defender.signature", "Signature version", signatureVersion,
                DetailLevel.Detailed, "The version of Defender's malware definitions.", "Source: WMI MSFT_MpComputerStatus"));

            var updated = row.Date("AntivirusSignatureLastUpdated");
            if (updated.HasValue)
            {
                var age = DateTime.Now - updated.Value;
                var ageItem = InfoItem.Of("security.defender.signatureAge", "Signature age",
                    $"{PcFormat.Since(updated.Value)}", DetailLevel.Basic,
                    "Definitions older than a few days leave new malware unrecognised.",
                    "Source: WMI MSFT_MpComputerStatus");
                if (age.TotalDays > 7) ageItem.Status = InfoStatus.Warning;
                defenderGroup.Items.Add(ageItem);
            }

            var tamper = row.Str("IsTamperProtected");
            if (tamper != null)
            {
                defenderGroup.Items.Add(InfoItem.Of("security.defender.tamper", "Tamper protection",
                    IsTrue(tamper) ? "On" : "Off", DetailLevel.Detailed,
                    "Stops other programs from switching Defender off.",
                    "Source: WMI MSFT_MpComputerStatus",
                    IsTrue(tamper) ? InfoStatus.Ok : InfoStatus.Info));
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        // ── Firewall ────────────────────────────────────────────────────────────
        var firewallGroup = section.Group("Firewall");
        var publicProfileOn = true;
        foreach (var (profile, label) in new[]
                 {
                     ("DomainProfile", "Domain network"),
                     ("StandardProfile", "Private network"),
                     ("PublicProfile", "Public network")
                 })
        {
            var enabled = PcRegistry.ReadLong(RegistryHive.LocalMachine, $@"{FirewallRoot}\{profile}", "EnableFirewall");
            var text = enabled is null ? null : enabled.Value != 0 ? "On" : "Off";

            var item = InfoItem.Of($"security.firewall.{profile}", $"Firewall — {label}", text, DetailLevel.Basic,
                "Blocks unsolicited incoming connections on this kind of network.",
                "Source: registry FirewallPolicy");
            if (enabled is not null) item.Status = enabled.Value != 0 ? InfoStatus.Ok : InfoStatus.Warning;
            if (profile == "PublicProfile" && enabled is not null) publicProfileOn = enabled.Value != 0;
            firewallGroup.Items.Add(item);
        }

        // ── User account control and hardening ──────────────────────────────────
        var hardening = section.Group("Windows hardening");
        const string policySource = "Source: registry Policies\\System";

        var uac = PcRegistry.ReadLong(RegistryHive.LocalMachine, PolicyKey, "EnableLUA");
        var uacItem = InfoItem.Of("security.uac", "User Account Control",
            uac is null ? null : uac.Value != 0 ? "On" : "Off (disabled)", DetailLevel.Basic,
            "Asks for confirmation before apps make system-wide changes.", policySource);
        if (uac is not null) uacItem.Status = uac.Value != 0 ? InfoStatus.Ok : InfoStatus.Problem;
        hardening.Items.Add(uacItem);

        var secureBoot = PcRegistry.ReadLong(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State",
            "UEFISecureBootEnabled");
        if (secureBoot is not null)
        {
            var sbItem = InfoItem.Of("security.secureBoot", "Secure Boot", secureBoot.Value != 0 ? "On" : "Off", DetailLevel.Basic,
                "Only lets signed boot software run.", "Source: registry SecureBoot state");
            sbItem.Status = secureBoot.Value != 0 ? InfoStatus.Ok : InfoStatus.Warning;
            hardening.Items.Add(sbItem);
        }

        var memoryIntegrity = PcRegistry.ReadLong(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        if (memoryIntegrity is not null)
        {
            var hvciItem = InfoItem.Of("security.hvci", "Memory integrity (HVCI)",
                memoryIntegrity.Value != 0 ? "On" : "Off", DetailLevel.Basic,
                "Runs kernel code in a protected area so malicious drivers cannot tamper with Windows.",
                "Source: registry DeviceGuard\\Scenarios");
            if (memoryIntegrity.Value == 0) hvciItem.Status = InfoStatus.Info;
            hardening.Items.Add(hvciItem);
        }

        var vbs = PcRegistry.ReadLong(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard",
            "EnableVirtualizationBasedSecurity");
        if (vbs is not null)
        {
            hardening.Items.Add(InfoItem.Of("security.vbs", "Virtualisation-based security",
                vbs.Value != 0 ? "On" : "Off", DetailLevel.Detailed,
                "Uses the processor's virtualisation to isolate security features from the rest of Windows.",
                "Source: registry DeviceGuard"));
        }

        var credentialGuard = PcRegistry.ReadLong(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\LSA", "LsaCfgFlags");
        hardening.Items.Add(InfoItem.Of("security.credentialGuard", "Credential Guard",
            credentialGuard is null ? null : credentialGuard.Value != 0 ? "Configured" : "Not configured", DetailLevel.Everything,
            "Protects sign-in secrets from being read by other code.", "Source: registry LSA"));

        var rdpValue = PcRegistry.ReadLong(RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
        if (rdpValue is not null)
        {
            var rdpItem = InfoItem.Of("security.rdp", "Remote Desktop",
                rdpValue.Value != 0 ? "Off" : "On", DetailLevel.Basic,
                "Lets another PC connect to this one. Only enable it on a private, trusted network.",
                "Source: registry Terminal Server");
            if (rdpValue.Value == 0 && publicProfileOn) rdpItem.Status = InfoStatus.Warning;
            hardening.Items.Add(rdpItem);
        }

        var smb1 = PcRegistry.ReadLong(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1");
        var smb1Client = PcRegistry.ReadLong(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "SMB1");
        var smb1On = (smb1 ?? 0) != 0 || (smb1Client ?? 0) != 0;
        var smbItem = InfoItem.Of("security.smb1", "SMBv1", smb1On ? "Enabled" : "Disabled", DetailLevel.Basic,
            "An old file-sharing protocol with known weaknesses. Windows 11 has it off by default.",
            "Source: registry LanmanServer / LanmanWorkstation");
        smbItem.Status = smb1On ? InfoStatus.Warning : InfoStatus.Ok;
        hardening.Items.Add(smbItem);

        var autoplay = PcRegistry.ReadLong(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun");
        hardening.Items.Add(InfoItem.Of("security.autoplay", "AutoPlay",
            autoplay is null ? null : autoplay.Value != 0 ? "Limited" : "On", DetailLevel.Everything,
            "Running media or programs automatically when a drive is connected.",
            "Source: registry Policies\\Explorer"));

        var smartScreen = PcRegistry.MachineString(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        hardening.Items.Add(InfoItem.Of("security.smartscreen", "SmartScreen", smartScreen, DetailLevel.Everything,
            "Warns about unknown apps and unsafe downloads.", "Source: registry Explorer"));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.7);

        // ── Accounts ────────────────────────────────────────────────────────────
        var accountGroup = section.Group("Local accounts");
        var accounts = Wmi.Query("SELECT Name, Disabled, LocalAccount FROM Win32_UserAccount WHERE LocalAccount = True");
        foreach (var account in accounts.OrderBy(a => a.Str("Name")))
        {
            var name = account.Str("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var disabled = IsTrue(account.Str("Disabled"));
            var item = InfoItem.Of($"security.account.{name}", name, disabled ? "Disabled" : "Enabled", DetailLevel.Detailed,
                "A local Windows account on this PC.", "Source: WMI Win32_UserAccount");
            if (name.Equals("Guest", StringComparison.OrdinalIgnoreCase) && !disabled)
                item.Status = InfoStatus.Warning;
            accountGroup.Items.Add(item);
        }

        // ── BitLocker ───────────────────────────────────────────────────────────
        var bitlockerGroup = section.Group("BitLocker");
        var volumes = Wmi.Query("SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod FROM Win32_EncryptableVolume",
            @"root\cimv2\security\microsoftvolumeencryption");

        if (volumes.Count == 0)
        {
            bitlockerGroup.Items.Add(InfoItem.MissingValue("security.bitlocker", "BitLocker",
                ElevationHelper.IsElevated ? MissingReason.NotReported : MissingReason.NeedsAdmin, DetailLevel.Basic,
                "Source: WMI Win32_EncryptableVolume"));
        }

        foreach (var volume in volumes)
        {
            var letter = volume.Str("DriveLetter");
            var protection = volume.Int("ProtectionStatus");
            var text = protection switch
            {
                0 => "Off",
                1 => "On",
                2 => "Unknown",
                _ => null
            };

            var item = InfoItem.Of($"security.bitlocker.{letter}", $"BitLocker {letter}",
                text, DetailLevel.Basic, "Full-disk encryption for this volume.",
                "Source: WMI Win32_EncryptableVolume");
            if (protection == 0) item.Status = InfoStatus.Info;
            else if (protection == 1) item.Status = InfoStatus.Ok;
            bitlockerGroup.Items.Add(item);
        }

        progress?.Report(1);
        return section;
    }

    private static bool IsTrue(string? value) => value is "True" or "true" or "1";
}
