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
}
