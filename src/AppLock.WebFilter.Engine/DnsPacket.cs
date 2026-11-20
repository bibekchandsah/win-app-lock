using System.Buffers.Binary;
using System.Text;

namespace AppLock.WebFilter.Engine;

/// <summary>The header and first question of a DNS query. Names are lower-case presentation form.</summary>
public readonly record struct DnsQuery(
    ushort Id,
    ushort Flags,
    string Name,
    ushort Type,
    ushort Class,
    int QuestionLength,
    int UdpPayloadSize)
{
    public bool IsRecursionDesired => (Flags & 0x0100) != 0;
}

public enum BlockedAnswer
{
    /// <summary>RCODE 3, no records.</summary>
    NxDomain = 0,

    /// <summary>NOERROR with 0.0.0.0 (A) or :: (AAAA); other types get an empty answer.</summary>
    NullAddress = 1
}

/// <summary>
/// Minimal, strictly bounded DNS wire handling. Only the header and the first question are parsed;
/// everything else is forwarded untouched, so this never has to implement the full DNS stack.
/// </summary>
public static class DnsPacket
{
    public const int HeaderLength = 12;
    public const int MaxLabelLength = 63;
    public const int MaxNameLength = 255;
    public const int DefaultUdpPayloadSize = 512;

    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort TypeOpt = 41;

    private const int MaxPointerJumps = 16;

    /// <summary>
    /// Parses a query. Returns false for anything this filter will not decide on: responses,
    /// truncated or oversized packets, pointer loops, bad label lengths, QDCOUNT != 1 and names
    /// that are not plain ASCII.
    /// </summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> packet, out DnsQuery query)
    {
        query = default;

        if (packet.Length < HeaderLength) return false;

        ushort id = ReadU16(packet, 0);
        ushort flags = ReadU16(packet, 2);
        if ((flags & 0x8000) != 0) return false;        // QR set: this is a response
        if (ReadU16(packet, 4) != 1) return false;      // QDCOUNT must be 1

        int offset = HeaderLength;
        var name = new StringBuilder(64);
        int nameBytes = 0;

        while (true)
        {
            if (offset >= packet.Length) return false;

            int labelLength = packet[offset++];
            if (labelLength == 0) break;

            // Compression pointers (0xC0) and reserved label types are not valid in a question.
            if ((labelLength & 0xC0) != 0) return false;
            if (labelLength > MaxLabelLength) return false;
            if (offset + labelLength > packet.Length) return false;

            nameBytes += labelLength + 1;
            if (nameBytes > MaxNameLength) return false;

            if (name.Length > 0) name.Append('.');
            for (int i = 0; i < labelLength; i++)
            {
                byte b = packet[offset + i];
                // Anything outside printable ASCII cannot be matched against a list; ignore the packet.
                if (b < 0x21 || b > 0x7E) return false;
                name.Append((char)(b >= 'A' && b <= 'Z' ? b + 32 : b));
            }

            offset += labelLength;
        }

        if (name.Length == 0) return false;                     // root query: nothing to decide
        if (offset + 4 > packet.Length) return false;

        ushort type = ReadU16(packet, offset);
        offset += 2;
        ushort qclass = ReadU16(packet, offset);
        offset += 2;

        int questionLength = offset - HeaderLength;
        int udpPayload = ReadUdpPayloadSize(packet, offset, ReadU16(packet, 10), type);

        query = new DnsQuery(id, flags, name.ToString(), type, qclass, questionLength, udpPayload);
        return true;
    }

    /// <summary>Reads the EDNS0 UDP payload size from the OPT record, if there is one.</summary>
    public static int ReadUdpPayloadSize(ReadOnlySpan<byte> packet, int offset, ushort additionalCount, ushort questionType)
    {
        // A query may carry an OPT record next to its single question; anything else is ignored.
        if (additionalCount == 0 || additionalCount > 8) return DefaultUdpPayloadSize;

        for (int record = 0; record < additionalCount; record++)
        {
            if (!TrySkipName(packet, ref offset)) return DefaultUdpPayloadSize;
            if (offset + 10 > packet.Length) return DefaultUdpPayloadSize;

            ushort type = ReadU16(packet, offset);
            ushort payloadSize = ReadU16(packet, offset + 2);
            int dataLength = ReadU16(packet, offset + 8);
            offset += 10;

            if (offset + dataLength > packet.Length) return DefaultUdpPayloadSize;

            if (type == TypeOpt && payloadSize >= DefaultUdpPayloadSize && payloadSize <= 4096)
            {
                return payloadSize;
            }

            offset += dataLength;
        }

        _ = questionType;
        return DefaultUdpPayloadSize;
    }

    /// <summary>Skips a possibly compressed name, refusing pointer loops.</summary>
    public static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
        int jumps = 0;
        int resume = -1;

        while (true)
        {
            if (offset >= packet.Length) return false;

            int length = packet[offset];

            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length) return false;
                if (++jumps > MaxPointerJumps) return false;

                int target = ((length & 0x3F) << 8) | packet[offset + 1];
                if (target >= packet.Length) return false;

                // Remember where the name really ends: after the pointer, not after the target.
                if (resume < 0) resume = offset + 2;
                offset = target;
                continue;
            }

            if ((length & 0xC0) != 0) return false;
            if (length == 0)
            {
                offset++;
                if (resume >= 0) offset = resume;
                return true;
            }

            offset += 1 + length;
        }
    }

    /// <summary>Builds the blocked answer for a query.</summary>
    public static byte[] BuildBlockedResponse(ReadOnlySpan<byte> request, in DnsQuery query, BlockedAnswer answer)
    {
        if (answer == BlockedAnswer.NullAddress && (query.Type == TypeA || query.Type == TypeAaaa))
        {
            return BuildAddressAnswer(request, query);
        }

        ushort rcode = answer == BlockedAnswer.NxDomain ? (ushort)3 : (ushort)0;
        var response = new byte[HeaderLength + query.QuestionLength];

        WriteU16(response, 0, query.Id);
        WriteU16(response, 2, (ushort)(0x8000 | 0x0080 | (query.Flags & 0x0100) | rcode));
        WriteU16(response, 4, 1);
        WriteU16(response, 6, 0);
        WriteU16(response, 8, 0);
        WriteU16(response, 10, 0);

        request.Slice(HeaderLength, query.QuestionLength).CopyTo(response.AsSpan(HeaderLength));
        return response;
    }

    private static byte[] BuildAddressAnswer(ReadOnlySpan<byte> request, in DnsQuery query)
    {
        int dataLength = query.Type == TypeA ? 4 : 16;
        var response = new byte[HeaderLength + query.QuestionLength + 16 + dataLength];

        WriteU16(response, 0, query.Id);
        WriteU16(response, 2, (ushort)(0x8000 | 0x0080 | (query.Flags & 0x0100)));
        WriteU16(response, 4, 1);
        WriteU16(response, 6, 1);
        WriteU16(response, 8, 0);
        WriteU16(response, 10, 0);

        request.Slice(HeaderLength, query.QuestionLength).CopyTo(response.AsSpan(HeaderLength));

        int offset = HeaderLength + query.QuestionLength;
        WriteU16(response, offset, 0xC00C);       // pointer to the question name
        WriteU16(response, offset + 2, query.Type);
        WriteU16(response, offset + 4, 1);        // class IN
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 6), 60);
        WriteU16(response, offset + 10, (ushort)dataLength);
        // The remaining dataLength bytes already are zero: 0.0.0.0 or ::.

        return response;
    }

    /// <summary>
    /// Sets the truncation bit and trims a UDP answer down to what the client can take. The question is
    /// kept so the client can match the reply and retry over TCP.
    /// </summary>
    public static byte[] FitForUdp(byte[] response, int udpPayloadSize, int questionLength)
    {
        int limit = Math.Max(DefaultUdpPayloadSize, udpPayloadSize);
        if (response.Length <= limit) return response;

        int keep = Math.Min(response.Length, HeaderLength + Math.Max(0, questionLength));
        var trimmed = new byte[keep];
        Array.Copy(response, trimmed, keep);

        WriteU16(trimmed, 2, (ushort)(ReadU16(response, 2) | 0x0200)); // TC
        if (keep >= HeaderLength)
        {
            WriteU16(trimmed, 4, 1);
            WriteU16(trimmed, 6, 0);
            WriteU16(trimmed, 8, 0);
            WriteU16(trimmed, 10, 0);
        }

        return trimmed;
    }

    /// <summary>Smallest TTL in the answer section, used to size the cache entry.</summary>
    public static bool TryReadMinTtl(ReadOnlySpan<byte> response, out uint ttl)
    {
        ttl = 0;

        if (response.Length < HeaderLength) return false;
        if ((ReadU16(response, 2) & 0x8000) == 0) return false;

        int questionCount = ReadU16(response, 4);
        int answerCount = ReadU16(response, 6);
        if (answerCount == 0) return false;

        int offset = HeaderLength;
        for (int i = 0; i < questionCount; i++)
        {
            if (!TrySkipName(response, ref offset)) return false;
            offset += 4;
            if (offset > response.Length) return false;
        }

        uint min = uint.MaxValue;
        bool found = false;

        for (int i = 0; i < answerCount; i++)
        {
            if (!TrySkipName(response, ref offset)) break;
            if (offset + 10 > response.Length) break;

            uint recordTtl = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(offset + 4, 4));
            int dataLength = ReadU16(response, offset + 8);
            offset += 10 + dataLength;
            if (offset > response.Length) break;

            if (recordTtl < min) min = recordTtl;
            found = true;
        }

        if (!found) return false;

        ttl = min;
        return true;
    }

    /// <summary>Rewrites the transaction id of a cached answer to the current query's id.</summary>
    public static byte[] WithId(byte[] response, ushort id)
    {
        var copy = new byte[response.Length];
        Array.Copy(response, copy, response.Length);
        WriteU16(copy, 0, id);
        return copy;
    }

    public static byte[] BuildQuery(string name, ushort type, ushort id = 0x4C4C)
    {
        var buffer = new List<byte>(64);
        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)id);
        buffer.Add(0x01); // RD
        buffer.Add(0x00);
        buffer.Add(0x00); buffer.Add(0x01); // QDCOUNT
        buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(0);
        buffer.Add(0); buffer.Add(0);

        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);

        buffer.Add((byte)(type >> 8));
        buffer.Add((byte)type);
        buffer.Add(0x00); buffer.Add(0x01);
        return buffer.ToArray();
    }

    public static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
