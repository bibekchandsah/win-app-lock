using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;

#if !APPLOCK_TESTS
using Microsoft.UI.Dispatching;
using AppLock.Services.SpeedMeter;
using AppLock.Views;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace AppLock.Services.Firewall;

public class CandidatePortInfo
{
    public string Protocol { get; set; } = "TCP";
    public int Port { get; set; }
    public string? AppPath { get; set; }
    public string? AppName { get; set; }
    public PortScope Scope { get; set; }
    public DateTime FirstSeen { get; set; }
    public bool Alerted { get; set; }
}

public class PortWatchService : IDisposable
{
    private readonly IListeningPortService _portService;
    private readonly IFirewallService? _firewallService;
#if !APPLOCK_TESTS
    private DispatcherQueue? _dispatcherQueue;
#endif
    private CancellationTokenSource? _cts;
    private Task? _watchTask;

    private readonly Dictionary<string, CandidatePortInfo> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CandidatePortInfo> _pendingBatch = new();
    private readonly List<CandidatePortInfo> _suppressedQueue = new();
    private DateTime _batchStartTime = DateTime.MinValue;

    public Func<AppSettings?>? SettingsProvider { get; set; }
    public Action<AppSettings>? SettingsSaver { get; set; }

    public DateTime AlertsSuppressedUntil { get; set; } = DateTime.MinValue;
    public bool IsSuppressed => DateTime.UtcNow < AlertsSuppressedUntil;

    public event EventHandler<IReadOnlyList<ListeningPort>>? BaselineSuggested;
    public event EventHandler<IReadOnlyList<CandidatePortInfo>>? PortsAlerted;

    public IReadOnlyDictionary<string, CandidatePortInfo> Candidates => _candidates;

    public PortWatchService(IListeningPortService portService, IFirewallService? firewallService = null)
    {
        _portService = portService;
        _firewallService = firewallService;
    }

#if !APPLOCK_TESTS
    public void SetDispatcherQueue(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }
#endif

    public void Start()
    {
        if (_watchTask != null && !_watchTask.IsCompleted)
            return;

        _cts = new CancellationTokenSource();
        _watchTask = Task.Run(() => WatchLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _watchTask = null;
    }

    public async Task<List<ListeningPort>> TakeBaselineAsync(CancellationToken ct = default)
    {
        var ports = await _portService.GetListeningPortsAsync(ct);
        var reachable = ports.Where(p => p.Scope != PortScope.LocalhostOnly).ToList();
        BaselineSuggested?.Invoke(this, reachable);
        return reachable;
    }

    private AppSettings GetSettings()
    {
#if !APPLOCK_TESTS
        return SettingsProvider?.Invoke() ?? ServiceContainer.Settings;
#else
        return SettingsProvider?.Invoke() ?? new AppSettings();
#endif
    }

    private void SaveSettings(AppSettings settings)
    {
        SettingsSaver?.Invoke(settings);
    }

    public void MarkBaselineAsKnown(IEnumerable<ListeningPort> reachablePorts)
    {
        var settings = GetSettings();
        var known = settings.ListeningPorts.KnownPorts ??= new List<KnownPort>();

        int added = 0;
        foreach (var p in reachablePorts)
        {
            if (!known.Any(k => k.Port == p.Port &&
                                string.Equals(k.Protocol, p.Protocol, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(k.AppPath, p.AppPath ?? "", StringComparison.OrdinalIgnoreCase)))
            {
                known.Add(new KnownPort
                {
                    Port = p.Port,
                    Protocol = p.Protocol.ToUpperInvariant(),
                    AppPath = p.AppPath ?? "",
                    Added = DateTime.Now
                });
                added++;
            }
        }

        // Suppress alerts for 10 minutes after baseline/enabling
        AlertsSuppressedUntil = DateTime.UtcNow.AddMinutes(10);

        if (added > 0)
        {
            SaveSettings(settings);
        }
    }

    public static bool IsDynamicSystemPort(int port, string? serviceName, string? appPath)
    {
        if (port < 49152 || port > 65535)
            return false;

        if (!string.IsNullOrEmpty(serviceName) && InstalledAppsService.IsSystemService(serviceName))
            return true;

        if (!string.IsNullOrEmpty(appPath) && InstalledAppsService.IsSystemCritical(appPath))
            return true;

        return false;
    }

    private static string GetKey(string protocol, int port, string? appPath)
    {
        return $"{protocol.ToUpperInvariant()}|{port}|{appPath ?? ""}";
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = GetSettings().ListeningPorts;
                if (!settings.WatchNewPorts)
                {
                    await Task.Delay(5000, ct);
                    continue;
                }

                await ScanAndEvaluateAsync(ct);

                // Check pending batch timeout (1 minute)
                CheckAndDispatchBatch();

                await Task.Delay(4000, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PortWatchService] Loop error: {ex.Message}");
                await Task.Delay(5000, ct);
            }
        }
    }

    public async Task ScanAndEvaluateAsync(CancellationToken ct = default)
    {
        var settings = GetSettings().ListeningPorts;
        var currentPorts = await _portService.GetListeningPortsAsync(ct);
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in currentPorts)
        {
            // Only care about ports reachable from the network
            if (p.Scope == PortScope.LocalhostOnly)
                continue;

            // Ignore firewall blocked ports
            if (p.Exposure == FirewallExposure.BlockedByAppLock || p.Exposure == FirewallExposure.BlockedByDefault)
                continue;

            // Ignore dynamic ports from Windows system services if enabled
            if (settings.WatchIgnoreSystemDynamic && IsDynamicSystemPort(p.Port, p.ServiceName, p.AppPath))
                continue;

            // Check if already in KnownPorts
            if (settings.KnownPorts != null &&
                settings.KnownPorts.Any(k => k.Port == p.Port &&
                                            string.Equals(k.Protocol, p.Protocol, StringComparison.OrdinalIgnoreCase) &&
                                            (string.IsNullOrEmpty(k.AppPath) || string.Equals(k.AppPath, p.AppPath ?? "", StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var key = GetKey(p.Protocol, p.Port, p.AppPath);
            activeKeys.Add(key);

            if (!_candidates.TryGetValue(key, out var candidate))
            {
                // New candidate port discovered!
                _candidates[key] = new CandidatePortInfo
                {
                    Protocol = p.Protocol,
                    Port = p.Port,
                    AppPath = p.AppPath,
                    AppName = p.AppName ?? "Unknown",
                    Scope = p.Scope,
                    FirstSeen = DateTime.UtcNow,
                    Alerted = false
                };
            }
            else if (!candidate.Alerted)
            {
                // Has it lived for at least 10 seconds? (Ignore short-lived ports)
                var elapsed = (DateTime.UtcNow - candidate.FirstSeen).TotalSeconds;
                if (elapsed >= 10)
                {
                    candidate.Alerted = true;
                    HandleQualifiedPort(candidate);
                }
            }
        }

        // Clean up candidates that disappeared (short-lived ports are discarded without alerting)
        var toRemove = _candidates.Keys.Where(k => !activeKeys.Contains(k)).ToList();
        foreach (var k in toRemove)
        {
            _candidates.Remove(k);
        }
    }

    private static bool IsFullscreen()
    {
#if !APPLOCK_TESTS
        return FullscreenDetector.IsFullscreenAppActive();
#else
        return false;
#endif
    }

    private void HandleQualifiedPort(CandidatePortInfo port)
    {
        // 1. Suppressed during initial baseline grace period (10 minutes)?
        if (IsSuppressed)
            return;

        // 2. Suppress in fullscreen?
        if (IsFullscreen())
        {
            _suppressedQueue.Add(port);
            return;
        }

        // 3. Batching within 1 minute
        if (_pendingBatch.Count == 0)
        {
            _batchStartTime = DateTime.UtcNow;
        }

        _pendingBatch.Add(port);
    }

    private void CheckAndDispatchBatch()
    {
        // If we have suppressed items from fullscreen, and fullscreen has ended, flush them
        if (_suppressedQueue.Count > 0 && !IsFullscreen())
        {
            foreach (var item in _suppressedQueue)
            {
                if (_pendingBatch.Count == 0) _batchStartTime = DateTime.UtcNow;
                _pendingBatch.Add(item);
            }
            _suppressedQueue.Clear();
        }

        if (_pendingBatch.Count == 0)
            return;

        // If batch has been collecting for >= 10 seconds or reached 3+ items, dispatch
        var elapsed = (DateTime.UtcNow - _batchStartTime).TotalSeconds;
        if (elapsed >= 10 || _pendingBatch.Count >= 3)
        {
            var batchCopy = _pendingBatch.ToList();
            _pendingBatch.Clear();

            DispatchToast(batchCopy);
        }
    }

    private void DispatchToast(List<CandidatePortInfo> batch)
    {
        if (batch.Count == 0) return;

        PortsAlerted?.Invoke(this, batch);

#if !APPLOCK_TESTS
        _dispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                if (batch.Count == 1)
                {
                    var port = batch[0];
                    var title = "New port open to your network";
                    var scopeText = port.Scope == PortScope.AllInterfaces ? "All interfaces" : "Local network";
                    var subtitle = $"{port.AppName} · {port.Protocol} Port {port.Port} ({scopeText})";

                    var toast = new PortWatchToastWindow(
                        title,
                        subtitle,
                        onBlock: () => _ = BlockPortAsync(port.Protocol, port.Port),
                        onAllow: () => AllowPort(port),
                        onDetails: () => OpenDetails(port));

                    toast.Activate();
                }
                else
                {
                    // Batch summary toast
                    var title = "Multiple new ports open to your network";
                    var portsSummary = string.Join(", ", batch.Select(p => $"{p.Protocol} {p.Port}"));
                    var subtitle = $"{batch.Count} new ports detected: {portsSummary}";

                    var toast = new PortWatchToastWindow(
                        title,
                        subtitle,
                        onBlock: null,
                        onAllow: () =>
                        {
                            foreach (var p in batch) AllowPort(p);
                        },
                        onDetails: () => OpenDetails(batch[0]),
                        isSummary: true);

                    toast.Activate();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PortWatchService] Toast dispatch error: {ex.Message}");
            }
        });
#endif
    }

    public Func<string, int, Task>? PortBlocker { get; set; }

    private async Task BlockPortAsync(string protocol, int port)
    {
        try
        {
            if (PortBlocker != null)
            {
                await PortBlocker(protocol, port);
            }
            else if (_firewallService != null)
            {
                var protoUpper = protocol.ToUpperInvariant();
                var id = Guid.NewGuid();
                await _firewallService.CreatePortBlockRuleAsync(protoUpper, port, id, int.MaxValue, "Blocked via Port Watch");

                var settings = GetSettings();
                settings.ListeningPorts.PortRules.RemoveAll(r =>
                    string.Equals(r.Protocol, protoUpper, StringComparison.OrdinalIgnoreCase) &&
                    r.LocalPorts == port.ToString());
                settings.ListeningPorts.PortRules.Add(new PortRuleEntry
                {
                    Id = id,
                    Protocol = protoUpper,
                    LocalPorts = port.ToString(),
                    Profiles = int.MaxValue,
                    Created = DateTime.UtcNow,
                    Note = "Blocked via Port Watch"
                });
                SaveSettings(settings);

                FirewallExposureEvaluator.InvalidateCache();
                ListeningPortService.InvalidateCache();
            }

#if !APPLOCK_TESTS
            ServiceContainer.NotificationService.ShowWarning(
                "Port Blocked",
                $"Incoming traffic on {protocol} port {port} is now blocked.");
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PortWatchService] Block error: {ex.Message}");
        }
    }

    private void AllowPort(CandidatePortInfo port)
    {
        var settings = GetSettings();
        var known = settings.ListeningPorts.KnownPorts ??= new List<KnownPort>();

        if (!known.Any(k => k.Port == port.Port &&
                            string.Equals(k.Protocol, port.Protocol, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(k.AppPath, port.AppPath ?? "", StringComparison.OrdinalIgnoreCase)))
        {
            known.Add(new KnownPort
            {
                Port = port.Port,
                Protocol = port.Protocol.ToUpperInvariant(),
                AppPath = port.AppPath ?? "",
                Added = DateTime.Now
            });

            SaveSettings(settings);

#if !APPLOCK_TESTS
            ServiceContainer.NotificationService.ShowSuccess(
                "Port Allowed",
                $"{port.Protocol} port {port.Port} added to known ports.");
#endif
        }
    }

    private void OpenDetails(CandidatePortInfo port)
    {
#if !APPLOCK_TESTS
        try
        {
            var win = App.MainWindowInstance ?? MainWindow.Instance;
            if (win != null)
            {
                win.Activate();
            }
        }
        catch { }
#endif
    }

    public void Dispose()
    {
        Stop();
    }
}
