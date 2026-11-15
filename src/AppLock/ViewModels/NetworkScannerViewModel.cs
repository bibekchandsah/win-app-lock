using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.Network;

namespace AppLock.ViewModels;

public partial class NetworkScannerViewModel : ObservableObject, IDisposable
{
    private readonly NetworkScannerService _scanner;
    private readonly SettingsStore _settingsStore;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    // ── Observable properties ──────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<NetworkDeviceViewModel> _devices = new();

    [ObservableProperty]
    private bool _scannerEnabled;

    [ObservableProperty]
    private bool _alertOnNewDevice;

    [ObservableProperty]
    private int _scanIntervalSeconds;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "Scanner is off";

    [ObservableProperty]
    private string _lastScanText = string.Empty;

    // ── Constructor ────────────────────────────────────────────────────────────

    public NetworkScannerViewModel(NetworkScannerService scanner, SettingsStore settingsStore)
    {
        _scanner = scanner;
        _settingsStore = settingsStore;

        var settings = ServiceContainer.Settings.NetworkDeviceScanner;
        _scannerEnabled = settings.ScannerEnabled;
        _alertOnNewDevice = settings.AlertOnNewDevice;
        _scanIntervalSeconds = settings.ScanIntervalSeconds;

        _scanner.ScanCompleted += OnScanCompleted;
        _scanner.NewDeviceDetected += OnNewDeviceDetected;
    }

    public void SetDispatcherQueue(Microsoft.UI.Dispatching.DispatcherQueue dq)
        => _dispatcherQueue = dq;

    // ── Property change handlers ───────────────────────────────────────────────

    partial void OnScannerEnabledChanged(bool value)
    {
        var s = ServiceContainer.Settings;
        s.NetworkDeviceScanner.ScannerEnabled = value;
        _settingsStore.Save(s);

        if (value)
        {
            _scanner.Start();
            StatusText = "Scanning…";
        }
        else
        {
            _scanner.Stop();
            StatusText = "Scanner is off";
        }
    }

    partial void OnAlertOnNewDeviceChanged(bool value)
    {
        var s = ServiceContainer.Settings;
        s.NetworkDeviceScanner.AlertOnNewDevice = value;
        _settingsStore.Save(s);
    }

    partial void OnScanIntervalSecondsChanged(int value)
    {
        int clamped = Math.Max(5, Math.Min(300, value));
        var s = ServiceContainer.Settings;
        s.NetworkDeviceScanner.ScanIntervalSeconds = clamped;
        _settingsStore.Save(s);
    }

    // ── Public commands ────────────────────────────────────────────────────────

    /// <summary>
    /// Initial load on page open — populate from persisted settings.
    /// Only called once; subsequent updates go through <see cref="ApplySnapshot"/>.
    /// </summary>
    public void LoadDevices()
    {
        Devices.Clear();

        // Prune any legacy multicast/broadcast entries that may have been saved previously
        var known = ServiceContainer.Settings.KnownNetworkDevices;
        for (int i = known.Count - 1; i >= 0; i--)
        {
            var e = known[i];
            if (string.IsNullOrEmpty(e.MacAddress) ||
                e.MacAddress.StartsWith("01:00:5E", StringComparison.OrdinalIgnoreCase) ||
                e.MacAddress.StartsWith("33:33", StringComparison.OrdinalIgnoreCase) ||
                (System.Net.IPAddress.TryParse(e.IpAddress, out var ip) && ip.GetAddressBytes()[0] >= 224 && ip.GetAddressBytes()[0] <= 239))
            {
                known.RemoveAt(i);
            }
        }

        foreach (var entry in known.OrderBy(d => d.FirstSeen))
            Devices.Add(new NetworkDeviceViewModel(entry));
        UpdateStatusText();
    }

    /// <summary>
    /// In-place diff update — same pattern as ActiveConnectionViewModel.
    /// Updates mutable properties on existing rows, removes rows whose MAC
    /// disappeared from <paramref name="latestEntries"/>, and appends new ones.
    /// No Clear() means no flicker and no loss of user focus / scroll position.
    /// </summary>
    private void ApplySnapshot(System.Collections.Generic.IReadOnlyList<NetworkDeviceEntry> latestEntries)
    {
        // Build a lookup by MAC (MAC is the stable identity)
        var latestByMac = latestEntries
            .GroupBy(e => e.MacAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var seenMacs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Update or remove existing rows
        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            var vm = Devices[i];
            if (latestByMac.TryGetValue(vm.MacAddress, out var updated))
            {
                seenMacs.Add(vm.MacAddress);
                // Mutate only the mutable fields — no row replace, no flicker
                vm.UpdateFromEntry(updated);
            }
            else
            {
                // Device no longer in the persisted list (was forgotten externally)
                Devices.RemoveAt(i);
            }
        }

        // 2. Append genuinely new entries (sorted by first-seen, appended at end)
        foreach (var entry in latestEntries.OrderBy(e => e.FirstSeen))
        {
            if (!seenMacs.Contains(entry.MacAddress))
                Devices.Add(new NetworkDeviceViewModel(entry));
        }

        UpdateStatusText();
    }

    /// <summary>Trigger a one-shot scan immediately (without changing the scanner loop).</summary>
    public async Task ScanNowAsync(CancellationToken ct = default)
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Scanning…";

        try
        {
            await _scanner.ScanOnceAsync(ct);
            LastScanText = $"Last scan: {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void TrustDevice(NetworkDeviceViewModel vm)
    {
        vm.IsTrusted = true;
        vm.Entry.IsTrusted = true;
        _settingsStore.Save(ServiceContainer.Settings);
    }

    public void ForgetDevice(NetworkDeviceViewModel vm)
    {
        Devices.Remove(vm);
        ServiceContainer.Settings.KnownNetworkDevices.Remove(vm.Entry);
        _settingsStore.Save(ServiceContainer.Settings);
        UpdateStatusText();
    }

    public void ForgetAll()
    {
        Devices.Clear();
        ServiceContainer.Settings.KnownNetworkDevices.Clear();
        _settingsStore.Save(ServiceContainer.Settings);
        UpdateStatusText();
    }

    // ── Scanner event handlers ─────────────────────────────────────────────────

    private void OnScanCompleted(object? sender, System.Collections.Generic.IReadOnlyList<NetworkDeviceEntry> snapshot)
    {
        Dispatch(() =>
        {
            // In-place diff — no Clear(), no flicker
            var persisted = ServiceContainer.Settings.KnownNetworkDevices;
            ApplySnapshot(persisted);
            LastScanText = $"Last scan: {DateTime.Now:HH:mm:ss}";
            if (ScannerEnabled)
                StatusText = $"Watching · {Devices.Count} device{(Devices.Count == 1 ? "" : "s")} known";
        });
    }

    private void OnNewDeviceDetected(object? sender, NetworkDeviceEntry device)
    {
        Dispatch(() =>
        {
            string label = string.IsNullOrEmpty(device.FriendlyName) ? device.IpAddress : device.FriendlyName;
            ServiceContainer.NotificationService.ShowWarning(
                "New device on your network",
                $"{label}  ·  {device.MacAddress}");
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void UpdateStatusText()
    {
        if (!ScannerEnabled)
        {
            StatusText = "Scanner is off";
            return;
        }
        StatusText = $"Watching · {Devices.Count} device{(Devices.Count == 1 ? "" : "s")} known";
    }

    private void Dispatch(Action action)
    {
        if (_dispatcherQueue != null)
            _dispatcherQueue.TryEnqueue(() => action());
        else
            action();
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.ScanCompleted -= OnScanCompleted;
        _scanner.NewDeviceDetected -= OnNewDeviceDetected;
        _scanner.Stop();
    }
}

/// <summary>UI wrapper around a <see cref="NetworkDeviceEntry"/>.</summary>
public partial class NetworkDeviceViewModel : ObservableObject
{
    public NetworkDeviceEntry Entry { get; }

    [ObservableProperty] private bool _isTrusted;
    [ObservableProperty] private string _friendlyName;
    [ObservableProperty] private string _ipAddress;
    [ObservableProperty] private string _lastSeen;
    [ObservableProperty] private string _hostname;
    [ObservableProperty] private string _deviceIcon;
    [ObservableProperty] private bool _isThisDevice;

    public string MacAddress => Entry.MacAddress;
    public string FirstSeen  => Entry.FirstSeen.ToString("yyyy-MM-dd HH:mm");
    public string Manufacturer => MacVendorDatabase.GetVendor(Entry.MacAddress);

    /// <summary>
    /// Device is "online" if last seen within 2× scan interval threshold.
    /// </summary>
    public bool IsOnline
    {
        get
        {
            var thresholdSeconds = ServiceContainer.Settings.NetworkDeviceScanner.ScanIntervalSeconds * 2;
            var elapsed = (DateTime.Now - Entry.LastSeen).TotalSeconds;
            return elapsed < thresholdSeconds;
        }
    }

    public string StatusText => IsOnline ? "Online" : "Offline";

    public NetworkDeviceViewModel(NetworkDeviceEntry entry)
    {
        Entry = entry;
        _isTrusted    = entry.IsTrusted;
        _friendlyName = entry.FriendlyName;
        _ipAddress    = entry.IpAddress;
        _lastSeen     = entry.LastSeen.ToString("yyyy-MM-dd HH:mm");
        _hostname     = entry.Hostname;
        _isThisDevice = entry.IsThisDevice;

        // If saved with Unknown type, infer it now
        if (entry.DeviceType == AppLock.Models.NetworkDeviceType.Unknown)
        {
            if (MacVendorDatabase.IsRandomMac(entry.MacAddress))
            {
                entry.DeviceType = AppLock.Models.NetworkDeviceType.Phone;
            }
            else
            {
                var v = MacVendorDatabase.GetVendor(entry.MacAddress);
                if (v.Contains("Motorola", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("Huawei", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("Oppo", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("Vivo", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("OnePlus", StringComparison.OrdinalIgnoreCase))
                {
                    entry.DeviceType = AppLock.Models.NetworkDeviceType.Phone;
                }
                else if (v.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                {
                    entry.DeviceType = AppLock.Models.NetworkDeviceType.Computer;
                }
                else if (v.Contains("TP-Link", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("D-Link", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("Netgear", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("ASUS", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("Nokia", StringComparison.OrdinalIgnoreCase) ||
                         v.Contains("Cisco", StringComparison.OrdinalIgnoreCase))
                {
                    entry.DeviceType = AppLock.Models.NetworkDeviceType.Router;
                }
            }
        }

        _deviceIcon = GlyphForType(entry.DeviceType);
    }

    /// <summary>
    /// Mutate observable properties in-place from a fresher entry snapshot.
    /// Only fields that actually changed are written so MVVM diff is minimal.
    /// </summary>
    public void UpdateFromEntry(NetworkDeviceEntry fresh)
    {
        if (fresh.IpAddress != Entry.IpAddress)
        {
            Entry.IpAddress = fresh.IpAddress;
            IpAddress = fresh.IpAddress;
        }

        var freshLastSeen = fresh.LastSeen.ToString("yyyy-MM-dd HH:mm");
        if (freshLastSeen != LastSeen)
        {
            Entry.LastSeen = fresh.LastSeen;
            LastSeen = freshLastSeen;
            // LastSeen affects IsOnline, so notify both
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(StatusText));
        }

        if (fresh.IsTrusted != Entry.IsTrusted)
        {
            Entry.IsTrusted = fresh.IsTrusted;
            IsTrusted = fresh.IsTrusted;
        }

        if (!string.IsNullOrEmpty(fresh.Hostname) && fresh.Hostname != Entry.Hostname)
        {
            Entry.Hostname = fresh.Hostname;
            Hostname = fresh.Hostname;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(SubtitleHostname));
        }

        // Always update device type if fresh scan has better information
        if (fresh.DeviceType != Entry.DeviceType || DeviceIcon != GlyphForType(fresh.DeviceType))
        {
            Entry.DeviceType = fresh.DeviceType;
            DeviceIcon = GlyphForType(fresh.DeviceType);
            OnPropertyChanged(nameof(DisplayName));
        }

        if (fresh.IsThisDevice != Entry.IsThisDevice)
        {
            Entry.IsThisDevice = fresh.IsThisDevice;
            IsThisDevice = fresh.IsThisDevice;
            OnPropertyChanged(nameof(DisplayName));
        }

        // FriendlyName: only push from entry if the user hasn't set one locally
        if (!string.IsNullOrEmpty(fresh.FriendlyName) && fresh.FriendlyName != FriendlyName)
        {
            Entry.FriendlyName = fresh.FriendlyName;
            FriendlyName = fresh.FriendlyName;
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    partial void OnFriendlyNameChanged(string value)
    {
        Entry.FriendlyName = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIsTrustedChanged(bool value)   => Entry.IsTrusted  = value;
    partial void OnIpAddressChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnHostnameChanged(string value)  => OnPropertyChanged(nameof(DisplayName));

    /// <summary>
    /// Priority: user FriendlyName → This PC → Router/Gateway → resolved Hostname →
    /// Random MAC ("Mobile device") → Vendor + short MAC → DeviceType + short MAC.
    /// Never returns raw IP address so the title and subtitle are distinct and meaningful.
    /// </summary>
    public string DisplayName
    {
        get
        {
            // 1. Manual user friendly name
            if (!string.IsNullOrWhiteSpace(FriendlyName))
                return FriendlyName;

            // 2. Special label for This PC
            if (IsThisDevice)
            {
                var machine = !string.IsNullOrWhiteSpace(Hostname) ? Hostname : Environment.MachineName;
                return $"This PC ({machine})";
            }

            // 3. Special label for Router / Gateway
            if (Entry.DeviceType == AppLock.Models.NetworkDeviceType.Router)
            {
                if (!string.IsNullOrWhiteSpace(Hostname) && !Hostname.Equals("router", StringComparison.OrdinalIgnoreCase))
                    return $"Router ({Hostname})";
                var mfg = Manufacturer;
                if (!string.IsNullOrWhiteSpace(mfg) && mfg != "Unknown")
                    return $"Router / Gateway ({mfg})";
                return "Router / Gateway";
            }

            // 4. Discovered hostname (via DNS, NetBIOS, etc.)
            if (!string.IsNullOrWhiteSpace(Hostname))
                return Hostname;

            // 5. Short MAC for identification
            var macParts = MacAddress.Split(':');
            var shortMac = macParts.Length == 6 ? string.Join(":", macParts[3..6]) : MacAddress;

            // 6. Randomized / Private MAC address (iOS, Android, Windows)
            if (MacVendorDatabase.IsRandomMac(MacAddress))
            {
                return $"Mobile device ({shortMac})";
            }

            // 7. Known manufacturer
            var manufacturer = Manufacturer;
            if (!string.IsNullOrWhiteSpace(manufacturer) && manufacturer != "Unknown")
            {
                return $"{manufacturer} ({shortMac})";
            }

            // 8. Fallback by inferred device type + short MAC
            var typeName = Entry.DeviceType switch
            {
                AppLock.Models.NetworkDeviceType.Phone       => "Phone",
                AppLock.Models.NetworkDeviceType.Computer    => "Computer",
                AppLock.Models.NetworkDeviceType.Printer     => "Printer",
                AppLock.Models.NetworkDeviceType.SmartTV     => "Smart TV",
                AppLock.Models.NetworkDeviceType.GameConsole => "Console",
                AppLock.Models.NetworkDeviceType.Camera      => "Camera",
                AppLock.Models.NetworkDeviceType.Speaker     => "Speaker",
                AppLock.Models.NetworkDeviceType.IoT         => "Smart device",
                _                                            => "Network device"
            };

            return $"{typeName} ({shortMac})";
        }
    }

    /// <summary>
    /// Hostname shown in the subtitle only if different from DisplayName.
    /// </summary>
    public string SubtitleHostname => !string.IsNullOrEmpty(Hostname) && Hostname != DisplayName ? Hostname : string.Empty;

    public void RefreshDisplay()
    {
        DeviceIcon = GlyphForType(Entry.DeviceType);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SubtitleHostname));
        OnPropertyChanged(nameof(Manufacturer));
        OnPropertyChanged(nameof(DeviceIcon));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(StatusText));
    }

    public string GlyphForTypePublic(AppLock.Models.NetworkDeviceType type) => GlyphForType(type);

    // ── Icon glyphs (Segoe Fluent Icons / MDL2) ────────────────────────────────
    public static string GlyphForType(AppLock.Models.NetworkDeviceType type) => type switch
    {
        AppLock.Models.NetworkDeviceType.ThisDevice  => "\uE721", // Desktop PC / Computer
        AppLock.Models.NetworkDeviceType.Router      => "\uE774", // Globe / Router Gateway
        AppLock.Models.NetworkDeviceType.Phone       => "\uE8EA", // Cell phone / Tablet
        AppLock.Models.NetworkDeviceType.Computer    => "\uE7F8", // Laptop / Computer
        AppLock.Models.NetworkDeviceType.Printer     => "\uE749", // Print
        AppLock.Models.NetworkDeviceType.SmartTV     => "\uE7F4", // TV / Monitor
        AppLock.Models.NetworkDeviceType.GameConsole => "\uE7FC", // Game console / Xbox
        AppLock.Models.NetworkDeviceType.Speaker     => "\uE767", // Volume / Speaker
        AppLock.Models.NetworkDeviceType.Camera      => "\uE8B8", // Camera
        AppLock.Models.NetworkDeviceType.IoT         => "\uECF0", // IOT / smart home
        _                                            => "\uE770", // Connected Devices silhouette
    };
}
