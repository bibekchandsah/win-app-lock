using System.Linq;
using AppLock.Models;
using Xunit;

namespace AppLock.Tests.WindowIsland;

public sealed class M8_CustomSearchCommandTests
{
    [Fact]
    public void NormalizeCommand_AddsAtPrefixAndLowercases()
    {
        Assert.Equal("@youtube", CustomSearchCommand.NormalizeCommand("youtube"));
        Assert.Equal("@youtube", CustomSearchCommand.NormalizeCommand("@youtube"));
        Assert.Equal("@youtube", CustomSearchCommand.NormalizeCommand("@YouTube"));
        Assert.Equal("@reddit", CustomSearchCommand.NormalizeCommand("REDDIT"));
        Assert.Equal(string.Empty, CustomSearchCommand.NormalizeCommand("   "));
    }

    [Fact]
    public void ExtractBaseUrl_ExtractsDomainCorrectly()
    {
        string url = "https://www.youtube.com/results?search_query=";
        string baseUrl = CustomSearchCommand.ExtractBaseUrl(url);
        Assert.Equal("https://www.youtube.com", baseUrl);

        string gitUrl = "https://github.com/search?q=";
        Assert.Equal("https://github.com", CustomSearchCommand.ExtractBaseUrl(gitUrl));
    }

    [Fact]
    public void BuildUrl_AppendsEscapedSearchTerm()
    {
        var cmd = new CustomSearchCommand("@youtube", "YouTube", "https://www.youtube.com/results?search_query=");
        string target = cmd.BuildUrl("nepali music");
        Assert.Equal("https://www.youtube.com/results?search_query=nepali%20music", target);
    }

    [Fact]
    public void BuildUrl_ReplacesQPlaceholder()
    {
        var cmd = new CustomSearchCommand("@test", "Test", "https://example.com/search?q={q}&lang=en");
        string target = cmd.BuildUrl("hello world");
        Assert.Equal("https://example.com/search?q=hello%20world&lang=en", target);
    }

    [Fact]
    public void BuildUrl_EmptyQuery_ReturnsBaseUrl()
    {
        var cmd = new CustomSearchCommand("@youtube", "YouTube", "https://www.youtube.com/results?search_query=", "https://www.youtube.com");
        string target = cmd.BuildUrl("");
        Assert.Equal("https://www.youtube.com", target);
    }

    [Fact]
    public void DefaultCustomSearches_ContainsAllRequestedEngines()
    {
        var defaults = WindowIslandSettings.DefaultCustomSearches();

        string[] requiredCommands =
        [
            "@incognito", "@youtube", "@maps", "@images", "@amazon", "@flipkart",
            "@wiki", "@wikihow", "@github", "@translate", "@stackoverflow",
            "@reddit", "@quora", "@medium", "@linkedin", "@twitter", "@x",
            "@news", "@scholar", "@weather", "@duckduckgo", "@spotify",
            "@coursera", "@udemy", "@discord", "@pinterest", "@unsplash", "@google"
        ];

        foreach (var req in requiredCommands)
        {
            var found = defaults.FirstOrDefault(d => d.Command == req);
            Assert.True(found != null, $"Missing expected default search command: {req}");
            Assert.False(string.IsNullOrWhiteSpace(found.QueryUrl), $"QueryUrl should not be empty for {req}");
        }

        var incognito = defaults.First(d => d.Command == "@incognito");
        Assert.True(incognito.IsIncognito);
    }

    [Fact]
    public void Settings_Clone_DeepCopiesCustomSearches()
    {
        var s = new WindowIslandSettings();
        var origCount = s.GetCustomSearches().Count;

        var clone = s.Clone();
        Assert.Equal(origCount, clone.GetCustomSearches().Count);

        // Mutating clone does not mutate original
        clone.GetCustomSearches().Add(new CustomSearchCommand("@custom", "Custom", "https://custom.com/search?q="));
        Assert.Equal(origCount + 1, clone.GetCustomSearches().Count);
        Assert.Equal(origCount, s.GetCustomSearches().Count);
    }
}
