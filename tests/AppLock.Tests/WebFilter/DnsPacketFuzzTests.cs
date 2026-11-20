using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

/// <summary>
/// Feeds mutated and random packets through the parser. Nothing may throw, and parsing must stay
/// cheap: a hostile packet is dropped, never expanded.
/// </summary>
public class DnsPacketFuzzTests
{
    private static readonly byte[][] Seeds =
    {
        DnsPacket.BuildQuery("example.com", DnsPacket.TypeA),
        DnsPacket.BuildQuery("a.b.c.d.e.f.example.co.uk", DnsPacket.TypeAaaa, 0xBEEF),
        DnsPacket.BuildQuery("xn--bcher-kva.example", DnsPacket.TypeA, 0x0001)
    };

    [Fact]
    public void MutatedPacketsNeverThrow()
    {
        var random = new Random(20261120);

        for (int iteration = 0; iteration < 5000; iteration++)
        {
            var packet = Mutate(Seeds[iteration % Seeds.Length], random);

            var parsed = false;
            var exception = Record.Exception(() => parsed = DnsPacket.TryParseQuery(packet, out _));

            Assert.Null(exception);
            _ = parsed;
        }
    }

    [Fact]
    public void RandomGarbageNeverThrows()
    {
        var random = new Random(4242);
        var buffer = new byte[3000];

        for (int iteration = 0; iteration < 3000; iteration++)
        {
            random.NextBytes(buffer);
            int length = random.Next(0, buffer.Length);

            var exception = Record.Exception(() => DnsPacket.TryParseQuery(buffer.AsSpan(0, length), out _));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void HugeInputIsRejectedOrParsedWithoutGrowingMemory()
    {
        var random = new Random(99);
        var packet = new byte[65_535];
        random.NextBytes(packet);

        // Force a plausible header so the parser walks the name section.
        packet[2] = 0x01;
        packet[3] = 0x00;
        packet[4] = 0x00;
        packet[5] = 0x01;

        for (int i = 0; i < 200; i++)
        {
            DnsPacket.TryParseQuery(packet, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
        {
            DnsPacket.TryParseQuery(packet, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // 2000 parses must not allocate per-attempt buffers of the packet size (65 KB each would be 128 MB).
        Assert.True(allocated < 4 * 1024 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void CompressionLoopsAreRejected()
    {
        // Header + a pointer that points at itself, with ARCOUNT claiming a record to walk.
        var packet = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0x01 };
        packet.Add(0xC0);
        packet.Add(0x0C);

        Assert.False(DnsPacket.TryParseQuery(packet.ToArray(), out _));
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var packet = (byte[])seed.Clone();

        int mutations = random.Next(1, 8);
        for (int i = 0; i < mutations; i++)
        {
            switch (random.Next(4))
            {
                case 0:
                    packet[random.Next(packet.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    packet[random.Next(packet.Length)] |= (byte)(1 << random.Next(8));
                    break;
                case 2:
                    if (packet.Length > DnsPacket.HeaderLength)
                    {
                        packet[random.Next(DnsPacket.HeaderLength, packet.Length)] = 0xC0;
                    }
                    break;
                default:
                    int length = random.Next(0, packet.Length + 1);
                    packet = packet.AsSpan(0, length).ToArray();
                    break;
            }

            if (packet.Length == 0) return packet;
        }

        return packet;
    }
}
