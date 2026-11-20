namespace AppLock.WebFilter.Engine;

/// <summary>
/// Small TTL-respecting LRU for resolved answers. Block decisions are not cached: they are cheap
/// and must follow rule changes immediately.
/// </summary>
public sealed class DnsCache
{
    private const int MinTtlSeconds = 30;
    private const int MaxTtlSeconds = 60 * 60;
    private const int DefaultTtlSeconds = 300;

    private readonly int _capacity;
    private readonly Dictionary<Key, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _recent = new();
    private readonly object _gate = new();

    public DnsCache(int capacity = 10_000)
    {
        _capacity = Math.Max(16, capacity);
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public bool TryGet(string name, ushort type, ushort queryId, out byte[] response)
    {
        response = Array.Empty<byte>();
        var key = new Key(name, type);

        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) return false;

            if (node.Value.ExpiresUtc <= DateTime.UtcNow)
            {
                _recent.Remove(node);
                _map.Remove(key);
                return false;
            }

            _recent.Remove(node);
            _recent.AddFirst(node);
            response = DnsPacket.WithId(node.Value.Response, queryId);
            return true;
        }
    }

    public void Set(string name, ushort type, byte[] response)
    {
        if (response.Length < DnsPacket.HeaderLength) return;

        uint ttl = DnsPacket.TryReadMinTtl(response, out uint found) ? found : DefaultTtlSeconds;
        int seconds = (int)Math.Clamp(ttl, MinTtlSeconds, MaxTtlSeconds);

        var key = new Key(name, type);
        var entry = new Entry
        {
            Key = key,
            Response = (byte[])response.Clone(),
            ExpiresUtc = DateTime.UtcNow.AddSeconds(seconds)
        };

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _recent.Remove(existing);
                _map.Remove(key);
            }

            var node = _recent.AddFirst(entry);
            _map[key] = node;

            while (_map.Count > _capacity && _recent.Last != null)
            {
                var oldest = _recent.Last;
                _recent.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _recent.Clear();
        }
    }

    private readonly record struct Key(string Name, ushort Type);

    private sealed class Entry
    {
        public Key Key { get; init; }
        public byte[] Response { get; init; } = Array.Empty<byte>();
        public DateTime ExpiresUtc { get; init; }
    }
}
