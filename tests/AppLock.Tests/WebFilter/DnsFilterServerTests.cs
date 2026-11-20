using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

/// <summary>
/// End-to-end tests of the listener on a non-privileged loopback port. Nothing here touches the
/// system DNS configuration: the server is asked directly with its own queries.
/// </summary>
public class DnsFilterServerTests
{
    private const string BlockedName = "blocked.example";
    private const string AllowedName = "allowed.example";

    [Fact]
    public async Task BlocksOneNameAndAnswersAnother()
    {
        await using var server = await StartServerAsync();
        Assert.NotNull(server.BoundEndPoint);

        var blocked = DnsPacket.BuildQuery(BlockedName, DnsPacket.TypeA, 0x1111);
        var blockedResponse = await QueryUdpAsync(server.BoundEndPoint!, blocked);

        Assert.NotNull(blockedResponse);
        Assert.Equal(0x1111, DnsPacket.ReadU16(blockedResponse!, 0));
        Assert.Equal(3, DnsPacket.ReadU16(blockedResponse!, 2) & 0x000F); // NXDOMAIN
        Assert.Equal(1L, server.Stats.BlockedQueries);

        var allowed = DnsPacket.BuildQuery(AllowedName, DnsPacket.TypeA, 0x2222);
        var allowedResponse = await QueryUdpAsync(server.BoundEndPoint!, allowed);

        Assert.NotNull(allowedResponse);
        Assert.Equal(0x2222, DnsPacket.ReadU16(allowedResponse!, 0));
        Assert.Equal(0, DnsPacket.ReadU16(allowedResponse!, 2) & 0x000F);
        Assert.Equal(1, DnsPacket.ReadU16(allowedResponse!, 6)); // one answer from the fake upstream
    }

    [Fact]
    public async Task AnswersOverTcpToo()
    {
        await using var server = await StartServerAsync();
        var endpoint = server.BoundEndPoint!;

        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Address, endpoint.Port);
        using var stream = client.GetStream();

        var packet = DnsPacket.BuildQuery(BlockedName, DnsPacket.TypeA, 0x3333);
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)packet.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(packet);

        await stream.ReadExactlyAsync(prefix);
        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        var response = new byte[length];
        await stream.ReadExactlyAsync(response);

        Assert.Equal(0x3333, DnsPacket.ReadU16(response, 0));
        Assert.Equal(3, DnsPacket.ReadU16(response, 2) & 0x000F);
    }

    [Fact]
    public async Task MalformedPacketsAreDroppedAndTheServerKeepsAnswering()
    {
        await using var server = await StartServerAsync();
        var endpoint = server.BoundEndPoint!;

        var garbage = new byte[] { 0xFF, 0x00, 0x01, 0x02, 0x03, 0x04 };
        Assert.Null(await QueryUdpAsync(endpoint, garbage, timeoutMs: 400));

        var query = DnsPacket.BuildQuery(BlockedName, DnsPacket.TypeA, 0x4444);
        var response = await QueryUdpAsync(endpoint, query);

        Assert.NotNull(response);
        Assert.Equal(0x4444, DnsPacket.ReadU16(response!, 0));
    }

    [Fact]
    public async Task CachesForwardedAnswers()
    {
        var upstream = new FakeUpstream();
        await using var server = await StartServerAsync(upstream);
        var endpoint = server.BoundEndPoint!;

        for (int i = 0; i < 3; i++)
        {
            var query = DnsPacket.BuildQuery(AllowedName, DnsPacket.TypeA, (ushort)(0x5000 + i));
            var response = await QueryUdpAsync(endpoint, query);
            Assert.NotNull(response);
            Assert.Equal((ushort)(0x5000 + i), DnsPacket.ReadU16(response!, 0));
        }

        Assert.Equal(1, upstream.Calls);
        Assert.Equal(2L, server.Stats.CachedAnswers);
    }

    [Fact]
    public async Task SelfTestSeesAWorkingListener()
    {
        await using var server = await StartServerAsync();

        Assert.True(await server.SelfTestAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SelfTestFailsWhenStopped()
    {
        await using var server = await StartServerAsync();
        await server.StopAsync();

        Assert.False(await server.SelfTestAsync(TimeSpan.FromMilliseconds(300)));
    }

    private static async Task<DnsFilterServer> StartServerAsync(IUpstreamResolver? upstream = null)
    {
        for (int port = 5300; port < 5320; port++)
        {
            var server = new DnsFilterServer(
                new DnsServerOptions { BindAddress = IPAddress.Loopback, Port = port, QueryTimeout = TimeSpan.FromSeconds(5) },
                upstream ?? new FakeUpstream(),
                new TestPolicy());

            if (await server.StartAsync()) return server;

            await server.DisposeAsync();
        }

        throw new InvalidOperationException("No free loopback port on 5300-5319");
    }

    private static async Task<byte[]?> QueryUdpAsync(IPEndPoint endpoint, byte[] packet, int timeoutMs = 2000)
    {
        using var client = new UdpClient(endpoint.AddressFamily);
        client.Client.Bind(new IPEndPoint(endpoint.Address, 0));
        await client.SendAsync(packet, packet.Length, endpoint);

        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            var received = await client.ReceiveAsync(cts.Token);
            return received.Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private sealed class TestPolicy : IDnsFilterPolicy
    {
        public FilterVerdict Check(in DnsQuery query)
            => query.Name == BlockedName ? FilterVerdict.Block("Test") : FilterVerdict.Allow;
    }

    private sealed class FakeUpstream : IUpstreamResolver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<byte[]?> ResolveAsync(DnsQuery query, byte[] request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<byte[]?>(DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress));
        }
    }
}
