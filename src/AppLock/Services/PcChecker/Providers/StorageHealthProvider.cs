using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Drive health from the storage reliability counters Windows exposes for NVMe and SMART-capable
/// drives. This is a Tier 2 (administrator) source: without elevation the rows say so instead of
/// pretending the drive reports nothing.
/// </summary>
public sealed class StorageHealthProvider : IInfoProvider
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public string Id => "storage.health";
    public string Category => "storage";
    public InfoTier Tier => InfoTier.Admin;
    public TimeSpan Timeout => TimeSpan.FromSeconds(12);

    /// <summary>Counters are live readings, never cached.</summary>
    public TimeSpan CacheLifetime => TimeSpan.Zero;

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Storage health", Category = Category };
        var group = section.Group("Drive health (SMART / NVMe)");

        var disks = Wmi.Query("SELECT DeviceId, FriendlyName, HealthStatus, MediaType FROM MSFT_PhysicalDisk", StorageScope);
        var counters = Wmi.Query(
            "SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, StartStopCycleCount, ReadErrorsTotal, " +
            "ReadErrorsUncorrected, WriteErrorsTotal, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter", StorageScope);

        if (counters.Count == 0)
        {
            var reason = ElevationHelper.IsElevated ? MissingReason.NotReported : MissingReason.NeedsAdmin;
            group.Items.Add(InfoItem.MissingValue("storage.health.overall", "Drive health", reason, DetailLevel.Basic,
                "Source: WMI MSFT_StorageReliabilityCounter"));
            group.Items.Add(InfoItem.MissingValue("storage.health.temperature", "Drive temperature", reason, DetailLevel.Basic,
                "Source: WMI MSFT_StorageReliabilityCounter"));
            group.Items.Add(InfoItem.MissingValue("storage.health.wear", "Wear", reason, DetailLevel.Basic,
                "Source: WMI MSFT_StorageReliabilityCounter"));
            group.Items.Add(InfoItem.MissingValue("storage.health.powerOn", "Power-on hours", reason, DetailLevel.Detailed,
                "Source: WMI MSFT_StorageReliabilityCounter"));
            progress?.Report(1);
            return section;
        }

        int index = 0;
        foreach (var counter in counters)
        {
            var deviceId = counter.Str("DeviceId");
            var disk = disks.FirstOrDefault(d => d.Str("DeviceId") == deviceId);
            var name = disk?.Str("FriendlyName") ?? $"Disk {deviceId ?? index.ToString()}";
            var prefix = $"storage.health.{deviceId ?? index.ToString()}";

            var health = disk?.Str("HealthStatus") ?? "Unknown";
            var healthItem = InfoItem.Of($"{prefix}.overall", $"{name} health", health, DetailLevel.Basic,
                "The drive's own health assessment.", "Source: WMI MSFT_PhysicalDisk");
            healthItem.Status = health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? InfoStatus.Ok
                : health.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? InfoStatus.Warning
                : health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) ? InfoStatus.Problem
                : InfoStatus.None;
            group.Items.Add(healthItem);

            int? temperature = counter.Int("Temperature");
            if (temperature is > 0)
            {
                var tempItem = InfoItem.Of($"{prefix}.temperature", $"{name} temperature", $"{temperature} °C", DetailLevel.Basic,
                    "Current drive temperature. SSDs are comfortable below 70 °C.", "Source: WMI storage reliability counter");
                if (temperature >= ServiceContainer.Settings.PcChecker.DiskTempWarn) tempItem.Status = InfoStatus.Warning;
                group.Items.Add(tempItem);
            }

            int? wear = counter.Int("Wear");
            if (wear is >= 0)
            {
                var wearItem = InfoItem.Of($"{prefix}.wear", $"{name} wear", $"{wear}% — {WearReading(wear.Value)}", DetailLevel.Basic,
                    "How much of the drive's write endurance has been used.", "Source: WMI storage reliability counter");
                if (wear >= 80) wearItem.Status = InfoStatus.Warning;
                group.Items.Add(wearItem);
            }

            int? hours = counter.Int("PowerOnHours");
            if (hours is > 0)
            {
                group.Items.Add(InfoItem.Of($"{prefix}.powerOn", $"{name} power-on hours",
                    $"{hours:N0} h ({hours.Value / 24.0 / 365.0:0.0} years)", DetailLevel.Detailed,
                    "How long the drive has been powered on.", "Source: WMI storage reliability counter"));
            }

            int? cycles = counter.Int("StartStopCycleCount");
            if (cycles is > 0)
            {
                group.Items.Add(InfoItem.Of($"{prefix}.cycles", $"{name} power cycles", cycles.Value.ToString("N0"),
                    DetailLevel.Everything, "How many times the drive has been switched on.",
                    "Source: WMI storage reliability counter"));
            }

            var readErrors = counter.Int("ReadErrorsUncorrected") ?? 0;
            var writeErrors = counter.Int("WriteErrorsUncorrected") ?? 0;
            var errorsItem = InfoItem.Of($"{prefix}.errors", $"{name} uncorrected errors",
                $"{readErrors} read · {writeErrors} write", DetailLevel.Detailed,
                "Uncorrected errors mean data could not be read or written. Any increase is worth watching.",
                "Source: WMI storage reliability counter");
            if (readErrors > 0 || writeErrors > 0) errorsItem.Status = InfoStatus.Warning;
            group.Items.Add(errorsItem);

            index++;
            ct.ThrowIfCancellationRequested();
        }

        progress?.Report(1);
        return section;
    }

    /// <summary>Plain-language reading of a wear percentage.</summary>
    public static string WearReading(int wear) => wear switch
    {
        <= 5 => "very healthy",
        <= 20 => "healthy",
        <= 50 => "normal for its age",
        <= 80 => "getting worn",
        _ => "near end of endurance",
    };
}
