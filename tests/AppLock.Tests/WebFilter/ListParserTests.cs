using System.Text;
using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

public class ListParserTests
{
    [Fact]
    public void ParsesHostsFormat()
    {
        var result = Parse(
            "0.0.0.0 ads.example.com\n" +
            "127.0.0.1 tracker.example.com\n" +
            "::1 v6.example.com\n" +
            "0.0.0.0 alias.example.com # inline note\n");

        Assert.Equal(
            new[] { "ads.example.com", "alias.example.com", "tracker.example.com", "v6.example.com" },
            result.Domains.OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.Equal(4, result.Domains.Count);
        Assert.Equal(0, result.InvalidLines);
    }

    [Fact]
    public void ParsesPlainAndAdblockLists()
    {
        var result = Parse(
            "example.com\n" +
            "sub.example.org\n" +
            "||adblock.example^\n" +
            "||second.example^$third-party\n");

        Assert.Contains("example.com", result.Domains);
        Assert.Contains("sub.example.org", result.Domains);
        Assert.Contains("adblock.example", result.Domains);
        Assert.Contains("second.example", result.Domains);
    }

    [Fact]
    public void IgnoresCommentsAndUnsupportedRules()
    {
        var result = Parse(
            "# comment\n" +
            "! adblock comment\n" +
            "\n" +
            "||\n" +
            "example.com##.ad\n" +
            "ads.example.com/banner\n" +
            "*.wild.example\n" +
            "8.8.8.8\n" +
            "localhost\n" +
            "0.0.0.0 localhost\n" +
            "127.0.0.1\n");

        Assert.Empty(result.Domains);
        Assert.True(result.CommentLines >= 2);
        Assert.True(result.InvalidLines >= 5);
    }

    [Fact]
    public void LowerCasesAndDeduplicates()
    {
        var result = Parse("Example.COM\nexample.com.\n0.0.0.0 example.com\n");

        Assert.Single(result.Domains);
        Assert.Equal("example.com", result.Domains[0]);
        Assert.Equal(2, result.DuplicateLines);
    }

    [Fact]
    public void RejectsOverlongLinesWithoutAllocatingThem()
    {
        var builder = new StringBuilder();
        builder.Append('a', ListParser.MaxLineLength + 500);
        builder.Append(".example\n");
        builder.Append("good.example\n");

        var result = Parse(builder.ToString());

        Assert.Single(result.Domains);
        Assert.Equal("good.example", result.Domains[0]);
        Assert.Equal(1, result.InvalidLines);
    }

    [Fact]
    public void DoesNotCrashOnHostileLines()
    {
        string[] lines =
        {
            "||" + new string('a', 10_000) + "^",
            new string('.', 5000),
            "0.0.0.0 " + new string('a', 300) + ".com",
            "\0\0\0",
            "||example.com^$script,domain=x",
            "http://example.com",
            "0.0.0.0 0.0.0.0 example.com extra"
        };

        foreach (var line in lines)
        {
            var exception = Record.Exception(() => ListParser.TryParseLine(line, out _));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void FuzzesRandomLinesWithoutThrowing()
    {
        var random = new Random(20261120);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789.-_/|^#$*: \t";

        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            int length = random.Next(0, 300);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var line = new string(chars);
            var exception = Record.Exception(() =>
            {
                ListParser.TryParseLine(line, out var domain);
                if (domain.Length > 0)
                {
                    Assert.True(DomainName.IsValidAsciiDomain(domain));
                }
            });

            Assert.Null(exception);
        }
    }

    [Fact]
    public void ParsesAStreamWithBom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("0.0.0.0 ads.example\n")).ToArray();
        using var stream = new MemoryStream(bytes);

        var result = ListParser.Parse(stream);

        Assert.Equal(new[] { "ads.example" }, result.Domains.ToArray());
    }

    private static ListParseResult Parse(string text)
    {
        using var reader = new StringReader(text);
        return ListParser.Parse(reader);
    }
}
