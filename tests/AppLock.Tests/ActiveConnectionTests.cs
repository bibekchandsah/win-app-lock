using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.ViewModels;
using Xunit;

namespace AppLock.Tests;

public class ActiveConnectionTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.5", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void IsLocalAddress_IdentifiesLoopback(string address, bool expected)
    {
        Assert.Equal(expected, ActiveConnection.IsLocalAddress(address));
    }

    [Theory]
    [InlineData("192.168.1.100", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.25.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    public void IsPrivateNetworkAddress_IdentifiesRfc1918AndLinkLocal(string address, bool expected)
    {
        Assert.Equal(expected, ActiveConnection.IsPrivateNetworkAddress(address));
    }

    [Fact]
    public void ActiveConnection_ClassifiesLoopbackSameAddress()
    {
        var conn = new ActiveConnection
        {
            LocalAddress = "192.168.1.50",
            RemoteAddress = "192.168.1.50",
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        Assert.True(conn.IsLocal);
        Assert.False(conn.IsInternet);
        Assert.Equal("Localhost", conn.ScopeText);
    }

    [Fact]
    public void ActiveConnection_ClassifiesPrivateNetwork()
    {
        var conn = new ActiveConnection
        {
            LocalAddress = "192.168.1.50",
            RemoteAddress = "192.168.1.1",
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        Assert.False(conn.IsLocal);
        Assert.True(conn.IsPrivateNetwork);
        Assert.False(conn.IsInternet);
        Assert.Equal("Local network", conn.ScopeText);
    }

    [Fact]
    public void ActiveConnection_ClassifiesPublicInternet()
    {
        var conn = new ActiveConnection
        {
            LocalAddress = "192.168.1.50",
            RemoteAddress = "142.250.190.46",
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        Assert.False(conn.IsLocal);
        Assert.False(conn.IsPrivateNetwork);
        Assert.True(conn.IsInternet);
        Assert.Equal("Internet", conn.ScopeText);
    }

    [Fact]
    public void GroupViewModel_GroupsCorrectly()
    {
        var conn1 = new ActiveConnection
        {
            ProcessId = 1234,
            ProcessName = "chrome.exe",
            ProcessPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            LocalAddress = "192.168.1.50",
            LocalPort = 50001,
            RemoteAddress = "142.250.190.46",
            RemotePort = 443,
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        var conn2 = new ActiveConnection
        {
            ProcessId = 1234,
            ProcessName = "chrome.exe",
            ProcessPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            LocalAddress = "192.168.1.50",
            LocalPort = 50002,
            RemoteAddress = "142.250.190.46",
            RemotePort = 443,
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        var group = new ActiveConnectionGroupViewModel("chrome.exe");
        group.UpdateGroup(new[] { conn1, conn2 });

        Assert.Equal("chrome.exe", group.ProcessName);
        Assert.Equal("PID 1234", group.PidText);
        Assert.Equal(2, group.Connections.Count);
        Assert.Equal("2 connections", group.ConnectionCountText);
        Assert.True(group.IsExpanded);

        // Toggle expand
        group.IsExpanded = false;
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void ActiveConnectionViewModel_FiltersLoopbackAndEstablished()
    {
        var vm = new ActiveConnectionViewModel();

        var localConn = new ActiveConnection
        {
            ProcessId = 100,
            ProcessName = "app.exe",
            LocalAddress = "127.0.0.1",
            LocalPort = 1234,
            RemoteAddress = "127.0.0.1",
            RemotePort = 5678,
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        var internetConn = new ActiveConnection
        {
            ProcessId = 200,
            ProcessName = "browser.exe",
            LocalAddress = "192.168.1.50",
            LocalPort = 50000,
            RemoteAddress = "8.8.8.8",
            RemotePort = 443,
            Protocol = "TCP",
            State = "ESTABLISHED"
        };

        var listeningConn = new ActiveConnection
        {
            ProcessId = 300,
            ProcessName = "server.exe",
            LocalAddress = "0.0.0.0",
            LocalPort = 8080,
            RemoteAddress = "0.0.0.0",
            RemotePort = 0,
            Protocol = "TCP",
            State = "LISTEN"
        };

        vm.Connections.Add(localConn);
        vm.Connections.Add(internetConn);
        vm.Connections.Add(listeningConn);
        vm.FilterConnections();

        // Default: HideLocalConnections = true, ShowOnlyEstablished = true, ShowInternet = true, ShowLocalNetwork = true
        Assert.True(vm.HideLocalConnections);
        Assert.True(vm.ShowOnlyEstablished);

        // Only internetConn matches
        Assert.Single(vm.FilteredConnections);
        Assert.Equal("browser.exe", vm.FilteredConnections[0].ProcessName);
        Assert.Equal(2, vm.LocalConnectionsHiddenCount);

        // Turn off HideLocalConnections: localConn should now be included
        vm.HideLocalConnections = false;
        Assert.Equal(2, vm.FilteredConnections.Count);
        Assert.Equal(2, vm.LocalConnectionsHiddenCount);

        // Turn off ShowOnlyEstablished: listeningConn should now also be included
        vm.ShowOnlyEstablished = false;
        Assert.Equal(3, vm.FilteredConnections.Count);

        // Filter Scope to Internet Only
        vm.SetScopeFilter("Internet");
        Assert.True(vm.IsScopeInternetOnly);
        // Both localConn and listeningConn are not internet
        Assert.Single(vm.FilteredConnections);
        Assert.Equal("browser.exe", vm.FilteredConnections[0].ProcessName);

        // Verify summary text
        Assert.Contains("apps with connections", vm.StatusText);
        Assert.Contains("total", vm.StatusText);
    }
}
