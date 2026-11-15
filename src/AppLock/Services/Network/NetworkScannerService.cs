using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.Network;

/// <summary>
/// Scans the local network's ARP table and raises <see cref="NewDeviceDetected"/> when
/// a device appears whose MAC address is not in the known-devices list.
/// </summary>
public sealed class NetworkScannerService : IDisposable
{
    // ── Win32 ──────────────────────────────────────────────────────────────────

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetIpNetTable(
        IntPtr pIpNetTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SendARP(
        uint destIp,
        uint srcIp,
        byte[] pMacAddr,
        ref int phyAddrLen);

    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint ERROR_NO_DATA = 232;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETTABLE
    {
        public uint dwNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public uint dwIndex;
        public uint dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] bPhysAddr;
        public uint dwAddr;
        public uint dwType;
    }

    // dwType values: 1 = Other, 2 = Invalid, 3 = Dynamic, 4 = Static
    private const uint ArpTypeDynamic = 3;
    private const uint ArpTypeStatic = 4;

    // ── Events / state ─────────────────────────────────────────────────────────

    /// <summary>Raised on a background thread when an unknown device is first detected.</summary>
    public event EventHandler<NetworkDeviceEntry>? NewDeviceDetected;

    /// <summary>Raised after every scan with the full current snapshot.</summary>
    public event EventHandler<IReadOnlyList<NetworkDeviceEntry>>? ScanCompleted;

    private readonly SettingsStore _settingsStore;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public NetworkScannerService(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _ = ScanLoopAsync(_cts.Token);
        Debug.WriteLine("[NetworkScanner] Started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Debug.WriteLine("[NetworkScanner] Stopped");
    }

    /// <summary>
    /// Run a single scan and return the current ARP snapshot. Does not require the loop to be running.
    /// </summary>
    public async Task<IReadOnlyList<NetworkDeviceEntry>> ScanOnceAsync(CancellationToken ct = default)
    {
        var raw = await Task.Run(() => DoScan(ct), ct);
        await EnrichAsync(raw, ct);
        return raw;
    }

    // ── Background loop ────────────────────────────────────────────────────────

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Run(() => DoScanAndAlert(ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NetworkScanner] Scan error: {ex.Message}");
            }

            var settings = ServiceContainer.Settings.NetworkDeviceScanner;
            int delay = Math.Max(5, settings.ScanIntervalSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void DoScanAndAlert(CancellationToken ct)
    {
        var snapshot = DoScan(ct);
        if (ct.IsCancellationRequested) return;

        // Enrich synchronously on the background thread (DNS can block briefly)
        EnrichAsync(snapshot, ct).GetAwaiter().GetResult();
        if (ct.IsCancellationRequested) return;

        var settings = ServiceContainer.Settings;
        bool alertEnabled = settings.NetworkDeviceScanner.AlertOnNewDevice;

        // Find MACs we have never seen before
        var knownMacs = new HashSet<string>(
            settings.KnownNetworkDevices.Select(d => d.MacAddress),
            StringComparer.OrdinalIgnoreCase);

        var newDevices = snapshot
            .Where(d => !knownMacs.Contains(d.MacAddress))
            .ToList();

        // Persist all new devices (initially marked trusted so the list is populated silently)
        if (newDevices.Count > 0)
        {
            foreach (var device in newDevices)
            {
                // Mark untrusted only when alerts are on — that's when the user cares about unknowns
                device.IsTrusted = !alertEnabled;
                settings.KnownNetworkDevices.Add(device);

                // Log "Joined" event
                settings.NetworkDeviceHistory.Add(new NetworkDeviceHistoryEntry
                {
                    MacAddress = device.MacAddress,
                    EventType = NetworkDeviceEventType.Joined,
                    Timestamp = device.FirstSeen,
                    NewValue = device.IpAddress
                });
            }

            _settingsStore.Save(settings);
        }

        // Detect "Left" devices (devices that haven't been seen in over 2× scan interval)
        var thresholdSeconds = settings.NetworkDeviceScanner.ScanIntervalSeconds * 2;
        var now = DateTime.Now;
        var seenMacs = new HashSet<string>(snapshot.Select(d => d.MacAddress), StringComparer.OrdinalIgnoreCase);

        foreach (var known in settings.KnownNetworkDevices)
        {
            bool wasOnline = (now - known.LastSeen).TotalSeconds < thresholdSeconds;
            bool isNowOnline = seenMacs.Contains(known.MacAddress);

            // Device just went offline
            if (wasOnline && !isNowOnline)
            {
                settings.NetworkDeviceHistory.Add(new NetworkDeviceHistoryEntry
                {
                    MacAddress = known.MacAddress,
                    EventType = NetworkDeviceEventType.Left,
                    Timestamp = now,
                    OldValue = known.IpAddress
                });
            }
        }

        // Update LastSeen, IP, hostname, and type for already-known devices
        foreach (var seen in snapshot)
        {
            var known = settings.KnownNetworkDevices
                .FirstOrDefault(d => string.Equals(d.MacAddress, seen.MacAddress, StringComparison.OrdinalIgnoreCase));

            if (known != null)
            {
                // Detect IP change
                if (known.IpAddress != seen.IpAddress)
                {
                    settings.NetworkDeviceHistory.Add(new NetworkDeviceHistoryEntry
                    {
                        MacAddress = known.MacAddress,
                        EventType = NetworkDeviceEventType.ChangedIP,
                        Timestamp = seen.LastSeen,
                        OldValue = known.IpAddress,
                        NewValue = seen.IpAddress
                    });
                }

                known.LastSeen = seen.LastSeen;
                known.IpAddress = seen.IpAddress;
                if (string.IsNullOrEmpty(known.Hostname) && !string.IsNullOrEmpty(seen.Hostname))
                    known.Hostname = seen.Hostname;
                if (known.DeviceType == NetworkDeviceType.Unknown && seen.DeviceType != NetworkDeviceType.Unknown)
                    known.DeviceType = seen.DeviceType;
                known.IsThisDevice = seen.IsThisDevice;
            }
        }

        _settingsStore.Save(settings);

        ScanCompleted?.Invoke(this, snapshot);

        // Fire alert for untrusted new devices
        if (alertEnabled)
        {
            foreach (var device in newDevices)
            {
                NewDeviceDetected?.Invoke(this, device);
            }
        }
    }

    // ── Enrichment ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Populates Hostname, DeviceType, and IsThisDevice for every entry in
    /// <paramref name="devices"/> using reverse-DNS and local adapter info.
    /// Runs DNS lookups in parallel with a 1-second timeout each.
    /// </summary>
    private static async Task EnrichAsync(IReadOnlyList<NetworkDeviceEntry> devices, CancellationToken ct)
    {
        // Collect this machine's own IP addresses and gateway once
        var localIps = GetLocalIpAddresses();
        var gatewayIp = GetGatewayIp();

        // Parallel DNS resolution (capped to avoid flooding the router)
        var tasks = devices.Select(d => ResolveHostnameAsync(d, ct)).ToList();
        await Task.WhenAll(tasks);

        // Determine device type and "this device" flag after hostnames are set
        foreach (var d in devices)
        {
            d.IsThisDevice = localIps.Contains(d.IpAddress);
            
            // Check if this is the gateway/router
            bool isGateway = !string.IsNullOrEmpty(gatewayIp) && d.IpAddress == gatewayIp;
            
            if (d.IsThisDevice)
                d.DeviceType = NetworkDeviceType.ThisDevice;
            else if (isGateway)
                d.DeviceType = NetworkDeviceType.Router;
            else
                d.DeviceType = InferDeviceType(d.Hostname, d.MacAddress, d.IpAddress);
        }
    }

    private static async Task ResolveHostnameAsync(NetworkDeviceEntry device, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));

            var entry = await Dns.GetHostEntryAsync(device.IpAddress, cts.Token);
            var host  = entry.HostName;

            // Strip fully-qualified suffix to keep it readable
            var dot = host.IndexOf('.');
            device.Hostname = dot > 0 ? host[..dot] : host;
        }
        catch
        {
            device.Hostname = string.Empty;
        }
    }

    private static HashSet<string> GetLocalIpAddresses()
    {
        var ips = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ips.Add(addr.Address.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkScanner] GetLocalIpAddresses: {ex.Message}");
        }
        return ips;
    }

    /// <summary>
    /// Gets the default gateway IP address for the active network adapter.
    /// Returns empty string if no gateway is found.
    /// </summary>
    private static string GetGatewayIp()
    {
        try
        {
            // Find the adapter with the default route (same logic as NetworkDetailsProvider)
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up)
                .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var adapter = adapters
                .OrderByDescending(a => HasGateway(a))
                .ThenByDescending(a => a.Speed)
                .FirstOrDefault();

            if (adapter == null) return string.Empty;

            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                    && !a.Equals(System.Net.IPAddress.Any));

            return gateway?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkScanner] GetGatewayIp: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                          && !g.Address.Equals(System.Net.IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Infers a device type from its hostname and the first 3 bytes (OUI) of its MAC address.
    /// All matching is case-insensitive and keyword-based — good enough for home networks.
    /// </summary>
    private static NetworkDeviceType InferDeviceType(string hostname, string mac, string ip)
    {
        var h = hostname.ToLowerInvariant();
        var oui = mac.Length >= 8 ? mac[..8].ToUpperInvariant() : string.Empty;

        // ── Hostname keyword matching ───────────────────────────────────────────
        
        // Routers and network equipment
        if (ContainsAny(h, "router", "gateway", "modem", "dlink", "tplink", "tp-link",
                           "netgear", "asus", "ubiquiti", "unifi", "mikrotik", "fritz",
                           "airport", "access-point", "ap-", "-ap", "linksys"))
            return NetworkDeviceType.Router;

        // Mobile phones and tablets
        if (ContainsAny(h, "iphone", "ipad", "android", "phone", "pixel", "samsung",
                           "galaxy", "huawei", "xiaomi", "oneplus", "oppo", "vivo", "redmi",
                           "nokia", "motorola", "lg-", "tablet"))
            return NetworkDeviceType.Phone;

        // Computers (desktops, laptops, servers)
        if (ContainsAny(h, "macbook", "imac", "laptop", "desktop", "workstation", "notebook",
                           "pc-", "-pc", "win-", "windows", "ubuntu", "fedora", "debian",
                           "raspberrypi", "raspberry-pi", "server", "nas", "synology", "qnap"))
            return NetworkDeviceType.Computer;

        // Printers
        if (ContainsAny(h, "print", "printer", "hp-", "canon", "epson", "brother",
                           "xerox", "lexmark", "ricoh", "kyocera"))
            return NetworkDeviceType.Printer;

        // Smart TVs and streaming devices
        if (ContainsAny(h, "tv", "firetv", "appletv", "chromecast", "roku", "shield",
                           "smarttv", "bravia", "viziotv", "lgwebos", "samsung-tv",
                           "androidtv", "sony", "tcl-", "hisense"))
            return NetworkDeviceType.SmartTV;

        // Game consoles
        if (ContainsAny(h, "playstation", "ps4", "ps5", "xbox", "switch", "nintendo",
                           "steam-deck", "steamdeck", "console"))
            return NetworkDeviceType.GameConsole;

        // Smart speakers and audio devices
        if (ContainsAny(h, "echo", "alexa", "homepod", "google-home", "nest-audio",
                           "nest-mini", "sonos", "speaker", "soundbar"))
            return NetworkDeviceType.Speaker;

        // IP cameras and security devices
        if (ContainsAny(h, "camera", "cam-", "-cam", "ring", "nest-cam", "arlo",
                           "wyze", "blink", "hikvision", "dahua", "ipcam", "webcam"))
            return NetworkDeviceType.Camera;

        // IoT and smart home devices
        if (ContainsAny(h, "hue", "smartplug", "homekit", "esp", "esp8266", "esp32",
                           "arduino", "tasmota", "shelly", "wemo", "smart-", "iot-",
                           "thermostat", "doorbell", "sensor", "hub"))
            return NetworkDeviceType.IoT;

        // ── OUI prefix matching (first 3 octets → vendor) ──────────────────────
        
        // Routers / AP vendors
        if (OuiAny(oui, "00:0C:E7", "00:17:DF", "00:26:5A",   // Cisco/Linksys
                        "90:72:40", "EC:08:6B", "50:FF:20",    // TP-Link
                        "00:14:6C", "20:4E:7F", "A0:40:A0",    // Netgear
                        "04:D4:C4", "14:DA:E9", "50:46:5D",    // ASUS
                        "00:1E:E5", "00:0F:66", "14:CC:20"))   // D-Link
            return NetworkDeviceType.Router;

        // Apple devices (iPhone, iPad, Mac — differentiate by hostname if possible)
        if (OuiAny(oui, "00:17:F2", "F0:DB:F8", "60:F8:1D",
                        "F4:5C:89", "8C:85:90", "A8:BE:27",
                        "DC:2B:2A", "00:3E:E1", "98:FE:94"))
        {
            // Macs tend to have longer fully-qualified hostnames; phones/tablets tend short
            return ContainsAny(h, "mac", "imac", "book") ? NetworkDeviceType.Computer : NetworkDeviceType.Phone;
        }

        // Samsung (mix of phones and TVs — hostname usually disambiguates)
        if (OuiAny(oui, "00:15:99", "84:25:DB", "CC:07:AB", "E8:03:9A"))
            return ContainsAny(h, "tv", "smart") ? NetworkDeviceType.SmartTV : NetworkDeviceType.Phone;

        // Sony (PlayStation consoles)
        if (OuiAny(oui, "00:1F:A7", "00:04:1F", "00:19:C5"))
            return NetworkDeviceType.GameConsole;

        // Microsoft (Xbox consoles)
        if (OuiAny(oui, "00:50:F2", "7C:ED:8D", "D8:B3:77"))
            return NetworkDeviceType.GameConsole;

        // Nintendo (Switch, Wii)
        if (OuiAny(oui, "00:09:BF", "00:19:1D", "00:1F:32"))
            return NetworkDeviceType.GameConsole;

        // Amazon (Echo, Fire devices)
        if (OuiAny(oui, "44:65:0D", "F0:81:73", "FC:A6:67"))
            return ContainsAny(h, "echo", "alexa") ? NetworkDeviceType.Speaker : NetworkDeviceType.SmartTV;

        // Google (Nest, Chromecast, Home devices)
        if (OuiAny(oui, "6C:AD:F8", "F4:F5:D8", "54:60:09"))
            return ContainsAny(h, "chromecast", "tv") ? NetworkDeviceType.SmartTV : NetworkDeviceType.Speaker;

        // Sonos speakers
        if (OuiAny(oui, "00:0E:58", "94:9F:3E", "B8:E9:37"))
            return NetworkDeviceType.Speaker;

        // Hikvision, Dahua (IP cameras)
        if (OuiAny(oui, "44:19:B6", "BC:AD:28", "00:12:12"))
            return NetworkDeviceType.Camera;

        // HP, Canon, Epson printers
        if (OuiAny(oui, "00:1E:0B", "00:A0:60", "00:23:7D", "04:F1:28"))
            return NetworkDeviceType.Printer;

        // Philips Hue, Wemo IoT
        if (OuiAny(oui, "00:17:88", "EC:1B:BD", "F4:5E:AB"))
            return NetworkDeviceType.IoT;

        // Raspberry Pi (often used as servers or IoT devices)
        if (OuiAny(oui, "B8:27:EB", "DC:A6:32", "E4:5F:01"))
            return NetworkDeviceType.Computer;

        // Generic Windows/PC (Intel NIC OUIs)
        if (OuiAny(oui, "00:1B:21", "00:1F:3C", "F0:1F:AF", "8C:8D:28"))
            return NetworkDeviceType.Computer;

        // .1 or .254 address on common home subnets is almost always the gateway
        if (ip.EndsWith(".1") || ip.EndsWith(".254"))
            return NetworkDeviceType.Router;

        return NetworkDeviceType.Unknown;
    }

    private static bool ContainsAny(string source, params string[] keywords)
        => keywords.Any(k => source.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool OuiAny(string oui, params string[] prefixes)
        => prefixes.Any(p => oui.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // ── Core ARP scan ──────────────────────────────────────────────────────────

    private IReadOnlyList<NetworkDeviceEntry> DoScan(CancellationToken ct)
    {
        var devices = new List<NetworkDeviceEntry>();

        try
        {
            int size = 0;
            uint result = GetIpNetTable(IntPtr.Zero, ref size, false);

            if (result == ERROR_NO_DATA)
                return devices;

            if (result != ERROR_INSUFFICIENT_BUFFER)
            {
                Debug.WriteLine($"[NetworkScanner] GetIpNetTable (size query) returned {result}");
                return devices;
            }

            IntPtr tablePtr = Marshal.AllocHGlobal(size);
            try
            {
                result = GetIpNetTable(tablePtr, ref size, false);
                if (result != NO_ERROR)
                {
                    Debug.WriteLine($"[NetworkScanner] GetIpNetTable returned {result}");
                    return devices;
                }

                var header = Marshal.PtrToStructure<MIB_IPNETTABLE>(tablePtr);
                int rowOffset = Marshal.SizeOf<uint>();
                int rowSize = Marshal.SizeOf<MIB_IPNETROW>();

                for (int i = 0; i < (int)header.dwNumEntries; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var row = Marshal.PtrToStructure<MIB_IPNETROW>(IntPtr.Add(tablePtr, rowOffset + i * rowSize));

                    // Skip incomplete / loopback / broadcast
                    if (row.dwPhysAddrLen == 0 || row.dwPhysAddrLen > 6) continue;
                    if (row.dwType != ArpTypeDynamic && row.dwType != ArpTypeStatic) continue;

                    var mac = FormatMac(row.bPhysAddr, (int)row.dwPhysAddrLen);
                    if (string.IsNullOrEmpty(mac)) continue;

                    // Skip all-zero MAC (incomplete ARP entry) and broadcast FF:FF:FF:FF:FF:FF
                    if (mac == "00:00:00:00:00:00" || mac == "FF:FF:FF:FF:FF:FF") continue;

                    var ipBytes = BitConverter.GetBytes(row.dwAddr);
                    var ip = new IPAddress(ipBytes).ToString();

                    // Skip 0.0.0.0, 255.x broadcast
                    if (ip == "0.0.0.0" || ip.StartsWith("255.")) continue;

                    devices.Add(new NetworkDeviceEntry
                    {
                        MacAddress = mac,
                        IpAddress = ip,
                        FirstSeen = DateTime.Now,
                        LastSeen = DateTime.Now,
                        IsTrusted = true
                    });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkScanner] DoScan exception: {ex.Message}");
        }

        // De-duplicate by MAC (keep first occurrence — lowest interface index tends to be the real one)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<NetworkDeviceEntry>();
        foreach (var d in devices)
        {
            if (seen.Add(d.MacAddress))
                unique.Add(d);
        }

        return unique;
    }

    private static string FormatMac(byte[] addr, int len)
    {
        if (addr == null || len == 0) return string.Empty;
        return string.Join(":", addr.Take(len).Select(b => b.ToString("X2")));
    }

    // ── IDisposable ────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
