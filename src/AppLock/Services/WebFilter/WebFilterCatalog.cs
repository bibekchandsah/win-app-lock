using System.Text.Json;
using AppLock.Models;

namespace AppLock.Services.WebFilter;

/// <summary>One category from <c>Assets/web-filter/sources.json</c>.</summary>
public sealed class WebFilterCategorySource
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>A short, plain statement of what this category does not catch.</summary>
    public string Limits { get; set; } = string.Empty;

    /// <summary>First-run default. The user can still turn the category off later.</summary>
    public bool DefaultEnabled { get; set; }

    public List<WebFilterListSource> Sources { get; set; } = new();
}

/// <summary>One downloadable/bundled list that belongs to a category.</summary>
public sealed class WebFilterListSource
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Informational only: the parser accepts hosts, plain and simple adblock lines.</summary>
    public string Format { get; set; } = "hosts";

    public string License { get; set; } = string.Empty;

    public string Attribution { get; set; } = string.Empty;

    /// <summary>File name under <c>Assets/web-filter/lists</c>, usually a .txt.gz snapshot.</summary>
    public string Bundled { get; set; } = string.Empty;
}

public sealed class WebFilterSourcesFile
{
    public int Version { get; set; } = 1;

    public List<WebFilterCategorySource> Categories { get; set; } = new();
}

public sealed class NeverBlockFile
{
    public int Version { get; set; } = 1;

    public List<string> Domains { get; set; } = new();
}

/// <summary>One service from <c>Assets/web-filter/services.json</c>.</summary>
public sealed class WebServiceSource
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public List<string> Domains { get; set; } = new();
}

public sealed class WebServicesFile
{
    public int Version { get; set; } = 1;

    public List<WebServiceSource> Services { get; set; } = new();
}

/// <summary>
/// The data-driven category and service catalogue.
/// </summary>
public sealed class WebFilterCatalog
{
    public static readonly IReadOnlyList<FilteredDnsProvider> DefaultProviders = new List<FilteredDnsProvider>
    {
        new()
        {
            Id = "cloudflare-family",
            Name = "Cloudflare Family",
            Description = "Blocks malware and adult content.",
            WebsiteUrl = "https://one.one.one.one/family/",
            Ipv4 = new() { "1.1.1.3", "1.0.0.3" },
            Ipv6 = new() { "2606:4700:4700::1113", "2606:4700:4700::1003" },
            BlockIps = new() { "0.0.0.0", "::" }
        },
        new()
        {
            Id = "cleanbrowsing-family",
            Name = "CleanBrowsing Family Filter",
            Description = "Blocks adult content, malicious domains, and enforces SafeSearch.",
            WebsiteUrl = "https://cleanbrowsing.org/filters/",
            Ipv4 = new() { "185.228.168.168", "185.228.169.168" },
            Ipv6 = new() { "2a0d:2a00:1::", "2a0d:2a00:2::" },
            BlockIps = new() { "185.228.168.10", "185.228.168.168", "0.0.0.0" }
        },
        new()
        {
            Id = "cleanbrowsing-adult",
            Name = "CleanBrowsing Adult Filter",
            Description = "Blocks adult content and malicious domains.",
            WebsiteUrl = "https://cleanbrowsing.org/filters/",
            Ipv4 = new() { "185.228.168.10", "185.228.169.11" },
            Ipv6 = new() { "2a0d:2a00:1::1", "2a0d:2a00:2::1" },
            BlockIps = new() { "185.228.168.10", "185.228.169.11", "0.0.0.0" }
        },
        new()
        {
            Id = "opendns-familyshield",
            Name = "OpenDNS FamilyShield",
            Description = "Pre-configured to block adult websites and phishing.",
            WebsiteUrl = "https://www.opendns.com/setupguide/#familyshield",
            Ipv4 = new() { "208.67.222.123", "208.67.220.123" },
            Ipv6 = new() { "2620:119:35::123", "2620:119:53::123" },
            BlockIps = new() { "146.112.61.106", "::ffff:146.112.61.106", "0.0.0.0" }
        },
        new()
        {
            Id = "adguard-family",
            Name = "AdGuard Family Protection",
            Description = "Blocks adult websites, ads, trackers, and enforces SafeSearch.",
            WebsiteUrl = "https://adguard-dns.io/en/public-dns.html",
            Ipv4 = new() { "94.140.14.15", "94.140.15.16" },
            Ipv6 = new() { "2a10:50c0::bad1:ff", "2a10:50c0::bad2:ff" },
            BlockIps = new() { "94.140.14.33", "94.140.15.16", "94.140.14.35", "0.0.0.0", "::" }
        }
    };

    public static readonly WebFilterCatalog Empty = new(
        Array.Empty<WebFilterCategorySource>(),
        Array.Empty<WebServiceSource>(),
        Array.Empty<string>(),
        DefaultProviders,
        Array.Empty<string>(),
        string.Empty,
        null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<WebFilterCategorySource> _categories;
    private readonly List<WebServiceSource> _services;
    private readonly List<string> _neverBlock;
    private readonly List<FilteredDnsProvider> _providers;
    private readonly List<string> _googleDomains;

    private WebFilterCatalog(
        IEnumerable<WebFilterCategorySource> categories,
        IEnumerable<WebServiceSource> services,
        IEnumerable<string> neverBlock,
        IEnumerable<FilteredDnsProvider> providers,
        IEnumerable<string> googleDomains,
        string assetsDirectory,
        string? loadError)
    {
        _categories = categories.ToList();
        _services = services.ToList();
        _neverBlock = neverBlock.ToList();
        _providers = providers.ToList();
        _googleDomains = googleDomains.ToList();
        AssetsDirectory = assetsDirectory;
        LoadError = loadError;
    }

    public IReadOnlyList<WebFilterCategorySource> Categories => _categories;

    public IReadOnlyList<WebServiceSource> Services => _services;

    public IReadOnlyList<string> NeverBlock => _neverBlock;

    public IReadOnlyList<FilteredDnsProvider> Providers => _providers;

    public IReadOnlyList<string> GoogleDomains => _googleDomains;

    public string AssetsDirectory { get; }

    public string? LoadError { get; }

    public WebFilterCategorySource? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : _categories.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public WebServiceSource? FindService(string? id)
        => string.IsNullOrEmpty(id) ? null : _services.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public FilteredDnsProvider? FindProvider(string? id)
        => string.IsNullOrEmpty(id) ? _providers.FirstOrDefault() : (_providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? _providers.FirstOrDefault());

    /// <summary>Full path of a bundled list, or null when the source has no bundle.</summary>
    public string? GetBundledPath(WebFilterListSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Bundled)) return null;

        var path = Path.Combine(AssetsDirectory, "lists", source.Bundled);
        return File.Exists(path) ? path : null;
    }

    public static WebFilterCatalog Load(string assetsDirectory)
    {
        string sourcesPath = Path.Combine(assetsDirectory, "sources.json");

        if (!File.Exists(sourcesPath))
        {
            return new WebFilterCatalog(
                Array.Empty<WebFilterCategorySource>(),
                Array.Empty<WebServiceSource>(),
                Array.Empty<string>(),
                DefaultProviders,
                Array.Empty<string>(),
                assetsDirectory,
                "Block list catalogue (sources.json) is missing");
        }

        try
        {
            var sources = JsonSerializer.Deserialize<WebFilterSourcesFile>(File.ReadAllText(sourcesPath), JsonOptions);
            var categories = sources?.Categories ?? new List<WebFilterCategorySource>();

            var neverBlock = new List<string>();
            string neverBlockPath = Path.Combine(assetsDirectory, "never-block.json");

            if (File.Exists(neverBlockPath))
            {
                var file = JsonSerializer.Deserialize<NeverBlockFile>(File.ReadAllText(neverBlockPath), JsonOptions);
                if (file?.Domains != null)
                {
                    neverBlock.AddRange(file.Domains);
                }
            }

            var services = new List<WebServiceSource>();
            string servicesPath = Path.Combine(assetsDirectory, "services.json");
            if (File.Exists(servicesPath))
            {
                var sf = JsonSerializer.Deserialize<WebServicesFile>(File.ReadAllText(servicesPath), JsonOptions);
                if (sf?.Services != null)
                {
                    services.AddRange(sf.Services);
                }
            }

            // Load providers.json (filtered DNS providers); fall back to built-in list on error
            var providers = LoadProviders(assetsDirectory);

            var googleDomains = new List<string>();
            string googleDomainsPath = Path.Combine(assetsDirectory, "google-domains.json");
            if (File.Exists(googleDomainsPath))
            {
                try
                {
                    var gList = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(googleDomainsPath), JsonOptions);
                    if (gList != null) googleDomains.AddRange(gList);
                }
                catch { }
            }

            return new WebFilterCatalog(categories, services, neverBlock, providers, googleDomains, assetsDirectory, null);
        }
        catch (Exception ex)
        {
            return new WebFilterCatalog(
                Array.Empty<WebFilterCategorySource>(),
                Array.Empty<WebServiceSource>(),
                Array.Empty<string>(),
                DefaultProviders,
                Array.Empty<string>(),
                assetsDirectory,
                "Block list catalogue could not be read: " + ex.Message);
        }
    }

    private static List<FilteredDnsProvider> LoadProviders(string assetsDirectory)
    {
        string providersPath = Path.Combine(assetsDirectory, "providers.json");
        if (!File.Exists(providersPath)) return new List<FilteredDnsProvider>(DefaultProviders);

        try
        {
            var loaded = JsonSerializer.Deserialize<List<FilteredDnsProvider>>(File.ReadAllText(providersPath), JsonOptions);
            if (loaded != null && loaded.Count > 0) return loaded;
        }
        catch { }

        return new List<FilteredDnsProvider>(DefaultProviders);
    }
}
