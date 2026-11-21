using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.WebFilter.Engine;

namespace AppLock.Services.WebFilter;

public sealed class WebFilterService : IAsyncDisposable, IDisposable
{
    private static readonly IPAddress[] CandidateLoopbackAddresses = new[]
    {
        IPAddress.Loopback,                    // 127.0.0.1
        IPAddress.Parse("127.0.0.53"),          // systemd-resolved style
        IPAddress.Parse("127.0.53.53")           // collision fallback
    };

    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly WebFilterListService _listService;
    private readonly DnsConfigurator _configurator;
    private readonly DnsVerifier _verifier;
    private readonly WebFilterLogService? _logService;
    private readonly WebFilterFirewallService _firewallService;
    private readonly BrowserPolicyService _browserPolicyService;
    private readonly VpnAndProxyDetector _vpnDetector;
    private readonly ConcurrentDictionary<string, DateTime> _notificationThrottle = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DnsFilterServer? _server;
    private UpstreamForwarder? _forwarder;
    private Timer? _watchdogTimer;
    private int _watchdogFailures;
    private bool _isDisposed;
    private bool _isProviderModeActive;    // true when running in Filtered DNS Provider mode
    private Port53ConflictResolver? _conflictResolver;

    public event EventHandler? StatusChanged;
    public event EventHandler<string>? TamperDetected;
    public event EventHandler<string>? ListenerFailed;
    public event EventHandler<BlockedQueryEventArgs>? QueryBlocked;

    public WebFilterService(
        AppSettings settings,
        Action<AppSettings> saveSettings,
        WebFilterListService listService,
        DnsConfigurator? configurator = null,
        WebFilterLogService? logService = null,
        WebFilterFirewallService? firewallService = null,
        BrowserPolicyService? browserPolicyService = null,
        VpnAndProxyDetector? vpnDetector = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        _listService = listService ?? throw new ArgumentNullException(nameof(listService));
        _configurator = configurator ?? new DnsConfigurator();
        _logService = logService;
        _firewallService = firewallService ?? new WebFilterFirewallService();
        _browserPolicyService = browserPolicyService ?? new BrowserPolicyService();
        _vpnDetector = vpnDetector ?? new VpnAndProxyDetector();

        _verifier = new DnsVerifier(
            _configurator,
            () => _settings.WebFilter,
            GetExpectedDnsAddress,
            GetProviderIps);

        _verifier.DnsDriftDetected += (s, adapters) =>
        {
            TamperDetected?.Invoke(this, $"DNS settings were changed outside AppLock on: {string.Join(", ", adapters)}");
        };
    }

    public DnsConfigurator Configurator => _configurator;
    public WebFilterFirewallService FirewallService => _firewallService;
    public BrowserPolicyService BrowserPolicyService => _browserPolicyService;
    public VpnAndProxyDetector VpnDetector => _vpnDetector;

    public bool IsRunning => _server is { IsRunning: true };

    /// <summary>True while the filter is active in any mode (local server or provider DNS).</summary>
    public bool IsActive => IsRunning || _isProviderModeActive;

    public string? BoundAddress => _server?.BoundEndPoint?.Address.ToString();

    public int? BoundPort => _server?.BoundEndPoint?.Port;

    public string? LastError { get; private set; }

    public DnsServerStats? Stats => _server?.Stats;

    /// <summary>Clears the DNS cache of the running filter server.</summary>
    public void ClearCache() => _server?.ClearCache();

    /// <summary>Updates SafeSearch and YouTube restriction rules on the running server and flushes cache.</summary>
    public void UpdateSafeSearch()
    {
        if (_server != null)
        {
            _server.SafeSearchRewriter = CreateSafeSearchRewriter();
            _server.ClearCache();
        }
    }

    /// <summary>
    /// Rebuilds the DNS filter policy from current settings and applies it to the running
    /// filter server, clearing the DNS cache so new rules take effect immediately.
    /// </summary>
    public void UpdatePolicy()
    {
        if (_server != null)
        {
            _listService.Refresh();
            _server.Policy = _listService.CreateDnsPolicy();
            _server.ClearCache();
            _configurator.AdapterService.FlushDnsCache();
        }
    }

    private SafeSearchRewriter CreateSafeSearchRewriter()
    {
        var googleDomains = _listService.Catalog.GoogleDomains;
        var mode = _settings.WebFilter.YouTubeMode switch
        {
            YouTubeRestriction.Moderate => YouTubeRestrictionMode.Moderate,
            YouTubeRestriction.Strict => YouTubeRestrictionMode.Strict,
            _ => YouTubeRestrictionMode.Off
        };
        return new SafeSearchRewriter(_settings.WebFilter.EnforceSafeSearch, mode, googleDomains);
    }

    /// <summary>
    /// Checks on startup whether DNS is currently pointing to loopback from an unexpected
    /// previous shutdown or kill, and repairs or starts the filter.
    /// </summary>
    public void CheckStartupRepair()
    {
        try
        {
            bool dnsPointsToFilter = _configurator.IsDnsPointingToFilter();

            if (_settings.WebFilter.Enabled && _settings.WebFilter.StartWithAppLock)
            {
                Debug.WriteLine("[WebFilter] Starting filter on application startup...");
                _ = Task.Run(async () =>
                {
                    bool ok = await StartAsync();
                    if (!ok && dnsPointsToFilter)
                    {
                        Debug.WriteLine("[WebFilter] Startup start failed. Restoring original DNS as fail-safe.");
                        RestoreNetworkSettings();
                    }
                });
            }
            else if (dnsPointsToFilter)
            {
                // DNS points to loopback but filter is disabled: clean up lingering settings!
                Debug.WriteLine("[WebFilter] Startup repair: DNS points to filter but WebFilter is disabled. Restoring original DNS.");
                _configurator.RestoreOriginalDns(_settings.WebFilter);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] Startup repair check failed: {ex.Message}");
        }
    }

    /// <summary>Starts the local filter server and points system DNS to it.</summary>
    public async Task<bool> StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsActive) return true;

            LastError = null;

            // ── Provider mode: no local server, just point DNS at the provider ─────────
            if (_settings.WebFilter.Mode == WebFilterMode.Provider)
            {
                return await StartProviderModeAsync().ConfigureAwait(false);
            }

            // ── Local filter mode ─────────────────────────────────────────────────────
            await _listService.InitializeAsync().ConfigureAwait(false);

            // 1. Create upstream forwarder
            _forwarder = CreateForwarder();

            // 2. Select policy
            var policy = _listService.CreateDnsPolicy();
            var safeSearchRewriter = CreateSafeSearchRewriter();

            // 3a. Free port 53 from conflicting services (e.g. ICS / Windows DNS Server)
            _conflictResolver = new Port53ConflictResolver();
            if (Port53ConflictResolver.IsPort53Conflicted())
            {
                Debug.WriteLine("[WebFilter] Port 53 conflict detected. Attempting to stop conflicting services...");
                bool freed = await _conflictResolver.ResolveConflictsAsync().ConfigureAwait(false);
                if (!freed)
                {
                    LastError = "Port 53 is in use by another service and could not be freed. " +
                                "Disable Internet Connection Sharing (ICS) or Windows DNS Server and try again.";
                    Debug.WriteLine($"[WebFilter] {LastError}");
                    _conflictResolver.Dispose();
                    _conflictResolver = null;
                    _forwarder?.Dispose();
                    _forwarder = null;
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                    return false;
                }
            }

            // 3b. Try candidate loopback addresses on port 53
            DnsFilterServer? startedServer = null;
            foreach (var candidate in CandidateLoopbackAddresses)
            {
                var options = new DnsServerOptions
                {
                    BindAddress = candidate,
                    Port = 53,
                    MaxConcurrentQueries = 256,
                    QueryTimeout = TimeSpan.FromSeconds(3),
                    BlockedAnswer = _settings.WebFilter.BlockResponse == BlockResponse.NullAddress
                        ? BlockedAnswer.NullAddress
                        : BlockedAnswer.NxDomain,
                    CacheEnabled = _settings.WebFilter.CacheEnabled
                };

                var server = new DnsFilterServer(options, _forwarder, policy, safeSearchRewriter: safeSearchRewriter);
                bool ok = await server.StartAsync().ConfigureAwait(false);
                if (ok)
                {
                    startedServer = server;
                    break;
                }
                else
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (startedServer == null)
            {
                LastError = "Port 53 could not be bound on any loopback address. Another DNS service may be using it.";
                Debug.WriteLine($"[WebFilter] {LastError}");
                _conflictResolver?.Dispose();
                _conflictResolver = null;
                _forwarder?.Dispose();
                _forwarder = null;
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            _server = startedServer;
            _server.QueryBlocked += OnServerQueryBlocked;
            _server.QueryProcessed += OnServerQueryProcessed;
            string boundIp = startedServer.BoundEndPoint!.Address.ToString();

            // 4. Point system DNS to the bound filter address
            bool dnsApplied = _configurator.ApplyFilterDns(_settings.WebFilter, boundIp, "::1");
            if (!dnsApplied)
            {
                LastError = "Failed to apply DNS to network adapters. Administrator privileges are required.";
                Debug.WriteLine($"[WebFilter] {LastError}");
                await StopInternalAsync(restoreDns: true).ConfigureAwait(false);
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            // 5. Apply bypass protection (firewall rules and browser policies)
            await ApplyBypassProtectionInternalAsync().ConfigureAwait(false);

            // Save settings with updated loopback address
            _saveSettings(_settings);

            // 6. Start DNS verifier and self-test watchdog
            _verifier.Start();
            StartWatchdog();

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"[WebFilter] StartAsync failed: {ex}");
            await StopInternalAsync(restoreDns: true).ConfigureAwait(false);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartProviderModeAsync()
    {
        // Already inside the gate lock
        try
        {
            var provider = _listService.Catalog.FindProvider(_settings.WebFilter.ProviderId);
            if (provider == null || (provider.Ipv4.Count == 0 && provider.Ipv6.Count == 0))
            {
                LastError = "The selected DNS provider has no IP addresses configured.";
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            // Apply provider DNS to all eligible adapters
            bool dnsApplied = _configurator.ApplyProviderDns(_settings.WebFilter, provider.Ipv4, provider.Ipv6);
            if (!dnsApplied)
            {
                Debug.WriteLine("[WebFilter] Warning: Provider DNS could not be applied to all adapters.");
            }

            // Apply bypass protection (same as local mode — provider IPs excluded from DoH block list)
            await ApplyBypassProtectionInternalAsync().ConfigureAwait(false);

            _isProviderModeActive = true;
            _saveSettings(_settings);

            // Start the verifier to watch for DNS drift
            _verifier.Start();

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"[WebFilter] StartProviderModeAsync failed: {ex}");
            _configurator.RestoreOriginalDns(_settings.WebFilter);
            _isProviderModeActive = false;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    /// <summary>Stops the filter server and restores the PC's original DNS settings.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync(restoreDns: true).ConfigureAwait(false);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync(bool restoreDns)
    {
        StopWatchdog();
        _verifier.Stop();

        if (restoreDns)
        {
            try
            {
                _configurator.RestoreOriginalDns(_settings.WebFilter);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebFilter] RestoreOriginalDns error on stop: {ex.Message}");
            }

            try
            {
                await RemoveBypassProtectionInternalAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebFilter] RemoveBypassProtection error on stop: {ex.Message}");
            }
        }

        if (_server != null)
        {
            try
            {
                await _server.StopAsync().ConfigureAwait(false);
                await _server.DisposeAsync().ConfigureAwait(false);
            }
            catch { }
            _server = null;
        }

        // Restore any services that were stopped to free port 53
        try
        {
            _conflictResolver?.RestoreServices();
            _conflictResolver?.Dispose();
            _conflictResolver = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] ConflictResolver restore error: {ex.Message}");
        }

        _isProviderModeActive = false;
        _forwarder?.Dispose();
        _forwarder = null;
    }

    /// <summary>Emergency restore button: disables filter, restores original DNS and saves settings.</summary>
    public void RestoreNetworkSettings()
    {
        try
        {
            _settings.WebFilter.Enabled = false;
            _configurator.RestoreOriginalDns(_settings.WebFilter);

            try
            {
                _firewallService.RemoveRulesAsync(_settings.WebFilter).GetAwaiter().GetResult();
                _browserPolicyService.RemoveBrowserPolicies(_settings.WebFilter);
            }
            catch { }

            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await StopInternalAsync(restoreDns: true).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }
            });
            _saveSettings(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] RestoreNetworkSettings failed: {ex.Message}");
        }
    }

    /// <summary>Normal application exit handler: restores DNS and displays toast.</summary>
    public void OnAppExit()
    {
        try
        {
            StopWatchdog();
            _verifier.Stop();

            if (_settings.WebFilter.Enabled)
            {
                _configurator.RestoreOriginalDns(_settings.WebFilter);

                try
                {
                    _firewallService.RemoveRulesAsync(_settings.WebFilter).GetAwaiter().GetResult();
                    _browserPolicyService.RemoveBrowserPolicies(_settings.WebFilter);
                }
                catch { }
            }

            if (_server != null)
            {
                _ = _server.StopAsync();
            }

            // Restore services stopped to free port 53
            try
            {
                _conflictResolver?.RestoreServices();
                _conflictResolver?.Dispose();
                _conflictResolver = null;
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] OnAppExit error: {ex.Message}");
        }
    }

    // ── Bypass Protection Helpers ─────────────────────────────────────────────

    public async Task UpdateBypassProtectionAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                await ApplyBypassProtectionInternalAsync().ConfigureAwait(false);
                _saveSettings(_settings);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyBypassProtectionInternalAsync()
    {
        try
        {
            var allowedUpstreamIps = GetAllowedUpstreamIps();
            await _firewallService.ApplyRulesAsync(_settings.WebFilter, allowedUpstreamIps).ConfigureAwait(false);

            if (_settings.WebFilter.Bypass.BrowserPolicies)
            {
                _browserPolicyService.ApplyBrowserPolicies(_settings.WebFilter);
            }
            else
            {
                _browserPolicyService.RemoveBrowserPolicies(_settings.WebFilter);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] ApplyBypassProtectionInternalAsync failed: {ex.Message}");
        }
    }

    private async Task RemoveBypassProtectionInternalAsync()
    {
        try
        {
            await _firewallService.RemoveRulesAsync(_settings.WebFilter).ConfigureAwait(false);
            _browserPolicyService.RemoveBrowserPolicies(_settings.WebFilter);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] RemoveBypassProtectionInternalAsync failed: {ex.Message}");
        }
    }

    private List<string> GetAllowedUpstreamIps()
    {
        var ips = new List<string>();

        // Include original DNS IPs from SavedDnsConfig
        foreach (var adapter in _settings.WebFilter.OriginalDns.Adapters)
        {
            ips.AddRange(adapter.Ipv4Servers);
            ips.AddRange(adapter.Ipv6Servers);
        }

        // In Provider mode: the provider's own IPs must not be blocked
        if (_settings.WebFilter.Mode == WebFilterMode.Provider)
        {
            var provider = _listService.Catalog.FindProvider(_settings.WebFilter.ProviderId);
            if (provider != null)
            {
                ips.AddRange(provider.Ipv4);
                ips.AddRange(provider.Ipv6);
            }
        }
        else
        {
            // Include upstream IPs if known or custom
            if (_settings.WebFilter.Upstream == UpstreamKind.CloudflareDoh)
            {
                ips.Add("1.1.1.1");
                ips.Add("1.0.0.1");
            }
            else if (_settings.WebFilter.Upstream == UpstreamKind.GoogleDoh)
            {
                ips.Add("8.8.8.8");
                ips.Add("8.8.4.4");
            }
            else if (_settings.WebFilter.Upstream == UpstreamKind.Quad9Doh)
            {
                ips.Add("9.9.9.9");
                ips.Add("149.112.112.112");
            }
            else if (_settings.WebFilter.Upstream == UpstreamKind.Custom &&
                     !string.IsNullOrWhiteSpace(_settings.WebFilter.CustomUpstream))
            {
                if (IPAddress.TryParse(_settings.WebFilter.CustomUpstream.Trim(), out var customIp))
                {
                    ips.Add(customIp.ToString());
                }
            }
        }

        return ips.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Returns the first expected DNS address for the DnsVerifier.
    /// In Local mode: the loopback address the server is bound to.
    /// In Provider mode: the provider's first IPv4 address.
    /// </summary>
    private string? GetExpectedDnsAddress()
    {
        if (_settings.WebFilter.Mode == WebFilterMode.Provider)
        {
            var provider = _listService.Catalog.FindProvider(_settings.WebFilter.ProviderId);
            return provider?.Ipv4.Count > 0 ? provider.Ipv4[0] : null;
        }
        return BoundAddress;
    }

    /// <summary>
    /// Returns all provider IP addresses for the DnsVerifier (used in Provider mode).
    /// </summary>
    private IReadOnlyList<string>? GetProviderIps()
    {
        if (_settings.WebFilter.Mode != WebFilterMode.Provider) return null;
        var provider = _listService.Catalog.FindProvider(_settings.WebFilter.ProviderId);
        if (provider == null) return null;
        var all = new List<string>(provider.Ipv4);
        all.AddRange(provider.Ipv6);
        return all;
    }

    /// <summary>ProcessExit safety net: ensures DNS is never left dead during abnormal CLR shutdown.</summary>
    public void OnUnexpectedExit()
    {
        try
        {
            if (_settings.WebFilter.Enabled)
            {
                _configurator.RestoreOriginalDns(_settings.WebFilter);
            }
        }
        catch { }
    }

    private void StartWatchdog()
    {
        _watchdogFailures = 0;
        _watchdogTimer = new Timer(WatchdogCheck, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private void StopWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    private async void WatchdogCheck(object? state)
    {
        if (!IsRunning || BoundAddress == null || !BoundPort.HasValue) return;

        bool healthy = await TestListenerHealthAsync(BoundAddress, BoundPort.Value).ConfigureAwait(false);
        if (healthy)
        {
            _watchdogFailures = 0;
            return;
        }

        _watchdogFailures++;
        Debug.WriteLine($"[WebFilter] Watchdog: health check failed ({_watchdogFailures}/3).");

        if (_watchdogFailures == 1)
        {
            // Attempt listener restart
            Debug.WriteLine("[WebFilter] Watchdog: attempting listener restart...");
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync();
                try
                {
                    if (_server != null)
                    {
                        await _server.StopAsync();
                        await _server.StartAsync();
                    }
                }
                finally
                {
                    _gate.Release();
                }
            });
        }
        else if (_watchdogFailures >= 5)
        {
            // 5 consecutive failures (100s): emergency restore to keep internet working!
            Debug.WriteLine("[WebFilter] Watchdog: 5 consecutive failures. Performing emergency DNS restore!");
            StopWatchdog();
            _configurator.RestoreOriginalDns(_settings.WebFilter);
            _settings.WebFilter.Enabled = false;
            _saveSettings(_settings);

            LastError = "Filter stopped responding. Original network settings were restored to maintain internet access.";
            ListenerFailed?.Invoke(this, LastError);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<bool> TestListenerHealthAsync(string address, int port)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 5000;
            udp.Client.SendTimeout = 2000;

            var target = new IPEndPoint(IPAddress.Parse(address), port);
            byte[] query = DnsPacket.BuildQuery("localhost", DnsPacket.TypeA, 0x1234);

            await udp.SendAsync(query, query.Length, target).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var receiveTask = udp.ReceiveAsync(cts.Token).AsTask();

            var result = await receiveTask.ConfigureAwait(false);
            return result.Buffer != null && result.Buffer.Length >= DnsPacket.HeaderLength;
        }
        catch
        {
            return false;
        }
    }

    private UpstreamForwarder CreateForwarder()
    {
        var dohList = new List<DohEndpoint>();
        var plainList = new List<IPEndPoint>();

        switch (_settings.WebFilter.Upstream)
        {
            case UpstreamKind.CloudflareDoh:
                dohList.Add(UpstreamForwarder.Cloudflare);
                dohList.Add(UpstreamForwarder.Quad9);
                break;
            case UpstreamKind.GoogleDoh:
                dohList.Add(UpstreamForwarder.Google);
                dohList.Add(UpstreamForwarder.Cloudflare);
                break;
            case UpstreamKind.Quad9Doh:
                dohList.Add(UpstreamForwarder.Quad9);
                dohList.Add(UpstreamForwarder.Cloudflare);
                break;
            case UpstreamKind.OriginalDns:
                foreach (var adapter in _settings.WebFilter.OriginalDns?.Adapters ?? new List<SavedAdapterDns>())
                {
                    foreach (var s in adapter.Ipv4Servers)
                    {
                        if (IPAddress.TryParse(s, out var ip))
                        {
                            plainList.Add(new IPEndPoint(ip, 53));
                        }
                    }
                }
                // Fallback to Cloudflare if no original servers recorded
                dohList.Add(UpstreamForwarder.Cloudflare);
                break;
            case UpstreamKind.Custom:
                if (Uri.TryCreate(_settings.WebFilter.CustomUpstream, UriKind.Absolute, out var uri))
                {
                    dohList.Add(new DohEndpoint("Custom", uri, null));
                }
                else if (IPAddress.TryParse(_settings.WebFilter.CustomUpstream, out var customIp))
                {
                    plainList.Add(new IPEndPoint(customIp, 53));
                }
                dohList.Add(UpstreamForwarder.Cloudflare);
                break;
        }

        string? machineSuffix = null;
        try
        {
            machineSuffix = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        }
        catch { }

        var options = new DnsUpstreamOptions
        {
            DohEndpoints = dohList,
            PlainServers = plainList,
            MachineDnsSuffix = machineSuffix,
            Timeout = TimeSpan.FromSeconds(3)
        };

        return new UpstreamForwarder(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_server != null)
        {
            _server.QueryBlocked -= OnServerQueryBlocked;
            _server.QueryProcessed -= OnServerQueryProcessed;
        }
        await StopInternalAsync(restoreDns: true).ConfigureAwait(false);
        _verifier.Dispose();
        _gate.Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_server != null)
        {
            _server.QueryBlocked -= OnServerQueryBlocked;
            _server.QueryProcessed -= OnServerQueryProcessed;
        }
        OnAppExit();
        _verifier.Dispose();
        _gate.Dispose();
    }

    private void OnServerQueryBlocked(object? sender, BlockedQueryEventArgs e)
    {
        // Log blocked request
        if (_logService != null && _settings.WebFilter.LogBlocked)
        {
            _ = _logService.LogQueryAsync(e.Domain, true, e.Reason, e.TimestampUtc);
        }

        // Throttled toast notification (once per domain per 5 minutes)
        if (_settings.WebFilter.NotifyOnBlock)
        {
            var now = DateTime.UtcNow;
            bool shouldNotify = false;
            _notificationThrottle.AddOrUpdate(
                e.Domain,
                _ => { shouldNotify = true; return now; },
                (_, last) =>
                {
                    if ((now - last).TotalMinutes >= 5)
                    {
                        shouldNotify = true;
                        return now;
                    }
                    return last;
                });

            if (shouldNotify)
            {
                ServiceContainer.NotificationService.ShowWarning(
                    "Web Filter",
                    $"Blocked: {e.Domain} ({e.Reason})");
            }
        }

        QueryBlocked?.Invoke(this, e);
    }

    private void OnServerQueryProcessed(object? sender, QueryEventArgs e)
    {
        // Log allowed requests if enabled
        if (!e.Blocked && _logService != null && _settings.WebFilter.LogAllowed)
        {
            _ = _logService.LogQueryAsync(e.Domain, false, null, e.TimestampUtc);
        }
    }
}
