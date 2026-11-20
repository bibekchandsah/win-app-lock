using AppLock.Models;
using AppLock.Services.WebFilter;

namespace AppLock.Tests.WebFilter;

/// <summary>
/// Exercises the app-side pipeline with the real bundled catalogue and snapshots: compile, matcher
/// defaults, a category block, the never-block exemption and an invalid input. Nothing here touches
/// the system DNS configuration.
/// </summary>
public class WebFilterListServiceTests
{
    [Fact]
    public async Task CompilesTheBundledListsAndAnswersChecks()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "web-filter");
        Assert.True(File.Exists(Path.Combine(assets, "sources.json")), "sources.json was not copied to the test output");

        using var directory = new TempDirectory();
        var settings = new AppSettings();
        var saved = 0;

        using var service = new WebFilterListService(
            settings,
            _ => saved++,
            assetsDirectory: assets,
            dataDirectory: directory.Path);

        await service.InitializeAsync();

        Assert.True(service.IsReady, service.LastError);
        Assert.Null(service.LastError);
        Assert.True(saved > 0, "the catalogue defaults should be persisted once");
        Assert.True(service.AvailableCategoryCount >= 8);
        Assert.True(service.GetCategoryDomainCount("malware") > 1000);
        Assert.True(service.GetCategoryDomainCount("adult") > 1000);

        // Defaults: malware on, bypass tools on, adult/gambling/social off.
        Assert.True(settings.WebFilter.Categories["malware"].Enabled);
        Assert.True(settings.WebFilter.Categories["bypass"].Enabled);
        Assert.False(settings.WebFilter.Categories["adult"].Enabled);

        // A public DoH resolver is on the bypass list and is blocked by default.
        var bypass = service.Check("dns.google");
        Assert.True(bypass.IsBlocked);
        Assert.Equal("bypass", bypass.CategoryId);

        // The never-block list protects AppLock's own list download hosts.
        var never = service.Check("https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts");
        Assert.False(never.IsBlocked);
        Assert.Equal("raw.githubusercontent.com", never.Host);

        // Ordinary sites and malformed input.
        Assert.False(service.Check("www.example.com").IsBlocked);
        Assert.False(service.Check("not a url").IsValid);
    }
}
