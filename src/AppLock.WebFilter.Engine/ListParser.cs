using System.Net;

namespace AppLock.WebFilter.Engine;

public sealed record ListParseResult(
    IReadOnlyList<string> Domains,
    int LinesRead,
    int CommentLines,
    int InvalidLines,
    int DuplicateLines);

/// <summary>
/// Parses the three list shapes the filter supports: hosts files (<c>0.0.0.0 domain</c>), plain
/// one-domain-per-line lists and the simple adblock form <c>||domain^</c>. Comments, cosmetic rules,
/// paths, ports and IP addresses are ignored. Lines are capped so a hostile list cannot make the
/// parser allocate without bound.
/// </summary>
public static class ListParser
{
    public const int MaxLineLength = 4096;

    public static ListParseResult Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    public static ListParseResult Parse(TextReader reader)
    {
        var domains = new HashSet<string>(StringComparer.Ordinal);
        int lines = 0;
        int comments = 0;
        int invalid = 0;
        int duplicates = 0;

        var lineReader = new BoundedLineReader(reader);

        while (lineReader.TryReadLine(out var line, out bool tooLong))
        {
            lines++;

            if (tooLong)
            {
                invalid++;
                continue;
            }

            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
            {
                continue;
            }

            if (trimmed[0] is '#' or '!')
            {
                comments++;
                continue;
            }

            if (!TryParseLine(trimmed, out var domain))
            {
                invalid++;
                continue;
            }

            if (!domains.Add(domain))
            {
                duplicates++;
            }
        }

        return new ListParseResult(domains.ToList(), lines, comments, invalid, duplicates);
    }

    /// <summary>Parses one list line. Never throws for hostile input.</summary>
    public static bool TryParseLine(string? line, out string domain)
        => TryParseLine(line.AsSpan(), out domain);

    /// <summary>Parses one list line. Never throws for hostile input.</summary>
    public static bool TryParseLine(ReadOnlySpan<char> line, out string domain)
    {
        domain = string.Empty;

        var span = line.Trim();
        if (span.IsEmpty) return false;
        if (span[0] is '#' or '!') return false;

        // Inline comments are common in hosts files ("0.0.0.0 ads.example # tracker"). A '#' that
        // is not at the start of a whitespace-separated field is part of the rule (adblock's ##).
        for (int i = 1; i < span.Length; i++)
        {
            if (span[i] != '#') continue;
            if (!char.IsWhiteSpace(span[i - 1])) continue;

            span = span[..i].Trim();
            break;
        }

        if (span.IsEmpty) return false;

        string candidate;

        if (span.StartsWith("||", StringComparison.Ordinal))
        {
            // Simple adblock host rule: ||domain^ (options after the caret are ignored).
            int caret = span.IndexOf('^');
            if (caret <= 2) return false;

            candidate = span[2..caret].ToString();
            if (candidate.Contains('/') || candidate.Contains('*') || candidate.Contains('$') || candidate.Contains(':'))
            {
                return false;
            }
        }
        else
        {
            var tokens = span.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            if (tokens.Length == 1)
            {
                candidate = tokens[0];
            }
            else if (tokens.Length == 2 && IsIpToken(tokens[0]))
            {
                // Hosts format: the first field is the address, the second the blocked name.
                candidate = tokens[1];
            }
            else
            {
                // Unknown extra fields (aliases, comments that survived) are not a plain domain line.
                return false;
            }
        }

        if (candidate.Contains('/') || candidate.Contains('*') || candidate.Contains(':') || candidate.Contains('$'))
        {
            return false;
        }

        return DomainName.TryNormalize(candidate, out domain);
    }

    private static bool IsIpToken(string token)
        => IPAddress.TryParse(token, out _) ||
           token.Equals("0", StringComparison.Ordinal) ||
           token.Equals("0.0.0.0", StringComparison.Ordinal);

    /// <summary>
    /// Reads lines with a hard length cap. An over-long line is consumed and reported instead of
    /// being materialized, so a malformed (or hostile) list cannot cause a large allocation.
    /// </summary>
    private sealed class BoundedLineReader
    {
        private readonly TextReader _reader;
        private readonly char[] _buffer = new char[8192];
        private readonly char[] _line = new char[MaxLineLength];
        private int _position;
        private int _length;
        private bool _endOfStream;

        public BoundedLineReader(TextReader reader)
        {
            _reader = reader;
        }

        public bool TryReadLine(out string line, out bool tooLong)
        {
            line = string.Empty;
            tooLong = false;

            int count = 0;
            bool sawAny = false;

            while (true)
            {
                if (_position >= _length)
                {
                    if (_endOfStream) break;

                    _length = _reader.Read(_buffer, 0, _buffer.Length);
                    _position = 0;

                    if (_length <= 0)
                    {
                        _endOfStream = true;
                        break;
                    }
                }

                char c = _buffer[_position++];
                sawAny = true;

                if (c == '\n') break;

                // Treat CR as part of the line ending; a lone CR file is normalized to line breaks.
                if (c == '\r') continue;

                if (count < _line.Length)
                {
                    _line[count++] = c;
                }
                else
                {
                    tooLong = true;
                }
            }

            if (!sawAny) return false;

            if (!tooLong)
            {
                line = new string(_line, 0, count);
            }

            return true;
        }
    }
}
