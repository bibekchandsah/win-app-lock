using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.Firewall;
using Xunit;

namespace AppLock.Tests;

public class FakeListeningPortService : IListeningPortService
{
    public List<ListeningPort> Sockets { get; } = new();

    public Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ListeningPort>>(Sockets.ToList());
    }
}

public class PortWatchTests
{
    private readonly AppSettings _settings;

    public PortWatchTests()
    {
        _settings = new AppSettings();
    }

    private PortWatchService CreateService(FakeListeningPortService fake)
    {
        return new PortWatchService(fake)
        {
            SettingsProvider = () => _settings,
            SettingsSaver = s => { }
        };
    }

    [Theory]
    [InlineData(50000, "svchost.exe", true)]
    [InlineData(49152, "lsass.exe", true)]
    [InlineData(50000, "node.exe", false)]
    [InlineData(80, "svchost.exe", false)]
    [InlineData(8080, "chrome.exe", false)]
    public void IsDynamicSystemPort_CorrectlyIdentifiesWindowsDynamicPorts(int port, string app, bool expected)
    {
        bool actual = PortWatchService.IsDynamicSystemPort(port, null, app);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TakeBaselineAsync_FiltersLocalhostOnly()
    {
        var fake = new FakeListeningPortService();
        fake.Sockets.Add(new ListeningPort(
            "TCP", IPAddress.Loopback, 8080, 100, @"C:\app.exe", "app", null,
            Array.Empty<string>(), PortScope.LocalhostOnly, FirewallExposure.LocalhostUnfiltered, null, null));

        fake.Sockets.Add(new ListeningPort(
            "TCP", IPAddress.Any, 9000, 200, @"C:\server.exe", "server", null,
            Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null));

        var watch = CreateService(fake);
        var baseline = await watch.TakeBaselineAsync();

        Assert.Single(baseline);
        Assert.Equal(9000, baseline[0].Port);
    }

    [Fact]
    public void MarkBaselineAsKnown_PopulatesKnownPortsAndSuppressesAlerts()
    {
        var fake = new FakeListeningPortService();
        var watch = CreateService(fake);

        var ports = new[]
        {
            new ListeningPort(
                "TCP", IPAddress.Any, 9000, 200, @"C:\server.exe", "server", null,
                Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null)
        };

        watch.MarkBaselineAsKnown(ports);

        Assert.Contains(_settings.ListeningPorts.KnownPorts, k => k.Port == 9000 && k.Protocol == "TCP");
        Assert.True(watch.IsSuppressed);
    }

    [Fact]
    public async Task ScanAndEvaluate_IgnoresShortLivedPorts()
    {
        var fake = new FakeListeningPortService();
        var watch = CreateService(fake);
        _settings.ListeningPorts.WatchNewPorts = true;
        _settings.ListeningPorts.KnownPorts.Clear();

        // 1. Port appears
        var testPort = new ListeningPort(
            "TCP", IPAddress.Any, 9999, 1234, @"C:\temp_server.exe", "temp_server", null,
            Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null);

        fake.Sockets.Add(testPort);
        await watch.ScanAndEvaluateAsync();

        // Candidate created with FirstSeen = now
        Assert.True(watch.Candidates.ContainsKey("TCP|9999|C:\\temp_server.exe"));
        Assert.False(watch.Candidates["TCP|9999|C:\\temp_server.exe"].Alerted);

        // 2. Port closes immediately (< 10 seconds)
        fake.Sockets.Clear();
        await watch.ScanAndEvaluateAsync();

        // Candidate was removed without ever alerting!
        Assert.False(watch.Candidates.ContainsKey("TCP|9999|C:\\temp_server.exe"));
    }

    [Fact]
    public async Task ScanAndEvaluate_DispatchesAlertWhenPortPersists()
    {
        var fake = new FakeListeningPortService();
        var watch = CreateService(fake);
        _settings.ListeningPorts.WatchNewPorts = true;
        _settings.ListeningPorts.KnownPorts.Clear();

        var testPort = new ListeningPort(
            "TCP", IPAddress.Any, 8888, 5678, @"C:\web_server.exe", "web_server", null,
            Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null);

        fake.Sockets.Add(testPort);

        // First scan: port is registered
        await watch.ScanAndEvaluateAsync();
        Assert.True(watch.Candidates.ContainsKey("TCP|8888|C:\\web_server.exe"));

        // Simulate 12 seconds passing
        watch.Candidates["TCP|8888|C:\\web_server.exe"].FirstSeen = DateTime.UtcNow.AddSeconds(-12);

        // Second scan: port is still alive after > 10s
        await watch.ScanAndEvaluateAsync();
        Assert.True(watch.Candidates["TCP|8888|C:\\web_server.exe"].Alerted);
    }

    [Fact]
    public async Task ScanAndEvaluate_IgnoresBlockedOrKnownPorts()
    {
        var fake = new FakeListeningPortService();
        var watch = CreateService(fake);
        _settings.ListeningPorts.WatchNewPorts = true;

        // Known port
        _settings.ListeningPorts.KnownPorts = new List<KnownPort>
        {
            new KnownPort { Port = 8080, Protocol = "TCP", AppPath = @"C:\known.exe" }
        };

        fake.Sockets.Add(new ListeningPort(
            "TCP", IPAddress.Any, 8080, 100, @"C:\known.exe", "known", null,
            Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.AllowedByRule, null, null));

        // Blocked port
        fake.Sockets.Add(new ListeningPort(
            "TCP", IPAddress.Any, 4444, 200, @"C:\blocked.exe", "blocked", null,
            Array.Empty<string>(), PortScope.AllInterfaces, FirewallExposure.BlockedByAppLock, null, null));

        await watch.ScanAndEvaluateAsync();

        // Neither port should be tracked as candidates
        Assert.Empty(watch.Candidates);
    }
}
