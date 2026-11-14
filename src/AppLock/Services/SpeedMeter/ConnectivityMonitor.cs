using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Windows.Networking.Connectivity;

namespace AppLock.Services.SpeedMeter;

/// <summary>One recorded outage.</summary>
public sealed class OutageRecord
{
    public DateTime Start { get; init; }
    public DateTime End { get; set; }
    public TimeSpan Duration => End - Start;
    public string AdapterName { get; init; } = string.Empty;
}

/// <summary>
/// Watches Windows' own connectivity state (INetworkListManager via the WinRT projection) and
/// only reports a change once it has been stable for the debounce period, so flapping links do not
/// produce alert storms. Uses no network requests of its own.
/// </summary>
public sealed class ConnectivityMonitor
{
    private const int MaxOutages = 50;

    private readonly DispatcherQueue _dispatcher;
    private readonly List<OutageRecord> _outages = new();

    private DispatcherQueueTimer? _timer;
    private DateTime _candidateSince = DateTime.MinValue;
    private bool _candidate;
    private bool _confirmed;
    private OutageRecord? _openOutage;

    public ConnectivityMonitor(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
    }

    /// <summary>Require full internet access instead of just a network link.</summary>
    public bool CheckRealInternet { get; set; } = true;

    public int DebounceSeconds { get; set; } = 3;

    /// <summary>True when the connection is up; false only after the debounce elapsed.</summary>
    public bool IsOnline { get; private set; } = true;

    /// <summary>Raised with <c>true</c> when the connection came back, <c>false</c> when it dropped.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    public IReadOnlyList<OutageRecord> Outages => _outages;

    public void Start()
    {
        if (_timer is not null) return;

        _confirmed = ReadOnline();
        IsOnline = _confirmed;
        _candidate = _confirmed;
        _candidateSince = DateTime.UtcNow;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Applies new settings without restarting the monitor.</summary>
    public void ApplySettings()
    {
        var alerts = ServiceContainer.Settings.SpeedMeter.Alerts;
        CheckRealInternet = alerts.CheckRealInternet;
        DebounceSeconds = Math.Clamp(alerts.DebounceSeconds, 1, 30);
    }

    private void Evaluate()
    {
        var online = ReadOnline();

        if (online != _candidate)
        {
            _candidate = online;
            _candidateSince = DateTime.UtcNow;
            return;
        }

        if (online == _confirmed) return;

        var stableFor = DateTime.UtcNow - _candidateSince;
        if (stableFor.TotalSeconds < Math.Max(0, DebounceSeconds)) return;

        _confirmed = online;
        IsOnline = online;

        if (online) CloseOutage();
        else OpenOutage();

        ConnectionChanged?.Invoke(this, online);
    }

    private void OpenOutage()
    {
        _openOutage = new OutageRecord
        {
            Start = DateTime.Now,
            End = DateTime.Now,
            AdapterName = ReadProfileName()
        };
    }

    private void CloseOutage()
    {
        if (_openOutage is null) return;

        _openOutage.End = DateTime.Now;
        _outages.Insert(0, _openOutage);
        _openOutage = null;

        while (_outages.Count > MaxOutages) _outages.RemoveAt(_outages.Count - 1);
    }

    public TimeSpan? CurrentOutageDuration =>
        _openOutage is null ? null : DateTime.Now - _openOutage.Start;

    private static string ReadProfileName()
    {
        try
        {
            return NetworkInformation.GetInternetConnectionProfile()?.ProfileName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private bool ReadOnline()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return false;

            var level = profile.GetNetworkConnectivityLevel();

            return CheckRealInternet
                ? level == NetworkConnectivityLevel.InternetAccess
                : level != NetworkConnectivityLevel.None;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Connectivity check failed: {ex.Message}");

            // Assume online when the API fails; a false alarm is worse than a missed one here.
            return true;
        }
    }

    /// <summary>Adapter/profile name for the toast subtitle.</summary>
    public string CurrentProfileName => ReadProfileName();

    /// <summary>Most recent outages, newest first.</summary>
    public IEnumerable<OutageRecord> RecentOutages => _outages.Take(10);
}
