using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.WindowIsland.Events;

namespace AppLock.WindowIsland.Modules.Notifications;

/// <summary>
/// Milestone 12: Monitors VPN state, battery thresholds, and mic/camera activity.
/// Raises typed events so WindowIslandWindow can push cards into the event queue.
///
/// Windows Notifications (UserNotificationListener) require package identity.
/// AppLock is unpackaged (WindowsPackageType=None), so that feature is explicitly
/// marked unavailable — no crash, no fake data.
/// </summary>
public sealed class WindowIslandNotificationService : IDisposable
{
    // ── Singleton ──────────────────────────────────────────────────────────────
    private static WindowIslandNotificationService? _instance;
    public  static WindowIslandNotificationService Instance
        => _instance ??= new WindowIslandNotificationService();

    // ── Events ─────────────────────────────────────────────────────────────────
    /// <summary>Fired when VPN connection state changes. Args: (connected, vpnAdapterName).</summary>
    public event EventHandler<VpnStateChangedArgs>? VpnStateChanged;

    /// <summary>Fired when battery drops to a threshold (20 / 10 / 5 %) while discharging.</summary>
    public event EventHandler<BatteryCardArgs>? BatteryLowCard;

    /// <summary>Fired when charging starts or finishes.</summary>
    public event EventHandler<BatteryCardArgs>? BatteryChargingCard;

    /// <summary>Fired when mic or camera active-session state changes.</summary>
    public event EventHandler<MicCamStateArgs>? MicCamStateChanged;

    // ── Windows Notifications availability ────────────────────────────────────
    /// <summary>
    /// Always false for unpackaged builds. UserNotificationListener requires
    /// a package identity (sparse MSIX or full MSIX); AppLock ships as
    /// WindowsPackageType=None, so this feature is unavailable in this build.
    /// </summary>
    public static bool NotificationsAvailable => false;
    public static string NotificationsUnavailableReason =>
        "Windows Notifications require a package identity (MSIX). " +
        "AppLock runs as an unpackaged app, so this feature is not available in this build. " +
        "Open Windows Settings › System › Notifications to manage app notifications.";

    // ── Battery threshold state ────────────────────────────────────────────────
    private static readonly int[] LowThresholds = { 20, 10, 5 };
    private readonly HashSet<int> _crossedThresholds = new();
    private bool _wasCharging;
    private bool _wasPluggedIn;
    private bool _batteryInitialized;

    // ── VPN state ─────────────────────────────────────────────────────────────
    private bool _vpnConnected;
    private string _vpnName = string.Empty;
    private bool _vpnInitialized;

    // ── Mic/Cam polling ───────────────────────────────────────────────────────
    private bool _micActive;
    private bool _camActive;
    private string _micAppName = string.Empty;
    private string _camAppName = string.Empty;
    private Timer? _pollTimer;
    private int _pollTick;
    private bool _disposed;

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0=offline, 1=online
        public byte BatteryFlag;         // bit3=charging, bit7=no battery, 0xFF=unknown
        public byte BatteryLifePercent;  // 0-100, 0xFF=unknown
        public byte SystemStatusFlag;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    // ── Construction ──────────────────────────────────────────────────────────
    private WindowIslandNotificationService()
    {
        // VPN: watch via NetworkChange events
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        // Poll mic/cam every 1 s (battery every 10 s); initial poll after 500 ms
        _pollTimer = new Timer(OnPollTimer, null,
            dueTime: TimeSpan.FromMilliseconds(500),
            period:  TimeSpan.FromSeconds(1));
    }

    // ── VPN Detection ─────────────────────────────────────────────────────────

    /// <summary>Detects VPN by looking for PPP/Tunnel network adapters that are Up.</summary>
    public static (bool Connected, string Name) GetCurrentVpnState()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Ppp
                                             or NetworkInterfaceType.Tunnel
                                             or NetworkInterfaceType.GenericModem)
                {
                    return (true, nic.Name);
                }
            }
        }
        catch { /* network permission denied on some configs */ }
        return (false, string.Empty);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
        => Task.Run(CheckVpnState);

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => Task.Run(CheckVpnState);

    private void CheckVpnState()
    {
        var (connected, name) = GetCurrentVpnState();
        bool changed = !_vpnInitialized
                       || connected != _vpnConnected
                       || (!connected && _vpnConnected)
                       || (connected && name != _vpnName);
        if (!changed) return;

        bool wasConnected = _vpnConnected;
        _vpnConnected = connected;
        _vpnName = name;
        _vpnInitialized = true;

        // Don't fire on first initialization (silent start)
        if (!changed || (!wasConnected && !connected)) return;

        VpnStateChanged?.Invoke(this,
            new VpnStateChangedArgs(connected, name));
    }

    // ── Battery Monitoring ────────────────────────────────────────────────────

    /// <summary>
    /// Reads current battery state. Returns (hasBattery, percent, isCharging, isPluggedIn).
    /// </summary>
    public static (bool HasBattery, int Percent, bool IsCharging, bool IsPluggedIn) GetBatteryState()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS s))
            return (false, 0, false, false);

        bool hasBattery = (s.BatteryFlag & 128) == 0
                          && s.BatteryFlag != 255
                          && s.BatteryLifePercent != 255;
        if (!hasBattery) return (false, 0, false, s.ACLineStatus == 1);

        int  pct       = Math.Clamp((int)s.BatteryLifePercent, 0, 100);
        bool charging  = (s.BatteryFlag & 8) != 0;
        bool pluggedIn = s.ACLineStatus == 1;
        return (true, pct, charging, pluggedIn);
    }

    private readonly object _batteryLock = new();

    public void NotifyPowerStatusChanged()
    {
        try { CheckBatteryState(); } catch { }
    }

    private void CheckBatteryState()
    {
        lock (_batteryLock)
        {
            var (hasBattery, pct, isCharging, isPluggedIn) = GetBatteryState();
            if (!hasBattery) return;

            // ── Threshold crossing (only while discharging) ────────────────────
            bool discharging = !isPluggedIn && !isCharging;
            if (discharging)
            {
                foreach (int threshold in LowThresholds)
                {
                    if (pct <= threshold && !_crossedThresholds.Contains(threshold))
                    {
                        _crossedThresholds.Add(threshold);
                        BatteryLowCard?.Invoke(this, new BatteryCardArgs(
                            BatteryCardKind.Low, pct, isCharging: false));
                    }
                }
            }
            else
            {
                // Reset thresholds when charging so they can fire again next time
                foreach (int t in LowThresholds)
                    if (pct > t + 5) _crossedThresholds.Remove(t);
            }

            // ── Charging / Plugged-in state change ────────────────────────────
            if (!_batteryInitialized)
            {
                _wasCharging  = isCharging;
                _wasPluggedIn = isPluggedIn;
                _batteryInitialized = true;
                return;
            }

            bool pluggedInNow     = isPluggedIn && !_wasPluggedIn;
            bool unpluggedNow     = !isPluggedIn && _wasPluggedIn;
            bool startedCharging  = pluggedInNow || (isCharging && !_wasCharging && isPluggedIn);
            bool chargingComplete = isPluggedIn && !isCharging && pct >= 100 && (_wasCharging || pluggedInNow);

            if (chargingComplete)
                BatteryChargingCard?.Invoke(this, new BatteryCardArgs(
                    BatteryCardKind.ChargingComplete, pct, isCharging: false));
            else if (pluggedInNow || (startedCharging && !_wasPluggedIn))
                BatteryChargingCard?.Invoke(this, new BatteryCardArgs(
                    BatteryCardKind.ChargingStarted, pct, isCharging: true));
            else if (unpluggedNow)
                BatteryChargingCard?.Invoke(this, new BatteryCardArgs(
                    BatteryCardKind.Unplugged, pct, isCharging: false));

            _wasCharging  = isCharging;
            _wasPluggedIn = isPluggedIn;
        }
    }

    // ── Mic / Camera Detection ────────────────────────────────────────────────
    // Reads HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{device}\*
    // A sub-key with LastUsedTimeStop = 0 and NonPackaged marker or app ID means the session is active.

    private const string MicConsentKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string CamConsentKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    /// <summary>Optional WASAPI capture-session fallback wired by the host window.</summary>
    public static Func<(bool Active, string AppName)>? CaptureSessionFallback { get; set; }

    /// <summary>Optional layered camera-usage fallback (Ring Light monitor) wired by the host window.</summary>
    public static Func<(bool Active, string AppName)>? CameraFallback { get; set; }

    /// <summary>Checks if mic or webcam are currently in use via CapabilityAccessManager registry.</summary>
    public static (bool MicActive, string MicApp, bool CamActive, string CamApp) GetMicCamState()
    {
        bool micActive = false, camActive = false;
        string micApp = string.Empty, camApp = string.Empty;

        try { (micActive, micApp) = IsDeviceInUse(MicConsentKey); } catch { }
        if (CaptureSessionFallback != null)
        {
            try
            {
                var (capActive, capApp) = CaptureSessionFallback();
                if (capActive)
                {
                    micActive = true;
                    micApp = MergeCommaApps(micApp, capApp);
                }
            }
            catch { }
        }
        // Windows' own per-app camera records decide *who* is capturing; the shared reader also turns
        // raw keys (msedge.exe, package family names) into product names and lists the newest session
        // first, so the app shown is the one that just started.
        try
        {
            var camApps = AppLock.Services.RingLight.CameraUsageMonitor.GetActiveCameraAppNames();
            if (camApps.Count > 0)
            {
                camActive = true;
                camApp = string.Join(", ", camApps);
            }
        }
        catch { }
        if (CameraFallback != null)
        {
            try
            {
                var (fbActive, fbApp) = CameraFallback();
                if (fbActive)
                {
                    camActive = true;
                    camApp = MergeCommaApps(camApp, fbApp);
                }
            }
            catch { }
        }

        return (micActive, micApp, camActive, camApp);
    }

    private static string MergeCommaApps(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return incoming;

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part)) list.Add(part);
        }
        foreach (string part in incoming.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part)) list.Add(part);
        }
        return string.Join(", ", list);
    }

    private static (bool Active, string AppName) IsDeviceInUse(string registrySubKey)
    {
        var activeEntries = new List<(long Start, string App)>();

        CheckRootDeviceInUse(Microsoft.Win32.Registry.CurrentUser, registrySubKey, activeEntries);
        CheckRootDeviceInUse(Microsoft.Win32.Registry.LocalMachine, registrySubKey, activeEntries);

        if (activeEntries.Count == 0) return (false, string.Empty);

        activeEntries.Sort((a, b) => b.Start.CompareTo(a.Start));
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, app) in activeEntries)
        {
            if (!string.IsNullOrWhiteSpace(app) && seen.Add(app))
            {
                names.Add(app);
            }
        }
        return (names.Count > 0, string.Join(", ", names));
    }

    private static void CheckRootDeviceInUse(
        Microsoft.Win32.RegistryKey root,
        string registrySubKey,
        List<(long Start, string App)> activeEntries)
    {
        try
        {
            using var key = root.OpenSubKey(registrySubKey, writable: false);
            if (key == null) return;

            // Check direct packaged app entries
            foreach (string subName in key.GetSubKeyNames())
            {
                if (string.Equals(subName, "NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryGetActiveSubKey(key, subName, nonPackaged: false, out long start, out string appName))
                {
                    activeEntries.Add((start, appName));
                }
            }

            // Check NonPackaged desktop app entries
            using var nonPkg = key.OpenSubKey("NonPackaged", writable: false);
            if (nonPkg != null)
            {
                foreach (string subName in nonPkg.GetSubKeyNames())
                {
                    if (TryGetActiveSubKey(nonPkg, subName, nonPackaged: true, out long start, out string appName))
                    {
                        activeEntries.Add((start, appName));
                    }
                }
            }
        }
        catch { }
    }

    private static bool TryGetActiveSubKey(Microsoft.Win32.RegistryKey parent, string subName, bool nonPackaged, out long startTime, out string appName)
    {
        startTime = 0;
        appName = string.Empty;
        try
        {
            using var sub = parent.OpenSubKey(subName, writable: false);
            if (sub == null) return false;

            var startVal = sub.GetValue("LastUsedTimeStart");
            var stopVal = sub.GetValue("LastUsedTimeStop");
            if (startVal == null || stopVal == null) return false;

            long start = ToInt64Safe(startVal);
            long stop  = ToInt64Safe(stopVal);

            if (start != 0 && stop == 0)
            {
                startTime = start;
                string nice = AppLock.Services.RingLight.CameraUsageMonitor.FriendlyAppName(subName, nonPackaged);
                appName = !string.IsNullOrWhiteSpace(nice) ? nice : FriendlyAppName(subName);
                return true;
            }
        }
        catch { /* registry access may fail for some entries */ }
        return false;
    }

    private static long ToInt64Safe(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            ulong ul => (long)ul,
            uint ui => ui,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => Convert.ToInt64(value)
        };
    }

    private static string FriendlyAppName(string registryKeyName)
    {
        // e.g. "C:#Users#Alice#AppData#Roaming#Zoom#bin#Zoom.exe" -> "Zoom"
        // or   "Microsoft.Teams_8wekyb3d8bbwe" -> "Teams"
        if (registryKeyName.Contains('#'))
        {
            var parts = registryKeyName.Split('#');
            var last = parts[^1];
            return System.IO.Path.GetFileNameWithoutExtension(last);
        }
        // Package family name: "AppName_hash" -> "AppName"
        int under = registryKeyName.IndexOf('_');
        return under > 0 ? registryKeyName[..under] : registryKeyName;
    }

    private void CheckMicCamState()
    {
        var (micActive, micApp, camActive, camApp) = GetMicCamState();

        bool changed = micActive != _micActive
                    || camActive != _camActive
                    || micApp != _micAppName
                    || camApp != _camAppName;
        if (!changed) return;

        _micActive  = micActive;
        _camActive  = camActive;
        _micAppName = micApp;
        _camAppName = camApp;

        MicCamStateChanged?.Invoke(this,
            new MicCamStateArgs(micActive, micApp, camActive, camApp));
    }

    // ── Poll timer ────────────────────────────────────────────────────────────

    private void OnPollTimer(object? state)
    {
        if (_disposed) return;
        int tick = Interlocked.Increment(ref _pollTick);
        if (tick == 1 || tick % 10 == 0)
        {
            try { CheckBatteryState(); } catch { }
        }
        try { CheckMicCamState();  } catch { }
        // VPN is event-driven; do an initial check on first poll
        if (!_vpnInitialized) Task.Run(CheckVpnState);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        NetworkChange.NetworkAddressChanged      -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (_instance == this) _instance = null;
    }
}

// ── Event arg types ───────────────────────────────────────────────────────────

public sealed class VpnStateChangedArgs(bool connected, string adapterName) : EventArgs
{
    public bool   Connected    { get; } = connected;
    public string AdapterName  { get; } = adapterName;
}

public enum BatteryCardKind { Low, ChargingStarted, ChargingComplete, Unplugged }

public sealed class BatteryCardArgs(BatteryCardKind kind, int percent, bool isCharging) : EventArgs
{
    public BatteryCardKind Kind       { get; } = kind;
    public int             Percent    { get; } = percent;
    public bool            IsCharging { get; } = isCharging;
}

public sealed class MicCamStateArgs(bool micActive, string micApp, bool camActive, string camApp) : EventArgs
{
    public bool   MicActive { get; } = micActive;
    public string MicApp    { get; } = micApp;
    public bool   CamActive { get; } = camActive;
    public string CamApp    { get; } = camApp;
}
