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

    public sealed record IslandWiFiNetwork(string Ssid, bool IsConnected, string Status);

    public static async Task<System.Collections.Generic.List<IslandWiFiNetwork>> GetWiFiNetworksAsync()
    {
        return await Task.Run(() =>
        {
            var list = new System.Collections.Generic.List<IslandWiFiNetwork>();
            string currentSsid = GetConnectedSsid();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "wlan show profiles",
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0 && line.Contains("All User Profile"))
                        {
                            string ssid = line[(colon + 1)..].Trim();
                            if (!string.IsNullOrEmpty(ssid) && !list.Any(w => w.Ssid == ssid))
                            {
                                bool isCur = string.Equals(ssid, currentSsid, StringComparison.OrdinalIgnoreCase);
                                list.Add(new IslandWiFiNetwork(ssid, isCur, isCur ? "Connected" : "Saved"));
                            }
                        }
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(currentSsid) && !list.Any(w => w.Ssid == currentSsid))
            {
                list.Insert(0, new IslandWiFiNetwork(currentSsid, true, "Connected"));
            }
            else
            {
                list = list.OrderByDescending(w => w.IsConnected).ToList();
            }

            return list;
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

    public sealed record IslandBtDevice(string Name, string MacAddress, string DeviceType, bool IsConnected);

    public static async Task<System.Collections.Generic.List<IslandBtDevice>> GetBluetoothDevicesAsync()
    {
        return await Task.Run(async () =>
        {
            var list = new System.Collections.Generic.List<IslandBtDevice>();
            try
            {
                using var devKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
                if (devKey != null)
                {
                    foreach (string mac in devKey.GetSubKeyNames())
                    {
                        using var subKey = devKey.OpenSubKey(mac);
                        if (subKey == null) continue;

                        string name = string.Empty;
                        object? nameVal = subKey.GetValue("Name");
                        if (nameVal is byte[] bytes)
                        {
                            name = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                        }
                        else if (nameVal is string strName)
                        {
                            name = strName.TrimEnd('\0');
                        }

                        if (string.IsNullOrWhiteSpace(name)) continue;

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
                        else if (lower.Contains("phone") || lower.Contains("oppo") || lower.Contains("iphone") ||
                                 lower.Contains("galaxy") || lower.Contains("redmi") || lower.Contains("pixel"))
                        {
                            type = "Phone";
                        }

                        bool isConnected = false;
                        try
                        {
                            if (ulong.TryParse(mac, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                            {
                                var btDev = await Windows.Devices.Bluetooth.BluetoothDevice.FromBluetoothAddressAsync(addr);
                                if (btDev != null)
                                {
                                    isConnected = btDev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected;
                                }
                            }
                        }
                        catch { }

                        list.Add(new IslandBtDevice(name, mac, type, isConnected));
                    }
                }
            }
            catch { }

            return list.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();
        });
    }

    // ── Bluetooth ──────────────────────────────────────────────────────────────

    public async Task<(bool Available, bool IsOn)> GetBluetoothStateAsync()
    {
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
