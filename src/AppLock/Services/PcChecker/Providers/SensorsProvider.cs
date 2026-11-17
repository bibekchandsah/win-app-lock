using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// The driver-free part of the sensors story: ACPI thermal zones, fan tachometers exposed through WMI and the
/// temperatures the drives and battery already report. Per-core clocks, voltages and DIY fan control need a
/// kernel driver, which this app deliberately does not install.
/// </summary>
public sealed class SensorsProvider : IInfoProvider
{
    public string Id => "sensors";
    public string Category => "sensors";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(12);
    public TimeSpan CacheLifetime => TimeSpan.FromSeconds(5);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Sensors", Category = Category };

        var thermal = section.Group("Thermal zones");
        var zones = Wmi.Query("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi");

        if (zones.Count == 0)
        {
            var noZones = InfoItem.Of("sensors.thermal", "ACPI thermal zones", null, DetailLevel.Basic,
                "The firmware did not expose any thermal zone to Windows.",
                "Source: WMI MSAcpi_ThermalZoneTemperature");
            noZones.Missing = MissingReason.NotReported;
            thermal.Items.Add(noZones);
        }

        foreach (var zone in zones)
        {
            // The value is tenths of a kelvin.
            var raw = zone.ULong("CurrentTemperature");
            var celsius = raw is { } value && value > 0 ? value / 10d - 273.15 : (double?)null;

            var item = InfoItem.Of($"sensors.thermal.{zone.Str("InstanceName")}", zone.Str("InstanceName") ?? "Thermal zone",
                celsius is { } temperature ? $"{temperature:0.0} °C" : null, DetailLevel.Basic,
                "A temperature the firmware reports for this zone. Laptops often report one shared zone.",
                "Source: WMI MSAcpi_ThermalZoneTemperature");

            if (celsius is { } hot && hot >= 90) item.Status = InfoStatus.Warning;
            thermal.Items.Add(item);
        }

        var fans = section.Group("Fans");
        var fanProbes = Wmi.Query("SELECT Name, DesiredSpeed, Status FROM Win32_Fan");

        if (fanProbes.Count == 0)
        {
            var noFans = InfoItem.Of("sensors.fans", "Fan speeds", null, DetailLevel.Basic,
                "Firmware fan tachometers are usually not exposed to Windows without a driver; this is a normal gap, not a fault.",
                "Source: WMI Win32_Fan");
            noFans.Missing = MissingReason.NotReported;
            fans.Items.Add(noFans);
        }

        foreach (var fan in fanProbes)
        {
            fans.Items.Add(InfoItem.Of($"sensors.fan.{fan.Str("Name")}", fan.Str("Name") ?? "Fan",
                fan.ULong("DesiredSpeed") is { } rpm && rpm > 0 ? $"{rpm} rpm (desired)" : fan.Str("Status"),
                DetailLevel.Basic, "A fan the firmware reports. Most report a target speed rather than a live reading.",
                "Source: WMI Win32_Fan"));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        var drives = section.Group("Drive temperatures");
        var storageHealth = Wmi.Query("SELECT FriendlyName, Temperature, TemperatureMax FROM MSFT_StorageReliabilityCounter",
            @"root\Microsoft\Windows\Storage");
        var found = false;

        foreach (var counter in storageHealth)
        {
            if (counter.ULong("Temperature") is not { } temperature || temperature == 0) continue;
            found = true;

            var max = counter.ULong("TemperatureMax");
            var item = InfoItem.Of($"sensors.drive.{counter.Str("FriendlyName")}", counter.Str("FriendlyName") ?? "Drive",
                max is { } limit && limit > 0 ? $"{temperature} °C (limit {limit} °C)" : $"{temperature} °C",
                DetailLevel.Basic, "Temperature reported by the drive itself (S.M.A.R.T.).",
                "Source: MSFT_StorageReliabilityCounter");

            if (max is { } threshold && temperature >= threshold - 5) item.Status = InfoStatus.Warning;
            drives.Items.Add(item);
        }

        if (!found)
        {
            var noDriveTemp = InfoItem.Of("sensors.drive.none", "Drive temperatures", null, DetailLevel.Basic,
                ElevationHelper.IsElevated
                    ? "None of the drives report a temperature. Many SATA and older NVMe drives do not."
                    : "Reading drive temperatures needs administrator rights. Restart AppLock as administrator.",
                "Source: MSFT_StorageReliabilityCounter");
            noDriveTemp.Missing = ElevationHelper.IsElevated ? MissingReason.NotReported : MissingReason.NeedsAdmin;
            drives.Items.Add(noDriveTemp);
        }

        var battery = section.Group("Battery temperature");
        var batteryHeat = Wmi.Query("SELECT Temperature, InstanceName FROM BatteryTemperature", @"root\wmi");

        if (batteryHeat.Count == 0)
        {
            var noBatteryTemp = InfoItem.Of("sensors.battery.temp", "Battery temperature", null, DetailLevel.Basic,
                "This battery does not report a temperature to Windows.",
                "Source: WMI BatteryTemperature");
            noBatteryTemp.Missing = MissingReason.NotReported;
            battery.Items.Add(noBatteryTemp);
        }

        foreach (var entry in batteryHeat)
        {
            var tenthsKelvin = entry.ULong("Temperature");
            var celsius = tenthsKelvin is { } value && value > 0 ? value / 10d - 273.15 : (double?)null;

            battery.Items.Add(InfoItem.Of($"sensors.battery.{entry.Str("InstanceName")}", "Battery",
                celsius is { } temperature ? $"{temperature:0.0} °C" : null, DetailLevel.Basic,
                "Battery temperature as reported by the battery controller.",
                "Source: WMI BatteryTemperature"));
        }

        progress?.Report(1);
        return section;
    }
}
