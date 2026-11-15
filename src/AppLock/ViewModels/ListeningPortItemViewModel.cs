using System.Net;
using AppLock.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

public partial class ListeningPortItemViewModel : ObservableObject
{
    public ListeningPort Model { get; }

    public string Protocol => Model.Protocol;
    public int Port => Model.Port;
    public IPAddress LocalAddress => Model.LocalAddress;
    public int Pid => Model.Pid;
    public string AppName => !string.IsNullOrEmpty(Model.AppName) ? Model.AppName : $"PID {Pid}";
    public string? AppPath => Model.AppPath;
    public PortScope Scope => Model.Scope;
    public FirewallExposure Exposure => Model.Exposure;

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
        FirewallExposure.FirewallOff     => "Firewall off",
        FirewallExposure.BlockedByAppLock=> "Blocked by AppLock",
        FirewallExposure.AllowedByRule   => "Allowed by rule",
        FirewallExposure.BlockedByDefault=> "Blocked by default",
        _                                => "Unknown"
    };

    public bool IsReachable => Scope != PortScope.LocalhostOnly;

    public ListeningPortItemViewModel(ListeningPort model)
    {
        Model = model;
    }
}
