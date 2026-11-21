using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services.WebFilter;
using Xunit;

namespace AppLock.Tests.WebFilter;

public class WebFilterPolishAndChecklistTests
{
    [Fact]
    public void WebFilterSettings_LegacyAppSettingsJson_LoadsWithSafeDefaults()
    {
        // Simulate an older AppSettings JSON that completely lacks the WebFilter section
        string oldAppSettingsJson = """
        {
            "AutoLockTimeoutMinutes": 15,
            "IsFirstRun": false
        }
        """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var settings = JsonSerializer.Deserialize<AppSettings>(oldAppSettingsJson, options);

        Assert.NotNull(settings);
        Assert.NotNull(settings.WebFilter);
        Assert.False(settings.WebFilter.Enabled);
        Assert.Equal(WebFilterMode.LocalFilter, settings.WebFilter.Mode);
        Assert.Equal(UpstreamKind.CloudflareDoh, settings.WebFilter.Upstream);
        Assert.Equal(BlockResponse.NxDomain, settings.WebFilter.BlockResponse);
        Assert.False(settings.WebFilter.EnforceSafeSearch);
        Assert.Equal(YouTubeRestriction.Off, settings.WebFilter.YouTubeMode);
        Assert.True(settings.WebFilter.CacheEnabled);
        Assert.True(settings.WebFilter.StartWithAppLock);
        Assert.True(settings.WebFilter.LogBlocked);
        Assert.True(settings.WebFilter.AutoUpdateLists);

        // Anti-bypass defaults
        Assert.True(settings.WebFilter.Bypass.BlockOtherDns);
        Assert.True(settings.WebFilter.Bypass.BlockKnownDoh);
        Assert.True(settings.WebFilter.Bypass.BrowserPolicies);
    }

    [Fact]
    public void WebFilterCatalog_HasValidSourcesAndLicenses()
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "web-filter");
        var catalog = WebFilterCatalog.Load(assetsDir);

        Assert.NotNull(catalog);
        Assert.NotEmpty(catalog.Categories);
        Assert.NotEmpty(catalog.Providers);

        foreach (var cat in catalog.Categories)
        {
            Assert.False(string.IsNullOrWhiteSpace(cat.Id));
            Assert.False(string.IsNullOrWhiteSpace(cat.Name));
            Assert.False(string.IsNullOrWhiteSpace(cat.Limits));

            foreach (var src in cat.Sources)
            {
                Assert.False(string.IsNullOrWhiteSpace(src.Name));
                Assert.False(string.IsNullOrWhiteSpace(src.License));
            }
        }

        foreach (var prov in catalog.Providers)
        {
            Assert.False(string.IsNullOrWhiteSpace(prov.Id));
            Assert.False(string.IsNullOrWhiteSpace(prov.Name));
            Assert.False(string.IsNullOrWhiteSpace(prov.WebsiteUrl));
            Assert.True(prov.Ipv4.Count > 0 || prov.Ipv6.Count > 0);
        }
    }

    [Fact]
    public void WebFilterSettings_PauseState_ComputesCorrectly()
    {
        var settings = new WebFilterSettings { Enabled = true };
        Assert.False(settings.IsPaused);

        // Paused in future
        settings.PausedUntilUtc = DateTime.UtcNow.AddMinutes(15);
        Assert.True(settings.IsPaused);

        // Paused in past (expired)
        settings.PausedUntilUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.False(settings.IsPaused);
    }

    [Fact]
    public void WebFilterSettings_AlwaysBlockAllowPrecedence_Preserved()
    {
        var settings = new WebFilterSettings();
        settings.AlwaysBlock.Add(new DomainEntry { Domain = "bad.example.com" });
        settings.AlwaysAllow.Add(new DomainEntry { Domain = "good.example.com" });
        settings.TempAllows.Add(new TempAllow { Domain = "temp.example.com", ExpiresUtc = DateTime.UtcNow.AddMinutes(15) });

        string json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<WebFilterSettings>(json);

        Assert.NotNull(restored);
        Assert.Single(restored.AlwaysBlock);
        Assert.Equal("bad.example.com", restored.AlwaysBlock[0].Domain);
        Assert.Single(restored.AlwaysAllow);
        Assert.Equal("good.example.com", restored.AlwaysAllow[0].Domain);
        Assert.Single(restored.TempAllows);
        Assert.Equal("temp.example.com", restored.TempAllows[0].Domain);
    }

    [Fact]
    public void WebFilterSettings_AlwaysBlockAllow_ModelLogic_WorksCorrectly()
    {
        var settings = new WebFilterSettings();

        // Add domains to Always Block
        settings.AlwaysBlock.Add(new DomainEntry { Domain = "facebook.com", Note = "distracting", AddedUtc = DateTime.UtcNow });
        settings.AlwaysBlock.Add(new DomainEntry { Domain = "youtube.com", Note = "entertainment", AddedUtc = DateTime.UtcNow });
        settings.AlwaysBlock.Add(new DomainEntry { Domain = "instagram.com", Note = "social", AddedUtc = DateTime.UtcNow });

        Assert.Equal(3, settings.AlwaysBlock.Count);

        // Filter / Search simulation
        string search = "tube";
        var filtered = settings.AlwaysBlock.Where(d => d.Domain.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(filtered);
        Assert.Equal("youtube.com", filtered[0].Domain);

        // Clear all
        settings.AlwaysBlock.Clear();
        Assert.Empty(settings.AlwaysBlock);

        // Always Allow
        settings.AlwaysAllow.Add(new DomainEntry { Domain = "github.com", Note = "work", AddedUtc = DateTime.UtcNow });
        settings.AlwaysAllow.Add(new DomainEntry { Domain = "gitlab.com", Note = "code", AddedUtc = DateTime.UtcNow });

        Assert.Equal(2, settings.AlwaysAllow.Count);
        settings.AlwaysAllow.Clear();
        Assert.Empty(settings.AlwaysAllow);
    }
}
