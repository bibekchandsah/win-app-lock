using System.IO.Compression;
using System.Text.Json;
using AppLock.Models;
using AppLock.WebFilter.Engine;

namespace AppLock.Services.WebFilter;

/// <summary>The verdict shown by the "Check a website" tool.</summary>
public sealed record WebFilterCheckResult(
    string Input,
    string Host,
    bool IsValid,
    bool IsBlocked,
    string? Reason,
    string? SourceName,
    string? CategoryId,
    string? ServiceId);

/// <summary>Per-list bookkeeping for conditional downloads and the last known good count.</summary>
public sealed class WebFilterListMetadata
{
    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public DateTimeOffset? UpdatedUtc { get; set; }

    public int Count { get; set; }

    public string SourceName { get; set; } = string.Empty;
}

/// <summary>
/// Owns the compiled category snapshots for the app: it compiles the bundled snapshots on first
/// run, loads snapshots through <see cref="ListSnapshot"/>, builds the matcher from the saved
/// settings, and updates one list at a time with the fail-safe <see cref="ListUpdater"/>.
/// It never touches the system DNS configuration.
/// </summary>
public sealed class WebFilterListService : IDisposable
{
    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        WriteIndented = true
    };

    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _saveSettings;
    private readonly string _assetsDirectory;
    private readonly string _listsDirectory;
    private readonly Func<DateTime> _localClock;
    private readonly Func<DateTime> _utcClock;

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly object _matcherGate = new();
    private readonly Dictionary<string, ListSnapshot> _snapshots = new(StringComparer.Ordinal);

    private WebFilterCatalog _catalog = WebFilterCatalog.Empty;
    private DomainMatcher? _matcher;
    private bool _initialized;
    private bool _disposed;

    public WebFilterListService(
        AppSettings settings,
        Action<AppSettings> saveSettings,
        string? assetsDirectory = null,
        string? dataDirectory = null,
        Func<DateTime>? localClock = null,
        Func<DateTime>? utcClock = null)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _localClock = localClock ?? (() => DateTime.Now);
        _utcClock = utcClock ?? (() => DateTime.UtcNow);

        _assetsDirectory = assetsDirectory ??
            Path.Combine(AppContext.BaseDirectory, "Assets", "web-filter");

        string localData = dataDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "webfilter");

        _listsDirectory = Path.Combine(localData, "lists");
    }

    public bool IsReady { get; private set; }

    public string? LastError { get; private set; }

    public WebFilterCatalog Catalog => _catalog;

    public int CategoryCount => _catalog.Categories.Count;

    public int AvailableCategoryCount
    {
        get
        {
            int count = 0;
            lock (_snapshotGate)
            {
                foreach (var category in _catalog.Categories)
                {
                    for (int i = 0; i < category.Sources.Count; i++)
                    {
                        if (_snapshots.TryGetValue(Key(category.Id, i), out var snapshot) && snapshot.Count > 0)
                        {
                            count++;
                            break;
                        }
                    }
                }
            }
            return count;
        }
    }

    public int TotalDomainCount
    {
        get
        {
            int total = 0;
            lock (_snapshotGate)
            {
                foreach (var snapshot in _snapshots.Values)
                {
                    total += snapshot.Count;
                }
            }
            return total;
        }
    }

    /// <summary>Number of domains in one category (sum of its loaded lists).</summary>
    public int GetCategoryDomainCount(string categoryId)
    {
        var category = _catalog.Find(categoryId);
        if (category == null) return 0;

        int total = 0;
        lock (_snapshotGate)
        {
            for (int i = 0; i < category.Sources.Count; i++)
            {
                if (_snapshots.TryGetValue(Key(category.Id, i), out var snapshot))
                {
                    total += snapshot.Count;
                }
            }
        }
        return total;
    }

    public string ListsDirectory => _listsDirectory;

    /// <summary>
    /// Loads the catalogue and compiles any missing bundled snapshot. Safe to call repeatedly and
    /// from the UI thread: the work runs on the thread pool.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            await Task.Run(() => InitializeCore(cancellationToken), cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private void InitializeCore(CancellationToken cancellationToken)
    {
        try
        {
            _catalog = WebFilterCatalog.Load(_assetsDirectory);
            if (!string.IsNullOrEmpty(_catalog.LoadError))
            {
                LastError = _catalog.LoadError;
            }

            Directory.CreateDirectory(_listsDirectory);
            EnsureCategoryDefaults();

            var loaded = new Dictionary<string, ListSnapshot>(StringComparer.Ordinal);

            foreach (var category in _catalog.Categories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < category.Sources.Count; i++)
                {
                    try
                    {
                        var snapshot = LoadOrCompile(category, category.Sources[i], i);
                        if (snapshot != null)
                        {
                            loaded[Key(category.Id, i)] = snapshot;
                        }
                    }
                    catch (Exception ex)
                    {
                        LastError = $"{category.Name}: {ex.Message}";
                    }
                }
            }

            lock (_matcherGate)
            {
                lock (_snapshotGate)
                {
                    foreach (var snapshot in _snapshots.Values)
                    {
                        snapshot.Dispose();
                    }
                    _snapshots.Clear();

                    foreach (var pair in loaded)
                    {
                        _snapshots[pair.Key] = pair.Value;
                    }
                }

                RebuildMatcher();
            }

            IsReady = true;
        }
        catch (Exception ex)
        {
            LastError = "Block lists could not be prepared: " + ex.Message;
            IsReady = false;
        }
    }

    /// <summary>Answers "which rule decides this name" for the check tool.</summary>
    public WebFilterCheckResult Check(string input)
    {
        if (!DomainName.TryExtractHost(input, out var host))
        {
            return new WebFilterCheckResult(input, string.Empty, false, false, null, null, null, null);
        }

        DomainMatch match;

        lock (_matcherGate)
        {
            var matcher = _matcher;
            if (matcher == null)
            {
                return new WebFilterCheckResult(input, host, true, false, null, null, null, null);
            }

            match = matcher.Match(host);
        }

        return new WebFilterCheckResult(
            input,
            host,
            true,
            match.IsBlocked,
            match.Reason,
            match.SourceName,
            match.CategoryId,
            match.ServiceId);
    }

    public async Task<WebFilterCheckResult> CheckAsync(string input, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return Check(input);
    }

    /// <summary>Rebuilds the matcher after the settings changed (toggles, schedules, allow lists).</summary>
    public void Refresh()
    {
        lock (_matcherGate)
        {
            RebuildMatcher();
        }
    }

    /// <summary>The policy the DNS listener will use from milestone 4 on.</summary>
    public IDnsFilterPolicy CreateDnsPolicy()
    {
        var filter = _settings.WebFilter;
        return new DomainMatcher(BuildRuleSet(filter.Enabled && !filter.IsPaused, filter.IsPaused), _localClock, _utcClock);
    }

    private ListSnapshot? LoadOrCompile(WebFilterCategorySource category, WebFilterListSource source, int index)
    {
        string snapshotPath = SnapshotPath(category.Id, index);

        if (File.Exists(snapshotPath))
        {
            try
            {
                return ListSnapshot.Open(snapshotPath);
            }
            catch (Exception)
            {
                TryDelete(snapshotPath);
            }
        }

        string? bundled = _catalog.GetBundledPath(source);
        if (bundled == null)
        {
            return null;
        }

        using (var stream = OpenListFile(bundled))
        {
            var parsed = ListParser.Parse(stream);
            if (parsed.Domains.Count == 0)
            {
                return null;
            }

            ListCompiler.CompileToFile(parsed.Domains, snapshotPath);
            SaveMetadata(category.Id, index, new WebFilterListMetadata
            {
                Count = parsed.Domains.Count,
                SourceName = source.Name,
                UpdatedUtc = DateTimeOffset.UtcNow
            });
        }

        return ListSnapshot.Open(snapshotPath);
    }

    private void RebuildMatcher()
    {
        _matcher = new DomainMatcher(BuildRuleSet(enabled: true, paused: false), _localClock, _utcClock);
    }

    private DomainRuleSet BuildRuleSet(bool enabled, bool paused)
    {
        var filter = _settings.WebFilter;
        var categories = new List<CategoryRuleSet>();

        foreach (var definition in _catalog.Categories)
        {
            if (!filter.Categories.TryGetValue(definition.Id, out var rule) || rule == null || !rule.Enabled)
            {
                continue;
            }

            var lists = new List<CategoryListSource>();
            lock (_snapshotGate)
            {
                for (int i = 0; i < definition.Sources.Count; i++)
                {
                    if (_snapshots.TryGetValue(Key(definition.Id, i), out var snapshot) && snapshot.Count > 0)
                    {
                        lists.Add(new CategoryListSource(definition.Sources[i].Name, snapshot));
                    }
                }
            }

            if (lists.Count == 0) continue;

            categories.Add(new CategoryRuleSet
            {
                Id = definition.Id,
                DisplayName = definition.Name,
                Lists = lists,
                ActiveWindows = ToWindows(rule.ActiveBlocks)
            });
        }

        var services = new List<ServiceRuleSet>();
        foreach (var svc in _catalog.Services)
        {
            if (!filter.Services.TryGetValue(svc.Id, out var rule) || !rule.Enabled)
            {
                continue;
            }

            services.Add(new ServiceRuleSet
            {
                Id = svc.Id,
                DisplayName = svc.Name,
                CategoryDisplayName = _catalog.Find(svc.Category)?.Name ?? svc.Category,
                Domains = new SuffixSet(svc.Domains),
                ActiveWindows = ToWindows(rule.ActiveBlocks)
            });
        }

        return new DomainRuleSet
        {
            Enabled = enabled,
            Paused = paused,
            NeverBlock = new SuffixSet(_catalog.NeverBlock),
            TempAllows = filter.TempAllows
                .Where(t => !string.IsNullOrWhiteSpace(t.Domain))
                .Select(t => new TempAllowEntry(t.Domain, t.ExpiresUtc))
                .ToList(),
            AlwaysAllow = new SuffixSet(filter.AlwaysAllow.Select(e => e.Domain)),
            AlwaysBlock = new SuffixSet(filter.AlwaysBlock.Select(e => e.Domain)),
            Categories = categories,
            Services = services
        };
    }

    private static List<TimeWindow> ToWindows(List<TimeBlock>? blocks)
    {
        var windows = new List<TimeWindow>();
        if (blocks == null) return windows;

        foreach (var block in blocks)
        {
            if (block.Start == block.End) continue;
            if (block.Days is not { Length: 7 }) continue;

            windows.Add(new TimeWindow(block.Start, block.End, (bool[])block.Days.Clone()));
        }

        return windows;
    }

    /// <summary>
    /// Seeds the catalogue defaults exactly once. Malware &amp; phishing starts on (the spec's only
    /// recommended default) and the bypass-tools category starts on when any other category is on.
    /// Later user choices are never overwritten.
    /// </summary>
    private void EnsureCategoryDefaults()
    {
        var filter = _settings.WebFilter;
        if (filter.DefaultsSeeded) return;

        foreach (var category in _catalog.Categories)
        {
            if (!filter.Categories.ContainsKey(category.Id))
            {
                filter.Categories[category.Id] = new CategoryRule { Enabled = category.DefaultEnabled };
            }
        }

        bool anyOther = filter.Categories.Any(pair =>
            !pair.Key.Equals("bypass", StringComparison.OrdinalIgnoreCase) && pair.Value?.Enabled == true);

        if (anyOther && _catalog.Find("bypass") != null)
        {
            if (filter.Categories.TryGetValue("bypass", out var bypass) && bypass != null)
            {
                bypass.Enabled = true;
            }
            else
            {
                filter.Categories["bypass"] = new CategoryRule { Enabled = true };
            }
        }

        filter.DefaultsSeeded = true;
        _saveSettings(_settings);
    }

    /// <summary>
    /// Downloads one list and swaps it in only when the download looks sane. The old memory-mapped
    /// snapshot is released first so the atomic file move can succeed, and re-opened on failure.
    /// </summary>
    public async Task<ListUpdateResult> UpdateSourceAsync(string categoryId, int sourceIndex, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var category = _catalog.Find(categoryId);
        if (category == null) return ListUpdateResult.Failed("Unknown category");
        if (sourceIndex < 0 || sourceIndex >= category.Sources.Count) return ListUpdateResult.Failed("Unknown list");

        var source = category.Sources[sourceIndex];
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            return ListUpdateResult.Rejected("This list has no download URL");
        }

        string key = Key(category.Id, sourceIndex);
        string snapshotPath = SnapshotPath(category.Id, sourceIndex);
        var metadata = LoadMetadata(category.Id, sourceIndex);

        int previousCount = metadata?.Count ?? 0;
        lock (_snapshotGate)
        {
            if (previousCount == 0 && _snapshots.TryGetValue(key, out var existing))
            {
                previousCount = existing.Count;
            }
        }

        // Release the map before replacing the file, and rebuild the matcher without that list so
        // no in-flight lookup can touch the disposed snapshot.
        ListSnapshot? previous;
        lock (_matcherGate)
        {
            lock (_snapshotGate)
            {
                _snapshots.Remove(key, out previous);
            }

            RebuildMatcher();
            previous?.Dispose();
        }

        ListUpdateResult result;
        using (var updater = new ListUpdater())
        {
            result = await updater
                .UpdateAsync(source.Url, snapshotPath, previousCount, metadata?.ETag, metadata?.LastModified, cancellationToken)
                .ConfigureAwait(false);
        }

        ListSnapshot? updated = null;
        if (File.Exists(snapshotPath))
        {
            try
            {
                updated = ListSnapshot.Open(snapshotPath);
            }
            catch (Exception ex)
            {
                result = ListUpdateResult.Failed("The updated list could not be read: " + ex.Message);
            }
        }

        if (updated != null)
        {
            lock (_matcherGate)
            {
                lock (_snapshotGate)
                {
                    _snapshots[key] = updated;
                }

                RebuildMatcher();
            }

            if (result.Status == ListUpdateStatus.Updated)
            {
                SaveMetadata(category.Id, sourceIndex, new WebFilterListMetadata
                {
                    ETag = result.ETag,
                    LastModified = result.LastModified,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Count = result.DomainCount,
                    SourceName = source.Name
                });
            }
        }

        return result;
    }

    private string SnapshotPath(string categoryId, int sourceIndex)
        => Path.Combine(_listsDirectory, $"{categoryId}.{sourceIndex}.snapshot");

    private string MetadataPath(string categoryId, int sourceIndex)
        => Path.Combine(_listsDirectory, $"{categoryId}.{sourceIndex}.json");

    private static string Key(string categoryId, int sourceIndex) => $"{categoryId}/{sourceIndex}";

    private WebFilterListMetadata? LoadMetadata(string categoryId, int sourceIndex)
    {
        try
        {
            string path = MetadataPath(categoryId, sourceIndex);
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<WebFilterListMetadata>(File.ReadAllText(path), MetadataJson);
        }
        catch
        {
            return null;
        }
    }

    private void SaveMetadata(string categoryId, int sourceIndex, WebFilterListMetadata metadata)
    {
        try
        {
            File.WriteAllText(MetadataPath(categoryId, sourceIndex), JsonSerializer.Serialize(metadata, MetadataJson));
        }
        catch
        {
            // Bookkeeping only: a failed metadata write must not lose the good snapshot.
        }
    }

    private static Stream OpenListFile(string path)
    {
        var file = File.OpenRead(path);

        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        return new GZipStream(file, CompressionMode.Decompress);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // The next start will try again.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_matcherGate)
        {
            lock (_snapshotGate)
            {
                foreach (var snapshot in _snapshots.Values)
                {
                    snapshot.Dispose();
                }
                _snapshots.Clear();
            }
        }

        _initializeGate.Dispose();
    }
}
