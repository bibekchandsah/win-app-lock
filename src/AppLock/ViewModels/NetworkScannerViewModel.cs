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
        foreach (var entry in ServiceContainer.Settings.KnownNetworkDevices.OrderBy(d => d.FirstSeen))
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
        _deviceIcon   = GlyphForType(entry.DeviceType);
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
        }

        // Always update device type if fresh scan has better information
        if (fresh.DeviceType != Entry.DeviceType)
        {
            Entry.DeviceType = fresh.DeviceType;
            DeviceIcon = GlyphForType(fresh.DeviceType);
        }

        if (fresh.IsThisDevice != Entry.IsThisDevice)
        {
            Entry.IsThisDevice = fresh.IsThisDevice;
            IsThisDevice = fresh.IsThisDevice;
        }

        // FriendlyName: only push from entry if the user hasn't set one locally
        if (!string.IsNullOrEmpty(fresh.FriendlyName) && fresh.FriendlyName != FriendlyName)
        {
            Entry.FriendlyName = fresh.FriendlyName;
            FriendlyName = fresh.FriendlyName;
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
    /// Priority: user FriendlyName → resolved Hostname → Vendor + short MAC → IP address.
    /// Special labels for "This PC" and "Router/Gateway".
    /// </summary>
    public string DisplayName
    {
        get
        {
            // Special label for this PC
            if (IsThisDevice)
            {
                var baseName = !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName :
                              !string.IsNullOrWhiteSpace(Hostname) ? Hostname : "This PC";
                return baseName;
            }

            // Special label for router/gateway
            if (Entry.DeviceType == AppLock.Models.NetworkDeviceType.Router && 
                string.IsNullOrWhiteSpace(FriendlyName) && 
                string.IsNullOrWhiteSpace(Hostname))
            {
                return "Router / Gateway";
            }

            if (!string.IsNullOrWhiteSpace(FriendlyName))
                return FriendlyName;
            
            if (!string.IsNullOrWhiteSpace(Hostname))
                return Hostname;
            
            // Fallback: Show "Vendor (short MAC)" for better readability
            var manufacturer = Manufacturer;
            if (!string.IsNullOrEmpty(manufacturer) && manufacturer != "Unknown")
            {
                // Get last 3 octets of MAC for short identifier
                var macParts = MacAddress.Split(':');
                if (macParts.Length == 6)
                {
                    var shortMac = string.Join(":", macParts[3..6]);
                    return $"{manufacturer} ({shortMac})";
                }
            }
            
            return IpAddress;
        }
    }

    // ── Icon glyphs (Segoe Fluent Icons / MDL2) ────────────────────────────────
    private static string GlyphForType(AppLock.Models.NetworkDeviceType type) => type switch
    {
        AppLock.Models.NetworkDeviceType.ThisDevice  => "\uE977", // PC / This PC
        AppLock.Models.NetworkDeviceType.Router      => "\uE968", // Network / Router
        AppLock.Models.NetworkDeviceType.Phone       => "\uE8EA", // Cell phone
        AppLock.Models.NetworkDeviceType.Computer    => "\uE7F4", // PC / Laptop
        AppLock.Models.NetworkDeviceType.Printer     => "\uE749", // Print
        AppLock.Models.NetworkDeviceType.SmartTV     => "\uE7F4", // TV / Monitor
        AppLock.Models.NetworkDeviceType.GameConsole => "\uE7FC", // Game console / Xbox
        AppLock.Models.NetworkDeviceType.Speaker     => "\uE767", // Volume / Speaker
        AppLock.Models.NetworkDeviceType.Camera      => "\uE8B8", // Camera
        AppLock.Models.NetworkDeviceType.IoT         => "\uECF0", // IOT / smart home
        _                                            => "\uE968", // Generic network node
    };
}
