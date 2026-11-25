using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace AppLock.WindowIsland.Modules.Controls;

public sealed class WindowIslandControlsService
{
    private static readonly Lazy<WindowIslandControlsService> _instance = new(() => new WindowIslandControlsService());
    public static WindowIslandControlsService Instance => _instance.Value;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("PowrProf.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out nint lpdwResult);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly nint HWND_BROADCAST = new(0xffff);

    private Radio? _wifiRadio;
    private Radio? _btRadio;
    private bool _radiosInitialized;

    public event EventHandler? ControlsStateChanged;

    public WindowIslandControlsService()
    {
        try
        {
            PowerManager.BatteryStatusChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);
            PowerManager.RemainingChargePercentChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);
            PowerManager.PowerSupplyStatusChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);
            NetworkChange.NetworkAddressChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);
            _ = InitializeRadiosAsync();
        }
        catch { }
    }

    public async Task InitializeRadiosAsync()
    {
        if (_radiosInitialized) return;
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                _wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                _btRadio   = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

                if (_wifiRadio != null)
                    _wifiRadio.StateChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);
                if (_btRadio != null)
                    _btRadio.StateChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);

                _radiosInitialized = true;
            }
        }
        catch { }
    }

    // ── Wi-Fi ──────────────────────────────────────────────────────────────────

    public async Task<(bool Available, bool IsOn, string Ssid)> GetWiFiStateAsync()
    {
        await InitializeRadiosAsync().ConfigureAwait(false);

        if (_wifiRadio == null)
        {
            // Fallback: check network interfaces
            bool hasWifiAdapter = NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            if (!hasWifiAdapter)
                return (false, false, "No adapter");

            string currentSsid = GetConnectedSsid();
            return (true, !string.IsNullOrEmpty(currentSsid), string.IsNullOrEmpty(currentSsid) ? "Available" : currentSsid);
        }

        bool isOn = _wifiRadio.State == RadioState.On;
        string ssid = isOn ? GetConnectedSsid() : "Off";
        if (string.IsNullOrEmpty(ssid) && isOn) ssid = "On (Not connected)";

        return (true, isOn, ssid);
    }

    public async Task ToggleWiFiAsync()
    {
        await InitializeRadiosAsync().ConfigureAwait(false);
        if (_wifiRadio == null) return;

        try
        {
            var targetState = _wifiRadio.State == RadioState.On ? RadioState.Off : RadioState.On;
            await _wifiRadio.SetStateAsync(targetState);
        }
        catch { }
    }

    private static string GetConnectedSsid()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile != null && profile.IsWlanConnectionProfile && profile.WlanConnectionProfileDetails != null)
            {
                return profile.WlanConnectionProfileDetails.GetConnectedSsid();
            }
        }
        catch { }
        return string.Empty;
    }

    public sealed record IslandWiFiNetwork(
        string Ssid,
        bool IsConnected,
        string Status,
        int SignalBars = 4,
        string Band = "2.4 GHz",
        bool IsSaved = false,
        bool IsSecured = true);

    public static bool LastWifiLocationBlocked { get; private set; }

    public static async Task<System.Collections.Generic.List<IslandWiFiNetwork>> GetWiFiNetworksAsync()
    {
        return await Task.Run(async () =>
        {
            LastWifiLocationBlocked = false;
            var list = new System.Collections.Generic.List<IslandWiFiNetwork>();
            string currentSsid = GetConnectedSsid();
            var savedProfiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Get saved profiles with UTF-8 encoding (supports emojis properly without mojibake)
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "wlan show profiles",
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0 && line.Contains("All User Profile"))
                        {
                            string ssid = line[(colon + 1)..].Trim();
                            if (!string.IsNullOrEmpty(ssid))
                            {
                                savedProfiles.Add(ssid);
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Query visible/nearby networks via WinRT WiFiAdapter
            var visibleMap = new System.Collections.Generic.Dictionary<string, (int signal, string band, bool secured)>(StringComparer.OrdinalIgnoreCase);
            bool winRtLocationDenied = false;
            try
            {
                var access = await Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync();
                if (access != Windows.Devices.WiFi.WiFiAccessStatus.Allowed)
                {
                    winRtLocationDenied = true;
                }
                else
                {
                    var adapters = await Windows.Devices.WiFi.WiFiAdapter.FindAllAdaptersAsync();
                    if (adapters.Count > 0)
                    {
                        var adapter = adapters[0];
                        var report = adapter.NetworkReport;
                        if (report != null && report.AvailableNetworks.Count > 0)
                        {
                            foreach (var net in report.AvailableNetworks)
                            {
                                if (string.IsNullOrWhiteSpace(net.Ssid)) continue;
                                string band = net.ChannelCenterFrequencyInKilohertz switch
                                {
                                    < 3000000 => "2.4 GHz",
                                    >= 4900000 and <= 5900000 => "5 GHz",
                                    >= 5925000 => "6 GHz",
                                    _ => "5 GHz"
                                };
                                bool secured = net.SecuritySettings.NetworkAuthenticationType != Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211 &&
                                               net.SecuritySettings.NetworkAuthenticationType != Windows.Networking.Connectivity.NetworkAuthenticationType.None;
                                int bars = Math.Clamp((int)net.SignalBars, 0, 4);

                                if (!visibleMap.TryGetValue(net.Ssid, out var existing) || bars > existing.signal)
                                {
                                    visibleMap[net.Ssid] = (bars, band, secured);
                                }
                            }
                        }

                        // Background scan for subsequent refreshes
                        _ = Task.Run(async () =>
                        {
                            try { await adapter.ScanAsync(); } catch { }
                        });
                    }
                }
            }
            catch { }

            // 3. Fallback to netsh mode=bssid if WinRT found no visible networks
            if (visibleMap.Count == 0)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "netsh.exe",
                        Arguments = "wlan show networks mode=bssid",
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync();
                        await proc.WaitForExitAsync();

                        if (output.Contains("location permission", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("ms-settings:privacy-location", StringComparison.OrdinalIgnoreCase))
                        {
                            LastWifiLocationBlocked = true;
                        }

                        string? curSsid = null;
                        int curSignal = 3;
                        string curBand = "2.4 GHz";
                        bool curSecured = true;

                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var rawLine in lines)
                        {
                            string line = rawLine.Trim();
                            if (line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrEmpty(curSsid))
                                {
                                    if (!visibleMap.TryGetValue(curSsid, out var ex) || curSignal > ex.signal)
                                        visibleMap[curSsid] = (curSignal, curBand, curSecured);
                                }
                                int colon = line.IndexOf(':');
                                curSsid = colon > 0 ? line[(colon + 1)..].Trim() : null;
                                curSignal = 3;
                                curBand = "2.4 GHz";
                                curSecured = true;
                            }
                            else if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase))
                            {
                                int colon = line.IndexOf(':');
                                if (colon > 0)
                                {
                                    string pctStr = line[(colon + 1)..].Replace("%", "").Trim();
                                    if (int.TryParse(pctStr, out int pct))
                                    {
                                        curSignal = pct >= 80 ? 4 : pct >= 60 ? 3 : pct >= 40 ? 2 : pct >= 20 ? 1 : 0;
                                    }
                                }
                            }
                            else if (line.StartsWith("Band", StringComparison.OrdinalIgnoreCase))
                            {
                                int colon = line.IndexOf(':');
                                if (colon > 0) curBand = line[(colon + 1)..].Trim();
                            }
                            else if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase))
                            {
                                curSecured = !line.Contains("Open", StringComparison.OrdinalIgnoreCase);
                            }
                        }

                        if (!string.IsNullOrEmpty(curSsid))
                        {
                            if (!visibleMap.TryGetValue(curSsid, out var ex) || curSignal > ex.signal)
                                visibleMap[curSsid] = (curSignal, curBand, curSecured);
                        }
                    }
                }
                catch { }
            }

            if (visibleMap.Count == 0 && winRtLocationDenied)
            {
                LastWifiLocationBlocked = true;
            }

            // 4. Merge all networks
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Connected network first
            if (!string.IsNullOrEmpty(currentSsid))
            {
                visibleMap.TryGetValue(currentSsid, out var vis);
                string band = !string.IsNullOrEmpty(vis.band) ? vis.band : "2.4 GHz";
                int signal = vis.signal > 0 ? vis.signal : 4;
                list.Add(new IslandWiFiNetwork(currentSsid, true, $"Connected · {band}", signal, band, true, vis.secured));
                seen.Add(currentSsid);
            }

            // Visible networks in range
            foreach (var kvp in visibleMap)
            {
                string ssid = kvp.Key;
                if (!seen.Add(ssid)) continue;

                bool isSaved = savedProfiles.Contains(ssid);
                string status = isSaved ? $"Saved · {kvp.Value.band}" : (kvp.Value.secured ? $"Secured · {kvp.Value.band}" : $"Open · {kvp.Value.band}");
                list.Add(new IslandWiFiNetwork(ssid, false, status, kvp.Value.signal, kvp.Value.band, isSaved, kvp.Value.secured));
            }

            // Saved networks out of range
            foreach (var saved in savedProfiles)
            {
                if (!seen.Add(saved)) continue;
                list.Add(new IslandWiFiNetwork(saved, false, "Saved (out of range)", 0, "", true, true));
            }

            return list
                .OrderByDescending(n => n.IsConnected)
                .ThenByDescending(n => n.SignalBars)
                .ThenByDescending(n => n.IsSaved)
                .ThenBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    public static async Task<bool> ConnectWiFiAsync(string ssid)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"wlan connect name=\"{ssid}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                return proc?.ExitCode == 0;
            }
            catch { return false; }
        });
    }

    public static async Task<bool> ConnectWiFiWithPasswordAsync(string ssid, string? password, bool isSecured)
    {
        return await Task.Run(async () =>
        {
            string? tempFile = null;
            try
            {
                string safeSsid = System.Security.SecurityElement.Escape(ssid) ?? ssid;
                string hexSsid = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(ssid));
                string authBlock = isSecured
                    ? $@"<authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{System.Security.SecurityElement.Escape(password ?? string.Empty)}</keyMaterial>
            </sharedKey>"
                    : @"<authEncryption>
                <authentication>open</authentication>
                <encryption>none</encryption>
                <useOneX>false</useOneX>
            </authEncryption>";

                string xml = $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{safeSsid}</name>
    <SSIDConfig>
        <SSID>
            <hex>{hexSsid}</hex>
            <name>{safeSsid}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            {authBlock}
        </security>
    </MSM>
</WLANProfile>";

                tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"applock_wlan_{Guid.NewGuid():N}.xml");
                System.IO.File.WriteAllText(tempFile, xml, System.Text.Encoding.UTF8);

                using (var addProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"wlan add profile filename=\"{tempFile}\" user=current",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }))
                {
                    addProc?.WaitForExit(3000);
                    if (addProc?.ExitCode != 0) return false;
                }

                using (var connProc = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = $"wlan connect name=\"{ssid}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }))
                {
                    connProc?.WaitForExit(3000);
                    if (connProc?.ExitCode != 0) return false;
                }

                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(500);
                    if (string.Equals(GetConnectedSsid(), ssid, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Clean up failed profile so wrong password doesn't remain saved as broken
                if (isSecured)
                {
                    using var delProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "netsh.exe",
                        Arguments = $"wlan delete profile name=\"{ssid}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    delProc?.WaitForExit(2000);
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tempFile != null)
                {
                    try { System.IO.File.Delete(tempFile); } catch { }
                }
            }
        });
    }

    public static async Task DisconnectWiFiAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "wlan disconnect",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(2000);
            }
            catch { }
        });
    }

    public sealed record IslandBtDevice(string Name, string MacAddress, string DeviceType, bool IsConnected, int? BatteryPercent);

    public static string GetBtBatteryGlyph(int percent) => percent switch
    {
        >= 95 => "\uEBAA", // 100%
        >= 85 => "\uEBA9", // 90%
        >= 75 => "\uEBA8", // 80%
        >= 65 => "\uEBA7", // 70%
        >= 55 => "\uEBA6", // 60%
        >= 45 => "\uEBA5", // 50%
        >= 35 => "\uEBA4", // 40%
        >= 25 => "\uEBA3", // 30%
        >= 15 => "\uEBA2", // 20%
        >= 5  => "\uEBA1", // 10%
        _     => "\uEBA0"  // 0%
    };

    public static async Task<System.Collections.Generic.List<IslandBtDevice>> GetBluetoothDevicesAsync()
    {
        return await Task.Run(async () =>
        {
            var list = new System.Collections.Generic.List<IslandBtDevice>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var (hasBt, btOn) = await Instance.GetBluetoothStateAsync();
            if (!hasBt || !btOn)
            {
                // When Bluetooth is disabled or unavailable, read paired devices but mark all as disconnected
                try
                {
                    using var devKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
                    if (devKey != null)
                    {
                        var rawEntries = new System.Collections.Generic.List<(string Name, string Mac)>();
                        foreach (string mac in devKey.GetSubKeyNames())
                        {
                            using var subKey = devKey.OpenSubKey(mac);
                            if (subKey == null) continue;
                            string name = string.Empty;
                            object? nameVal = subKey.GetValue("Name") ?? subKey.GetValue("FriendlyName");
                            if (nameVal is byte[] bytes) name = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                            else if (nameVal is string strName) name = strName.TrimEnd('\0').Trim();
                            if (!string.IsNullOrWhiteSpace(name)) rawEntries.Add((name, mac));
                        }
                        var groupedByName = rawEntries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var group in groupedByName)
                        {
                            string name = group.Key;
                            string lower = name.ToLowerInvariant();
                            string type = "Bluetooth";
                            if (lower.Contains("buds") || lower.Contains("airdopes") || lower.Contains("airpods") ||
                                lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earphone")) type = "Headphones";
                            else if (lower.Contains("speaker") || lower.Contains("breeze") || lower.Contains("boom") || lower.Contains("audio")) type = "Speaker";
                            else if (lower.Contains("keyboard") || lower.Contains(" k1") || lower.Contains("keychron") || lower.Contains("kbd")) type = "Keyboard";
                            else if (lower.Contains("mouse") || lower.Contains("polygear") || lower.Contains("trackball")) type = "Mouse";
                            else if (lower.Contains("phone") || lower.Contains("oppo") || lower.Contains("iphone") ||
                                     lower.Contains("galaxy") || lower.Contains("redmi") || lower.Contains("pixel")) type = "Phone";

                            list.Add(new IslandBtDevice(name, group.First().Mac, type, IsConnected: false, BatteryPercent: null));
                        }
                    }
                }
                catch { }
                return list.OrderBy(d => d.Name).ToList();
            }

            // 1. Query battery properties across devices
            var batteryMap = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var props = new[] { "System.ItemNameDisplay", "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2" };
                var devs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                    "System.Devices.InterfaceClassGuid:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\" OR System.Devices.DeviceInstanceId:~~\"BTHENUM\" OR System.Devices.DeviceInstanceId:~~\"BTHLE\"",
                    props,
                    Windows.Devices.Enumeration.DeviceInformationKind.Device);

                foreach (var d in devs)
                {
                    if (d.Properties.TryGetValue("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", out var bVal) && bVal != null)
                    {
                        int bat = Convert.ToInt32(bVal);
                        var m = System.Text.RegularExpressions.Regex.Match(d.Id, @"([0-9A-Fa-f]{12})");
                        if (m.Success)
                        {
                            batteryMap[m.Groups[1].Value.ToLowerInvariant()] = bat;
                        }
                        if (!string.IsNullOrWhiteSpace(d.Name))
                        {
                            batteryMap[d.Name] = bat;
                        }
                    }
                }
            }
            catch { }

            // 2. Query paired devices with real-time IsConnected status via WinRT DeviceInformation (Classic + BLE)
            var connMacMap = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var connNameMap = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var classicSelector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var leSelector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                var properties = new[]
                {
                    "System.ItemNameDisplay",
                    "System.Devices.Aep.IsConnected",
                    "System.Devices.Aep.DeviceAddress"
                };

                var classicDevs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(classicSelector, properties);
                foreach (var ad in classicDevs)
                {
                    bool isConn = ad.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var cVal) && cVal is bool b && b;
                    if (ad.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var aVal) && aVal != null)
                    {
                        string clean = aVal.ToString()!.Replace(":", "").ToLowerInvariant();
                        connMacMap[clean] = isConn;
                    }
                    if (!string.IsNullOrWhiteSpace(ad.Name))
                    {
                        connNameMap[ad.Name] = isConn;
                    }
                }

                var leDevs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(leSelector, properties);
                foreach (var ad in leDevs)
                {
                    bool isConn = ad.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var cVal) && cVal is bool b && b;
                    if (ad.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var aVal) && aVal != null)
                    {
                        string clean = aVal.ToString()!.Replace(":", "").ToLowerInvariant();
                        if (isConn || !connMacMap.ContainsKey(clean))
                            connMacMap[clean] = isConn;
                    }
                    if (!string.IsNullOrWhiteSpace(ad.Name))
                    {
                        if (isConn || !connNameMap.ContainsKey(ad.Name))
                            connNameMap[ad.Name] = isConn;
                    }
                }
            }
            catch { }

            // 2b. Query active Bluetooth HID devices (Keyboards, Mice) from PnP
            try
            {
                var hidDevs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                    "System.Devices.DeviceInstanceId:~~\"HID\" AND (System.Devices.DeviceInstanceId:~~\"1812\" OR System.Devices.DeviceInstanceId:~~\"1124\")",
                    new[] { "System.ItemNameDisplay", "System.Devices.Present" },
                    Windows.Devices.Enumeration.DeviceInformationKind.Device);

                foreach (var hd in hidDevs)
                {
                    if (hd.Properties.TryGetValue("System.Devices.Present", out var presVal) && presVal is bool isPresent && !isPresent)
                        continue;

                    string idPart = hd.Id;
                    int closeBrace = idPart.IndexOf('}');
                    if (closeBrace >= 0)
                        idPart = idPart.Substring(closeBrace + 1);

                    var m = System.Text.RegularExpressions.Regex.Match(idPart, @"[_\&]([0-9A-Fa-f]{12})[_\&\\\/]");
                    if (m.Success)
                    {
                        connMacMap[m.Groups[1].Value.ToLowerInvariant()] = true;
                    }
                }
            }
            catch { }

            // Also check MMDevices audio render active state for audio devices
            var activeAudioNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var mmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
                if (mmKey != null)
                {
                    foreach (string guid in mmKey.GetSubKeyNames())
                    {
                        using var devSub = mmKey.OpenSubKey(guid);
                        if (devSub == null) continue;

                        object? stateVal = devSub.GetValue("DeviceState");
                        if (stateVal is int state && state == 1)
                        {
                            using var propKey = devSub.OpenSubKey("Properties");
                            if (propKey != null)
                            {
                                foreach (string valName in propKey.GetValueNames())
                                {
                                    if (valName.Contains("a45c254e-df1c-4efd-8020-67d146a850e0", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (propKey.GetValue(valName) is string fname && !string.IsNullOrWhiteSpace(fname))
                                        {
                                            activeAudioNames.Add(fname);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Enumerate paired devices from registry
            try
            {
                using var devKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
                if (devKey != null)
                {
                    var rawEntries = new System.Collections.Generic.List<(string Name, string Mac)>();
                    foreach (string mac in devKey.GetSubKeyNames())
                    {
                        using var subKey = devKey.OpenSubKey(mac);
                        if (subKey == null) continue;

                        string name = string.Empty;
                        object? nameVal = subKey.GetValue("Name");
                        if (nameVal is byte[] bytes)
                        {
                            name = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                        }
                        else if (nameVal is string strName)
                        {
                            name = strName.TrimEnd('\0').Trim();
                        }

                        if (string.IsNullOrWhiteSpace(name))
                        {
                            object? fnVal = subKey.GetValue("FriendlyName");
                            if (fnVal is byte[] fnBytes)
                                name = System.Text.Encoding.UTF8.GetString(fnBytes).TrimEnd('\0').Trim();
                            else if (fnVal is string strFn)
                                name = strFn.TrimEnd('\0').Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            rawEntries.Add((name, mac));
                        }
                    }

                    // Group by name so if a device has multiple MAC registrations (e.g. multi-profile mouse), we choose the connected one
                    var groupedByName = rawEntries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

                    foreach (var group in groupedByName)
                    {
                        string name = group.Key;
                        string lower = name.ToLowerInvariant();
                        string type = "Bluetooth";
                        if (lower.Contains("buds") || lower.Contains("airdopes") || lower.Contains("airpods") ||
                            lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earphone"))
                        {
                            type = "Headphones";
                        }
                        else if (lower.Contains("speaker") || lower.Contains("breeze") || lower.Contains("boom") || lower.Contains("audio"))
                        {
                            type = "Speaker";
                        }
                        else if (lower.Contains("keyboard") || lower.Contains(" k1") || lower.Contains("keychron") || lower.Contains("kbd"))
                        {
                            type = "Keyboard";
                        }
                        else if (lower.Contains("mouse") || lower.Contains("polygear") || lower.Contains("trackball"))
                        {
                            type = "Mouse";
                        }
                        else if (lower.Contains("phone") || lower.Contains("oppo") || lower.Contains("iphone") ||
                                 lower.Contains("galaxy") || lower.Contains("redmi") || lower.Contains("pixel"))
                        {
                            type = "Phone";
                        }

                        // Determine if any MAC in group is connected
                        string bestMac = group.First().Mac;
                        bool isConnected = false;

                        foreach (var entry in group)
                        {
                            string cMac = entry.Mac.Replace(":", "").ToLowerInvariant();
                            if (connMacMap.TryGetValue(cMac, out bool cmc) && cmc)
                            {
                                isConnected = true;
                                bestMac = entry.Mac;
                                break;
                            }
                        }

                        if (!isConnected)
                        {
                            if (connNameMap.TryGetValue(name, out bool cmn) && cmn)
                                isConnected = true;
                            else if (connNameMap.Keys.Any(k => (k.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(k, StringComparison.OrdinalIgnoreCase)) && connNameMap[k]))
                                isConnected = true;
                            else
                                isConnected = activeAudioNames.Any(an => an.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(an, StringComparison.OrdinalIgnoreCase));
                        }

                        string cleanBestMac = bestMac.Replace(":", "").ToLowerInvariant();
                        int? battery = null;
                        if (batteryMap.TryGetValue(cleanBestMac, out int bMac))
                        {
                            battery = bMac;
                        }
                        else
                        {
                            // Try other MACs in group for battery
                            foreach (var entry in group)
                            {
                                string cMac = entry.Mac.Replace(":", "").ToLowerInvariant();
                                if (batteryMap.TryGetValue(cMac, out int bOther))
                                {
                                    battery = bOther;
                                    break;
                                }
                            }

                            if (!battery.HasValue)
                            {
                                foreach (var bKvp in batteryMap)
                                {
                                    if (bKvp.Key.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(bKvp.Key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        battery = bKvp.Value;
                                        break;
                                    }
                                }
                            }
                        }

                        list.Add(new IslandBtDevice(name, bestMac, type, isConnected, battery));
                    }
                }
            }
            catch { }

            return list.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();
        });
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern nint BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out nint phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindNextRadio(nint hFind, out nint phRadio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(nint hFind);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool BluetoothFindNextDevice(nint hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(nint hFind);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(
        nint hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref Guid pGuidService,
        uint dwServiceFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        ref ulong lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public int dwSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte cTimeoutMultiplier;
        public nint hRadio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x0041000C;
    private static readonly Guid AudioSinkServiceGuid = Guid.Parse("0000110b-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HandsfreeServiceGuid = Guid.Parse("0000111e-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HidServiceGuid       = Guid.Parse("00001124-0000-1000-8000-00805f9b34fb");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDisposable> _activeAudioConnections = new(StringComparer.OrdinalIgnoreCase);

    private static bool TryFindBluetoothDeviceInfo(ulong addr, out BLUETOOTH_DEVICE_INFO info)
    {
        info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), szName = string.Empty };
        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnUnknown = false,
            fReturnConnected = true,
            fIssueInquiry = false,
            cTimeoutMultiplier = 0,
            hRadio = nint.Zero
        };

        nint hFind = BluetoothFindFirstDevice(ref search, ref info);
        if (hFind == nint.Zero) return false;
        try
        {
            do
            {
                if (info.Address == addr)
                {
                    return true;
                }
                info.dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();
            } while (BluetoothFindNextDevice(hFind, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(hFind);
        }
        return false;
    }

    private static bool TrySetBluetoothServicesState(ulong addr, bool enable)
    {
        if (!TryFindBluetoothDeviceInfo(addr, out var devInfo))
        {
            devInfo = new BLUETOOTH_DEVICE_INFO
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                Address = addr,
                szName = string.Empty
            };
        }

        uint flag = enable ? 1u : 0u;
        bool anyOk = false;
        Guid[] guids = { AudioSinkServiceGuid, HandsfreeServiceGuid, HidServiceGuid };

        foreach (var g in guids)
        {
            Guid svc = g;
            uint res = BluetoothSetServiceState(nint.Zero, ref devInfo, ref svc, flag);
            if (res != 0 && enable)
            {
                // Toggle trick: disable first then enable if the service was in a stale state
                svc = g;
                BluetoothSetServiceState(nint.Zero, ref devInfo, ref svc, 0u);
                svc = g;
                res = BluetoothSetServiceState(nint.Zero, ref devInfo, ref svc, 1u);
            }
            if (res == 0) anyOk = true;
        }
        return anyOk;
    }

    public static async Task<bool> ConnectBluetoothDeviceAsync(string mac, string? deviceName = null)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (!ulong.TryParse(mac, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                    return false;

                // 1. Enable native Windows Bluetooth services (A2DP Audio Sink / HFP / HID) so Windows persists the connection
                if (TrySetBluetoothServicesState(addr, enable: true))
                {
                    return true;
                }

                // 2. Fallback: Classic Bluetooth RFCOMM / BLE GATT (keep instance alive so it doesn't immediately disconnect)
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var dev = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(addr).AsTask(cts.Token);
                    if (dev != null)
                    {
                        var rfcomm = await dev.GetRfcommServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached).AsTask(cts.Token);
                        if (dev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                        {
                            if (_activeAudioConnections.TryRemove(mac, out var prev)) prev.Dispose();
                            _activeAudioConnections[mac] = dev;
                            return true;
                        }
                        dev.Dispose();
                    }
                }
                catch { }

                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var leDev = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(cts.Token);
                    if (leDev != null)
                    {
                        var gatt = await leDev.GetGattServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached).AsTask(cts.Token);
                        if (leDev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                        {
                            if (_activeAudioConnections.TryRemove(mac, out var prev)) prev.Dispose();
                            _activeAudioConnections[mac] = leDev;
                            return true;
                        }
                        leDev.Dispose();
                    }
                }
                catch { }
            }
            catch { }
            return false;
        });
    }

    public static async Task<bool> DisconnectBluetoothDeviceAsync(string mac)
    {
        return await Task.Run(() =>
        {
            bool disconnected = false;
            try
            {
                if (_activeAudioConnections.TryRemove(mac, out var conn))
                {
                    try
                    {
                        conn.Dispose();
                        disconnected = true;
                    }
                    catch { }
                }

                if (ulong.TryParse(mac, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                {
                    if (TrySetBluetoothServicesState(addr, enable: false))
                    {
                        disconnected = true;
                    }

                    var findParams = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
                    nint hFind = BluetoothFindFirstRadio(ref findParams, out nint hRadio);
                    if (hFind != nint.Zero)
                    {
                        try
                        {
                            do
                            {
                                if (hRadio != nint.Zero)
                                {
                                    try
                                    {
                                        if (DeviceIoControl(hRadio, IOCTL_BTH_DISCONNECT_DEVICE, ref addr, sizeof(ulong), nint.Zero, 0, out _, nint.Zero))
                                        {
                                            disconnected = true;
                                        }
                                    }
                                    finally
                                    {
                                        CloseHandle(hRadio);
                                    }
                                }
                            } while (BluetoothFindNextRadio(hFind, out hRadio));
                        }
                        finally
                        {
                            BluetoothFindRadioClose(hFind);
                        }
                    }
                }
            }
            catch { }
            return disconnected;
        });
    }

    // ── Bluetooth ──────────────────────────────────────────────────────────────

    public async Task<(bool Available, bool IsOn)> GetBluetoothStateAsync()
    {
        try
        {
            var adapter = await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
            if (adapter != null)
            {
                var radio = await adapter.GetRadioAsync();
                if (radio != null)
                {
                    if (_btRadio == null)
                    {
                        _btRadio = radio;
                        _btRadio.StateChanged += (_, _) => ControlsStateChanged?.Invoke(this, EventArgs.Empty);
                    }
                    return (true, radio.State == RadioState.On);
                }
            }
        }
        catch { }

        await InitializeRadiosAsync().ConfigureAwait(false);
        if (_btRadio == null) return (false, false);

        return (true, _btRadio.State == RadioState.On);
    }

    public async Task ToggleBluetoothAsync()
    {
        await InitializeRadiosAsync().ConfigureAwait(false);
        if (_btRadio == null) return;

        try
        {
            var targetState = _btRadio.State == RadioState.On ? RadioState.Off : RadioState.On;
            await _btRadio.SetStateAsync(targetState);
        }
        catch { }
    }

    // ── Dark Theme (Windows) ───────────────────────────────────────────────────

    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var val = key.GetValue("AppsUseLightTheme");
                if (val is int intVal) return intVal == 0;
            }
        }
        catch { }
        return false;
    }

    public static void ToggleSystemDarkTheme()
    {
        try
        {
            bool isDark = IsSystemDarkTheme();
            int newVal = isDark ? 1 : 0; // if dark, toggle to light (1); else toggle to dark (0)

            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            if (key != null)
            {
                key.SetValue("AppsUseLightTheme", newVal, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", newVal, RegistryValueKind.DWord);

                // Notify Windows shell to repaint immediately
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 200, out _);
            }
        }
        catch { }
    }

    // ── Battery ────────────────────────────────────────────────────────────────

    public static (bool HasBattery, int Percent, bool IsCharging, bool IsPluggedIn, bool IsBatterySaverOn, string StatusText) GetDetailedBatteryInfo()
    {
        try
        {
            var status = PowerManager.BatteryStatus;
            bool isPluggedIn = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            bool isSaverOn = PowerManager.EnergySaverStatus == EnergySaverStatus.On
                             || IsRegistryEnergySaverOn();

            if (status == BatteryStatus.NotPresent)
            {
                return (false, 100, true, true, isSaverOn, "On mains power");
            }

            int percent = PowerManager.RemainingChargePercent;
            bool isCharging = status == BatteryStatus.Charging;

            string text = isCharging
                ? "Charging"
                : (isPluggedIn ? (percent >= 100 ? "Fully charged" : "Plugged in") : "On battery");

            if (!isPluggedIn && !isCharging && status == BatteryStatus.Discharging)
            {
                var remaining = PowerManager.RemainingDischargeTime;
                if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromHours(24))
                {
                    text = $"{remaining.Hours}h {remaining.Minutes}m left";
                }
            }

            return (true, percent, isCharging, isPluggedIn, isSaverOn, text);
        }
        catch
        {
            return (false, 100, false, false, false, "Unavailable");
        }
    }

    public static bool IsBatterySaverActive()
    {
        try
        {
            return PowerManager.EnergySaverStatus == EnergySaverStatus.On
                   || IsRegistryEnergySaverOn();
        }
        catch
        {
            return IsRegistryEnergySaverOn();
        }
    }

    private static bool IsRegistryEnergySaverOn()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power", writable: false);
            return key?.GetValue("EnergySaverState") is int state && state == 1;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> ToggleBatterySaverAsync()
    {
        try
        {
            bool currentlyOn = IsBatterySaverActive();
            int newState = currentlyOn ? 0 : 1;
            int percent = PowerManager.RemainingChargePercent;
            int threshold = currentlyOn ? (percent <= 20 ? 0 : 20) : 100;

            return await Task.Run(() =>
            {
                RunPowerCfg($"/setdcvalueindex SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD {threshold}");
                RunPowerCfg($"/setacvalueindex SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD {threshold}");
                RunPowerCfg("/setactive SCHEME_CURRENT");

                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power", writable: true);
                    if (key != null)
                    {
                        key.SetValue("EnergySaverState", newState, RegistryValueKind.DWord);
                        return true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // App runs unelevated; elevate reg.exe to sync EnergySaverState with Windows Settings.
                    return RunElevatedRegSetEnergySaver(newState);
                }
                catch (System.Security.SecurityException)
                {
                    return RunElevatedRegSetEnergySaver(newState);
                }
                catch { }

                return true;
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool RunElevatedRegSetEnergySaver(int state)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $@"add ""HKLM\SYSTEM\CurrentControlSet\Control\Power"" /v EnergySaverState /t REG_DWORD /d {state} /f",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunPowerCfg(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        proc?.WaitForExit(3000);
    }

    public static (bool HasBattery, int Percent, bool IsCharging, string StatusText) GetBatteryInfo()
    {
        try
        {
            var status = PowerManager.BatteryStatus;
            if (status == BatteryStatus.NotPresent)
            {
                return (false, 100, true, "On mains power");
            }

            int percent = PowerManager.RemainingChargePercent;
            bool isCharging = status == BatteryStatus.Charging;

            string text = isCharging ? "Charging" : (status == BatteryStatus.Idle ? "Fully charged" : "Discharging");
            if (!isCharging && status == BatteryStatus.Discharging)
            {
                var remaining = PowerManager.RemainingDischargeTime;
                if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromHours(24))
                {
                    text = $"{remaining.Hours}h {remaining.Minutes}m remaining";
                }
            }

            return (true, percent, isCharging, text);
        }
        catch
        {
            return (false, 100, true, "On mains power");
        }
    }

    // ── VPN ────────────────────────────────────────────────────────────────────

    public static (bool IsConnected, string Name) GetVpnStatus()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                    return (true, string.IsNullOrEmpty(ni.Name) ? "VPN" : ni.Name);

                string desc = ni.Description.ToLowerInvariant();
                string name = ni.Name.ToLowerInvariant();
                if (desc.Contains("vpn") || desc.Contains("wireguard") || desc.Contains("tap-windows") ||
                    desc.Contains("wintun") || name.Contains("vpn") || name.Contains("wireguard"))
                {
                    return (true, ni.Name);
                }
            }
        }
        catch { }
        return (false, "Disconnected");
    }

    public static async Task ToggleVpnAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Toggle via rasdial: if connected, disconnect active connection
                var (isConnected, name) = GetVpnStatus();
                if (isConnected && !string.IsNullOrEmpty(name))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "rasdial.exe",
                        Arguments = $"\"{name}\" /disconnect",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                else
                {
                    // Open Windows Network & Internet -> VPN settings
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "ms-settings:network-vpn",
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        });
    }

    // ── System Power Actions ───────────────────────────────────────────────────

    public static void LockComputer()
    {
        try { LockWorkStation(); } catch { }
    }

    public static void SleepComputer()
    {
        try { SetSuspendState(false, true, false); } catch { }
    }

    public static void RestartComputer()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    public static void ShutdownComputer()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 0",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }
}

/// <summary>
/// Tracks per-device Bluetooth battery warning thresholds (e.g. below 80, 50, 20, 10%)
/// so each threshold triggers once per discharge cycle without spamming.
/// </summary>
public sealed class BtBatteryWarningTracker
{
    private readonly Dictionary<string, HashSet<int>> _crossed = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSignature = string.Empty;

    public int? CheckThreshold(string deviceKey, int batteryPercent, IReadOnlyList<int> thresholds)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || thresholds == null || thresholds.Count == 0)
            return null;

        var ordered = thresholds.Where(t => t >= 1 && t <= 99).Distinct().OrderByDescending(t => t).ToList();
        if (ordered.Count == 0) return null;

        string sig = string.Join(",", ordered);
        if (!string.Equals(sig, _lastSignature, StringComparison.Ordinal))
        {
            _crossed.Clear();
            _lastSignature = sig;
        }

        if (!_crossed.TryGetValue(deviceKey, out var seen))
        {
            seen = new HashSet<int>();
            _crossed[deviceKey] = seen;
        }

        // Reset thresholds if device was recharged
        foreach (int t in ordered)
        {
            if (batteryPercent >= t + 5)
            {
                seen.Remove(t);
            }
        }

        int? lowestNew = null;
        foreach (int t in ordered)
        {
            if (batteryPercent < t && !seen.Contains(t))
            {
                seen.Add(t);
                lowestNew = lowestNew.HasValue ? Math.Min(lowestNew.Value, t) : t;
            }
        }

        return lowestNew;
    }

    public void Reset() => _crossed.Clear();
}
