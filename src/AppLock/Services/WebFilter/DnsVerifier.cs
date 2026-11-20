using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.WebFilter;

public sealed class DnsVerifier : IDisposable
{
    private readonly DnsConfigurator _configurator;
    private readonly Func<WebFilterSettings> _getSettings;
    private readonly Func<string?> _getExpectedAddress;
    private readonly Func<IReadOnlyList<string>?>? _getProviderIps;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private bool _running;
    private bool _verifying;
    private bool _disposed;

    public event EventHandler<IReadOnlyList<string>>? DnsDriftDetected;

    public DnsVerifier(
        DnsConfigurator configurator,
        Func<WebFilterSettings> getSettings,
        Func<string?> getExpectedAddress,
        Func<IReadOnlyList<string>?>? getProviderIps = null)
    {
        _configurator = configurator ?? throw new ArgumentNullException(nameof(configurator));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _getExpectedAddress = getExpectedAddress ?? throw new ArgumentNullException(nameof(getExpectedAddress));
        _getProviderIps = getProviderIps;

        _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running || _disposed) return;
            _running = true;

            try
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChange;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChange;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebFilter] Could not hook NetworkChange: {ex.Message}");
            }

            _timer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;

            try
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChange;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChange;
            }
            catch { }

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerTick(object? state)
    {
        VerifyNow();
    }

    private void OnNetworkChange(object? sender, EventArgs e)
    {
        // Debounce network changes slightly
        Task.Delay(500).ContinueWith(_ => VerifyNow());
    }

    private void OnNetworkAvailabilityChange(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            Task.Delay(500).ContinueWith(_ => VerifyNow());
        }
    }

    public void VerifyNow()
    {
        lock (_gate)
        {
            if (!_running || _disposed || _verifying) return;
            _verifying = true;
        }

        try
        {
            var settings = _getSettings();
            if (!settings.Enabled || settings.IsPaused) return;

            string? expectedAddress = _getExpectedAddress();
            if (string.IsNullOrWhiteSpace(expectedAddress)) return;

            var adapters = _configurator.AdapterService.GetEligibleAdapters();
            var drifted = new List<string>();

            if (settings.Mode == WebFilterMode.Provider)
            {
                // In Provider mode: verify that at least one of the provider's IPs is set
                var providerIps = _getProviderIps?.Invoke();
                if (providerIps == null || providerIps.Count == 0) return;

                foreach (var adapter in adapters)
                {
                    bool hasProviderIpv4 = adapter.Ipv4Servers.Any(s =>
                        providerIps.Any(p => string.Equals(s.Trim(), p.Trim(), StringComparison.OrdinalIgnoreCase)));

                    if (!hasProviderIpv4)
                    {
                        drifted.Add(adapter.Name);
                    }
                }

                if (drifted.Count > 0)
                {
                    Debug.WriteLine($"[WebFilter] Provider DNS drifted on {string.Join(", ", drifted)}. Re-applying...");
                    var ipv4 = providerIps.Where(ip => !ip.Contains(':')).ToList();
                    var ipv6 = providerIps.Where(ip => ip.Contains(':')).ToList();
                    _configurator.ApplyProviderDns(settings, ipv4, ipv6);
                    DnsDriftDetected?.Invoke(this, drifted);
                }
            }
            else
            {
                // Local filter mode: verify loopback address
                foreach (var adapter in adapters)
                {
                    bool hasExpectedIpv4 = adapter.Ipv4Servers.Any(s =>
                        string.Equals(s.Trim(), expectedAddress.Trim(), StringComparison.OrdinalIgnoreCase));

                    bool hasExpectedIpv6 = adapter.Ipv6Servers.Any(s =>
                        string.Equals(s.Trim(), "::1", StringComparison.OrdinalIgnoreCase));

                    if (!hasExpectedIpv4 || (!adapter.Ipv6FromDhcp && !hasExpectedIpv6 && adapter.Ipv6Servers.Count > 0))
                    {
                        drifted.Add(adapter.Name);
                    }
                }

                if (drifted.Count > 0)
                {
                    Debug.WriteLine($"[WebFilter] DNS settings drifted on {string.Join(", ", drifted)}. Re-applying...");
                    _configurator.ApplyFilterDns(settings, expectedAddress, "::1");
                    DnsDriftDetected?.Invoke(this, drifted);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilter] DnsVerifier check failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _verifying = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }

        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChange;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChange;
        }
        catch { }

        _timer.Dispose();
    }
}
