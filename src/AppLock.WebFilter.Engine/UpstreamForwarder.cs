using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace AppLock.WebFilter.Engine;

public sealed record DohEndpoint(string Name, Uri Uri, IPAddress? Address);

public sealed class DnsUpstreamOptions
{
    /// <summary>DoH resolvers in order: the first is used, the next one is the fallback.</summary>
    public IReadOnlyList<DohEndpoint> DohEndpoints { get; init; } = Array.Empty<DohEndpoint>();

    /// <summary>The DNS servers Windows used before AppLock changed them (plain DNS).</summary>
    public IReadOnlyList<IPEndPoint> PlainServers { get; init; } = Array.Empty<IPEndPoint>();

    /// <summary>The machine's DNS suffix, so its own names keep resolving through the network.</summary>
    public string? MachineDnsSuffix { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
}

public interface IUpstreamResolver
{
    Task<byte[]?> ResolveAsync(DnsQuery query, byte[] request, CancellationToken cancellationToken);
}

/// <summary>
/// Sends allowed queries upstream: DoH for public names, the network's own servers for local ones,
/// with a fallback after repeated failures. Never disables TLS validation.
/// </summary>
public sealed class UpstreamForwarder : IUpstreamResolver, IDisposable
{
    public static readonly DohEndpoint Cloudflare = new("Cloudflare", new Uri("https://cloudflare-dns.com/dns-query"), IPAddress.Parse("1.1.1.1"));
    public static readonly DohEndpoint Google = new("Google", new Uri("https://dns.google/dns-query"), IPAddress.Parse("8.8.8.8"));
    public static readonly DohEndpoint Quad9 = new("Quad9", new Uri("https://dns.quad9.net/dns-query"), IPAddress.Parse("9.9.9.9"));

    private const int FailuresBeforeFallback = 3;

    private readonly DnsUpstreamOptions _options;
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private int _activeEndpoint;
    private int _consecutiveFailures;
    private bool _usingFallback;

    public UpstreamForwarder(DnsUpstreamOptions options)
    {
        _options = options;
    }

    /// <summary>True once the primary resolver failed three times in a row and the fallback is in use.</summary>
    public bool IsUsingFallback => Volatile.Read(ref _usingFallback);

    public string? LastError { get; private set; }

    public async Task<byte[]?> ResolveAsync(DnsQuery query, byte[] request, CancellationToken cancellationToken)
    {
        try
        {
            return LocalNameRules.IsLocalName(query.Name, _options.MachineDnsSuffix)
                ? await ForwardPlainAsync(request, cancellationToken).ConfigureAwait(false)
                : await ForwardDohAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private async Task<byte[]?> ForwardDohAsync(byte[] request, CancellationToken cancellationToken)
    {
        var endpoints = _options.DohEndpoints;
        if (endpoints.Count == 0)
        {
            return _options.PlainServers.Count > 0
                ? await ForwardPlainAsync(request, cancellationToken).ConfigureAwait(false)
                : null;
        }

        var endpoint = endpoints[Math.Clamp(_activeEndpoint, 0, endpoints.Count - 1)];

        try
        {
            var response = await PostDohAsync(endpoint, request, cancellationToken).ConfigureAwait(false);
            _consecutiveFailures = 0;
            _usingFallback = false;
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = $"{endpoint.Name}: {ex.Message}";
            _consecutiveFailures++;

            if (_consecutiveFailures >= FailuresBeforeFallback && endpoints.Count > 1)
            {
                _activeEndpoint = (_activeEndpoint + 1) % endpoints.Count;
                _consecutiveFailures = 0;
                _usingFallback = true;
            }

            // A single failed query still gets one plain-DNS attempt so the user is not left offline.
            if (_options.PlainServers.Count > 0)
            {
                try
                {
                    return await ForwardPlainAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception plainEx)
                {
                    LastError = $"{LastError}; plain: {plainEx.Message}";
                }
            }

            return null;
        }
    }

    private async Task<byte[]> PostDohAsync(DohEndpoint endpoint, byte[] request, CancellationToken cancellationToken)
    {
        var client = GetClient(endpoint);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.Uri)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);

        if (body.Length < DnsPacket.HeaderLength || (DnsPacket.ReadU16(body, 2) & 0x8000) == 0)
        {
            throw new HttpRequestException("Not a DNS response");
        }

        if (DnsPacket.ReadU16(body, 0) != DnsPacket.ReadU16(request, 0))
        {
            throw new HttpRequestException("Transaction id mismatch");
        }

        return body;
    }

    private HttpClient GetClient(DohEndpoint endpoint)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(endpoint.Uri.AbsoluteUri, out var existing)) return existing;

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = _options.Timeout,
                AutomaticDecompression = DecompressionMethods.None,
                // Connect to the resolver's IP so the filter never needs itself to find its upstream,
                // while TLS still validates the real host name (SNI and certificate checks unchanged).
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        var target = endpoint.Address ?? (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false))[0];
                        await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            _clients[endpoint.Uri.AbsoluteUri] = client;
            return client;
        }
    }

    private async Task<byte[]> ForwardPlainAsync(byte[] request, CancellationToken cancellationToken)
    {
        if (_options.PlainServers.Count == 0)
        {
            throw new InvalidOperationException("No original DNS servers are known");
        }

        Exception? last = null;

        foreach (var server in _options.PlainServers)
        {
            try
            {
                var response = await PlainUdpAsync(server, request, cancellationToken).ConfigureAwait(false);
                if (IsTruncated(response))
                {
                    return await PlainTcpAsync(server, request, cancellationToken).ConfigureAwait(false);
                }
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("No original DNS server answered");
    }

    private async Task<byte[]> PlainUdpAsync(IPEndPoint server, byte[] request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.SendToAsync(request, SocketFlags.None, server, timeout.Token).ConfigureAwait(false);

        var buffer = new byte[4096];
        var any = new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (true)
        {
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, timeout.Token).ConfigureAwait(false);
            if (received.ReceivedBytes < DnsPacket.HeaderLength) continue;
            if (received.RemoteEndPoint is IPEndPoint from && !from.Equals(server)) continue;
            if (DnsPacket.ReadU16(buffer, 0) != DnsPacket.ReadU16(request, 0)) continue;

            var response = new byte[received.ReceivedBytes];
            Array.Copy(buffer, response, response.Length);
            return response;
        }
    }

    private async Task<byte[]> PlainTcpAsync(IPEndPoint server, byte[] request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(server, timeout.Token).ConfigureAwait(false);

        using var stream = new NetworkStream(socket, ownsSocket: false);

        var lengthPrefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthPrefix, (ushort)request.Length);
        await stream.WriteAsync(lengthPrefix, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);

        await stream.ReadExactlyAsync(lengthPrefix, timeout.Token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
        var response = new byte[length];
        await stream.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
        return response;
    }

    private static bool IsTruncated(byte[] response)
        => response.Length >= DnsPacket.HeaderLength && (DnsPacket.ReadU16(response, 2) & 0x0200) != 0;

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();
        }
    }
}
