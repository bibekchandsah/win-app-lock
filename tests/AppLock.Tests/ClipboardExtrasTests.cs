using System;
using System.Text.RegularExpressions;
using AppLock.Models;
using Xunit;

namespace AppLock.Tests;

public class ClipboardExtrasTests
{
    [Theory]
    [InlineData("#FF0000", true)]
    [InlineData("#00ff00", true)]
    [InlineData("#12345678", true)]
    [InlineData("#ABC", true)]
    [InlineData("not a color", false)]
    [InlineData("#12345", false)]
    public void ColorSwatchRegex_MatchesValidHexCodes(string input, bool expectedMatch)
    {
        var regex = new Regex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
        Assert.Equal(expectedMatch, regex.IsMatch(input.Trim()));
    }

    [Theory]
    [InlineData("https://example.com/test", true)]
    [InlineData("http://google.com", true)]
    [InlineData("www.github.com", true)]
    [InlineData("hello world", false)]
    public void LinkDetection_MatchesUrls(string input, bool expectedLink)
    {
        var trimmed = input.Trim();
        bool isLink = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                      trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                      trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedLink, isLink);
    }

    [Fact]
    public void TextTransforms_WorkCorrectly()
    {
        string raw = "  hello WORLD!  \r\nthis is a TEST\n";

        // Trim
        Assert.Equal("hello WORLD!  \r\nthis is a TEST", raw.Trim());

        // UPPERCASE
        Assert.Equal("HELLO WORLD", "hello world".ToUpperInvariant());

        // lowercase
        Assert.Equal("hello world", "HELLO WORLD".ToLowerInvariant());

        // Title Case
        var titleCase = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase("hello world");
        Assert.Equal("Hello World", titleCase);

        // Remove line breaks
        var singleLine = raw.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        Assert.DoesNotContain("\n", singleLine);
        Assert.DoesNotContain("\r", singleLine);

        // URL encode / decode
        string urlRaw = "Hello World & Co = 100%";
        string encoded = Uri.EscapeDataString(urlRaw);
        string decoded = Uri.UnescapeDataString(encoded);
        Assert.Equal(urlRaw, decoded);

        // Base64 encode / decode
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(urlRaw));
        string unB64 = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Equal(urlRaw, unB64);
    }

    [Fact]
    public void Snippet_CreationAndProperties()
    {
        var snippet = new ClipboardItem
        {
            Id = 1,
            Type = "text",
            ContentString = "console.log('test');",
            PreviewString = "console.log('test');",
            Label = "JS Log",
            IsSnippet = true,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        Assert.True(snippet.IsSnippet);
        Assert.Equal("JS Log", snippet.Label);
        Assert.Equal("console.log('test');", snippet.ContentString);
    }
}
