using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

/// <summary>Result of a single detection method (hardware, sensor monitor, Windows consent records).</summary>
public sealed record CameraCheckSignal(string Label, bool Detected, string Detail);

/// <summary>
/// Event-driven webcam usage monitor matching <c>CamUsageWin.cpp</c>:
/// <list type="number">
///   <item><b>LIVE (Sensor Activity Monitor)</b>: dynamically loads <c>mfsensorgroup.dll</c>
///   (<c>MFCreateSensorActivityMonitor</c>) + <c>mfplat.dll</c> (<c>MFStartup</c>/<c>MFShutdown</c>)
///   for instant callbacks on active streaming PIDs, resolving <c>msedgewebview2.exe</c> children
///   up the parent chain with PID-reuse protection.</item>
///   <item><b>ConsentStore Registry</b>: watches
///   <c>HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam</c>
///   (+ <c>NonPackaged</c>) via <c>RegNotifyChangeKeyValue</c> where <c>LastUsedTimeStart != 0</c>
///   and <c>LastUsedTimeStop == 0</c>, with running-process stale protection for desktop apps.</item>
/// </list>
/// Triggers are coalesced with a 150 ms trailing-edge debounce plus a 5-second safety timer.
/// </summary>
public sealed class CameraUsageMonitor : ICameraUsageSource
{
    private const string WebcamConsentSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private const int DebounceMs = 150;
    private const int SafetyIntervalMs = 5000;
    private const int ConsentReadCacheMilliseconds = 400;

    private static readonly (string ProcessName, string DisplayName)[] KnownCameraProcesses =
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

    private static readonly (string PackageName, string DisplayName)[] KnownStoreCameraApps =
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

    private static readonly Dictionary<string, string> KnownCameraProcessLookup = BuildLookup(KnownCameraProcesses);
    private static readonly Dictionary<string, string> KnownStoreCameraLookup = BuildLookup(KnownStoreCameraApps);

    private static Dictionary<string, string> BuildLookup((string Key, string DisplayName)[] entries)
    {
        var map = new Dictionary<string, string>(entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, display) in entries) map[key] = display;
        return map;
    }

    public event EventHandler<(bool InUse, IReadOnlyList<string> ActiveApps)>? CameraUsageChanged;

    private readonly object _lock = new();
    private readonly object _sensorLock = new();
    private readonly object _evalLock = new();

    private Timer? _debounceTimer;
    private Timer? _safetyTimer;
    private ManualResetEventSlim? _stopEvent;
    private Thread? _regWatchThread;

    private SensorCallback? _sensorCallback;
    private IMFSensorActivityMonitor? _sensorMonitor;
    private bool _mfStarted;
    private bool _sensorSupported = true;

    private bool _isMonitoring;
    private bool _isInUse;
    private string _lastNotifiedAppsKey = string.Empty;
    private List<(string Name, string ExePath)> _activeDetails = new();
    private bool _disposed;

    public CameraUsageMonitor(
        int inactiveDebounceSeconds = 5,
        int pollIntervalMs = 1000,
        bool useBrowserCaptureHeuristic = true,
        double appActivitySmoothing = 0.3)
    {
        // Parameters kept for binary/source compatibility with callers; detection is now
        // event-driven via MFCreateSensorActivityMonitor + RegNotifyChangeKeyValue.
        _ = inactiveDebounceSeconds;
        _ = pollIntervalMs;
        _ = useBrowserCaptureHeuristic;
        _ = appActivitySmoothing;
    }

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
                if (NativeLibrary.TryLoad("mfsensorgroup.dll", out nint h))
                {
                    NativeLibrary.Free(h);
                    return true;
                }
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
            _isInUse = false;
            _lastNotifiedAppsKey = string.Empty;
            _activeDetails = new List<(string Name, string ExePath)>();

            _stopEvent = new ManualResetEventSlim(false);
            _debounceTimer = new Timer(_ => EvaluateUsage(), null, Timeout.Infinite, Timeout.Infinite);
            _safetyTimer = new Timer(_ => OnSafetyTick(), null, SafetyIntervalMs, SafetyIntervalMs);

            StartSensorMonitor();

            var stopEvt = _stopEvent;
            _regWatchThread = new Thread(() => RegWatchLoop(stopEvt))
            {
                IsBackground = true,
                Name = "CameraConsentRegWatcher"
            };
            _regWatchThread.Start();
        }

        // Evaluate initial state right after starting sensor + registry watcher.
        ScheduleRefresh();
    }

    public void Stop()
    {
        ManualResetEventSlim? stopEvt;
        Thread? regThread;
        Timer? debounce;
        Timer? safety;

        lock (_lock)
        {
            if (!_isMonitoring) return;
            _isMonitoring = false;
            stopEvt = _stopEvent;
            regThread = _regWatchThread;
            debounce = _debounceTimer;
            safety = _safetyTimer;
            _stopEvent = null;
            _regWatchThread = null;
            _debounceTimer = null;
            _safetyTimer = null;
            _isInUse = false;
            _lastNotifiedAppsKey = string.Empty;
            _activeDetails = new List<(string Name, string ExePath)>();
        }

        try { debounce?.Dispose(); } catch { }
        try { safety?.Dispose(); } catch { }
        try { stopEvt?.Set(); } catch { }
        try { regThread?.Join(500); } catch { }
        try { stopEvt?.Dispose(); } catch { }

        StopSensorMonitor();
    }

    /// <summary>
    /// Currently active camera apps (live sensor monitor + non-stale ConsentStore records).
    /// </summary>
    public IReadOnlyList<string> GetActiveCameraApps()
    {
        var details = GetActiveDetailsSnapshot();
        if (details.Count > 0)
        {
            var names = new List<string>(details.Count);
            foreach (var d in details) AddAppName(names, d.Name);
            return names;
        }
        return GetActiveCameraAppNames();
    }

    /// <summary>
    /// Active camera apps with their resolved executable paths (when available for icons).
    /// </summary>
    public IReadOnlyList<(string Name, string ExePath)> GetActiveCameraDetails()
        => GetActiveDetailsSnapshot();

    private IReadOnlyList<(string Name, string ExePath)> GetActiveDetailsSnapshot()
    {
        lock (_lock)
        {
            if (_isMonitoring && _activeDetails.Count > 0)
            {
                return new List<(string Name, string ExePath)>(_activeDetails);
            }
        }
        return CollectAllActiveDetails();
    }

    // ── Triggering & Evaluation (150 ms trailing-edge debounce) ───────────────

    private void ScheduleRefresh()
    {
        lock (_lock)
        {
            if (!_isMonitoring || _disposed) return;
            try { _debounceTimer?.Change(DebounceMs, Timeout.Infinite); } catch { }
        }
    }

    private void OnSafetyTick()
    {
        if (_disposed || !IsMonitoring) return;
        if (_sensorSupported && _sensorMonitor == null)
        {
            StartSensorMonitor();
        }
        EvaluateUsage();
    }

    private void EvaluateUsage()
    {
        if (_disposed || !IsMonitoring) return;

        lock (_evalLock)
        {
            if (_disposed || !IsMonitoring) return;

            var details = CollectAllActiveDetails();
            var activeApps = new List<string>(details.Count);
            foreach (var d in details) AddAppName(activeApps, d.Name);

            bool shouldNotify = false;
            bool inUseNow = activeApps.Count > 0;
            string appsKey = string.Empty;

            if (inUseNow)
            {
                var keyNames = new List<string>(activeApps);
                keyNames.Sort(StringComparer.OrdinalIgnoreCase);
                appsKey = string.Join(",", keyNames);
            }

            lock (_lock)
            {
                if (!_isMonitoring) return;
                _activeDetails = details;

                if (inUseNow)
                {
                    if (!_isInUse || !string.Equals(_lastNotifiedAppsKey, appsKey, StringComparison.Ordinal))
                    {
                        _isInUse = true;
                        _lastNotifiedAppsKey = appsKey;
                        shouldNotify = true;
                    }
                }
                else if (_isInUse)
                {
                    _isInUse = false;
                    _lastNotifiedAppsKey = string.Empty;
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
            {
                NotifyUsageChanged(inUseNow, activeApps);
            }
        }
    }

    private List<(string Name, string ExePath)> CollectAllActiveDetails()
    {
        var result = new List<(string Name, string ExePath)>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Dictionary<uint, ProcInfo>? procs = null;
        Dictionary<uint, ProcInfo> GetProcs() => procs ??= SnapshotProcesses();

        // 1) Live Media Foundation Sensor Activity Monitor (matches CamUsageWin.cpp BuildLiveRows)
        List<LiveCam> liveSnap;
        lock (_sensorLock)
        {
            liveSnap = _sensorCallback != null ? _sensorCallback.GetLiveSnapshot() : new List<LiveCam>();
        }

        if (liveSnap.Count > 0)
        {
            var procMap = GetProcs();
            uint selfPid = (uint)Environment.ProcessId;
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cam in liveSnap)
            {
                uint hostPid = ResolveHostPid(cam.Pid, procMap);
                if (hostPid == selfPid) continue;

                string exePath = PidToExePath(hostPid);
                string fallbackName = procMap.TryGetValue(hostPid, out var pInfo) ? pInfo.Exe : string.Empty;
                string dedupKey = (string.IsNullOrEmpty(exePath) ? $"pid:{hostPid}" : exePath) + "|" + cam.Camera;
                if (!seenKeys.Add(dedupKey)) continue;

                string friendly = ResolveLiveFriendlyName(hostPid, exePath, fallbackName);
                if (string.IsNullOrWhiteSpace(friendly) || IsAppLock(friendly)) continue;

                if (seenNames.Add(friendly))
                {
                    result.Add((friendly, exePath));
                }
            }
        }

        // 2) ConsentStore\webcam (Start != 0 && Stop == 0), with running-process stale protection
        var regEntries = ReadActiveCameraEntries();
        if (regEntries.Count > 0)
        {
            var procMap = GetProcs();
            foreach (var entry in regEntries)
            {
                string exePath = entry.NonPackaged ? entry.RawKey.Replace('#', '\\') : string.Empty;
                if (entry.NonPackaged && !IsDesktopAppRunning(exePath, procMap))
                {
                    continue; // Stale entry: process exited/crashed while Stop == 0
                }

                string friendly = FriendlyAppName(entry.RawKey, entry.NonPackaged);
                if (string.IsNullOrWhiteSpace(friendly) || IsAppLock(friendly)) continue;

                if (seenNames.Add(friendly))
                {
                    result.Add((friendly, File.Exists(exePath) ? exePath : string.Empty));
                }
            }
        }

        return result;
    }

    // ── Source 1: Media Foundation Sensor Activity Monitor ────────────────────

    private readonly record struct LiveCam(uint Pid, string Camera);

    [ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorProcessActivity
    {
        [PreserveSig] int GetProcessId(out uint pPID);
        [PreserveSig] int GetStreamingState([MarshalAs(UnmanagedType.Bool)] out bool pfStreaming);
        [PreserveSig] int GetStreamingMode(out int pMode);
        [PreserveSig] int GetReportTime(out long pft);
    }

    [ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivityReport
    {
        [PreserveSig] int GetFriendlyName([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder friendlyName, uint cchFriendlyName, out uint pcchWritten);
        [PreserveSig] int GetSymbolicLink([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder symbolicLink, uint cchSymbolicLink, out uint pcchWritten);
        [PreserveSig] int GetProcessCount(out uint pcCount);
        [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity ppProcessActivity);
    }

    [ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivitiesReport
    {
        [PreserveSig] int GetCount(out uint pcCount);
        [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport sensorActivityReport);
        [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string symbolicName, out IMFSensorActivityReport sensorActivityReport);
    }

    [ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivitiesReportCallback
    {
        [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport sensorActivitiesReport);
    }

    [ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSensorActivityMonitor
    {
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MFCreateSensorActivityMonitorDelegate(
        IMFSensorActivitiesReportCallback pCallback,
        out IMFSensorActivityMonitor ppActivityMonitor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MFStartupDelegate(uint version, uint dwFlags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MFShutdownDelegate();

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class SensorCallback : IMFSensorActivitiesReportCallback
    {
        private readonly CameraUsageMonitor _owner;
        private readonly object _mtx = new();
        private List<LiveCam> _live = new();

        public SensorCallback(CameraUsageMonitor owner) => _owner = owner;

        public List<LiveCam> GetLiveSnapshot()
        {
            lock (_mtx) return new List<LiveCam>(_live);
        }

        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            try
            {
                var now = new List<LiveCam>();
                if (report != null && report.GetCount(out uint count) == 0)
                {
                    var nameBuf = new StringBuilder(256);
                    for (uint i = 0; i < count; i++)
                    {
                        if (report.GetActivityReport(i, out var act) != 0 || act == null) continue;
                        try
                        {
                            nameBuf.Clear();
                            string camName = act.GetFriendlyName(nameBuf, (uint)nameBuf.Capacity, out _) == 0
                                ? nameBuf.ToString()
                                : string.Empty;

                            if (act.GetProcessCount(out uint procCount) == 0)
                            {
                                for (uint j = 0; j < procCount; j++)
                                {
                                    if (act.GetProcessActivity(j, out var pa) != 0 || pa == null) continue;
                                    try
                                    {
                                        if (pa.GetStreamingState(out bool streaming) == 0 && streaming &&
                                            pa.GetProcessId(out uint pid) == 0 && pid != 0)
                                        {
                                            now.Add(new LiveCam(pid, camName));
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.ReleaseComObject(pa);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(act);
                        }
                    }
                }

                now.Sort((a, b) => a.Pid != b.Pid
                    ? a.Pid.CompareTo(b.Pid)
                    : string.CompareOrdinal(a.Camera, b.Camera));

                bool changed;
                lock (_mtx)
                {
                    changed = now.Count != _live.Count;
                    if (!changed)
                    {
                        for (int k = 0; k < now.Count; k++)
                        {
                            if (now[k] != _live[k]) { changed = true; break; }
                        }
                    }
                    if (changed) _live = now;
                }

                if (changed)
                {
                    _owner.ScheduleRefresh();
                }
            }
            catch
            {
                // Swallow: exceptions must never cross the COM boundary.
            }
            return 0; // S_OK
        }
    }

    private void StartSensorMonitor()
    {
        lock (_sensorLock)
        {
            if (_sensorMonitor != null || !_sensorSupported) return;

            try
            {
                if (!NativeLibrary.TryLoad("mfsensorgroup.dll", out nint hSensorDll) ||
                    !NativeLibrary.TryGetExport(hSensorDll, "MFCreateSensorActivityMonitor", out nint pCreate))
                {
                    _sensorSupported = false;
                    return;
                }

                if (!NativeLibrary.TryLoad("mfplat.dll", out nint hMfPlat) ||
                    !NativeLibrary.TryGetExport(hMfPlat, "MFStartup", out nint pStartup))
                {
                    _sensorSupported = false;
                    return;
                }

                var mfStartup = Marshal.GetDelegateForFunctionPointer<MFStartupDelegate>(pStartup);
                const uint MF_VERSION = 0x00020070;
                const uint MFSTARTUP_NOSOCKET = 1;
                if (mfStartup(MF_VERSION, MFSTARTUP_NOSOCKET) < 0) return;
                _mfStarted = true;

                var create = Marshal.GetDelegateForFunctionPointer<MFCreateSensorActivityMonitorDelegate>(pCreate);
                _sensorCallback = new SensorCallback(this);
                if (create(_sensorCallback, out var mon) < 0 || mon == null)
                {
                    StopSensorMonitorLocked();
                    return;
                }

                _sensorMonitor = mon;
                if (_sensorMonitor.Start() < 0)
                {
                    StopSensorMonitorLocked();
                }
            }
            catch
            {
                StopSensorMonitorLocked();
            }
        }
    }

    private void StopSensorMonitor()
    {
        lock (_sensorLock) StopSensorMonitorLocked();
    }

    private void StopSensorMonitorLocked()
    {
        if (_sensorMonitor != null)
        {
            try { _sensorMonitor.Stop(); } catch { }
            try { Marshal.ReleaseComObject(_sensorMonitor); } catch { }
            _sensorMonitor = null;
        }
        _sensorCallback = null;

        if (_mfStarted)
        {
            try
            {
                if (NativeLibrary.TryLoad("mfplat.dll", out nint hMfPlat) &&
                    NativeLibrary.TryGetExport(hMfPlat, "MFShutdown", out nint pShutdown))
                {
                    Marshal.GetDelegateForFunctionPointer<MFShutdownDelegate>(pShutdown)();
                }
            }
            catch { }
            _mfStarted = false;
        }
    }

    // ── Source 2: ConsentStore Registry Watcher & Reader ──────────────────────

    private void RegWatchLoop(ManualResetEventSlim stopEvt)
    {
        nint hKey = nint.Zero;
        using var regEvt = new AutoResetEvent(false);
        var waits = new WaitHandle[] { stopEvt.WaitHandle, regEvt };

        try
        {
            while (!stopEvt.IsSet)
            {
                if (hKey == nint.Zero)
                {
                    int openRc = RegOpenKeyExW(
                        HKEY_CURRENT_USER,
                        WebcamConsentSubKey,
                        0,
                        KEY_NOTIFY | KEY_READ,
                        out hKey);
                    if (openRc != 0 || hKey == nint.Zero)
                    {
                        hKey = nint.Zero;
                        if (stopEvt.Wait(5000)) break;
                        continue;
                    }
                }

                int notifyRc = RegNotifyChangeKeyValue(
                    hKey,
                    bWatchSubtree: true,
                    REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                    regEvt.SafeWaitHandle.DangerousGetHandle(),
                    fAsynchronous: true);

                if (notifyRc != 0)
                {
                    RegCloseKey(hKey);
                    hKey = nint.Zero;
                    if (stopEvt.Wait(5000)) break;
                    continue;
                }

                int signaled = WaitHandle.WaitAny(waits);
                if (signaled == 0 || stopEvt.IsSet) break;
                if (signaled == 1) ScheduleRefresh();
            }
        }
        catch { }
        finally
        {
            if (hKey != nint.Zero)
            {
                try { RegCloseKey(hKey); } catch { }
            }
        }
    }

    private static readonly object _consentCacheLock = new();
    private static readonly List<ActiveCameraEntry> _consentCache = new();
    private static DateTime _consentCacheAt = DateTime.MinValue;

    private readonly record struct ActiveCameraEntry(string RawKey, bool NonPackaged, long Start);

    /// <summary>
    /// Apps with an open ConsentStore camera session (<c>Start != 0 &amp;&amp; Stop == 0</c>), newest first.
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

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                if (subKeyName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (TryReadActiveEntry(baseKey, subKeyName, out long start))
                    into.Add(new ActiveCameraEntry(subKeyName, false, start));
            }

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

    // Exact rule from CamUsageWin.cpp: in use <=> Start != 0 && Stop == 0 (missing Stop => not in use).
    private static bool TryReadActiveEntry(RegistryKey parent, string subKeyName, out long start)
    {
        start = 0;
        try
        {
            using var sub = parent.OpenSubKey(subKeyName);
            if (sub == null) return false;

            var startValue = sub.GetValue("LastUsedTimeStart");
            var stopValue = sub.GetValue("LastUsedTimeStop");
            if (startValue == null || stopValue == null) return false;

            start = ToInt64Safe(startValue);
            long stop = ToInt64Safe(stopValue);
            return start != 0 && stop == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Process Snapshot, WebView2 Host Resolution & Stale Protection ─────────

    private readonly record struct ProcInfo(uint ParentPid, string Exe);

    private static Dictionary<uint, ProcInfo> SnapshotProcesses()
    {
        var map = new Dictionary<uint, ProcInfo>(256);
        nint snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return map;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    map[entry.th32ProcessID] = new ProcInfo(entry.th32ParentProcessID, entry.szExeFile ?? string.Empty);
                }
                while (Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    private static uint ResolveHostPid(uint pid, Dictionary<uint, ProcInfo> procs)
    {
        uint cur = pid;
        for (int hop = 0; hop < 7; hop++)
        {
            if (!procs.TryGetValue(cur, out var info)) return pid;
            if (!string.Equals(info.Exe, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) return cur;

            uint parent = info.ParentPid;
            if (parent == 0 || parent == cur) return pid;
            if (TryGetProcessCreateTime(cur, out ulong tc) &&
                TryGetProcessCreateTime(parent, out ulong tp) &&
                tp > tc)
            {
                return pid; // PID reuse guard: parent cannot be younger than child
            }
            cur = parent;
        }
        return pid;
    }

    private static bool IsDesktopAppRunning(string exePath, Dictionary<uint, ProcInfo> procs)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        string leaf = Path.GetFileName(exePath);
        foreach (var kv in procs)
        {
            if (!string.Equals(kv.Value.Exe, leaf, StringComparison.OrdinalIgnoreCase)) continue;
            string p = PidToExePath(kv.Key);
            if (string.IsNullOrEmpty(p) || string.Equals(p, exePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string PidToExePath(uint pid)
    {
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == nint.Zero) return string.Empty;
        try
        {
            var sb = new StringBuilder(512);
            int cap = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref cap) && cap > 0)
            {
                return sb.ToString(0, cap);
            }
        }
        catch { }
        finally
        {
            CloseHandle(h);
        }
        return string.Empty;
    }

    private static bool TryGetProcessCreateTime(uint pid, out ulong createTime)
    {
        createTime = 0;
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == nint.Zero) return false;
        try
        {
            if (GetProcessTimes(h, out long c, out _, out _, out _))
            {
                createTime = (ulong)c;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string ResolveLiveFriendlyName(uint hostPid, string exePath, string fallbackExe)
    {
        if (!string.IsNullOrEmpty(exePath))
        {
            string? pkg = ExtractStorePackageIdentity(exePath);
            if (!string.IsNullOrEmpty(pkg))
            {
                return DescribePackagedApp(pkg);
            }
            return DescribeDesktopApp(exePath);
        }

        if (!string.IsNullOrEmpty(fallbackExe))
        {
            return DescribeDesktopApp(fallbackExe);
        }

        return $"PID {hostPid}";
    }

    // ── Friendly Naming ───────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> _friendlyNameCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _friendlyNameLock = new();

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

        try
        {
            if (File.Exists(path))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(description) && !description.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    return description;
            }
        }
        catch { }

        return Path.GetFileNameWithoutExtension(exe);
    }

    private static string DescribePackagedApp(string rawKey)
    {
        string identity = rawKey;
        int underscore = identity.IndexOf('_');
        if (underscore > 0) identity = identity[..underscore];

        if (KnownStoreCameraLookup.TryGetValue(identity, out var known)) return known;

        string tail = identity;
        int dot = tail.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < tail.Length) tail = tail[(dot + 1)..];

        var spaced = AddWordSpaces(tail);
        return string.IsNullOrWhiteSpace(spaced) ? identity : spaced;
    }

    private static string AddWordSpaces(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]) && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string? ExtractStorePackageIdentity(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;

        const string appsRoot = @"\WindowsApps\";
        int root = imagePath.IndexOf(appsRoot, StringComparison.OrdinalIgnoreCase);
        if (root < 0) return null;

        var rest = imagePath[(root + appsRoot.Length)..];
        int separator = rest.IndexOf('\\');
        var folder = separator > 0 ? rest[..separator] : rest;

        int underscore = folder.IndexOf('_');
        return underscore > 0 ? folder[..underscore] : folder;
    }

    // ── Hardware & Diagnostic Probes (preserved for Settings / Tests) ─────────

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
            Debug.WriteLine($"[CameraUsageMonitor] Hardware enumeration error: {ex.Message}");
        }
        return list;
    }

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
            Debug.WriteLine($"[CameraUsageMonitor] DeviceInformation enumeration error: {ex.Message}");
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

    public static IReadOnlyList<string> GetRunningCameraAppNames()
    {
        var names = new List<string>();
        foreach (var entry in ReadActiveCameraEntries())
        {
            var name = FriendlyAppName(entry.RawKey, entry.NonPackaged);
            if (!string.IsNullOrWhiteSpace(name) && !IsAppLock(name)) AddAppName(names, name);
        }
        return names;
    }

    public static IReadOnlyList<string> GetActiveVideoCaptureProcesses() => GetRunningCameraAppNames();

    public async Task<CameraCheckResult> CheckCameraStatusAsync()
    {
        var installed = await GetInstalledCamerasAsync();

        bool startedTempSensor = false;
        lock (_sensorLock)
        {
            if (_sensorMonitor == null && _sensorSupported)
            {
                StartSensorMonitor();
                startedTempSensor = _sensorMonitor != null;
            }
        }

        if (startedTempSensor)
        {
            // Allow Media Foundation to deliver the initial OnActivitiesReport callback.
            await Task.Delay(250);
        }

        IReadOnlyList<(string Name, string ExePath)> details;
        try
        {
            details = CollectAllActiveDetails();
        }
        finally
        {
            if (startedTempSensor)
            {
                StopSensorMonitor();
            }
        }

        var activeApps = new List<string>(details.Count);
        foreach (var d in details) AddAppName(activeApps, d.Name);

        var regApps = GetActiveCameraAppNames();
        var checks = new List<CameraCheckSignal>
        {
            new("Camera hardware", installed.Count > 0,
                installed.Count > 0 ? string.Join(", ", installed) : "no camera device found"),
            new("Sensor Activity Monitor", _sensorSupported,
                activeApps.Count > 0
                    ? string.Join(", ", activeApps)
                    : (_sensorSupported ? "listening (no active stream)" : "unsupported on this OS")),
            new("Windows ConsentStore", regApps.Count > 0,
                regApps.Count > 0 ? string.Join(", ", regApps) : "no active ConsentStore entry"),
        };

        bool isStreaming = activeApps.Count > 0;
        string reason = installed.Count == 0
            ? "No camera hardware detected"
            : isStreaming ? string.Join(", ", activeApps) : "Camera is idle";

        return new CameraCheckResult
        {
            InstalledCameras = installed,
            IsStreaming = isStreaming,
            ActiveApps = activeApps,
            Reason = reason,
            Checks = checks
        };
    }

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
        }
        Stop();
    }

    // ── Helpers & Win32 P/Invokes ─────────────────────────────────────────────

    private static void AddAppName(List<string> apps, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!apps.Exists(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            apps.Add(name);
    }

    private void NotifyUsageChanged(bool inUse, IReadOnlyList<string> activeApps)
    {
        try
        {
            CameraUsageChanged?.Invoke(this, (inUse, activeApps));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CameraUsageMonitor] Event error: {ex.Message}");
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
        => name.Contains("AppLock", StringComparison.OrdinalIgnoreCase);

    private static readonly nint HKEY_CURRENT_USER = unchecked((nint)0x80000001);
    private static readonly nint INVALID_HANDLE_VALUE = new(-1);
    private const uint KEY_READ = 0x20019;
    private const uint KEY_NOTIFY = 0x0010;
    private const uint REG_NOTIFY_CHANGE_NAME = 0x00000001;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(nint hKey, string lpSubKey, uint ulOptions, uint samDesired, out nint phkResult);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(nint hKey);

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(nint hKey, bool bWatchSubtree, uint dwNotifyFilter, nint hEvent, bool fAsynchronous);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(nint hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
