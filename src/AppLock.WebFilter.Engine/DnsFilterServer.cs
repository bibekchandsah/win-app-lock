using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AppLock.WebFilter.Engine;

public sealed class DnsServerOptions
{
    /// <summary>Loopback address to bind. Windows can be pointed at any 127.x.x.x address.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    public int Port { get; init; } = 53;

    public int MaxConcurrentQueries { get; init; } = 256;

    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public BlockedAnswer BlockedAnswer { get; init; } = BlockedAnswer.NxDomain;

    public bool CacheEnabled { get; init; } = true;

    /// <summary>Largest UDP datagram accepted; anything bigger is ignored.</summary>
    public int MaxUdpReceive { get; init; } = 4096;
}

public sealed class DnsServerStats
{
    private long _totalQueries;
    private long _blockedQueries;
    private long _cachedAnswers;
    private long _failedQueries;

    public long TotalQueries => Interlocked.Read(ref _totalQueries);
    public long BlockedQueries => Interlocked.Read(ref _blockedQueries);
    public long CachedAnswers => Interlocked.Read(ref _cachedAnswers);
    public long FailedQueries => Interlocked.Read(ref _failedQueries);

    internal void RecordQuery() => Interlocked.Increment(ref _totalQueries);
    internal void RecordBlocked() => Interlocked.Increment(ref _blockedQueries);
    internal void RecordCached() => Interlocked.Increment(ref _cachedAnswers);
    internal void RecordFailed() => Interlocked.Increment(ref _failedQueries);
}

public sealed class BlockedQueryEventArgs : EventArgs
{
    public BlockedQueryEventArgs(string domain, string reason)
    {
        Domain = domain;
        Reason = reason;
        TimestampUtc = DateTime.UtcNow;
    }

    public string Domain { get; }
    public string Reason { get; }
    public DateTime TimestampUtc { get; }
}

public sealed class QueryEventArgs : EventArgs
{
    public QueryEventArgs(string domain, bool blocked, string? reason)
    {
        Domain = domain;
        Blocked = blocked;
        Reason = reason;
        TimestampUtc = DateTime.UtcNow;
    }

    public string Domain { get; }
    public bool Blocked { get; }
    public string? Reason { get; }
    public DateTime TimestampUtc { get; }
}

/// <summary>
/// The local DNS filter: listens on UDP and TCP, asks the policy, answers blocked names itself and
/// forwards everything else upstream. Has no UI dependencies.
/// </summary>
public sealed class DnsFilterServer : IAsyncDisposable
{
    private readonly DnsServerOptions _options;
    private readonly IUpstreamResolver _upstream;
    private readonly IDnsFilterPolicy _policy;
    private readonly DnsCache? _cache;
    private readonly SemaphoreSlim _inFlight;

    private Socket? _udp;
    private Socket? _tcp;
    private CancellationTokenSource? _cts;
    private Task? _udpLoop;
    private Task? _tcpLoop;
    private bool _disposed;

    public DnsFilterServer(DnsServerOptions options, IUpstreamResolver upstream, IDnsFilterPolicy policy, DnsCache? cache = null)
    {
        _options = options;
        _upstream = upstream;
        _policy = policy;
        _cache = options.CacheEnabled ? cache ?? new DnsCache() : null;
        _inFlight = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentQueries));
    }

    public DnsServerStats Stats { get; } = new();

    public IPEndPoint? BoundEndPoint { get; private set; }

    public string? LastError { get; private set; }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public event EventHandler<BlockedQueryEventArgs>? QueryBlocked;
    public event EventHandler<QueryEventArgs>? QueryProcessed;

    /// <summary>Binds UDP and TCP. Returns false (with <see cref="LastError"/>) when the address is in use.</summary>
    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.FromResult(true);

        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _udp = new Socket(_options.BindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _udp.Bind(new IPEndPoint(_options.BindAddress, _options.Port));

            _tcp = new Socket(_options.BindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _tcp.Bind(new IPEndPoint(_options.BindAddress, _options.Port));
            _tcp.Listen(64);

            BoundEndPoint = (IPEndPoint)_udp.LocalEndPoint!;

            var token = _cts.Token;
            var udp = _udp;
            var tcp = _tcp;

            _udpLoop = Task.Run(() => UdpLoopAsync(udp, token), CancellationToken.None);
            _tcpLoop = Task.Run(() => TcpLoopAsync(tcp, token), CancellationToken.None);
            return Task.FromResult(true);
        }
        catch (SocketException ex)
        {
            LastError = $"Port {_options.Port} on {_options.BindAddress}: {ex.SocketErrorCode}";
            ReleaseSockets();
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ReleaseSockets();
            return Task.FromResult(false);
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts == null) return;

        _cts = null;

        try { cts.Cancel(); } catch { }

        try
        {
            if (_udpLoop != null) await Task.WhenAny(_udpLoop, Task.Delay(2000)).ConfigureAwait(false);
            if (_tcpLoop != null) await Task.WhenAny(_tcpLoop, Task.Delay(2000)).ConfigureAwait(false);
        }
        catch { }

        ReleaseSockets();
        cts.Dispose();
        BoundEndPoint = null;
        _udpLoop = null;
        _tcpLoop = null;
    }

    private void ReleaseSockets()
    {
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _udp = null;
        _tcp = null;
    }

    private async Task UdpLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[Math.Max(512, _options.MaxUdpReceive)];
        var any = new IPEndPoint(
            _options.BindAddress.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;

            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize or SocketError.Interrupted)
            {
                continue;
            }
            catch (SocketException) { break; }

            if (result.ReceivedBytes <= 0) continue;

            var packet = new byte[result.ReceivedBytes];
            Array.Copy(buffer, packet, packet.Length);
            _ = HandleUdpAsync(socket, packet, result.RemoteEndPoint, token);
        }
    }

    private async Task HandleUdpAsync(Socket socket, byte[] packet, EndPoint remote, CancellationToken token)
    {
        Stats.RecordQuery();

        if (!await _inFlight.WaitAsync(0, token).ConfigureAwait(false))
        {
            Stats.RecordFailed();
            return;
        }

        try
        {
            var (response, query) = await ResolveAsync(packet, token).ConfigureAwait(false);
            if (response == null || query == null) return;

            var fitted = DnsPacket.FitForUdp(response, query.Value.UdpPayloadSize, query.Value.QuestionLength);
            await socket.SendToAsync(fitted, SocketFlags.None, remote, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Stats.RecordFailed();
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task TcpLoopAsync(Socket listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;

            try
            {
                client = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = HandleTcpClientAsync(client, token);
        }
    }

    private async Task HandleTcpClientAsync(Socket client, CancellationToken token)
    {
        Stats.RecordQuery();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(_options.QueryTimeout);

        try
        {
            using (client)
            using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                if (!await _inFlight.WaitAsync(0, linked.Token).ConfigureAwait(false))
                {
                    Stats.RecordFailed();
                    return;
                }

                try
                {
                    var prefix = new byte[2];
                    await stream.ReadExactlyAsync(prefix, linked.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
                    if (length is < DnsPacket.HeaderLength or > 65535) return;

                    var packet = new byte[length];
                    await stream.ReadExactlyAsync(packet, linked.Token).ConfigureAwait(false);

                    var (response, _) = await ResolveAsync(packet, linked.Token).ConfigureAwait(false);
                    if (response == null) return;

                    var responsePrefix = new byte[2];
                    BinaryPrimitives.WriteUInt16BigEndian(responsePrefix, (ushort)response.Length);
                    await stream.WriteAsync(responsePrefix, linked.Token).ConfigureAwait(false);
                    await stream.WriteAsync(response, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    _inFlight.Release();
                }
            }
        }
        catch (Exception)
        {
            Stats.RecordFailed();
        }
    }

    private async Task<(byte[]? Response, DnsQuery? Query)> ResolveAsync(byte[] packet, CancellationToken token)
    {
        if (!DnsPacket.TryParseQuery(packet, out var query))
        {
            // Malformed or not a single-question query: drop it silently.
            return (null, null);
        }

        var verdict = _policy.Check(query);
        if (verdict.IsBlocked)
        {
            Stats.RecordBlocked();
            string reason = verdict.Reason ?? "Blocked";
            RaiseBlocked(query.Name, reason);
            RaiseProcessed(query.Name, true, reason);
            return (DnsPacket.BuildBlockedResponse(packet, query, _options.BlockedAnswer), query);
        }

        RaiseProcessed(query.Name, false, null);

        if (_cache != null && _cache.TryGet(query.Name, query.Type, query.Id, out var cached))
        {
            Stats.RecordCached();
            return (cached, query);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.QueryTimeout);

        var upstream = await _upstream.ResolveAsync(query, packet, timeout.Token).ConfigureAwait(false);
        if (upstream == null)
        {
            Stats.RecordFailed();
            return (null, query);
        }

        _cache?.Set(query.Name, query.Type, upstream);
        return (upstream, query);
    }

    private void RaiseBlocked(string domain, string reason)
    {
        var handler = QueryBlocked;
        if (handler == null) return;

        try { handler(this, new BlockedQueryEventArgs(domain, reason)); }
        catch { /* a listener must never break the DNS path */ }
    }

    private void RaiseProcessed(string domain, bool blocked, string? reason)
    {
        var handler = QueryProcessed;
        if (handler == null) return;

        try { handler(this, new QueryEventArgs(domain, blocked, reason)); }
        catch { /* a listener must never break the DNS path */ }
    }

    /// <summary>
    /// Sends a probe query to this server and waits for any valid DNS answer. The watchdog uses this
    /// to tell "listening" from "bound but dead".
    /// </summary>
    public async Task<bool> SelfTestAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (BoundEndPoint == null) return false;

        const ushort probeId = 0x5A5A;

        try
        {
            using var socket = new Socket(BoundEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

            var any = new IPEndPoint(
                BoundEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            var probe = DnsPacket.BuildQuery("applock-selftest.invalid", DnsPacket.TypeA, probeId);
            await socket.SendToAsync(probe, SocketFlags.None, BoundEndPoint, linked.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, linked.Token).ConfigureAwait(false);
            if (received.ReceivedBytes < DnsPacket.HeaderLength) return false;

            return DnsPacket.ReadU16(buffer, 0) == probeId && (DnsPacket.ReadU16(buffer, 2) & 0x8000) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);
        _inFlight.Dispose();
    }
}
