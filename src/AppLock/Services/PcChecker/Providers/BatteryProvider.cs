using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Battery and power: charge state, design vs full-charge capacity and wear, chemistry, cycle count,
/// AC state and the current power mode. Desktop PCs report "No battery" instead of empty rows.
/// </summary>
public sealed class BatteryProvider : IInfoProvider
{
    public string Id => "battery";
    public string Category => "battery";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);

    public TimeSpan CacheLifetime => TimeSpan.Zero;

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Battery & power", Category = Category };
        var group = section.Group("Battery");
        const string wmiSource = "Source: WMI Win32_Battery";

        var wmiBattery = Wmi.Query("SELECT Name, EstimatedChargeRemaining, BatteryStatus, DesignCapacity, FullChargeCapacity, Chemistry, DeviceID FROM Win32_Battery");
        bool hasSystemBattery = GetSystemPowerStatus(out var power);
        bool present = wmiBattery.Count > 0 || (hasSystemBattery && power.BatteryFlag != 128);

        if (!present)
        {
            group.Items.Add(InfoItem.Of("battery.present", "Battery", "No battery", DetailLevel.Basic,
                "This PC runs from mains power only.", wmiSource, InfoStatus.None));
            AddPowerMode(section);
            progress?.Report(1);
            return section;
        }

        var staticData = Wmi.Query("SELECT DesignedCapacity, ManufactureName, DeviceName, SerialNumber FROM BatteryStaticData",
            @"root\WMI");
        var fullCharge = Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\WMI");
        var cycles = Wmi.Query("SELECT CycleCount FROM BatteryCycleCount", @"root\WMI");

        ulong designed = staticData.Count > 0 ? staticData[0].ULong("DesignedCapacity") ?? 0 : 0;
        ulong full = fullCharge.Count > 0 ? fullCharge[0].ULong("FullChargedCapacity") ?? 0 : 0;

        var battery = wmiBattery.FirstOrDefault();
        if (designed == 0 && battery != null) designed = battery.ULong("DesignCapacity") ?? 0;
        if (full == 0 && battery != null) full = battery.ULong("FullChargeCapacity") ?? 0;

        group.Items.Add(InfoItem.Of("battery.present", "Battery", "Present", DetailLevel.Basic,
            "This PC has an internal battery.", wmiSource, InfoStatus.Ok));

        if (hasSystemBattery && power.BatteryLifePercent != 255)
        {
            group.Items.Add(InfoItem.Of("battery.charge", "Charge", $"{power.BatteryLifePercent}%", DetailLevel.Basic,
                "How full the battery is right now.", "Source: GetSystemPowerStatus"));
        }

        if (hasSystemBattery)
        {
            var ac = power.ACLineStatus switch
            {
                0 => "On battery",
                1 => "Plugged in",
                _ => "Unknown"
            };
            group.Items.Add(InfoItem.Of("battery.ac", "Power source", ac, DetailLevel.Basic,
                "Whether the PC is running from the battery or from the charger.", "Source: GetSystemPowerStatus"));

            if (power.BatteryFlag != 255 && (power.BatteryFlag & 8) != 0)
            {
                group.Items.Add(InfoItem.Of("battery.charging", "Charging", "Yes", DetailLevel.Detailed, null,
                    "Source: GetSystemPowerStatus"));
            }

            if (power.BatteryLifeTime != -1 && power.BatteryLifeTime > 0)
            {
                group.Items.Add(InfoItem.Of("battery.remaining", "Estimated remaining",
                    PcFormat.Uptime(TimeSpan.FromSeconds(power.BatteryLifeTime)), DetailLevel.Detailed,
                    "Windows' estimate of how long the battery will last.", "Source: GetSystemPowerStatus"));
            }
        }

        if (designed > 0)
        {
            group.Items.Add(InfoItem.Of("battery.design", "Design capacity", $"{designed:N0} mWh", DetailLevel.Basic,
                "How much energy the battery held when it was new.", "Source: WMI BatteryStaticData"));
        }

        if (full > 0)
        {
            group.Items.Add(InfoItem.Of("battery.full", "Full-charge capacity", $"{full:N0} mWh", DetailLevel.Basic,
                "How much energy the battery holds now, fully charged.", "Source: WMI BatteryFullChargedCapacity"));
        }

        if (designed > 0 && full > 0)
        {
            var wear = (int)Math.Round((1 - (double)full / designed) * 100);
            if (wear < 0) wear = 0;

            var wearItem = InfoItem.Of("battery.wear", "Wear",
                $"{wear}% — {(wear > 60 ? "consider replacing the battery" : wear > 40 ? "noticeably worn" : wear > 15 ? "normal for its age" : "very healthy")}",
                DetailLevel.Basic, "How much capacity the battery has lost compared with when it was new.",
                "Computed from design and full-charge capacity");
            if (wear > 60) wearItem.Status = InfoStatus.Warning;
            else if (wear > 40) wearItem.Status = InfoStatus.Info;
            group.Items.Add(wearItem);
        }
        else
        {
            group.Items.Add(InfoItem.MissingValue("battery.wear", "Wear", MissingReason.NotReported, DetailLevel.Basic,
                "This battery does not report its design or full-charge capacity."));
        }

        var cycleCount = cycles.Count > 0 ? cycles[0].ULong("CycleCount") : null;
        group.Items.Add(cycleCount is > 0
            ? InfoItem.Of("battery.cycles", "Cycle count", cycleCount.Value.ToString("N0"), DetailLevel.Detailed,
                "How many full charge cycles the battery has been through.", "Source: WMI BatteryCycleCount")
            : InfoItem.MissingValue("battery.cycles", "Cycle count", MissingReason.NotReported, DetailLevel.Detailed));

        var chemistry = battery?.Str("Chemistry");
        if (!string.IsNullOrWhiteSpace(chemistry))
        {
            group.Items.Add(InfoItem.Of("battery.chemistry", "Chemistry", chemistry, DetailLevel.Detailed,
                "The battery's cell chemistry.", wmiSource));
        }

        var manufacturer = staticData.Count > 0 ? staticData[0].Str("ManufactureName") : null;
        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            group.Items.Add(InfoItem.Of("battery.manufacturer", "Manufacturer", manufacturer, DetailLevel.Detailed, null,
                "Source: WMI BatteryStaticData"));
        }

        var serial = staticData.Count > 0 ? staticData[0].Str("SerialNumber") : null;
        if (!string.IsNullOrWhiteSpace(serial))
        {
            group.Items.Add(InfoItem.Of("battery.serial", "Serial", serial, DetailLevel.Everything, null,
                "Source: WMI BatteryStaticData", sensitivity: InfoSensitivity.Identifier));
        }

        AddPowerMode(section);
        progress?.Report(1);
        return section;
    }

    private static void AddPowerMode(InfoSection section)
    {
        var group = section.Group("Power");
        const string overlayKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        var active = PcRegistry.MachineString(overlayKey, "ActivePowerScheme");
        if (!string.IsNullOrWhiteSpace(active))
        {
            group.Items.Add(InfoItem.Of("power.activeScheme", "Active power scheme", active, DetailLevel.Detailed,
                "The GUID of the power plan Windows is using. Friendly names come from powercfg and are not read here.",
                "Source: registry PowerSchemes"));
        }

        var fastStartup = PcRegistry.ReadLong(Microsoft.Win32.RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
        if (fastStartup.HasValue)
        {
            group.Items.Add(InfoItem.Of("power.fastStartup", "Fast Startup",
                fastStartup.Value != 0 ? "On" : "Off", DetailLevel.Detailed,
                "Fast Startup keeps part of Windows in memory when shutting down, which makes boot faster but a shutdown less complete.",
                "Source: registry Session Manager\\Power"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
