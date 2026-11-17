using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using AppLock.Services.PcChecker.Parsers;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>Motherboard, BIOS/UEFI, chassis, TPM and Secure Boot state.</summary>
public sealed class FirmwareProvider : IInfoProvider
{
    private const string SecureBootKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    public string Id => "firmware";
    public string Category => "firmware";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(30);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Motherboard & firmware", Category = Category };
        var table = SmbiosParser.GetTable();
        const string smbiosSource = "Source: SMBIOS firmware table (GetSystemFirmwareTable)";

        var biosGroup = section.Group("BIOS / UEFI");
        var bios = table?.First(0);
        if (bios != null)
        {
            var vendor = bios.StringAt(0x04);
            var version = bios.StringAt(0x05);
            var date = bios.StringAt(0x08);

            biosGroup.Items.Add(InfoItem.Of("bios.vendor", "BIOS vendor", Empty(vendor), DetailLevel.Basic,
                "Who wrote the firmware of this PC.", smbiosSource));
            biosGroup.Items.Add(InfoItem.Of("bios.version", "BIOS version", Empty(version), DetailLevel.Basic,
                "The firmware version. Compare it with the manufacturer's site before updating.", smbiosSource));
            biosGroup.Items.Add(InfoItem.Of("bios.date", "Release date", Empty(date), DetailLevel.Basic,
                "When this firmware version was released.", smbiosSource));
        }
        else
        {
            var vendor = PcRegistry.MachineString(BiosKey, "BIOSVendor");
            var version = PcRegistry.MachineString(BiosKey, "BIOSVersion");
            var date = PcRegistry.MachineString(BiosKey, "BIOSReleaseDate");

            biosGroup.Items.Add(vendor != null
                ? InfoItem.Of("bios.vendor", "BIOS vendor", vendor, DetailLevel.Basic, null, "Source: registry HARDWARE\\DESCRIPTION\\System\\BIOS")
                : InfoItem.MissingValue("bios.vendor", "BIOS vendor", MissingReason.NotReported, DetailLevel.Basic, smbiosSource));
            biosGroup.Items.Add(InfoItem.Of("bios.version", "BIOS version", version, DetailLevel.Basic, null,
                "Source: registry HARDWARE\\DESCRIPTION\\System\\BIOS"));
            biosGroup.Items.Add(InfoItem.Of("bios.date", "Release date", date, DetailLevel.Basic, null,
                "Source: registry HARDWARE\\DESCRIPTION\\System\\BIOS"));
        }

        biosGroup.Items.Add(InfoItem.Of("firmware.type", "Firmware type", FirmwareTypeText(), DetailLevel.Basic,
            "Whether this PC boots in UEFI mode or the older legacy BIOS mode.", "Source: GetFirmwareType"));
        biosGroup.Items.Add(InfoItem.Of("firmware.smbios", "SMBIOS version",
            table != null ? $"{SmbiosParser.ReadVersion().Major}.{SmbiosParser.ReadVersion().Minor}" : null, DetailLevel.Detailed,
            "The version of the SMBIOS firmware description Windows can read.", smbiosSource));

        var secureBoot = SecureBootState();
        var secureItem = InfoItem.Of("firmware.secureBoot", "Secure Boot", secureBoot.Text, DetailLevel.Basic,
            "Secure Boot only lets signed boot software run, which blocks some boot-level malware.",
            "Source: registry UEFI SecureBoot state");
        secureItem.Status = secureBoot.Enabled == true ? InfoStatus.Ok : secureBoot.Enabled == false ? InfoStatus.Warning : InfoStatus.None;
        biosGroup.Items.Add(secureItem);

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.4);

        // ── Board and system ────────────────────────────────────────────────────
        var boardGroup = section.Group("Motherboard");
        var board = table?.First(2);
        var system = table?.First(1);

        boardGroup.Items.Add(InfoItem.Of("board.manufacturer", "Manufacturer",
            Empty(board?.StringAt(0x04)) ?? PcRegistry.MachineString(BiosKey, "BaseBoardManufacturer"), DetailLevel.Basic,
            "Who made the motherboard.", smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("board.product", "Model",
            Empty(board?.StringAt(0x05)) ?? PcRegistry.MachineString(BiosKey, "BaseBoardProduct"), DetailLevel.Basic,
            "The motherboard model.", smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("board.version", "Version", Empty(board?.StringAt(0x06)), DetailLevel.Detailed, null, smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("board.serial", "Serial number", Empty(board?.StringAt(0x07)), DetailLevel.Detailed,
            "The motherboard's serial number.", smbiosSource, sensitivity: InfoSensitivity.Identifier));
        boardGroup.Items.Add(InfoItem.Of("board.assetTag", "Asset tag", Empty(board?.StringAt(0x08)), DetailLevel.Everything, null, smbiosSource,
            sensitivity: InfoSensitivity.Identifier));

        boardGroup.Items.Add(InfoItem.Of("system.manufacturer", "System manufacturer",
            Empty(system?.StringAt(0x04)) ?? PcRegistry.MachineString(BiosKey, "SystemManufacturer"), DetailLevel.Basic,
            "Who built this PC.", smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("system.model", "System model",
            Empty(system?.StringAt(0x05)) ?? PcRegistry.MachineString(BiosKey, "SystemProductName"), DetailLevel.Basic,
            "The exact model name, useful when looking up drivers or specifications.", smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("system.family", "System family", Empty(system?.StringAt(0x1A)), DetailLevel.Everything, null, smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("system.sku", "System SKU", Empty(system?.StringAt(0x19)), DetailLevel.Everything, null, smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("system.serial", "System serial", Empty(system?.StringAt(0x07)), DetailLevel.Detailed,
            "The serial number of this PC, often the service tag.", smbiosSource, sensitivity: InfoSensitivity.Identifier));

        var uuid = FormatUuid(system?.BytesAt(0x08, 16));
        boardGroup.Items.Add(InfoItem.Of("system.uuid", "System UUID", uuid, DetailLevel.Everything,
            "A unique id for this machine, used by Windows and management tools.", smbiosSource,
            sensitivity: InfoSensitivity.Identifier));

        var chassis = table?.First(3);
        var chassisCode = chassis?.ByteAt(0x05) ?? 0;
        boardGroup.Items.Add(InfoItem.Of("system.chassis", "Chassis type",
            chassis != null ? SmbiosParser.ChassisTypeName(chassisCode) : null, DetailLevel.Basic,
            "The kind of machine this is: desktop, laptop, all-in-one, tablet and so on.", smbiosSource));
        boardGroup.Items.Add(InfoItem.Of("system.chassisSerial", "Chassis serial", Empty(chassis?.StringAt(0x07)), DetailLevel.Everything,
            null, smbiosSource, sensitivity: InfoSensitivity.Identifier));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.7);

        // ── TPM ─────────────────────────────────────────────────────────────────
        var tpmGroup = section.Group("Trusted Platform Module (TPM)");
        var tpm = ReadTpm();

        if (tpm.Present == null)
        {
            tpmGroup.Items.Add(InfoItem.MissingValue("tpm.present", "TPM", MissingReason.NeedsAdmin, DetailLevel.Basic, tpm.Source));
        }
        else if (tpm.Present == false)
        {
            tpmGroup.Items.Add(InfoItem.Of("tpm.present", "TPM", "Not present or disabled", DetailLevel.Basic,
                "A security chip that stores keys for BitLocker, Windows Hello and Secure Boot.", tpm.Source,
                InfoStatus.Warning));
        }
        else
        {
            tpmGroup.Items.Add(InfoItem.Of("tpm.present", "TPM", "Present", DetailLevel.Basic,
                "A security chip that stores keys for BitLocker, Windows Hello and Secure Boot.", tpm.Source,
                InfoStatus.Ok));
        }

        if (tpm.Present == true)
        {
            tpmGroup.Items.Add(InfoItem.Of("tpm.version", "Version", tpm.Version, DetailLevel.Basic,
                "TPM 2.0 is required for Windows 11 and for most BitLocker features.", tpm.Source));
            tpmGroup.Items.Add(InfoItem.Of("tpm.manufacturer", "Manufacturer", tpm.Manufacturer, DetailLevel.Detailed, null, tpm.Source));
            tpmGroup.Items.Add(InfoItem.Of("tpm.enabled", "Enabled", tpm.Enabled, DetailLevel.Detailed, null, tpm.Source));
            tpmGroup.Items.Add(InfoItem.Of("tpm.activated", "Activated", tpm.Activated, DetailLevel.Detailed, null, tpm.Source));
        }

        progress?.Report(1);
        return section;
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirmwareTypeText()
    {
        try
        {
            return GetFirmwareType(out var type) && type == 2 ? "UEFI" : "Legacy BIOS";
        }
        catch
        {
            return "Not reported";
        }
    }

    private static (string Text, bool? Enabled) SecureBootState()
    {
        var enabled = PcRegistry.ReadLong(Microsoft.Win32.RegistryHive.LocalMachine, SecureBootKey, "UEFISecureBootEnabled");
        if (enabled.HasValue)
        {
            return enabled.Value != 0 ? ("On", true) : ("Off", false);
        }

        // A PC without the UEFI Secure Boot state key boots in legacy mode.
        return FirmwareTypeText() == "UEFI" ? ("Not reported", null) : ("Not available (legacy BIOS)", false);
    }

    /// <summary>Present is null when the TPM namespace could not be read (it needs administrator rights).</summary>
    private sealed record TpmInfo(bool? Present, string? Version, string? Manufacturer, string? Enabled, string? Activated, string? Source);

    private static TpmInfo ReadTpm()
    {
        const string source = "Source: WMI root\\cimv2\\security\\microsofttpm Win32_Tpm";
        var rows = Wmi.Query("SELECT SpecVersion, ManufacturerIdTxt, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm",
            @"root\cimv2\security\microsofttpm");

        if (rows.Count == 0)
        {
            // A PC without a TPM reports no instances; a standard user usually cannot read the
            // namespace at all. Both look the same here, so the UI says it needs administrator rights.
            return new TpmInfo(null, null, null, null, null, source);
        }

        var row = rows[0];
        var version = row.Str("SpecVersion");
        var manufacturer = row.Str("ManufacturerIdTxt")?.Trim();
        var enabled = row.Str("IsEnabled_InitialValue");
        var activated = row.Str("IsActivated_InitialValue");

        return new TpmInfo(true, version, manufacturer, BoolText(enabled), BoolText(activated), source);
    }

    private static string? BoolText(string? raw)
        => raw switch
        {
            null => null,
            "True" or "true" => "Yes",
            "False" or "false" => "No",
            _ => raw
        };

    private static string? FormatUuid(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 16) return null;

        bool allZero = true;
        bool allFf = true;
        foreach (var b in bytes)
        {
            if (b != 0) allZero = false;
            if (b != 0xFF) allFf = false;
        }
        if (allZero || allFf) return null;

        // SMBIOS stores the first three fields little-endian.
        return $"{bytes[3]:X2}{bytes[2]:X2}{bytes[1]:X2}{bytes[0]:X2}-" +
               $"{bytes[5]:X2}{bytes[4]:X2}-" +
               $"{bytes[7]:X2}{bytes[6]:X2}-" +
               $"{bytes[8]:X2}{bytes[9]:X2}-" +
               $"{bytes[10]:X2}{bytes[11]:X2}{bytes[12]:X2}{bytes[13]:X2}{bytes[14]:X2}{bytes[15]:X2}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);
}
