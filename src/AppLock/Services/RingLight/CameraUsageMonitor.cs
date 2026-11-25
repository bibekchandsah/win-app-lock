using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace AppLock.Services.RingLight;

/// <summary>
/// Status result returned when checking camera devices and active streaming state.
/// </summary>
public sealed class CameraCheckResult
{
    public bool HasHardware => InstalledCameras.Count > 0;
    public IReadOnlyList<string> InstalledCameras { get; init; } = Array.Empty<string>();
    public bool IsStreaming { get; init; }
    public IReadOnlyList<string> ActiveApps { get; init; } = Array.Empty<string>();
    public string? Reason { get; init; }

    /// <summary>One entry per detection method that ran, so the UI can show what was checked.</summary>
    public IReadOnlyList<CameraCheckSignal> Checks { get; init; } = Array.Empty<CameraCheckSignal>();
}

/// <summary>Result of a single detection method (hardware, Windows records, app processes, browsers).</summary>
public sealed record CameraCheckSignal(string Label, bool Detected, string Detail);

/// <summary>
/// Detects webcam usage with layered checks:
///   1. the Windows privacy consent store (CapabilityAccessManager), the record Windows itself
///      keeps for camera access — precise, but recent builds only write it once a session ends;
///   2. Chromium video capture service processes that are actually consuming CPU (the process
///      lingers after a call, so existence alone is not evidence of use);
///   3. Store (MSIX/UWP) camera apps that are running *and* working, used as a fallback where Windows
///      publishes no usage at all. They are detected by package identity, because their executable
///      name (e.g. WhatsApp.Root.exe) never matches the desktop process list;
///   4. desktop camera apps that are running *and* working (Windows Camera, Teams, Zoom, OBS, ...).
///      The running-app layers require real CPU activity, since an open-but-idle WhatsApp, Phone Link
///      or Telegram is not using the camera and must not turn the ring light on.
/// Implements 1-second continuous use debounce for 'On' and 5-second silence debounce for 'Off'.
/// </summary>
public sealed class CameraUsageMonitor : ICameraUsageSource
{
    private const string WebcamConsentSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private static readonly (string ProcessName, string DisplayName)[] KnownCameraProcesses = new[]
    {
        ("WindowsCamera", "Windows Camera"),
        ("Teams", "Microsoft Teams"),
        ("ms-teams", "Microsoft Teams"),
        ("msteams", "Microsoft Teams"),
        ("Zoom", "Zoom"),
        ("Discord", "Discord"),
        ("obs64", "OBS Studio"),
        ("obs32", "OBS Studio"),
        ("obs", "OBS Studio"),
        ("Skype", "Skype"),
        ("SkypeApp", "Skype"),
        ("WhatsApp", "WhatsApp"),
        ("WhatsApp.Root", "WhatsApp"),
        ("Telegram", "Telegram"),
        ("slack", "Slack"),
        ("Signal", "Signal"),
        ("webex", "Webex"),
        ("CiscoWebex", "Webex"),
        ("CamoStudio", "Camo Studio"),
        ("viber", "Viber"),
    };

    /// <summary>
    /// Store (MSIX/UWP) apps that can use the camera, keyed by package identity name (the part of the
    /// package family name before the publisher id). Windows does not publish live camera usage for
    /// them — the consent store is only written once a session ends — so a running packaged camera app
    /// is the signal used here. Store apps often spawn helper executables (WhatsApp runs as
    /// WhatsApp.Root.exe), which is why they are matched by package identity instead of process name.
    /// </summary>
    private static readonly (string PackageName, string DisplayName)[] KnownStoreCameraApps = new[]
    {
        ("5319275A.WhatsAppDesktop", "WhatsApp"),
        ("5319275A.51895FA4EA97F", "WhatsApp"),
        ("Microsoft.WindowsCamera", "Windows Camera"),
        ("MicrosoftTeams", "Microsoft Teams"),
        ("MSTeams", "Microsoft Teams"),
        ("Microsoft.SkypeApp", "Skype"),
        ("Microsoft.YourPhone", "Phone Link"),
        ("TelegramMessengerLLP.TelegramDesktop", "Telegram"),
        ("ReincubateLtd.CamoStudio", "Camo Studio"),
        ("Microsoft.Zoom", "Zoom"),
        ("Zoom.Zoom", "Zoom"),
        ("TheBrowserCompany.Dia", "Dia"),
        ("windows.immersivecontrolpanel", "Settings"),
    };

    /// <summary>
    /// Desktop executables, as they appear in the webcam consent store's NonPackaged keys
    /// (<c>C:#Program Files#Google#Chrome#Application#chrome.exe</c>). Windows records the exe name,
    /// which is not what the app is called, so known apps are mapped to their product name.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDesktopExeLookup =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = "Microsoft Edge",
        ["chrome.exe"] = "Google Chrome",
        ["firefox.exe"] = "Mozilla Firefox",
        ["brave.exe"] = "Brave",
        ["opera.exe"] = "Opera",
        ["vivaldi.exe"] = "Vivaldi",
        ["dia.exe"] = "Dia",
        ["zoom.exe"] = "Zoom",
        ["teams.exe"] = "Microsoft Teams",
        ["ms-teams.exe"] = "Microsoft Teams",
        ["msteams.exe"] = "Microsoft Teams",
        ["whatsapp.exe"] = "WhatsApp",
        ["telegram.exe"] = "Telegram",
        ["discord.exe"] = "Discord",
        ["slack.exe"] = "Slack",
        ["signal.exe"] = "Signal",
        ["skype.exe"] = "Skype",
        ["obs64.exe"] = "OBS Studio",
        ["obs32.exe"] = "OBS Studio",
        ["obs.exe"] = "OBS Studio",
        ["webexmta.exe"] = "Webex",
        ["viber.exe"] = "Viber",
        ["windowscamera.exe"] = "Windows Camera",
    };

    private static readonly Dictionary<string, string> KnownCameraProcessLookup =
        BuildLookup(KnownCameraProcesses);

    private static readonly Dictionary<string, string> KnownStoreCameraLookup =
        BuildLookup(KnownStoreCameraApps);

    private static Dictionary<string, string> BuildLookup((string Key, string DisplayName)[] entries)
    {
        var map = new Dictionary<string, string>(entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, display) in entries) map[key] = display;
        return map;
    }

    public event EventHandler<(bool InUse, IReadOnlyList<string> ActiveApps)>? CameraUsageChanged;

    private readonly object _lock = new();
    private readonly int _inactiveDebounceSeconds;
    private readonly int _pollIntervalMs;
    private readonly bool _useBrowserCaptureHeuristic;
    private readonly double _appActivitySmoothing;
    private Timer? _pollTimer;
    private int _isPolling;
    private bool _isMonitoring;
    private bool _isDebouncedInUse;
    private string _lastNotifiedAppsKey = string.Empty;
    private int _consecutiveActiveSeconds;
    private int _consecutiveInactiveSeconds;
    private bool _disposed;

    public CameraUsageMonitor(
        int inactiveDebounceSeconds = 5,
        int pollIntervalMs = 1000,
        bool useBrowserCaptureHeuristic = true,
        double appActivitySmoothing = AppActivitySmoothing)
    {
        _inactiveDebounceSeconds = Math.Max(1, inactiveDebounceSeconds);
        _pollIntervalMs = Math.Clamp(pollIntervalMs, 250, 5000);
        _useBrowserCaptureHeuristic = useBrowserCaptureHeuristic;
        _appActivitySmoothing = Math.Clamp(appActivitySmoothing, 0.1, 1.0);
    }

    // Browser capture-service sampling: a Chromium "VideoCaptureService" utility process lingers
    // after a call ends, so its CPU time is sampled to tell a streaming service from an idle one.
    private const double BrowserCaptureCpuThresholdMs = 8.0;
    private readonly Dictionary<uint, CaptureSample> _captureSamples = new();
    private readonly object _captureSamplesLock = new();

    private readonly record struct CaptureSample(TimeSpan Cpu, DateTime At, bool Active);

    // The capture-service WMI query costs a few milliseconds, and the poll runs every second while a
    // browser is open, so its result is reused briefly instead of being queried on every tick.
    private const int CaptureQueryCacheMilliseconds = 1000;
    private static readonly object CaptureQueryLock = new();
    private static List<(uint Pid, string Display)> _captureQueryCache = new();
    private static DateTime _captureQueryAt = DateTime.MinValue;

    // Running-app sampling: on builds that only write the camera consent record when a session ends,
    // an open-but-idle app looked like an active camera user, so the app's own CPU time is sampled.
    // Measured on this machine: Windows Camera streaming used 109-234 ms of CPU per second, while the
    // same apps idle at 0 ms/s. Turning on therefore needs a real stream (well clear of idle) held for
    // two consecutive samples, and turning off waits until the rate falls to a much lower hold
    // threshold — a single busy moment can neither invent nor hide a running call.
    private const double AppStartThresholdMsPerSecond = 30.0;
    private const double AppHoldThresholdMsPerSecond = 10.0;
    private const int AppRateWindowSamples = 6;          // ~3 s at the 500 ms poll
    private const int AppRateWindowMinimumSamples = 4;   // never judge on a shorter history
    private const double AppActivitySmoothing = 0.3;
    private readonly Dictionary<uint, PidCpuSample> _pidCpu = new();
    private readonly Dictionary<string, AppActivitySample> _appActivity = new();
    private readonly Dictionary<string, RateWindow> _appWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appConfirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activityLock = new();

    /// <summary>Last few CPU rates of one app, so a one-second burst is not mistaken for a stream.</summary>
    private sealed class RateWindow
    {
        private readonly double[] _rates = new double[AppRateWindowSamples];
        private int _next;

        public int Count { get; private set; }

        public double Average
        {
            get
            {
                if (Count == 0) return 0;
                double sum = 0;
                for (int i = 0; i < Count; i++) sum += _rates[i];
                return sum / Count;
            }
        }

        public void Add(double rate)
        {
            _rates[_next] = rate;
            _next = (_next + 1) % _rates.Length;
            if (Count < _rates.Length) Count++;
        }

        public int CountAtLeast(double value)
        {
            int n = 0;
            for (int i = 0; i < Count; i++) if (_rates[i] >= value) n++;
            return n;
        }

        public void Clear()
        {
            Array.Clear(_rates);
            _next = 0;
            Count = 0;
        }
    }

    private readonly record struct PidCpuSample(TimeSpan Cpu, DateTime At, double RateMsPerSecond);
    private readonly record struct AppActivitySample(DateTime At, double RateMsPerSecond, bool Measured);

    // Process scans are the expensive part of detection (one image-path query per process), so one scan
    // is shared by the poll and the test button, and each process' package identity is resolved once and
    // cached until that pid disappears (the name is re-checked in case Windows reused the pid).
    private const int ScanCacheMilliseconds = 1000;
    private readonly object _scanLock = new();
    private readonly Dictionary<uint, (string Name, string? Identity)> _packageIdentity = new();
    private List<(uint Pid, string Display)> _storeCandidates = new();
    private List<(uint Pid, string Display)> _desktopCandidates = new();
    private DateTime _scanAt = DateTime.MinValue;
    private bool _scanValid;

    public bool IsMonitoring
    {
        get { lock (_lock) return _isMonitoring; }
    }

    public bool IsSupported
    {
        get
        {
            try
            {
                using var cuKey = Registry.CurrentUser.OpenSubKey(WebcamConsentSubKey);
                if (cuKey != null) return true;
                using var lmKey = Registry.LocalMachine.OpenSubKey(WebcamConsentSubKey);
                return lmKey != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _isMonitoring) return;
            _isMonitoring = true;
            _consecutiveActiveSeconds = 0;
            _consecutiveInactiveSeconds = 0;
            _isDebouncedInUse = false;

            // Poll on a background thread
            _pollTimer = new Timer(OnPollTick, null, _pollIntervalMs, _pollIntervalMs);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isMonitoring) return;
            _isMonitoring = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            _consecutiveActiveSeconds = 0;
            _consecutiveInactiveSeconds = 0;
            _isDebouncedInUse = false;
        }
    }

    /// <summary>
    /// Apps Windows says are using the camera right now, as friendly names, most recent first.
    /// </summary>
    public IReadOnlyList<string> GetActiveCameraApps() => GetActiveCameraAppNames();

    // ── Windows' own per-app camera records ───────────────────────────────────
    // HKCU/HKLM ...\CapabilityAccessManager\ConsentStore\webcam holds one key per app that has ever
    // opened the camera. While a session is open LastUsedTimeStop stays behind LastUsedTimeStart
    // (usually 0), which is the only per-app, real-time source Windows publishes — so this is the
    // authority on *who* is using the camera, and the heuristics below only cover the case where a
    // build records nothing at all.

    private const int ConsentReadCacheMilliseconds = 400;
    private static readonly object _consentCacheLock = new();
    private static readonly List<ActiveCameraEntry> _consentCache = new();
    private static DateTime _consentCacheAt = DateTime.MinValue;

    private readonly record struct ActiveCameraEntry(string RawKey, bool NonPackaged, long Start);

    /// <summary>
    /// Apps with an open camera session, newest session first, deduplicated by friendly name.
    /// The consent store is read at most once every 400 ms, so the monitor, the test button and the
    /// notification service all share a single enumeration.
    /// </summary>
    public static IReadOnlyList<string> GetActiveCameraAppNames()
    {
        var names = new List<string>();
        foreach (var entry in ReadActiveCameraEntries())
        {
            var name = FriendlyAppName(entry.RawKey, entry.NonPackaged);
            if (string.IsNullOrWhiteSpace(name) || IsAppLock(name)) continue;
            AddAppName(names, name);
        }
        return names;
    }

    private static List<ActiveCameraEntry> ReadActiveCameraEntries()
    {
        lock (_consentCacheLock)
        {
            if ((DateTime.UtcNow - _consentCacheAt).TotalMilliseconds < ConsentReadCacheMilliseconds)
                return new List<ActiveCameraEntry>(_consentCache);
        }

        var entries = new List<ActiveCameraEntry>();
        CollectActiveEntries(Registry.CurrentUser, entries);
        if (entries.Count == 0) CollectActiveEntries(Registry.LocalMachine, entries);

        // The latest session wins, so the app the user just started is the one shown.
        entries.Sort((a, b) => b.Start.CompareTo(a.Start));

        lock (_consentCacheLock)
        {
            _consentCache.Clear();
            _consentCache.AddRange(entries);
            _consentCacheAt = DateTime.UtcNow;
            return new List<ActiveCameraEntry>(_consentCache);
        }
    }

    private static void CollectActiveEntries(RegistryKey root, List<ActiveCameraEntry> into)
    {
        try
        {
            using var baseKey = root.OpenSubKey(WebcamConsentSubKey);
            if (baseKey == null) return;

            // Packaged (Store/MSIX) apps: the key is the package family name.
            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (subKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryReadActiveEntry(baseKey, subKeyName, out long start))
                    into.Add(new ActiveCameraEntry(subKeyName, false, start));
            }

            // Desktop apps: the key is the exe path with '#' instead of '\'.
            using var nonPackaged = baseKey.OpenSubKey("NonPackaged");
            if (nonPackaged == null) return;
            foreach (var subKeyName in nonPackaged.GetSubKeyNames())
            {
                if (TryReadActiveEntry(nonPackaged, subKeyName, out long start))
                    into.Add(new ActiveCameraEntry(subKeyName, true, start));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraUsageMonitor] Consent store read failed: {ex.Message}");
        }
    }

    private static bool TryReadActiveEntry(RegistryKey parent, string subKeyName, out long start)
    {
        start = 0;
        try
        {
            using var sub = parent.OpenSubKey(subKeyName);
            if (sub == null) return false;

            var startValue = sub.GetValue("LastUsedTimeStart");
            if (startValue == null) return false;

            start = ToInt64Safe(startValue);
            var stopValue = sub.GetValue("LastUsedTimeStop");
            long stop = stopValue != null ? ToInt64Safe(stopValue) : 0;

            return start > stop;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Dictionary<string, string> _friendlyNameCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _friendlyNameLock = new();

    /// <summary>Product name of a consent-store key, e.g. <c>msedge.exe</c> or
    /// <c>5319275A.WhatsAppDesktop_cv1g1gvanyjgm</c>.</summary>
    public static string FriendlyAppName(string rawKey, bool nonPackaged)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return string.Empty;

        string cacheKey = (nonPackaged ? "np:" : "pk:") + rawKey;
        lock (_friendlyNameLock)
        {
            if (_friendlyNameCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        string name = nonPackaged ? DescribeDesktopApp(rawKey) : DescribePackagedApp(rawKey);

        lock (_friendlyNameLock) _friendlyNameCache[cacheKey] = name;
        return name;
    }

    private static string DescribeDesktopApp(string rawKey)
    {
        string path = rawKey.Replace('#', '\\');
        string exe = Path.GetFileName(path);
        if (string.IsNullOrEmpty(exe)) return path;

        if (KnownDesktopExeLookup.TryGetValue(exe, out var knownExe)) return knownExe;
        if (KnownCameraProcessLookup.TryGetValue(Path.GetFileNameWithoutExtension(exe), out var knownApp)) return knownApp;

        // Unknown desktop app: its own version resource is the best name available.
        try
        {
            if (File.Exists(path))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(description) && !description.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    return description;
            }
        }
        catch
        {
            // Unreadable image (permissions, deleted file): fall back to the executable name.
        }

        return Path.GetFileNameWithoutExtension(exe);
    }

    private static string DescribePackagedApp(string rawKey)
    {
        // Package family name: <Identity>_<PublisherId>.
        string identity = rawKey;
        int underscore = identity.IndexOf('_');
        if (underscore > 0) identity = identity[..underscore];

        if (FindStoreCameraApp(identity) is string known) return known;

        // "Microsoft.WindowsCamera" -> "WindowsCamera", "TheBrowserCompany.Dia" -> "Dia".
        string tail = identity;
        int dot = tail.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < tail.Length) tail = tail[(dot + 1)..];

        var spaced = AddWordSpaces(tail);
        return string.IsNullOrWhiteSpace(spaced) ? identity : spaced;
    }

    private static string AddWordSpaces(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new System.Text.StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Enumerates physical camera devices recognized by Windows PnP (e.g. Integrated Camera, USB Webcams).
    /// </summary>
    public IReadOnlyList<string> GetInstalledCameras()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name))
                {
                    list.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Hardware enumeration error: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Enumerates video capture devices asynchronously via Windows.Devices.Enumeration,
    /// falling back to WMI if empty.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetInstalledCamerasAsync()
    {
        var list = new List<string>();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (var d in devices)
            {
                if (!string.IsNullOrWhiteSpace(d.Name) && !list.Contains(d.Name))
                {
                    list.Add(d.Name);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] DeviceInformation enumeration error: {ex.Message}");
        }

        if (list.Count == 0)
        {
            foreach (var c in GetInstalledCameras())
            {
                if (!list.Contains(c)) list.Add(c);
            }
        }

        return list;
    }

    /// <summary>
    /// One process scan, shared by every caller for a fraction of a second. Returns the running Store
    /// (MSIX/UWP) camera apps and desktop camera apps, each with its process ids: a Store app may run
    /// several helper processes that all belong to the same app.
    /// </summary>
    private (List<(uint Pid, string Display)> Store, List<(uint Pid, string Display)> Desktop)
        ScanRunningCameraApps()
    {
        lock (_scanLock)
        {
            if (_scanValid && (DateTime.UtcNow - _scanAt).TotalMilliseconds < ScanCacheMilliseconds)
                return (_storeCandidates, _desktopCandidates);
        }

        var store = new List<(uint Pid, string Display)>();
        var desktop = new List<(uint Pid, string Display)>();
        var alive = new HashSet<uint>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = (uint)process.Id;
                alive.Add(pid);
                var name = process.ProcessName;

                string? identity;
                lock (_scanLock)
                {
                    if (_packageIdentity.TryGetValue(pid, out var cached) && cached.Name == name)
                    {
                        identity = cached.Identity;
                    }
                    else
                    {
                        identity = GetStorePackageIdentity(process);
                        _packageIdentity[pid] = (name, identity);
                    }
                }

                var storeDisplay = FindStoreCameraApp(identity);
                if (storeDisplay != null)
                {
                    store.Add((pid, storeDisplay));
                    continue;
                }

                if (KnownCameraProcessLookup.TryGetValue(name, out var desktopDisplay))
                    desktop.Add((pid, desktopDisplay));
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        lock (_scanLock)
        {
            foreach (var pid in new List<uint>(_packageIdentity.Keys))
            {
                if (!alive.Contains(pid)) _packageIdentity.Remove(pid);
            }

            _storeCandidates = store;
            _desktopCandidates = desktop;
            _scanAt = DateTime.UtcNow;
            _scanValid = true;
        }

        PruneActivitySamples(alive);
        return (store, desktop);
    }

    /// <summary>
    /// Splits running camera apps into the ones that are actually working and the ones that are only
    /// open. A process' first sighting records a CPU baseline and returns no verdict, so callers needing
    /// an immediate answer must sample twice (see <see cref="CheckCameraStatusAsync"/>).
    /// </summary>
    private (List<string> Active, List<string> Idle) ClassifyRunningApps(List<(uint Pid, string Display)> apps)
    {
        var active = new List<string>();
        var idle = new List<string>();
        var now = DateTime.UtcNow;

        // Group an app's processes together, so a helper process doing the capture still counts.
        var byApp = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, display) in apps)
        {
            if (!byApp.TryGetValue(display, out var pids)) byApp[display] = pids = new List<uint>();
            if (!pids.Contains(pid)) pids.Add(pid);
        }

        foreach (var (display, pids) in byApp)
        {
            var rate = SampleAppActivity(display, pids, now);
            if (UpdateAppState(display, rate)) active.Add(display);
            else idle.Add(display);
        }

        // The busiest app is the one actually capturing, so it should be named first.
        active.Sort((a, b) => RateOf(b).CompareTo(RateOf(a)));
        return (active, idle);
    }

    /// <summary>
    /// Hysteresis over the app's recent CPU history. Turning on needs a sustained rate (the window
    /// average above the start threshold, with almost every sample above the hold threshold), which
    /// is what a real capture looks like; a background sync that burns CPU for a second or two no
    /// longer counts. Once on, only the window average falling below the hold threshold turns it off.
    /// </summary>
    private bool UpdateAppState(string display, double? rate)
    {
        lock (_activityLock)
        {
            if (!_appWindows.TryGetValue(display, out var window))
                _appWindows[display] = window = new RateWindow();

            // No baseline yet (first sighting of the process): keep whatever we already know.
            if (rate is not double measured) return _appConfirmed.Contains(display);

            window.Add(measured);

            bool active = _appConfirmed.Contains(display)
                ? window.Average >= AppHoldThresholdMsPerSecond
                : window.Count >= AppRateWindowMinimumSamples &&
                  window.Average >= AppStartThresholdMsPerSecond &&
                  window.CountAtLeast(AppHoldThresholdMsPerSecond) >= window.Count - 1;

            if (active) _appConfirmed.Add(display);
            else _appConfirmed.Remove(display);

            return active;
        }
    }

    /// <summary>Adds a name once, ignoring case ("WhatsApp" and "whatsapp" are one app).</summary>
    private static void AddAppName(List<string> apps, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!apps.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            apps.Add(name);
    }

    private double RateOf(string display)
    {
        lock (_activityLock)
        {
            return _appActivity.TryGetValue(display, out var sample) ? sample.RateMsPerSecond : 0;
        }
    }

    /// <summary>
    /// CPU usage of everything an app is running (all its processes) in milliseconds per second,
    /// smoothed so a single busy moment does not flip the state. Null while no baseline is known yet.
    /// </summary>
    private double? SampleAppActivity(string display, List<uint> pids, DateTime now)
    {
        double? total = null;

        foreach (var pid in pids)
        {
            var rate = SampleProcessCpuRate(pid, now);
            if (rate is double value) total = (total ?? 0) + value;
        }

        if (total is null) return null;

        lock (_activityLock)
        {
            // The first real measurement is used as-is so a call that is already running is seen
            // immediately; later samples are smoothed to keep a lone busy second from flipping it.
            // The first real measurement is used as-is so a call that is already running is seen
            // immediately; later samples are smoothed so one busy moment does not dominate.
            var smoothed = _appActivity.TryGetValue(display, out var previous) && previous.Measured
                ? previous.RateMsPerSecond * (1 - _appActivitySmoothing) + total.Value * _appActivitySmoothing
                : total.Value;

            _appActivity[display] = new AppActivitySample(now, smoothed, true);
            return smoothed;
        }
    }

    /// <summary>CPU rate of one process in milliseconds per second, or null if not measured yet.</summary>
    private double? SampleProcessCpuRate(uint pid, DateTime now)
    {
        TimeSpan cpu;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            cpu = process.TotalProcessorTime;
        }
        catch
        {
            return null;
        }

        lock (_activityLock)
        {
            if (_pidCpu.TryGetValue(pid, out var previous))
            {
                var elapsedMs = (now - previous.At).TotalMilliseconds;
                if (elapsedMs >= 250)
                {
                    var usedMs = Math.Max(0, (cpu - previous.Cpu).TotalMilliseconds);
                    var rate = usedMs / elapsedMs * 1000.0;
                    _pidCpu[pid] = new PidCpuSample(cpu, now, rate);
                    return rate;
                }

                // Asked again inside the sampling window (the Store and desktop lists are both
                // classified in one tick): reuse the rate just measured instead of answering
                // "unknown", which used to make the state flap between ticks.
                return previous.RateMsPerSecond;
            }

            _pidCpu[pid] = new PidCpuSample(cpu, now, 0);
            return null;
        }
    }

    /// <summary>Drops CPU samples of processes that closed or stopped being sampled.</summary>
    private void PruneActivitySamples(HashSet<uint> alive)
    {
        var now = DateTime.UtcNow;
        lock (_activityLock)
        {
            foreach (var pid in new List<uint>(_pidCpu.Keys))
            {
                if (!alive.Contains(pid) || (now - _pidCpu[pid].At).TotalSeconds > 30) _pidCpu.Remove(pid);
            }

            foreach (var app in new List<string>(_appActivity.Keys))
            {
                if ((now - _appActivity[app].At).TotalSeconds <= 30) continue;
                _appActivity.Remove(app);
                _appWindows.Remove(app);
                _appConfirmed.Remove(app);
            }
        }
    }

    /// <summary>Display name of a known Store camera app for the given package identity, if any.</summary>
    private static string? FindStoreCameraApp(string? packageIdentity)
    {
        if (string.IsNullOrEmpty(packageIdentity)) return null;
        return KnownStoreCameraLookup.TryGetValue(packageIdentity, out var display) ? display : null;
    }

    /// <summary>
    /// Package identity an already-running process belongs to, or null for unpackaged (desktop) apps.
    /// Reads the image path and takes the package folder name under <c>WindowsApps</c>, stripping the
    /// version/architecture/publisher suffix.
    /// </summary>
    private static string? GetStorePackageIdentity(Process process)
    {
        string? path;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch
        {
            // Processes of other users or elevated processes refuse access; no identity then.
            return null;
        }
        return ExtractStorePackageIdentity(path);
    }

    private static string? ExtractStorePackageIdentity(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;

        const string appsRoot = @"\WindowsApps\";
        int root = imagePath.IndexOf(appsRoot, StringComparison.OrdinalIgnoreCase);
        if (root < 0) return null;

        // ...\WindowsApps\<Identity>_<Version>_<Arch>__<Publisher>\app.exe
        var rest = imagePath[(root + appsRoot.Length)..];
        int separator = rest.IndexOf('\\');
        var folder = separator > 0 ? rest[..separator] : rest;

        int underscore = folder.IndexOf('_');
        return underscore > 0 ? folder[..underscore] : folder;
    }

    /// <summary>
    /// Detects active video capture utility processes spawned by Chromium browsers
    /// (Google Chrome, Microsoft Edge, Brave, Opera, Vivaldi) and WebView2/Electron apps when a web page
    /// or web call (such as Instagram, Google Meet, Teams Web, Zoom Web) starts streaming the webcam.
    ///
    /// Uses WMI to find processes whose CommandLine contains the Chromium video capture service
    /// argument. This subprocess is spawned by the browser ONLY while the camera is actively streaming
    /// and disappears the moment the camera is released — making it a precise, real-time indicator.
    /// </summary>
    /// <summary>
    /// Best-effort names of the processes currently using a camera. Kept as a public static probe so
    /// diagnostics (and tests) can query it without constructing a monitor. Never throws.
    /// </summary>
    public static IReadOnlyList<string> GetRunningCameraAppNames()
    {
        try
        {
            return GetActiveVideoCaptureProcesses();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Running camera app probe error: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> GetActiveVideoCaptureProcesses()
    {
        var found = new List<string>();
        foreach (var proc in QueryVideoCaptureServiceProcesses())
        {
            if (!found.Contains(proc.Display)) found.Add(proc.Display);
        }
        return found;
    }

    /// <summary>
    /// Chromium video capture utility processes with their PIDs. The process outlives the camera
    /// session, so callers must check whether it is actually working (see GetActiveBrowserCaptureApps).
    /// Results are cached for a second, because the poll runs every second while a browser is open.
    /// </summary>
    private static List<(uint Pid, string Display)> QueryVideoCaptureServiceProcesses()
    {
        lock (CaptureQueryLock)
        {
            if ((DateTime.UtcNow - _captureQueryAt).TotalMilliseconds < CaptureQueryCacheMilliseconds)
                return _captureQueryCache;
        }

        var found = new List<(uint Pid, string Display)>();
        try
        {
            // ManagementScope and ObjectQuery do not implement IDisposable — do not wrap in using.
            var scope = new System.Management.ManagementScope(@"\\.\root\cimv2");
            scope.Options.Timeout = TimeSpan.FromSeconds(5);

            // WHERE CommandLine IS NOT NULL skips system/protected processes where WMI cannot
            // read CommandLine (avoids silent filter failures on null rows).
            var query = new System.Management.ObjectQuery(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process " +
                "WHERE CommandLine IS NOT NULL AND CommandLine LIKE '%video_capture.mojom.VideoCaptureService%'");

            var options = new System.Management.EnumerationOptions { ReturnImmediately = false };
            using var searcher = new System.Management.ManagementObjectSearcher(scope, query, options);

            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";

                // Only real browsers: other processes can match the command-line filter (for
                // example a shell running a command that mentions the marker).
                string? display = name.ToLowerInvariant() switch
                {
                    "chrome.exe"  => "Google Chrome",
                    "msedge.exe"  => "Microsoft Edge",
                    "brave.exe"   => "Brave",
                    "opera.exe"   => "Opera",
                    "vivaldi.exe" => "Vivaldi",
                    // WebView2 hosts (new Teams, WhatsApp/Store apps with embedded web calls).
                    "msedgewebview2.exe" => "Web view app",
                    _ => null
                };

                if (display == null) continue;

                // The service is hosted by the app that opened the camera — the browser itself, or the
                // WebView2 process of an app like WhatsApp or new Teams. Naming the owner instead of
                // the host type is what the user recognizes.
                uint parent = 0;
                try { parent = Convert.ToUInt32(obj["ParentProcessId"]); } catch { }
                if (DescribeCaptureOwner(parent) is string owner) display = owner;

                uint pid = 0;
                try { pid = Convert.ToUInt32(obj["ProcessId"]); } catch { }

                if (pid != 0 && !found.Exists(f => f.Pid == pid))
                    found.Add((pid, display));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] VideoCaptureService WMI query error: {ex.Message}");
        }

        lock (CaptureQueryLock)
        {
            _captureQueryCache = found;
            _captureQueryAt = DateTime.UtcNow;
        }

        return found;
    }

    /// <summary>
    /// Browser web-call apps whose Chromium video capture service is actually working.
    /// The service process stays alive after a call ends, so its CPU time is sampled and only a
    /// service that keeps consuming CPU (converting frames) counts as an active camera user.
    /// </summary>
    private IReadOnlyList<string> GetActiveBrowserCaptureApps()
    {
        var active = new List<string>();

        if (!HasRunningBrowserOrCameraProcess())
        {
            PruneCaptureSamples(new HashSet<uint>());
            return active;
        }

        var processes = QueryVideoCaptureServiceProcesses();
        var seen = new HashSet<uint>();
        foreach (var proc in processes) seen.Add(proc.Pid);
        PruneCaptureSamples(seen);

        var now = DateTime.UtcNow;
        foreach (var proc in processes)
        {
            TimeSpan cpu;
            try
            {
                using var p = Process.GetProcessById((int)proc.Pid);
                cpu = p.TotalProcessorTime;
            }
            catch
            {
                continue;
            }

            bool isWorking = false;
            lock (_captureSamplesLock)
            {
                if (_captureSamples.TryGetValue(proc.Pid, out var previous))
                {
                    isWorking = previous.Active;
                    if ((now - previous.At).TotalMilliseconds >= 400)
                    {
                        var usedMs = (cpu - previous.Cpu).TotalMilliseconds;
                        isWorking = usedMs >= BrowserCaptureCpuThresholdMs;
                        _captureSamples[proc.Pid] = new CaptureSample(cpu, now, isWorking);
                    }
                }
                else
                {
                    // First sighting: remember a baseline, decide on the next sample.
                    _captureSamples[proc.Pid] = new CaptureSample(cpu, now, false);
                }
            }

            if (isWorking && !active.Contains(proc.Display)) active.Add(proc.Display);
        }

        return active;
    }

    /// <summary>
    /// Product name of the app that owns a capture service process. WebView2 hosts the service in
    /// <c>msedgewebview2.exe</c>, whose parent is the actual app, so the chain is walked a little.
    /// </summary>
    private static string? DescribeCaptureOwner(uint pid)
    {
        for (int depth = 0; depth < 3 && pid != 0; depth++)
        {
            string name;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
            }
            catch
            {
                return null;
            }

            if (!name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                return FriendlyRunningProcessName(name, pid);

            pid = GetParentProcessId(pid);
        }

        return null;
    }

    private static string? FriendlyRunningProcessName(string processName, uint pid)
    {
        if (KnownCameraProcessLookup.TryGetValue(processName, out var known)) return known;
        if (KnownDesktopExeLookup.TryGetValue(processName + ".exe", out var knownExe)) return knownExe;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(description)) return description;
            }
        }
        catch
        {
            // Access denied or closed process: the caller falls back to the host type.
        }

        return null;
    }

    private static uint GetParentProcessId(uint pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
            {
                return Convert.ToUInt32(obj["ParentProcessId"]);
            }
        }
        catch { }

        return 0;
    }

    private void PruneCaptureSamples(HashSet<uint> stillRunning)
    {
        lock (_captureSamplesLock)
        {
            if (_captureSamples.Count == 0) return;
            var stale = new List<uint>();
            foreach (var pid in _captureSamples.Keys)
            {
                if (!stillRunning.Contains(pid)) stale.Add(pid);
            }
            foreach (var pid in stale) _captureSamples.Remove(pid);
        }
    }

    /// <summary>
    /// Fast pre-check so the WMI query only runs while a browser or camera app is open.
    /// </summary>
    private static bool HasRunningBrowserOrCameraProcess()
    {
        try
        {
            return Process.GetProcessesByName("chrome").Length > 0 ||
                   Process.GetProcessesByName("msedge").Length > 0 ||
                   Process.GetProcessesByName("brave").Length > 0 ||
                   Process.GetProcessesByName("opera").Length > 0 ||
                   Process.GetProcessesByName("vivaldi").Length > 0 ||
                   Process.GetProcessesByName("msedgewebview2").Length > 0 ||
                   Process.GetProcessesByName("WindowsCamera").Length > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Reports camera hardware plus the apps Windows currently has an open camera session for.
    /// </summary>
    public async Task<CameraCheckResult> CheckCameraStatusAsync()
    {
        var installed = await GetInstalledCamerasAsync();

        // Detection is layered because no single Windows source is reliable everywhere:
        //  1. Windows usage records (consent store) — precise, but recent builds only write them once
        //     a session ends.
        //  2. Chromium capture-service processes, but only while they are actually consuming CPU,
        //     since the utility process stays alive as long as the browser is open.
        //  3. Running Store (UWP) and desktop camera apps, but only while they are actually working.
        var recordApps = GetActiveCameraApps();
        var (storeCandidates, desktopCandidates) = ScanRunningCameraApps();
        var (storeActive, storeIdle) = ClassifyRunningApps(storeCandidates);
        var (desktopActive, desktopIdle) = ClassifyRunningApps(desktopCandidates);
        var browserApps = GetActiveBrowserCaptureApps();

        // A process' first sighting is only a CPU baseline, and a sustained stream needs a few samples
        // before it is accepted, so the check keeps sampling instead of reporting "not capturing" for a
        // call that is running right now.
        for (int round = 0; round < 4; round++)
        {
            bool needBrowser = browserApps.Count == 0;
            bool needApps = (storeCandidates.Count > 0 || desktopCandidates.Count > 0) &&
                            storeActive.Count == 0 && desktopActive.Count == 0;
            if (!needBrowser && !needApps) break;

            await Task.Delay(600);

            if (needBrowser)
            {
                var again = GetActiveBrowserCaptureApps();
                if (again.Count > 0) browserApps = again;
            }

            if (storeCandidates.Count > 0) (storeActive, storeIdle) = ClassifyRunningApps(storeCandidates);
            if (desktopCandidates.Count > 0) (desktopActive, desktopIdle) = ClassifyRunningApps(desktopCandidates);
        }

        var checks = new List<CameraCheckSignal>
        {
            new("Camera hardware", installed.Count > 0,
                installed.Count > 0 ? string.Join(", ", installed) : "no camera device found"),
            new("Windows usage records", recordApps.Count > 0,
                recordApps.Count > 0 ? string.Join(", ", recordApps) : "no app reported as using the camera"),
            new("Store (UWP) camera apps", storeActive.Count > 0,
                DescribeRunningApps(storeActive, storeIdle, "no Store camera app is running")),
            new("Desktop camera apps", desktopActive.Count > 0,
                DescribeRunningApps(desktopActive, desktopIdle, "no known desktop camera app is running")),
            new("Browser video capture", browserApps.Count > 0,
                browserApps.Count > 0
                    ? string.Join(", ", browserApps) + " (capture service active)"
                    : "no browser is capturing"),
        };

        var activeApps = new List<string>();
        foreach (var group in new[] { recordApps, storeActive, desktopActive, browserApps })
        {
            foreach (var app in group) AddAppName(activeApps, app);
        }

        bool isStreaming = activeApps.Count > 0;
        string reason = installed.Count == 0
            ? "No camera hardware detected"
            : isStreaming ? string.Join(", ", activeApps) : "Camera is idle";

        Debug.WriteLine(
            $"[CameraUsageMonitor] Check: streaming={isStreaming} apps=[{string.Join(", ", activeApps)}] " +
            $"records=[{recordApps.Count}] storeApps=[{storeActive.Count}/{storeCandidates.Count}] " +
            $"apps=[{desktopActive.Count}/{desktopCandidates.Count}] browsers=[{browserApps.Count}]");

        return new CameraCheckResult
        {
            InstalledCameras = installed,
            IsStreaming = isStreaming,
            ActiveApps = activeApps,
            Reason = reason,
            Checks = checks
        };
    }

    /// <summary>
    /// Detail line for a running-app check: who is actually working and who is only open.
    /// </summary>
    private static string DescribeRunningApps(List<string> active, List<string> idle, string noneText)
    {
        if (active.Count == 0 && idle.Count == 0) return noneText;

        var parts = new List<string>();
        if (active.Count > 0) parts.Add(string.Join(", ", active) + " (active)");
        if (idle.Count > 0) parts.Add(string.Join(", ", idle) + " (open, no camera activity)");
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Checks whether active camera apps match the specified <see cref="AutoRingSettings"/> filter.
    /// </summary>
    public static bool MatchesFilter(IReadOnlyList<string> activeApps, AutoRingSettings auto)
    {
        if (activeApps.Count == 0) return false;
        if (!auto.OnlySelectedApps || auto.AppFilter.Count == 0) return true;

        foreach (var app in activeApps)
        {
            foreach (var filter in auto.AppFilter)
            {
                if (string.IsNullOrWhiteSpace(filter)) continue;
                var trimmed = filter.Trim();
                if (app.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains(app, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _isMonitoring = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void OnPollTick(object? state)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0) return;

        try
        {
            if (!_isMonitoring) return;

            // Layered detection, same as CheckCameraStatusAsync: Windows usage records first, then
            // browser capture services that are actually using CPU, then Store (UWP) and desktop
            // camera apps that are actually working (an idle app is not using the camera).
            // The later (more expensive) checks only run while the earlier ones found nothing.
            var activeApps = new List<string>();
            foreach (var app in GetActiveCameraApps()) AddAppName(activeApps, app);

            if (activeApps.Count == 0 && _useBrowserCaptureHeuristic)
            {
                foreach (var app in GetActiveBrowserCaptureApps()) AddAppName(activeApps, app);
            }

            if (activeApps.Count == 0)
            {
                // Both lists are classified every tick so every candidate keeps a warm CPU baseline;
                // previously a free Store app hid the desktop app actually streaming.
                var (storeCandidates, desktopCandidates) = ScanRunningCameraApps();

                var ranked = new List<(string Display, double Rate)>();
                foreach (var app in ClassifyRunningApps(storeCandidates).Active) ranked.Add((app, RateOf(app)));
                foreach (var app in ClassifyRunningApps(desktopCandidates).Active) ranked.Add((app, RateOf(app)));
                ranked.Sort((a, b) => b.Rate.CompareTo(a.Rate));

                foreach (var (display, _) in ranked) AddAppName(activeApps, display);
            }

            lock (_lock)
            {
                if (!_isMonitoring) return;

                if (activeApps.Count > 0)
                {
                    _consecutiveActiveSeconds++;
                    _consecutiveInactiveSeconds = 0;

                    string appsKey = string.Join(",", activeApps);
                    // 1 tick of continuous use required to turn on, or update if the active app changed
                    if ((!_isDebouncedInUse && _consecutiveActiveSeconds >= 1) ||
                        (_isDebouncedInUse && !string.Equals(_lastNotifiedAppsKey, appsKey, StringComparison.Ordinal)))
                    {
                        _isDebouncedInUse = true;
                        _lastNotifiedAppsKey = appsKey;
                        NotifyUsageChanged(true, activeApps);
                    }
                }
                else
                {
                    _consecutiveInactiveSeconds++;
                    _consecutiveActiveSeconds = 0;

                    // Silence debounce before turning off
                    if (_isDebouncedInUse && _consecutiveInactiveSeconds >= _inactiveDebounceSeconds)
                    {
                        _isDebouncedInUse = false;
                        _lastNotifiedAppsKey = string.Empty;
                        NotifyUsageChanged(false, Array.Empty<string>());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraUsageMonitor] Poll tick error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private void NotifyUsageChanged(bool inUse, IReadOnlyList<string> activeApps)
    {
        try
        {
            CameraUsageChanged?.Invoke(this, (inUse, activeApps));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraUsageMonitor] Event error: {ex.Message}");
        }
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

    private static bool IsAppLock(string name)
    {
        return name.Contains("AppLock", StringComparison.OrdinalIgnoreCase);
    }
}
