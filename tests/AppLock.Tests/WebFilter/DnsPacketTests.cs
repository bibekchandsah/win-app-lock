using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

public class DnsPacketTests
{
    [Fact]
    public void ParsesAValidQuery()
    {
        var packet = DnsPacket.BuildQuery("Example.COM", DnsPacket.TypeA, 0x1234);

        Assert.True(DnsPacket.TryParseQuery(packet, out var query));
        Assert.Equal(0x1234, query.Id);
        Assert.Equal("example.com", query.Name);
        Assert.Equal(DnsPacket.TypeA, query.Type);
        Assert.Equal(1, query.Class);
        Assert.True(query.IsRecursionDesired);
        Assert.Equal(packet.Length - DnsPacket.HeaderLength, query.QuestionLength);
        Assert.Equal(512, query.UdpPayloadSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void RejectsShortPackets(int length)
    {
        Assert.False(DnsPacket.TryParseQuery(new byte[length], out _));
    }

    [Fact]
    public void RejectsResponses()
    {
        var packet = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        packet[2] |= 0x80; // QR

        Assert.False(DnsPacket.TryParseQuery(packet, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RequiresExactlyOneQuestion(int questionCount)
    {
        var packet = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        packet[4] = (byte)(questionCount >> 8);
        packet[5] = (byte)questionCount;

        Assert.False(DnsPacket.TryParseQuery(packet, out _));
    }

    [Fact]
    public void RejectsCompressionPointerInQuestion()
    {
        var packet = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        packet[12] = 0xC0;
        packet[13] = 0x0C;

        Assert.False(DnsPacket.TryParseQuery(packet, out _));
    }

    [Fact]
    public void RejectsOverlongLabel()
    {
        var packet = new List<byte>(DnsPacket.BuildQuery("a.com", DnsPacket.TypeA));
        packet[12] = 64; // label longer than 63

        Assert.False(DnsPacket.TryParseQuery(packet.ToArray(), out _));
    }

    [Fact]
    public void RejectsNameLongerThan255()
    {
        var builder = new List<byte> { 0xAB, 0xCD, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        for (int i = 0; i < 5; i++)
        {
            builder.Add(63);
            builder.AddRange(Enumerable.Repeat((byte)'a', 63));
        }
        builder.Add(0);

        Assert.False(DnsPacket.TryParseQuery(builder.ToArray(), out _));
    }

    [Fact]
    public void RejectsTruncatedQuestion()
    {
        var packet = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        Assert.False(DnsPacket.TryParseQuery(packet.AsSpan(0, packet.Length - 3), out _));
    }

    [Fact]
    public void RejectsRootName()
    {
        var packet = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        packet[12] = 0; // root label

        Assert.False(DnsPacket.TryParseQuery(packet, out _));
    }

    [Fact]
    public void RejectsNonAsciiLabels()
    {
        var packet = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        packet[13] = 0x01; // control character inside the first label

        Assert.False(DnsPacket.TryParseQuery(packet, out _));
    }

    [Fact]
    public void ReadsEdnsUdpPayloadSize()
    {
        var packet = AddEdns(DnsPacket.BuildQuery("example.com", DnsPacket.TypeA), 1232);

        Assert.True(DnsPacket.TryParseQuery(packet, out var query));
        Assert.Equal(1232, query.UdpPayloadSize);
    }

    [Fact]
    public void NxDomainResponseIsValidDns()
    {
        var request = DnsPacket.BuildQuery("blocked.example", DnsPacket.TypeA, 0x7777);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NxDomain);

        Assert.Equal(0x7777, DnsPacket.ReadU16(response, 0));
        Assert.Equal(0x8000, DnsPacket.ReadU16(response, 2) & 0x8000); // QR
        Assert.Equal(0x0080, DnsPacket.ReadU16(response, 2) & 0x0080); // RA
        Assert.Equal(3, DnsPacket.ReadU16(response, 2) & 0x000F);      // NXDOMAIN
        Assert.Equal(1, DnsPacket.ReadU16(response, 4));               // QDCOUNT
        Assert.Equal(0, DnsPacket.ReadU16(response, 6));               // ANCOUNT
        Assert.Equal(request.Length, response.Length);
        Assert.Equal(request.AsSpan(12).ToArray(), response.AsSpan(12).ToArray());
    }

    [Fact]
    public void NullAddressAnswersA()
    {
        var request = DnsPacket.BuildQuery("blocked.example", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress);

        Assert.Equal(0, DnsPacket.ReadU16(response, 2) & 0x000F);
        Assert.Equal(1, DnsPacket.ReadU16(response, 6));

        int answerOffset = DnsPacket.HeaderLength + query.QuestionLength;
        Assert.Equal((ushort)0xC00C, DnsPacket.ReadU16(response, answerOffset));
        Assert.Equal(DnsPacket.TypeA, DnsPacket.ReadU16(response, answerOffset + 2));
        Assert.Equal(4, DnsPacket.ReadU16(response, answerOffset + 10));
        Assert.Equal(new byte[4], response.AsSpan(response.Length - 4).ToArray());
    }

    [Fact]
    public void NullAddressAnswersAaaa()
    {
        var request = DnsPacket.BuildQuery("blocked.example", DnsPacket.TypeAaaa);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress);

        int answerOffset = DnsPacket.HeaderLength + query.QuestionLength;
        Assert.Equal(16, DnsPacket.ReadU16(response, answerOffset + 10));
        Assert.Equal(new byte[16], response.AsSpan(response.Length - 16).ToArray());
    }

    [Fact]
    public void NullAddressLeavesOtherTypesEmpty()
    {
        const ushort txt = 16;
        var request = DnsPacket.BuildQuery("blocked.example", txt);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress);

        Assert.Equal(0, DnsPacket.ReadU16(response, 6));
        Assert.Equal(0, DnsPacket.ReadU16(response, 2) & 0x000F);
        Assert.Equal(request.Length, response.Length);
    }

    [Fact]
    public void TruncatesAnOversizedUdpAnswerButKeepsTheQuestion()
    {
        var request = DnsPacket.BuildQuery("blocked.example", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress);
        var padded = response.Concat(new byte[2000]).ToArray();

        var fitted = DnsPacket.FitForUdp(padded, 512, query.QuestionLength);

        Assert.Equal(DnsPacket.HeaderLength + query.QuestionLength, fitted.Length);
        Assert.NotEqual(0, DnsPacket.ReadU16(fitted, 2) & 0x0200); // TC
        Assert.Equal(request.AsSpan(12).ToArray(), fitted.AsSpan(12).ToArray());
    }

    [Fact]
    public void WithIdRewritesOnlyTheId()
    {
        var request = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NxDomain);
        var copy = DnsPacket.WithId(response, 0x2222);

        Assert.Equal(0x2222, DnsPacket.ReadU16(copy, 0));
        Assert.Equal(response.AsSpan(2).ToArray(), copy.AsSpan(2).ToArray());
        Assert.NotEqual(copy, response);
    }

    [Fact]
    public void ReadsTheSmallestAnswerTtl()
    {
        var request = DnsPacket.BuildQuery("example.com", DnsPacket.TypeA);
        Assert.True(DnsPacket.TryParseQuery(request, out var query));

        var response = DnsPacket.BuildBlockedResponse(request, query, BlockedAnswer.NullAddress);

        Assert.True(DnsPacket.TryReadMinTtl(response, out uint ttl));
        Assert.Equal(60u, ttl);
    }

    private static byte[] AddEdns(byte[] request, ushort payloadSize)
    {
        var packet = new List<byte>(request);
        packet[10] = 0;
        packet[11] = 1; // ARCOUNT = 1

        packet.Add(0);                              // root name
        packet.Add(0); packet.Add((byte)DnsPacket.TypeOpt);
        packet.Add((byte)(payloadSize >> 8));
        packet.Add((byte)payloadSize);              // CLASS = payload size
        packet.Add(0); packet.Add(0); packet.Add(0); packet.Add(0); // TTL
        packet.Add(0); packet.Add(0);               // RDLENGTH

        return packet.ToArray();
    }

    [Fact]
    public void BuildQueryEncodesLabels()
    {
        var packet = DnsPacket.BuildQuery("a.b.example", DnsPacket.TypeA);

        Assert.Equal(29, packet.Length);            // 12 header + 13 name + 4 type/class
        Assert.Equal(1, packet[12]);
        Assert.Equal((byte)'a', packet[13]);
        Assert.Equal(1, packet[14]);
        Assert.Equal((byte)'b', packet[15]);
        Assert.Equal(7, packet[16]);
        Assert.True(DnsPacket.TryParseQuery(packet, out var query));
        Assert.Equal("a.b.example", query.Name);
    }
}
