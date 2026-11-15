using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
    public Task<IReadOnlyList<NetworkDeviceEntry>> ScanOnceAsync(CancellationToken ct = default)
        => Task.Run(() => (IReadOnlyList<NetworkDeviceEntry>)DoScan(ct), ct);

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
            }

            _settingsStore.Save(settings);
        }

        // Update LastSeen and IP for already-known devices
        foreach (var seen in snapshot)
        {
            var known = settings.KnownNetworkDevices
                .FirstOrDefault(d => string.Equals(d.MacAddress, seen.MacAddress, StringComparison.OrdinalIgnoreCase));

            if (known != null)
            {
                known.LastSeen = seen.LastSeen;
                known.IpAddress = seen.IpAddress; // IP can change (DHCP)
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
