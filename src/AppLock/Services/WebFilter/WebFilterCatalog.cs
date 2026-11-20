using System.Text.Json;

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

/// <summary>
/// The data-driven category catalogue. Everything a user sees about a category (name, description,
/// default, sources, licences) comes from the JSON asset, so a list source can be fixed without
/// rebuilding the code.
/// </summary>
public sealed class WebFilterCatalog
{
    public static readonly WebFilterCatalog Empty = new(Array.Empty<WebFilterCategorySource>(), Array.Empty<string>(), string.Empty, null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<WebFilterCategorySource> _categories;
    private readonly List<string> _neverBlock;

    private WebFilterCatalog(
        IEnumerable<WebFilterCategorySource> categories,
        IEnumerable<string> neverBlock,
        string assetsDirectory,
        string? loadError)
    {
        _categories = categories.ToList();
        _neverBlock = neverBlock.ToList();
        AssetsDirectory = assetsDirectory;
        LoadError = loadError;
    }

    public IReadOnlyList<WebFilterCategorySource> Categories => _categories;

    public IReadOnlyList<string> NeverBlock => _neverBlock;

    public string AssetsDirectory { get; }

    public string? LoadError { get; }

    public WebFilterCategorySource? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : _categories.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

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

            return new WebFilterCatalog(categories, neverBlock, assetsDirectory, null);
        }
        catch (Exception ex)
        {
            return new WebFilterCatalog(
                Array.Empty<WebFilterCategorySource>(),
                Array.Empty<string>(),
                assetsDirectory,
                "Block list catalogue could not be read: " + ex.Message);
        }
    }
}
