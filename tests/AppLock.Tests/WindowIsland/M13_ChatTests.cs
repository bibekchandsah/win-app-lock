using System;
using System.IO;
using System.Linq;
using AppLock.Models;
using AppLock.WindowIsland.Modules.Chat;
using Xunit;

namespace AppLock.Tests.WindowIsland;

/// <summary>
/// Milestone 13 unit tests:
/// - ChatSite model and default sites
/// - URL validation and normalization
/// - Site list management (bounds, max 6, remove safety)
/// - Active site resolution
/// - Idle timeout clamping
/// - Settings cloning and persistence
/// - User-data folder path resolution
/// </summary>
public sealed class M13_ChatTests
{
    // ── ChatSite Model ────────────────────────────────────────────────────────

    [Fact]
    public void ChatSite_DefaultConstructor_SetsDefaults()
    {
        var site = new ChatSite();
        Assert.Equal(string.Empty, site.Name);
        Assert.Equal(string.Empty, site.Url);
        Assert.Equal("\uE8BD", site.Icon);
    }

    [Fact]
    public void ChatSite_CustomConstructor_SetsProperties()
    {
        var site = new ChatSite("Claude", "https://claude.ai", "\uE710");
        Assert.Equal("Claude", site.Name);
        Assert.Equal("https://claude.ai", site.Url);
        Assert.Equal("\uE710", site.Icon);
    }

    [Fact]
    public void DefaultChatSites_ContainsGeminiDeepSeekChatGPT()
    {
        var defaults = WindowIslandSettings.DefaultChatSites();
        Assert.Equal(3, defaults.Count);
        Assert.Contains(defaults, s => s.Name == "Gemini" && s.Url.Contains("gemini.google.com"));
        Assert.Contains(defaults, s => s.Name == "DeepSeek" && s.Url.Contains("deepseek.com"));
        Assert.Contains(defaults, s => s.Name == "ChatGPT" && s.Url.Contains("chatgpt.com"));
    }

    // ── URL Validation & Normalization ────────────────────────────────────────

    [Theory]
    [InlineData("https://gemini.google.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("chat.openai.com", true)]
    [InlineData("https://claude.ai/chat", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("ftp://files.example.com", false)]
    [InlineData("mailto:test@example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    public void IsValidUrl_ValidatesCorrectly(string? input, bool expected)
    {
        bool result = WindowIslandChatManager.IsValidUrl(input, out var uri);
        Assert.Equal(expected, result);
        if (expected)
        {
            Assert.NotNull(uri);
            Assert.True(uri!.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }

    [Fact]
    public void NormalizeUrl_PrependsHttpsWhenMissing()
    {
        string normalized = WindowIslandChatManager.NormalizeUrl("chatgpt.com");
        Assert.StartsWith("https://", normalized);
    }

    // ── Site List Management ──────────────────────────────────────────────────

    [Fact]
    public void AddChatSite_AddsValidSite()
    {
        var settings = new WindowIslandSettings();
        int initialCount = settings.ChatSites.Count;

        bool added = WindowIslandChatManager.AddChatSite(settings, "Claude", "https://claude.ai");
        Assert.True(added);
        Assert.Equal(initialCount + 1, settings.ChatSites.Count);
        Assert.Equal("Claude", settings.ChatSites.Last().Name);
    }

    [Fact]
    public void AddChatSite_RejectsEmptyName()
    {
        var settings = new WindowIslandSettings();
        bool added = WindowIslandChatManager.AddChatSite(settings, "", "https://claude.ai");
        Assert.False(added);
    }

    [Fact]
    public void AddChatSite_RejectsInvalidUrl()
    {
        var settings = new WindowIslandSettings();
        bool added = WindowIslandChatManager.AddChatSite(settings, "BadSite", "ftp://invalid");
        Assert.False(added);
    }

    [Fact]
    public void AddChatSite_EnforcesMax6Limit()
    {
        var settings = new WindowIslandSettings();
        // Starts with 3 defaults: add 3 more to reach 6
        Assert.True(WindowIslandChatManager.AddChatSite(settings, "Site 4", "https://site4.com"));
        Assert.True(WindowIslandChatManager.AddChatSite(settings, "Site 5", "https://site5.com"));
        Assert.True(WindowIslandChatManager.AddChatSite(settings, "Site 6", "https://site6.com"));
        Assert.Equal(WindowIslandChatManager.MaxChatSites, settings.ChatSites.Count);

        // 7th should be rejected
        bool added7th = WindowIslandChatManager.AddChatSite(settings, "Site 7", "https://site7.com");
        Assert.False(added7th);
        Assert.Equal(6, settings.ChatSites.Count);
    }

    [Fact]
    public void UpdateChatSite_UpdatesNameAndUrlSuccessfully()
    {
        var settings = new WindowIslandSettings();
        // Add a custom 4th site
        Assert.True(WindowIslandChatManager.AddChatSite(settings, "CustomBot", "https://custom.ai"));
        int customIndex = settings.ChatSites.Count - 1;

        // Update the site
        bool updated = WindowIslandChatManager.UpdateChatSite(settings, customIndex, "Perplexity AI", "https://perplexity.ai");
        Assert.True(updated);
        Assert.Equal("Perplexity AI", settings.ChatSites[customIndex].Name);
        Assert.Equal("https://perplexity.ai/", settings.ChatSites[customIndex].Url);
    }

    [Fact]
    public void UpdateChatSite_RejectsInvalidUrlOrEmptyName()
    {
        var settings = new WindowIslandSettings();
        Assert.False(WindowIslandChatManager.UpdateChatSite(settings, 0, "", "https://valid.com"));
        Assert.False(WindowIslandChatManager.UpdateChatSite(settings, 0, "   ", "https://valid.com"));
        Assert.False(WindowIslandChatManager.UpdateChatSite(settings, 0, "Name", "not_a_url"));
        Assert.False(WindowIslandChatManager.UpdateChatSite(settings, 0, "Name", "ftp://unsupported.com"));
    }

    [Fact]
    public void UpdateChatSite_RejectsOutOfBoundsIndex()
    {
        var settings = new WindowIslandSettings();
        Assert.False(WindowIslandChatManager.UpdateChatSite(settings, -1, "Name", "https://valid.com"));
        Assert.False(WindowIslandChatManager.UpdateChatSite(settings, 99, "Name", "https://valid.com"));
    }

    [Fact]
    public void RemoveChatSite_RemovesSiteSuccessfully()
    {
        var settings = new WindowIslandSettings();
        int countBefore = settings.ChatSites.Count;

        bool removed = WindowIslandChatManager.RemoveChatSite(settings, 1);
        Assert.True(removed);
        Assert.Equal(countBefore - 1, settings.ChatSites.Count);
    }

    [Fact]
    public void RemoveChatSite_PreservesAtLeastOneSite()
    {
        var settings = new WindowIslandSettings
        {
            ChatSites = new() { new ChatSite("Solo", "https://solo.com") }
        };

        bool removed = WindowIslandChatManager.RemoveChatSite(settings, 0);
        Assert.False(removed);
        Assert.Single(settings.ChatSites);
    }

    [Fact]
    public void RemoveChatSite_AdjustsActiveIndexIfOutOfBounds()
    {
        var settings = new WindowIslandSettings
        {
            ActiveChatIndex = 2
        };

        WindowIslandChatManager.RemoveChatSite(settings, 2);
        Assert.Equal(1, settings.ActiveChatIndex);
    }

    // ── Active Site Resolution ────────────────────────────────────────────────

    [Fact]
    public void GetActiveSite_ReturnsCorrectSite()
    {
        var settings = new WindowIslandSettings { ActiveChatIndex = 1 };
        var active = WindowIslandChatManager.GetActiveSite(settings);
        Assert.Equal("DeepSeek", active.Name);
    }

    [Fact]
    public void GetActiveSite_ClampsNegativeIndex()
    {
        var settings = new WindowIslandSettings { ActiveChatIndex = -5 };
        var active = WindowIslandChatManager.GetActiveSite(settings);
        Assert.Equal("Gemini", active.Name);
        Assert.Equal(0, settings.ActiveChatIndex);
    }

    [Fact]
    public void GetActiveSite_ClampsExcessiveIndex()
    {
        var settings = new WindowIslandSettings { ActiveChatIndex = 99 };
        var active = WindowIslandChatManager.GetActiveSite(settings);
        Assert.Equal("ChatGPT", active.Name);
        Assert.Equal(settings.ChatSites.Count - 1, settings.ActiveChatIndex);
    }

    // ── Idle Timeout Clamping ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(100, 60)]
    public void ClampIdleMinutes_ClampsToRange(int input, int expected)
    {
        Assert.Equal(expected, WindowIslandChatManager.ClampIdleMinutes(input));
    }

    // ── Settings Cloning ──────────────────────────────────────────────────────

    [Fact]
    public void WindowIslandSettings_Clone_DuplicatesChatSitesIndependently()
    {
        var s = new WindowIslandSettings
        {
            ChatIdleMinutes = 10,
            ActiveChatIndex = 1
        };

        var clone = s.Clone();
        Assert.Equal(10, clone.ChatIdleMinutes);
        Assert.Equal(1, clone.ActiveChatIndex);
        Assert.Equal(s.ChatSites.Count, clone.ChatSites.Count);

        // Modifying clone's site list shouldn't affect original
        clone.ChatSites.Add(new ChatSite("Perplexity", "https://perplexity.ai"));
        Assert.NotEqual(s.ChatSites.Count, clone.ChatSites.Count);
    }

    // ── User Data Folder ──────────────────────────────────────────────────────

    [Fact]
    public void GetUserDataFolder_ResolvesCorrectPath()
    {
        string path = WindowIslandChatManager.GetUserDataFolder();
        Assert.NotEmpty(path);
        Assert.EndsWith(Path.Combine("AppLock", "island", "webview"), path, StringComparison.OrdinalIgnoreCase);
    }
}
