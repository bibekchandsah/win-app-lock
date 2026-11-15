using System.Net;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.Firewall;
using Xunit;

namespace AppLock.Tests;

public class ListeningPortTests
{
    [Theory]
    [InlineData("0.0.0.0", PortScope.AllInterfaces)]
    [InlineData("::", PortScope.AllInterfaces)]
    [InlineData("127.0.0.1", PortScope.LocalhostOnly)]
    [InlineData("127.0.0.2", PortScope.LocalhostOnly)]
    [InlineData("::1", PortScope.LocalhostOnly)]
    [InlineData("192.168.1.100", PortScope.LocalNetwork)]
    [InlineData("10.0.0.5", PortScope.LocalNetwork)]
    [InlineData("172.16.0.1", PortScope.LocalNetwork)]
    public void ClassifyScope_CorrectlyIdentifiesScope(string ipStr, PortScope expected)
    {
        var ip = IPAddress.Parse(ipStr);
        var actual = ListeningPortService.ClassifyScope(ip);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task GetListeningPortsAsync_ReturnsActiveSocketsOnThisMachine()
    {
        var service = new ListeningPortService();
        var ports = await service.GetListeningPortsAsync();

        Assert.NotNull(ports);
        Assert.NotEmpty(ports);

        // Windows always has system listening sockets (e.g. RPC port 135)
        Assert.Contains(ports, p => p.Protocol == "TCP");
        Assert.Contains(ports, p => p.Protocol == "UDP");

        foreach (var p in ports)
        {
            Assert.InRange(p.Port, 1, 65535);
            Assert.True(p.Pid >= 0);
            Assert.NotNull(p.LocalAddress);
            Assert.False(string.IsNullOrEmpty(p.AppName));
        }
    }

    [Theory]
    [InlineData("TCP", 53, PortScope.AllInterfaces, "DNS", HintSeverity.Info)]
    [InlineData("TCP", 3389, PortScope.AllInterfaces, "Remote Desktop", HintSeverity.Important)]
    [InlineData("TCP", 8000, PortScope.AllInterfaces, "Dev Server (HTTP/Python)", HintSeverity.Review)]
    [InlineData("TCP", 8000, PortScope.LocalhostOnly, "Dev Server (HTTP/Python)", HintSeverity.Info)]
    [InlineData("TCP", 50000, PortScope.AllInterfaces, "Windows dynamic port", HintSeverity.Info)]
    public void PortClassifier_ClassifiesKnownPortsAndSeverities(
        string proto, int port, PortScope scope, string expectedService, HintSeverity expectedSeverity)
    {
        var result = PortClassifier.Classify(proto, port, scope);
        Assert.NotNull(result);
        Assert.Equal(expectedService, result.ServiceName);
        Assert.NotNull(result.Hint);
        Assert.Equal(expectedSeverity, result.Hint.Severity);
    }

    [Fact]
    public void ListeningPortsViewModel_FilteringAndToggles_WorkCorrectly()
    {
        var fakePorts = new List<ListeningPort>
        {
            new("TCP", IPAddress.Parse("0.0.0.0"), 80, 100, @"C:\web\nginx.exe", "nginx", "Nginx", Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null),
            new("TCP", IPAddress.Parse("127.0.0.1"), 3000, 200, @"C:\dev\node.exe", "node", null, Array.Empty<string>(), PortScope.LocalhostOnly, FirewallExposure.LocalhostUnfiltered, null, null),
            new("UDP", IPAddress.Parse("0.0.0.0"), 5353, 300, @"C:\Windows\System32\svchost.exe", "svchost", "Microsoft Corporation", new[] { "Dnscache" }, PortScope.AllInterfaces, FirewallExposure.AllowedByRule, "Dnscache", null),
            new("TCP", IPAddress.Parse("::1"), 8080, 400, @"C:\dev\python.exe", "python", null, Array.Empty<string>(), PortScope.LocalhostOnly, FirewallExposure.BlockedByAppLock, null, null),
            new("TCP", IPAddress.Parse("192.168.1.50"), 445, 4, @"System", "System", "Microsoft Corporation", Array.Empty<string>(), PortScope.LocalNetwork, FirewallExposure.BlockedByDefault, null, null)
        };

        var fakeService = new FakeListeningPortService(fakePorts);
        var vm = new AppLock.ViewModels.ListeningPortsViewModel(fakeService);
        vm.ApplySnapshot(fakePorts);

        Assert.Equal(5, vm.TotalListeningCount);
        Assert.Equal(3, vm.ReachableCount);
        Assert.Equal(2, vm.LocalhostCount);
        Assert.Equal(1, vm.BlockedCount);

        // Filter: Reachable
        vm.SelectedFilter = "Reachable";
        Assert.Equal(3, vm.Ports.Count);
        Assert.All(vm.Ports, p => Assert.True(p.IsReachable));

        // Filter: Localhost
        vm.SelectedFilter = "Localhost";
        Assert.Equal(2, vm.Ports.Count);
        Assert.All(vm.Ports, p => Assert.Equal(PortScope.LocalhostOnly, p.Scope));

        // Filter: Blocked
        vm.SelectedFilter = "Blocked";
        Assert.Equal(2, vm.Ports.Count); // 8080 (BlockedByAppLock) + 445 (BlockedByDefault)

        // Filter: UDP
        vm.SelectedFilter = "UDP";
        Assert.Single(vm.Ports);
        Assert.Equal(5353, vm.Ports[0].Port);

        // Filter: IPv6
        vm.SelectedFilter = "IPv6";
        Assert.Single(vm.Ports);
        Assert.Equal(8080, vm.Ports[0].Port);

        // Reset Filter to All, test HideUdp toggle
        vm.SelectedFilter = "All";
        vm.HideUdp = true;
        Assert.Equal(4, vm.Ports.Count);
        Assert.DoesNotContain(vm.Ports, p => p.Protocol == "UDP");
        vm.HideUdp = false;

        // Test ShowSystemServices toggle (should hide svchost and System)
        vm.ShowSystemServices = false;
        Assert.Equal(3, vm.Ports.Count);
        Assert.DoesNotContain(vm.Ports, p => p.AppName == "svchost" || p.AppName == "System");
        vm.ShowSystemServices = true;

        // Test Search Query
        vm.SearchQuery = "nginx";
        Assert.Single(vm.Ports);
        Assert.Equal("nginx", vm.Ports[0].AppName);

        vm.SearchQuery = string.Empty;
    }

    [Fact]
    public void ListeningPortsViewModel_Sorting_SortsStably()
    {
        var fakePorts = new List<ListeningPort>
        {
            new("TCP", IPAddress.Parse("127.0.0.1"), 8080, 200, @"C:\b.exe", "ZebraApp", null, Array.Empty<string>(), PortScope.LocalhostOnly, FirewallExposure.LocalhostUnfiltered, null, null),
            new("TCP", IPAddress.Parse("0.0.0.0"), 80, 500, @"C:\a.exe", "AlphaApp", null, Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null),
            new("TCP", IPAddress.Parse("192.168.1.1"), 443, 100, @"C:\c.exe", "BetaApp", null, Array.Empty<string>(), PortScope.LocalNetwork, FirewallExposure.AllowedByRule, null, null)
        };

        var fakeService = new FakeListeningPortService(fakePorts);
        var vm = new AppLock.ViewModels.ListeningPortsViewModel(fakeService);
        vm.ApplySnapshot(fakePorts);

        // Sort by Port (default)
        vm.SortBy = AppLock.ViewModels.ListeningPortSortOrder.Port;
        Assert.Equal(80, vm.Ports[0].Port);
        Assert.Equal(443, vm.Ports[1].Port);
        Assert.Equal(8080, vm.Ports[2].Port);

        // Sort by App
        vm.SortBy = AppLock.ViewModels.ListeningPortSortOrder.App;
        Assert.Equal("AlphaApp", vm.Ports[0].AppName);
        Assert.Equal("BetaApp", vm.Ports[1].AppName);
        Assert.Equal("ZebraApp", vm.Ports[2].AppName);

        // Sort by Scope (AllInterfaces first, then LocalNetwork, then LocalhostOnly)
        vm.SortBy = AppLock.ViewModels.ListeningPortSortOrder.Scope;
        Assert.Equal(PortScope.AllInterfaces, vm.Ports[0].Scope);
        Assert.Equal(PortScope.LocalNetwork, vm.Ports[1].Scope);
        Assert.Equal(PortScope.LocalhostOnly, vm.Ports[2].Scope);

        // Sort by PID
        vm.SortBy = AppLock.ViewModels.ListeningPortSortOrder.Pid;
        Assert.Equal(100, vm.Ports[0].Pid);
        Assert.Equal(200, vm.Ports[1].Pid);
        Assert.Equal(500, vm.Ports[2].Pid);
    }

    [Fact]
    public void ListeningPortsViewModel_Grouping_PreservesGroupsAndItems()
    {
        var fakePorts = new List<ListeningPort>
        {
            new("TCP", IPAddress.Parse("0.0.0.0"), 80, 100, @"C:\web\nginx.exe", "nginx", "Nginx", Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null),
            new("TCP", IPAddress.Parse("0.0.0.0"), 443, 100, @"C:\web\nginx.exe", "nginx", "Nginx", Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null),
            new("TCP", IPAddress.Parse("127.0.0.1"), 3000, 200, @"C:\dev\node.exe", "node", null, Array.Empty<string>(), PortScope.LocalhostOnly, FirewallExposure.LocalhostUnfiltered, null, null)
        };

        var fakeService = new FakeListeningPortService(fakePorts);
        var vm = new AppLock.ViewModels.ListeningPortsViewModel(fakeService);
        vm.ApplySnapshot(fakePorts);

        Assert.Equal(2, vm.GroupedPorts.Count);

        var nginxGroup = vm.GroupedPorts.First(g => g.AppName == "nginx");
        Assert.Equal("2 ports", nginxGroup.PortCountText);
        Assert.Equal(2, nginxGroup.Ports.Count);
        Assert.True(nginxGroup.HasMultiplePorts);

        var nodeGroup = vm.GroupedPorts.First(g => g.AppName == "node");
        Assert.Equal("1 port", nodeGroup.PortCountText);
        Assert.Single(nodeGroup.Ports);
        Assert.False(nodeGroup.HasMultiplePorts);
    }

    [Fact]
    public void ListeningPortItemViewModel_NewBadge_BehavesCorrectly()
    {
        var port = new ListeningPort("TCP", IPAddress.Parse("127.0.0.1"), 8000, 1234, @"C:\test.exe", "test", null, Array.Empty<string>(), PortScope.LocalhostOnly, FirewallExposure.LocalhostUnfiltered, null, null);

        var initialItem = new AppLock.ViewModels.ListeningPortItemViewModel(port, isNewlyDiscovered: false);
        Assert.False(initialItem.IsNew);

        var newItem = new AppLock.ViewModels.ListeningPortItemViewModel(port, isNewlyDiscovered: true);
        Assert.True(newItem.IsNew);

        var oldItem = new AppLock.ViewModels.ListeningPortItemViewModel(port, discoveredAt: DateTime.UtcNow.AddSeconds(-65));
        Assert.False(oldItem.IsNew);
    }

    private class FakeListeningPortService : IListeningPortService
    {
        private readonly IReadOnlyList<ListeningPort> _ports;
        public FakeListeningPortService(IReadOnlyList<ListeningPort> ports) => _ports = ports;
        public Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult(_ports);
    }
}
