using System;
using AppLock.WindowIsland.Modules.Text;
using Xunit;

namespace AppLock.Tests.WindowIsland;

public class M14_TextTests
{
    // ── Case Transforms ────────────────────────────────────────────────────────

    [Fact]
    public void ToUpperCase_TransformsCorrectly()
    {
        Assert.Equal("HELLO WORLD!", WindowIslandTextManager.ToUpperCase("Hello World!"));
        Assert.Equal("", WindowIslandTextManager.ToUpperCase(""));
        Assert.Equal("", WindowIslandTextManager.ToUpperCase(null));
        Assert.Equal("123 ABC", WindowIslandTextManager.ToUpperCase("123 abc"));
    }

    [Fact]
    public void ToLowerCase_TransformsCorrectly()
    {
        Assert.Equal("hello world!", WindowIslandTextManager.ToLowerCase("Hello World!"));
        Assert.Equal("", WindowIslandTextManager.ToLowerCase(""));
        Assert.Equal("", WindowIslandTextManager.ToLowerCase(null));
        Assert.Equal("123 abc", WindowIslandTextManager.ToLowerCase("123 ABC"));
    }

    [Fact]
    public void ToTitleCase_TransformsCorrectly()
    {
        Assert.Equal("The Quick Brown Fox", WindowIslandTextManager.ToTitleCase("the quick brown fox"));
        Assert.Equal("The Quick Brown Fox", WindowIslandTextManager.ToTitleCase("THE QUICK BROWN FOX"));
        Assert.Equal("Hello World", WindowIslandTextManager.ToTitleCase("hELlo wORld"));
        Assert.Equal("", WindowIslandTextManager.ToTitleCase(""));
        Assert.Equal("", WindowIslandTextManager.ToTitleCase(null));
    }

    // ── Whitespace & Line Transforms ──────────────────────────────────────────

    [Fact]
    public void TrimText_TrimsOuterAndPerLine()
    {
        string input = "   line one   \r\n   line two   \r\n";
        string trimmed = WindowIslandTextManager.TrimText(input);
        Assert.Equal($"line one{Environment.NewLine}line two", trimmed);

        Assert.Equal("", WindowIslandTextManager.TrimText("   "));
        Assert.Equal("", WindowIslandTextManager.TrimText(null));
    }

    [Fact]
    public void RemoveLineBreaks_ReplacesWithSingleSpace()
    {
        string input = "Hello\r\nWorld!\nThis  is\ra test.";
        string result = WindowIslandTextManager.RemoveLineBreaks(input);
        Assert.Equal("Hello World! This is a test.", result);

        Assert.Equal("", WindowIslandTextManager.RemoveLineBreaks(""));
        Assert.Equal("", WindowIslandTextManager.RemoveLineBreaks(null));
    }

    [Fact]
    public void SortLines_SortsAscendingAndDescending()
    {
        string input = $"Zebra{Environment.NewLine}Apple{Environment.NewLine}Mango";

        string sortedAsc = WindowIslandTextManager.SortLines(input, descending: false);
        Assert.Equal($"Apple{Environment.NewLine}Mango{Environment.NewLine}Zebra", sortedAsc);

        string sortedDesc = WindowIslandTextManager.SortLines(input, descending: true);
        Assert.Equal($"Zebra{Environment.NewLine}Mango{Environment.NewLine}Apple", sortedDesc);

        Assert.Equal("", WindowIslandTextManager.SortLines(""));
        Assert.Equal("", WindowIslandTextManager.SortLines(null));
    }

    // ── Statistics Computation ────────────────────────────────────────────────

    [Fact]
    public void ComputeStatistics_NullOrEmpty_ReturnsZeros()
    {
        var stats1 = WindowIslandTextManager.ComputeStatistics(null);
        Assert.Equal(0, stats1.Words);
        Assert.Equal(0, stats1.Characters);
        Assert.Equal(0, stats1.CharactersNoSpaces);
        Assert.Equal(0, stats1.Lines);

        var stats2 = WindowIslandTextManager.ComputeStatistics("");
        Assert.Equal(0, stats2.Words);
        Assert.Equal(0, stats2.Characters);
    }

    [Fact]
    public void ComputeStatistics_SingleLine_CountsCorrectly()
    {
        string text = "The quick brown fox jumps.";
        var stats = WindowIslandTextManager.ComputeStatistics(text);

        Assert.Equal(5, stats.Words);
        Assert.Equal(26, stats.Characters);
        Assert.Equal(22, stats.CharactersNoSpaces);
        Assert.Equal(1, stats.Lines);
    }

    [Fact]
    public void ComputeStatistics_MultiLine_CountsCorrectly()
    {
        string text = $"Line 1 has four words.{Environment.NewLine}Line 2 has five more words.";
        var stats = WindowIslandTextManager.ComputeStatistics(text);

        Assert.Equal(11, stats.Words);
        Assert.Equal(text.Length, stats.Characters);
        Assert.Equal(2, stats.Lines);
    }

    // ── Strip Formatting ──────────────────────────────────────────────────────

    [Fact]
    public void StripFormatting_RemovesHtmlAndRtfTags()
    {
        string html = "<p>Hello <b>World</b>! <a href=\"#\">Click here</a>.</p>";
        string strippedHtml = WindowIslandTextManager.StripFormatting(html);
        Assert.Equal("Hello World ! Click here .", strippedHtml);

        string rtf = @"{\rtf1\ansi\deff0 {\fonttbl {\f0 Courier;}}\f0\fs24 Hello \b World\b0!}";
        string strippedRtf = WindowIslandTextManager.StripFormatting(rtf);
        Assert.Contains("Hello", strippedRtf);
        Assert.Contains("World", strippedRtf);
        Assert.DoesNotContain(@"\rtf", strippedRtf);
    }
}
