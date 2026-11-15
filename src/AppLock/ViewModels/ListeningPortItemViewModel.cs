using System.Net;
using AppLock.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

public partial class ListeningPortItemViewModel : ObservableObject
{
    public ListeningPort Model { get; private set; }

    public string Key => $"{Protocol}:{LocalAddress}:{Port}:{Pid}";

    public string Protocol => Model.Protocol;
    public int Port => Model.Port;
    public IPAddress LocalAddress => Model.LocalAddress;
    public int Pid => Model.Pid;
    public string AppName => !string.IsNullOrEmpty(Model.AppName) ? Model.AppName : $"PID {Pid}";
    public string? AppPath => Model.AppPath;
    public string? Publisher => Model.Publisher;
    public IReadOnlyList<string> Services => Model.Services;
    public string? ServiceLabel => Model.ServiceLabel;
    public string? ServiceName => Model.ServiceName;
    public PortHint? Hint => Model.Hint;
    public PortScope Scope => Model.Scope;
    public FirewallExposure Exposure => Model.Exposure;
    public string? MatchingRuleName => Model.MatchingRuleName;
    public string ProcessUser => Model.ProcessUser ?? "Unknown";
    public DateTime? ProcessStartTime => Model.ProcessStartTime;
    public string ProcessStartTimeText => Model.ProcessStartTime.HasValue ? Model.ProcessStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown";
    public string ActiveProfileName => Model.ActiveProfileName ?? "Standard";
    public bool HasServices => Services != null && Services.Count > 0;
    public bool HasAppPath => !string.IsNullOrEmpty(AppPath);

    public string DisplayAppName
    {
        get
        {
            if (!string.IsNullOrEmpty(ServiceLabel))
                return $"svchost · {ServiceLabel}";
            return AppName;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            if (!string.IsNullOrEmpty(Publisher))
                return $"PID {Pid}  ·  {Publisher}";
            return $"PID {Pid}";
        }
    }

    public string PortAndServiceText => !string.IsNullOrEmpty(ServiceName)
        ? $"Port {Port} · {ServiceName}"
        : $"Port {Port}";

    public bool HasHint => Hint != null;
    public string HintText => Hint?.Text ?? string.Empty;
    public HintSeverity HintSeverity => Hint?.Severity ?? HintSeverity.None;

    public string HintIcon => HintSeverity switch
    {
        HintSeverity.Important => "\uE7BA", // Warning / Alert
        HintSeverity.Review    => "\uE897", // Search / Review
        _                      => "\uE946"  // Info
    };

    public string DefaultProcessGlyph
    {
        get
        {
            if (Services != null && Services.Count > 0 || (AppName != null && AppName.Contains("svchost", StringComparison.OrdinalIgnoreCase)))
                return "\uE71D"; // Settings / Gear / Service
            if (AppName == "System" || Pid == 4)
                return "\uE770"; // Computer / Server
            return "\uE74C"; // Application
        }
    }

    public void UpdateFrom(ListeningPort fresh)
    {
        bool scopeChanged = fresh.Scope != Model.Scope;
        bool exposureChanged = fresh.Exposure != Model.Exposure;
        bool appNameChanged = fresh.AppName != Model.AppName;
        bool appPathChanged = fresh.AppPath != Model.AppPath;
        bool publisherChanged = fresh.Publisher != Model.Publisher;
        bool serviceChanged = fresh.ServiceLabel != Model.ServiceLabel;
        bool hintChanged = fresh.Hint != Model.Hint;
        bool serviceNameChanged = fresh.ServiceName != Model.ServiceName;
        bool ruleChanged = fresh.MatchingRuleName != Model.MatchingRuleName;

        Model = fresh;

        if (scopeChanged)
        {
            OnPropertyChanged(nameof(Scope));
            OnPropertyChanged(nameof(ScopeText));
            OnPropertyChanged(nameof(ScopeTooltip));
            OnPropertyChanged(nameof(ScopeIcon));
            OnPropertyChanged(nameof(IsReachable));
        }

        if (exposureChanged || ruleChanged)
        {
            OnPropertyChanged(nameof(Exposure));
            OnPropertyChanged(nameof(ExposureText));
            OnPropertyChanged(nameof(ExposureTooltip));
            OnPropertyChanged(nameof(MatchingRuleName));
            OnPropertyChanged(nameof(ActiveProfileName));
        }

        if (appNameChanged || serviceChanged)
        {
            OnPropertyChanged(nameof(AppName));
            OnPropertyChanged(nameof(ServiceLabel));
            OnPropertyChanged(nameof(DisplayAppName));
            OnPropertyChanged(nameof(DefaultProcessGlyph));
        }

        if (appPathChanged || publisherChanged)
        {
            OnPropertyChanged(nameof(AppPath));
            OnPropertyChanged(nameof(Publisher));
            OnPropertyChanged(nameof(DisplaySubtitle));
        }

        if (serviceNameChanged)
        {
            OnPropertyChanged(nameof(ServiceName));
            OnPropertyChanged(nameof(PortAndServiceText));
        }

        if (hintChanged)
        {
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(HasHint));
            OnPropertyChanged(nameof(HintText));
            OnPropertyChanged(nameof(HintSeverity));
            OnPropertyChanged(nameof(HintIcon));
        }
    }

    public string AddressAndPort => LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{LocalAddress}]:{Port}"
        : $"{LocalAddress}:{Port}";

    public string ScopeText => Scope switch
    {
        PortScope.LocalhostOnly => "Localhost only",
        PortScope.LocalNetwork  => "Local network",
        PortScope.AllInterfaces => "All interfaces",
        _                       => "Unknown"
    };

    public string ScopeTooltip => Scope switch
    {
        PortScope.LocalhostOnly => "Localhost only (127.0.0.1 / ::1):\nOnly programs running directly on this computer can connect. External devices on your Wi-Fi or network cannot reach this port.",
        PortScope.LocalNetwork  => "Local network:\nAccessible to other devices connected to your local Wi-Fi or LAN, but not exposed to the broader internet.",
        PortScope.AllInterfaces => "All interfaces (0.0.0.0 / ::):\nOpen to incoming network connections. Other devices on your Wi-Fi, local network, and potentially the internet can attempt to reach this port.",
        _                       => "Network reachability scope is unknown."
    };

    public string LocalAddressText => LocalAddress.ToString();
    public string PidText => $"PID {Pid}";

    public string ScopeIcon => Scope switch
    {
        PortScope.LocalhostOnly => "\uE718", // Lock / Shield
        PortScope.LocalNetwork  => "\uE839", // Group / Network
        PortScope.AllInterfaces => "\uE789", // Warning / Alert
        _                       => "\uE9CE"
    };

    public string ExposureText => Exposure switch
    {
        FirewallExposure.LocalhostUnfiltered => "Localhost (Unfiltered)",
        FirewallExposure.FirewallOff         => "Firewall off",
        FirewallExposure.BlockedByAppLock    => "Blocked by AppLock",
        FirewallExposure.AllowedByRule       => "Allowed by rule",
        FirewallExposure.BlockedByDefault    => "Blocked by default",
        _                                    => "Unknown"
    };

    public string ExposureTooltip
    {
        get
        {
            if (Exposure == FirewallExposure.LocalhostUnfiltered)
                return "Loopback traffic is not filtered by Windows Firewall. Only apps running on this PC can access it.";
            if (Exposure == FirewallExposure.FirewallOff)
                return "Windows Firewall is turned off for this network profile. Anyone on your network can connect.";
            if (Exposure == FirewallExposure.BlockedByAppLock)
                return !string.IsNullOrEmpty(MatchingRuleName)
                    ? $"Blocked by AppLock rule:\n{MatchingRuleName}"
                    : "Incoming connections are blocked by an AppLock firewall rule.";
            if (Exposure == FirewallExposure.AllowedByRule)
                return !string.IsNullOrEmpty(MatchingRuleName)
                    ? $"Allowed by Windows rule:\n\"{MatchingRuleName}\"\n\nEstimate based on your Windows Firewall rules."
                    : "An active Windows Firewall inbound rule explicitly permits network traffic to this port.";
            if (Exposure == FirewallExposure.BlockedByDefault)
                return "Windows Firewall blocks incoming traffic to this port by default because no allow rule permits it.";
            return "AppLock could not verify the firewall rule status for this port (third-party firewall or restricted permissions).";
        }
    }

    public bool IsReachable => Scope != PortScope.LocalhostOnly;

    public DateTime DiscoveredAt { get; }
    public bool IsNew => (DateTime.UtcNow - DiscoveredAt).TotalSeconds < 60;

    public bool IsIPv4 => LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    public bool IsIPv6 => LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    public bool IsBlocked => Exposure == FirewallExposure.BlockedByAppLock || Exposure == FirewallExposure.BlockedByDefault;

    public bool IsSystemOrService =>
        (Services != null && Services.Count > 0) ||
        (!string.IsNullOrEmpty(AppName) && (
            AppName.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            AppName.IndexOf("svchost", StringComparison.OrdinalIgnoreCase) >= 0 ||
            AppName.IndexOf("services.exe", StringComparison.OrdinalIgnoreCase) >= 0 ||
            AppName.IndexOf("lsass.exe", StringComparison.OrdinalIgnoreCase) >= 0 ||
            AppName.IndexOf("wininit.exe", StringComparison.OrdinalIgnoreCase) >= 0)) ||
        Pid == 4;

    public void RefreshIsNew()
    {
        OnPropertyChanged(nameof(IsNew));
    }

    public ListeningPortItemViewModel(ListeningPort model, bool isNewlyDiscovered = false, DateTime? discoveredAt = null)
    {
        Model = model;
        DiscoveredAt = discoveredAt ?? (isNewlyDiscovered ? DateTime.UtcNow : DateTime.MinValue);
    }
}
