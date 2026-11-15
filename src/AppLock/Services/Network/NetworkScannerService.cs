using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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
        var gatewayIp = GetGatewayIp();
        await SweepSubnetAsync(gatewayIp, ct).ConfigureAwait(false);

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
        var gatewayIp = GetGatewayIp();
        SweepSubnetAsync(gatewayIp, ct).GetAwaiter().GetResult();

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
        // 1. If it's This PC, use the machine name
        if (device.IsThisDevice)
        {
            device.Hostname = Environment.MachineName;
            return;
        }

        // 2. Reverse DNS lookup
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));

            var entry = await Dns.GetHostEntryAsync(device.IpAddress, cts.Token);
            var host  = entry.HostName;

            // Strip fully-qualified suffix to keep it readable
            var dot = host.IndexOf('.');
            var resolved = dot > 0 ? host[..dot] : host;
            if (!string.IsNullOrWhiteSpace(resolved) && resolved != device.IpAddress)
            {
                device.Hostname = resolved;
                return;
            }
        }
        catch { }

        // 3. Fallback: NetBIOS name query on UDP 137 (finds Windows PCs, NAS, and Samba shares)
        try
        {
            var netbiosName = await QueryNetBiosNameAsync(device.IpAddress, 300);
            if (!string.IsNullOrWhiteSpace(netbiosName))
            {
                device.Hostname = netbiosName;
                return;
            }
        }
        catch { }

        device.Hostname = string.Empty;
    }

    private static async Task<string?> QueryNetBiosNameAsync(string ipAddress, int timeoutMs = 300)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;
            var target = new IPEndPoint(IPAddress.Parse(ipAddress), 137);

            // NetBIOS node status request (50 bytes)
            byte[] request = new byte[50];
            request[0] = 0x82; // Transaction ID
            request[1] = 0x28;
            request[4] = 0x00; // Questions: 1
            request[5] = 0x01;
            request[12] = 0x20; // Question length
            request[13] = 0x43; // 'C'
            request[14] = 0x4B; // 'K'
            for (int i = 15; i < 45; i++) request[i] = 0x41; // 'A'
            request[46] = 0x00; // Type: NBSTAT
            request[47] = 0x21;
            request[48] = 0x00; // Class: IN
            request[49] = 0x01;

            await udp.SendAsync(request, request.Length, target);

            using var cts = new CancellationTokenSource(timeoutMs);
            var receiveTask = udp.ReceiveAsync();
            var completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, cts.Token));

            if (completedTask == receiveTask)
            {
                var result = receiveTask.Result;
                if (result.Buffer.Length > 56)
                {
                    int numNames = result.Buffer[56];
                    int offset = 57;
                    for (int i = 0; i < numNames && offset + 18 <= result.Buffer.Length; i++)
                    {
                        byte nameType = result.Buffer[offset + 15];
                        byte flags = result.Buffer[offset + 16];
                        bool isGroup = (flags & 0x80) != 0;
                        if (!isGroup && nameType == 0x00) // Workstation / Computer name
                        {
                            string name = Encoding.ASCII.GetString(result.Buffer, offset, 15).Trim();
                            if (!string.IsNullOrEmpty(name))
                                return name;
                        }
                        offset += 18;
                    }
                }
            }
        }
        catch { }
        return null;
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

        // ── 2. Randomized MAC: mobile phones/tablets use random MACs by default ──
        if (MacVendorDatabase.IsRandomMac(mac))
            return NetworkDeviceType.Phone;

        // ── 3. Vendor-based inference via MacVendorDatabase ──────────────────────
        var vendor = MacVendorDatabase.GetVendor(mac);
        if (!string.IsNullOrEmpty(vendor) && vendor != "Unknown")
        {
            if (vendor.Contains("Motorola", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("OnePlus", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Oppo", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Vivo", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Huawei", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.Phone;

            if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.Computer;

            if (vendor.Contains("TP-Link", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("D-Link", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Netgear", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("ASUS", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Nokia", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Cisco", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.Router;

            if (vendor.Contains("HP", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Canon", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Epson", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.Printer;

            if (vendor.Contains("Sony", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Nintendo", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.GameConsole;

            if (vendor.Contains("Amazon", StringComparison.OrdinalIgnoreCase) ||
                vendor.Contains("Sonos", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.Speaker;

            if (vendor.Contains("Espressif", StringComparison.OrdinalIgnoreCase))
                return NetworkDeviceType.IoT;

            if (vendor.Contains("Apple", StringComparison.OrdinalIgnoreCase))
                return ContainsAny(h, "mac", "imac", "book") ? NetworkDeviceType.Computer : NetworkDeviceType.Phone;

            if (vendor.Contains("Samsung", StringComparison.OrdinalIgnoreCase))
                return ContainsAny(h, "tv", "smart") ? NetworkDeviceType.SmartTV : NetworkDeviceType.Phone;
        }

        // ── 4. Gateway IP pattern ───────────────────────────────────────────────
        if (ip.EndsWith(".1") || ip.EndsWith(".254"))
            return NetworkDeviceType.Router;

        return NetworkDeviceType.Unknown;
    }

    private static bool ContainsAny(string source, params string[] keywords)
        => keywords.Any(k => source.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool OuiAny(string oui, params string[] prefixes)
        => prefixes.Any(p => oui.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // ── Subnet sweep via SendARP ───────────────────────────────────────────────

    private static async Task SweepSubnetAsync(string gatewayIp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(gatewayIp)) return;

        var lastDot = gatewayIp.LastIndexOf('.');
        if (lastDot <= 0) return;
        var prefix = gatewayIp[..(lastDot + 1)];

        var tasks = new List<Task>();
        using var sem = new SemaphoreSlim(64); // concurrency cap as per spec section 5.2

        for (int i = 1; i <= 254; i++)
        {
            if (ct.IsCancellationRequested) break;
            var targetIpStr = prefix + i;
            if (!IPAddress.TryParse(targetIpStr, out var ipAddr)) continue;

            uint destIp = BitConverter.ToUInt32(ipAddr.GetAddressBytes(), 0);

            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    byte[] mac = new byte[6];
                    int len = mac.Length;
                    SendARP(destIp, 0, mac, ref len);
                }
                catch { }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }
    }

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

                    // Skip multicast MACs (IPv4 multicast starts with 01:00:5E, IPv6 with 33:33)
                    if (mac.StartsWith("01:00:5E", StringComparison.OrdinalIgnoreCase) ||
                        mac.StartsWith("33:33", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ipBytes = BitConverter.GetBytes(row.dwAddr);
                    var ip = new IPAddress(ipBytes).ToString();

                    // Skip 0.0.0.0, 255.x broadcast
                    if (ip == "0.0.0.0" || ip.StartsWith("255.")) continue;

                    // Skip multicast IPs (224.0.0.0 to 239.255.255.255) and loopback (127.x)
                    if (ipBytes[0] >= 224 && ipBytes[0] <= 239) continue;
                    if (ipBytes[0] == 127) continue;

                    // Validate private IP range (RFC 1918 + Link-Local: 10.x, 172.16-31.x, 192.168.x, 169.254.x)
                    if (!IsPrivateIp(ipBytes)) continue;

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

        // Ensure "This PC" is included in the list
        AddThisDeviceIfMissing(devices);

        // De-duplicate by MAC (keep first occurrence)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<NetworkDeviceEntry>();
        foreach (var d in devices)
        {
            if (seen.Add(d.MacAddress))
                unique.Add(d);
        }

        return unique;
    }

    private static bool IsPrivateIp(byte[] b)
    {
        if (b == null || b.Length < 4) return false;
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 172.16.0.0/12 (172.16.0.0 – 172.31.255.255)
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        // 169.254.0.0/16 (link-local)
        if (b[0] == 169 && b[1] == 254) return true;
        return false;
    }

    private static void AddThisDeviceIfMissing(List<NetworkDeviceEntry> devices)
    {
        try
        {
            var localIps = GetLocalIpAddresses();
            if (devices.Any(d => d.IsThisDevice || localIps.Contains(d.IpAddress)))
                return;

            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.OperationalStatus == OperationalStatus.Up)
                .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var adapter = adapters
                .OrderByDescending(a => HasGateway(a))
                .ThenByDescending(a => a.Speed)
                .FirstOrDefault();

            if (adapter == null) return;

            var ip = adapter.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?
                .Address.ToString();

            var physAddr = adapter.GetPhysicalAddress().GetAddressBytes();
            var mac = FormatMac(physAddr, physAddr.Length);

            if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(mac))
            {
                devices.Insert(0, new NetworkDeviceEntry
                {
                    MacAddress = mac,
                    IpAddress = ip,
                    Hostname = Environment.MachineName,
                    DeviceType = NetworkDeviceType.ThisDevice,
                    IsThisDevice = true,
                    FirstSeen = DateTime.Now,
                    LastSeen = DateTime.Now,
                    IsTrusted = true
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NetworkScanner] AddThisDeviceIfMissing: {ex.Message}");
        }
    }
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
