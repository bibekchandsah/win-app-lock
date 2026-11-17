using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker;

/// <summary>
/// Runs providers with per-provider timeouts, a small concurrency limit and a short result cache.
/// A slow or failing provider never blocks the page: whatever arrived is shown, the rest gets a
/// plain-language "Not available" reason.
/// </summary>
public sealed class InfoCollector
{
    private readonly List<IInfoProvider> _providers;
    private readonly Dictionary<string, CachedSection> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly IClock _clock;
    private readonly SystemContext _context;

    public InfoCollector(IEnumerable<IInfoProvider> providers, SystemContext? context = null, IClock? clock = null)
    {
        _providers = providers.ToList();
        _context = context ?? SystemContext.Create();
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>The default provider set for milestone 1 (OS, CPU, memory, GPU, storage basics).</summary>
    public static InfoCollector CreateDefault(SystemContext? context = null, IClock? clock = null)
        => new(new IInfoProvider[]
        {
            new Providers.OsProvider(),
            new Providers.FirmwareProvider(),
            new Providers.CpuProvider(),
            new Providers.MemoryProvider(),
            new Providers.GpuProvider(),
            new Providers.DisplayProvider(),
            new Providers.StorageProvider(),
            new Providers.StorageHealthProvider()
        }, context, clock);

    public IReadOnlyList<IInfoProvider> Providers => _providers;

    public SystemContext Context => _context;

    public IInfoProvider? GetProvider(string id)
        => _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A cached section, when it is still fresh. Used by the UI to paint instantly.</summary>
    public InfoSection? TryGetCached(string providerId)
    {
        if (!_cache.TryGetValue(providerId, out var cached)) return null;
        if (cached.ExpiresUtc <= _clock.UtcNow) return null;
        return cached.Section;
    }

    public void ClearCache() => _cache.Clear();

    public async Task<InfoSection> CollectAsync(
        string providerId,
        CollectOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var provider = GetProvider(providerId);
        if (provider == null)
        {
            return new InfoSection
            {
                Id = providerId,
                Title = providerId,
                Category = providerId,
                Error = "No provider for this category",
                CollectedAt = DateTime.Now
            };
        }

        var cached = TryGetCached(providerId);
        if (cached != null)
        {
            progress?.Report(1);
            return cached;
        }

        if (!provider.IsSupported(_context))
        {
            return new InfoSection
            {
                Id = provider.Id,
                Title = provider.Category,
                Category = provider.Category,
                Error = "Not supported on this PC",
                CollectedAt = DateTime.Now
            };
        }

        var section = await RunProviderAsync(provider, options, ct).ConfigureAwait(false);

        if (section.Error == null && provider.CacheLifetime > TimeSpan.Zero)
        {
            _cache[provider.Id] = new CachedSection(section, _clock.UtcNow + provider.CacheLifetime);
        }

        progress?.Report(1);
        return section;
    }

    /// <summary>Collects several providers in parallel (at most four at a time).</summary>
    public async Task<Dictionary<string, InfoSection>> CollectManyAsync(
        IEnumerable<string> providerIds,
        CollectOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var ids = providerIds.Where(id => GetProvider(id) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new Dictionary<string, InfoSection>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return results;

        int completed = 0;
        var tasks = ids.Select(async id =>
        {
            var section = await CollectAsync(id, options, null, ct).ConfigureAwait(false);
            lock (results)
            {
                results[id] = section;
            }
            progress?.Report((double)Interlocked.Increment(ref completed) / ids.Count);
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelling keeps whatever already arrived.
        }

        return results;
    }

    private async Task<InfoSection> RunProviderAsync(IInfoProvider provider, CollectOptions options, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var progress = new Progress<double>();
            var work = Task.Run(async () =>
            {
                try
                {
                    return await provider.CollectAsync(options, progress, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return Failure(provider, Describe(ex));
                }
            }, timeoutCts.Token);

            var timeout = Task.Delay(provider.Timeout, timeoutCts.Token);
            var winner = await Task.WhenAny(work, timeout).ConfigureAwait(false);

            if (winner != work)
            {
                timeoutCts.Cancel();
                return Failure(provider, $"Timed out after {provider.Timeout.TotalSeconds:0}s");
            }

            var section = await work.ConfigureAwait(false);
            if (string.IsNullOrEmpty(section.Id)) section.Id = provider.Id;
            if (string.IsNullOrEmpty(section.Category)) section.Category = provider.Category;
            section.CollectedAt = DateTime.Now;
            return section;
        }
        catch (OperationCanceledException)
        {
            return Failure(provider, "Cancelled");
        }
        catch (Exception ex)
        {
            return Failure(provider, Describe(ex));
        }
        finally
        {
            _gate.Release();
            started.Stop();
        }
    }

    private static InfoSection Failure(IInfoProvider provider, string message) => new()
    {
        Id = provider.Id,
        Title = provider.Category,
        Category = provider.Category,
        Error = message,
        CollectedAt = DateTime.Now
    };

    private static string Describe(Exception ex)
    {
        var text = ex.Message?.Trim();
        return string.IsNullOrEmpty(text) ? ex.GetType().Name : text;
    }

    private readonly record struct CachedSection(InfoSection Section, DateTime ExpiresUtc);
}
