using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// A read-only Device Manager equivalent: problem devices first, then the device tree summary and the
/// specific views people look for (USB, HID, cameras, printers, Bluetooth, biometrics).
/// </summary>
public sealed class DevicesProvider : IInfoProvider
{
    public string Id => "devices";
    public string Category => "devices";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(2);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private sealed record PnpDevice(string Name, string Class, string? Manufacturer, string? DeviceId, int ErrorCode,
        string? Status, string? Service);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Devices & peripherals", Category = Category };
        const string source = "Source: WMI Win32_PnPEntity";

        var rows = Wmi.Query("SELECT Name, PNPClass, PNPDeviceID, Manufacturer, Status, ConfigManagerErrorCode, Service FROM Win32_PnPEntity");
        var devices = new List<PnpDevice>();

        foreach (var row in rows)
        {
            var name = row.Str("Name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            devices.Add(new PnpDevice(name!, row.Str("PNPClass") ?? "Unknown", row.Str("Manufacturer"),
                row.Str("PNPDeviceID"), row.Int("ConfigManagerErrorCode") ?? 0, row.Str("Status"), row.Str("Service")));
        }

        var problems = devices.Where(d => d.ErrorCode != 0).OrderBy(d => d.Class).ToList();
        var summary = section.Group("Summary");

        summary.Items.Add(InfoItem.Of("devices.total", "Plug and Play devices", devices.Count.ToString(), DetailLevel.Basic,
            "How many devices Windows knows about.", source));

        var problemItem = InfoItem.Of("devices.problems", "Devices with a problem", problems.Count.ToString(), DetailLevel.Basic,
            "Devices Windows reports a problem with, the same list Device Manager shows with a warning icon.", source);
        problemItem.Status = problems.Count == 0 ? InfoStatus.Ok : InfoStatus.Warning;
        summary.Items.Add(problemItem);

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.4);

        // ── Problem devices ─────────────────────────────────────────────────────
        var problemGroup = section.Group("Problem devices");
        if (problems.Count == 0)
        {
            problemGroup.Items.Add(InfoItem.Of("devices.noProblems", "Problem devices", "None", DetailLevel.Basic,
                "Every device Windows knows about is working.", source, InfoStatus.Ok));
        }

        foreach (var device in problems)
        {
            var item = InfoItem.Of($"devices.problem.{device.DeviceId}", device.Name,
                $"Code {device.ErrorCode}: {ExplainErrorCode(device.ErrorCode)}", DetailLevel.Basic,
                "A device Windows cannot use properly. Device Manager shows the same code in the device's properties.",
                source, InfoStatus.Warning);
            problemGroup.Items.Add(item);

            if (!string.IsNullOrWhiteSpace(device.Class))
            {
                problemGroup.Items.Add(InfoItem.Of($"devices.problem.{device.DeviceId}.class", $"{device.Name} class",
                    device.Class, DetailLevel.Everything, null, source));
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        // ── Class summary ───────────────────────────────────────────────────────
        var classGroup = section.Group("Device classes");
        foreach (var group in devices.GroupBy(d => d.Class).OrderByDescending(g => g.Count()))
        {
            classGroup.Items.Add(InfoItem.Of($"devices.class.{group.Key}", group.Key,
                group.Count() == 1 ? "1 device" : $"{group.Count()} devices", DetailLevel.Detailed,
                "Devices in this Device Manager class.", source));
        }

        // ── Specific views ──────────────────────────────────────────────────────
        AddView(section, "USB controllers and devices", "usb", devices.Where(d =>
            d.Class is "USB" or "USBDevice" or "HIDClass" or "WinUsb"
            || (d.DeviceId?.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ?? false)));
        AddView(section, "Keyboards, mice and HID", "hid", devices.Where(d =>
            d.Class is "Keyboard" or "Mouse" or "HIDClass"));
        AddView(section, "Cameras and imaging", "camera", devices.Where(d => d.Class is "Camera" or "Image" or "Media"));
        AddView(section, "Bluetooth", "bluetooth", devices.Where(d =>
            d.Class is "Bluetooth" || (d.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) && d.Class != "Unknown")));
        AddView(section, "Biometric (Windows Hello) devices", "biometric", devices.Where(d => d.Class is "Biometric"));
        AddView(section, "Sensors", "sensor", devices.Where(d => d.Class is "Sensor" or "SensorCollection"));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.85);

        // ── Printers ────────────────────────────────────────────────────────────
        var printerGroup = section.Group("Printers");
        var printers = Wmi.Query("SELECT Name, DriverName, PortName, WorkOffline, PrinterStatus, Default, Shared FROM Win32_Printer");
        if (printers.Count == 0)
        {
            printerGroup.Items.Add(InfoItem.MissingValue("devices.printer", "Printer", MissingReason.NotReported, DetailLevel.Basic,
                "Source: WMI Win32_Printer"));
        }

        foreach (var printer in printers)
        {
            var name = printer.Str("Name") ?? "Printer";
            var facts = new List<string>();
            if (printer.Str("DriverName") is { } driver) facts.Add(driver);
            if (printer.Str("PortName") is { } port) facts.Add(port);
            if (printer.Str("Default") is "True") facts.Add("default");
            if (printer.Str("WorkOffline") is "True") facts.Add("offline");

            var item = InfoItem.Of($"devices.printer.{name}", name, string.Join(" · ", facts), DetailLevel.Detailed,
                "A printer Windows has installed.", "Source: WMI Win32_Printer");
            if (printer.Str("WorkOffline") is "True") item.Status = InfoStatus.Info;
            printerGroup.Items.Add(item);
        }

        progress?.Report(1);
        return section;
    }

    private static void AddView(InfoSection section, string title, string prefix, IEnumerable<PnpDevice> devices)
    {
        var group = section.Group(title);
        var list = devices.ToList();

        if (list.Count == 0)
        {
            group.Items.Add(InfoItem.MissingValue($"devices.{prefix}.none", title, MissingReason.NotReported, DetailLevel.Detailed,
                "Source: WMI Win32_PnPEntity"));
            return;
        }

        foreach (var device in list.Take(40))
        {
            var item = InfoItem.Of($"devices.{prefix}.{device.DeviceId}", device.Name,
                device.ErrorCode == 0 ? device.Class : $"Code {device.ErrorCode}", DetailLevel.Detailed,
                "One device in this group.", "Source: WMI Win32_PnPEntity");
            if (device.ErrorCode != 0) item.Status = InfoStatus.Warning;
            group.Items.Add(item);
        }

        if (list.Count > 40)
        {
            group.Items.Add(InfoItem.Of($"devices.{prefix}.more", title, $"{list.Count - 40} more…", DetailLevel.Everything,
                "The list is capped so the page stays fast; the total is shown above.", "Source: WMI Win32_PnPEntity"));
        }
    }

    /// <summary>Plain-language meaning of a Device Manager problem code.</summary>
    public static string ExplainErrorCode(int code) => code switch
    {
        1 => "This device is not configured correctly.",
        3 => "Windows ran out of memory for this device.",
        10 => "This device cannot start.",
        12 => "There are not enough free resources for this device.",
        14 => "This device needs a restart to work.",
        18 => "Reinstall the drivers for this device.",
        19 => "Windows cannot read this device's settings from the registry.",
        21 => "Windows is removing this device.",
        22 => "This device is disabled.",
        24 => "This device is not present, does not work, or is missing its drivers.",
        28 => "The drivers for this device are not installed.",
        31 => "Windows cannot load the drivers for this device.",
        32 => "The driver for this device is disabled.",
        37 => "Windows cannot set up this device's driver.",
        43 => "The driver reported that the device failed.",
        45 => "This device is not connected right now.",
        52 => "Windows cannot verify the digital signature of this device's driver.",
        _ => "Windows reports a problem with this device."
    };
}
