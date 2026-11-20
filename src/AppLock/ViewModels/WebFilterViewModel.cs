using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.WebFilter;
using AppLock.WebFilter.Engine;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Header, status, "Check a website" and Categories tab state for the Web Filter page.
/// </summary>
public partial class WebFilterViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly WebFilterListService _lists;

    public WebFilterActivityViewModel Activity { get; }

    [ObservableProperty] private bool _isVpnOrProxyActive;
    [ObservableProperty] private string _vpnOrProxyMessage = "A VPN or proxy is active. Web Filter may not apply to traffic that goes through it.";
    [ObservableProperty] private bool _isDomainJoined;

    public WebFilterViewModel(AppSettings settings, SettingsStore settingsStore, WebFilterListService lists, WebFilterLogService? logService = null)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _lists = lists;
        Activity = new WebFilterActivityViewModel(settings, logService ?? ServiceContainer.WebFilterLogService);

        _isDomainJoined = BrowserPolicyService.IsDomainJoined();
        _isVpnOrProxyActive = ServiceContainer.VpnAndProxyDetector.IsVpnOrProxyActive;
        if (!string.IsNullOrEmpty(ServiceContainer.VpnAndProxyDetector.StatusSummary))
        {
            _vpnOrProxyMessage = ServiceContainer.VpnAndProxyDetector.StatusSummary;
        }

        ServiceContainer.VpnAndProxyDetector.StatusChanged += (s, active) =>
        {
            IsVpnOrProxyActive = active;
            if (!string.IsNullOrEmpty(ServiceContainer.VpnAndProxyDetector.StatusSummary))
            {
                VpnOrProxyMessage = ServiceContainer.VpnAndProxyDetector.StatusSummary;
            }
        };
    }

    public bool BlockOtherDns
    {
        get => Filter.Bypass.BlockOtherDns;
        set
        {
            if (Filter.Bypass.BlockOtherDns != value)
            {
                Filter.Bypass.BlockOtherDns = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
                _ = ServiceContainer.WebFilterService.UpdateBypassProtectionAsync();
            }
        }
    }

    public bool BlockKnownDoh
    {
        get => Filter.Bypass.BlockKnownDoh;
        set
        {
            if (Filter.Bypass.BlockKnownDoh != value)
            {
                Filter.Bypass.BlockKnownDoh = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
                _ = ServiceContainer.WebFilterService.UpdateBypassProtectionAsync();
            }
        }
    }

    public bool BrowserPolicies
    {
        get => Filter.Bypass.BrowserPolicies;
        set
        {
            if (Filter.Bypass.BrowserPolicies != value)
            {
                Filter.Bypass.BrowserPolicies = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
                _ = ServiceContainer.WebFilterService.UpdateBypassProtectionAsync();
            }
        }
    }

    public WebFilterSettings Filter => _settings.WebFilter;

    public bool IsEnabled => Filter.Enabled;

    // ── Mode (Local / Provider) ───────────────────────────────────────────────

    /// <summary>True when the filter is running in Filtered DNS Provider mode.</summary>
    public bool IsProviderMode => Filter.Mode == WebFilterMode.Provider;

    /// <summary>True when the filter is running in Local Filter mode (the default).</summary>
    public bool IsLocalMode => Filter.Mode == WebFilterMode.LocalFilter;

    /// <summary>Provider list built from the catalog.</summary>
    public IReadOnlyList<FilteredDnsProvider> Providers => _lists.Catalog.Providers;

    /// <summary>The currently selected provider (never null; falls back to first).</summary>
    public FilteredDnsProvider? SelectedProvider
    {
        get => _lists.Catalog.FindProvider(Filter.ProviderId);
        set
        {
            if (value == null) return;
            if (string.Equals(Filter.ProviderId, value.Id, StringComparison.OrdinalIgnoreCase)) return;
            Filter.ProviderId = value.Id;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Switches between Local filter and Provider mode.
    /// Requires authentication when the filter is on; returns false if auth was denied.
    /// </summary>
    public async Task<bool> SetModeAsync(WebFilterMode newMode, Func<Task<bool>> requestAuth)
    {
        if (Filter.Mode == newMode) return true;

        if (Filter.Enabled)
        {
            bool ok = await requestAuth().ConfigureAwait(true);
            if (!ok) return false;
        }

        Filter.Mode = newMode;
        _settingsStore.Save(_settings);

        if (Filter.Enabled)
        {
            await ServiceContainer.WebFilterService.StopAsync().ConfigureAwait(true);
            await ServiceContainer.WebFilterService.StartAsync().ConfigureAwait(true);
        }

        OnPropertyChanged(nameof(IsProviderMode));
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(StatusTitle));

        return true;
    }

    public async Task SetProviderAsync(string providerId)
    {
        if (string.Equals(Filter.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)) return;
        Filter.ProviderId = providerId;
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(StatusDetail));

        if (Filter.Enabled && IsProviderMode)
        {
            await ServiceContainer.WebFilterService.StopAsync().ConfigureAwait(true);
            await ServiceContainer.WebFilterService.StartAsync().ConfigureAwait(true);
        }
    }

    // ── Local filter settings ─────────────────────────────────────────────────

    public int UpstreamIndex
    {
        get => (int)Filter.Upstream;
        set
        {
            if ((int)Filter.Upstream != value)
            {
                Filter.Upstream = (UpstreamKind)value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomUpstream));
                if (Filter.Enabled && IsLocalMode)
                {
                    _ = Task.Run(async () =>
                    {
                        await ServiceContainer.WebFilterService.StopAsync();
                        await ServiceContainer.WebFilterService.StartAsync();
                    });
                }
            }
        }
    }

    public bool IsCustomUpstream => Filter.Upstream == UpstreamKind.Custom;

    public string CustomUpstream
    {
        get => Filter.CustomUpstream;
        set
        {
            if (Filter.CustomUpstream != value)
            {
                Filter.CustomUpstream = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    public bool CacheEnabled
    {
        get => Filter.CacheEnabled;
        set
        {
            if (Filter.CacheEnabled != value)
            {
                Filter.CacheEnabled = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    public int BlockResponseIndex
    {
        get => (int)Filter.BlockResponse;
        set
        {
            if ((int)Filter.BlockResponse != value)
            {
                Filter.BlockResponse = (BlockResponse)value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    public void ClearCache()
    {
        ServiceContainer.WebFilterService.ClearCache();
    }

    public async Task<string> TestUpstreamAsync()
    {
        try
        {
            var resolver = new UpstreamForwarder(new DnsUpstreamOptions
            {
                DohEndpoints = Filter.Upstream switch
                {
                    UpstreamKind.GoogleDoh => new[] { UpstreamForwarder.Google },
                    UpstreamKind.Quad9Doh => new[] { UpstreamForwarder.Quad9 },
                    UpstreamKind.Custom when Uri.TryCreate(Filter.CustomUpstream, UriKind.Absolute, out var uri) =>
                        new[] { new DohEndpoint("Custom", uri, null) },
                    _ => new[] { UpstreamForwarder.Cloudflare }
                },
                Timeout = TimeSpan.FromSeconds(3)
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            byte[] queryBytes = DnsPacket.BuildQuery("one.one.one.one", DnsPacket.TypeA);
            DnsPacket.TryParseQuery(queryBytes, out var q);
            var response = await resolver.ResolveAsync(q, queryBytes, cts.Token);
            if (response != null && response.Length >= DnsPacket.HeaderLength)
            {
                return "Upstream DNS responded successfully.";
            }
            return "No valid response from upstream DNS.";
        }
        catch (Exception ex)
        {
            return $"Upstream DNS test failed: {ex.Message}";
        }
    }

    public int EnabledCategoryCount => Filter.Categories.Values.Count(c => c.Enabled);

    public string StatusTitle => Filter.Enabled
        ? (Filter.IsPaused ? "Web Filter is paused" : "Web Filter is On")
        : "Web Filter is Off";

    public string StatusDetail
    {
        get
        {
            if (Filter.IsPaused && Filter.PausedUntilUtc is { } until)
            {
                return $"Paused until {until.ToLocalTime():HH:mm}.";
            }

            if (!Filter.Enabled)
            {
                return "No websites are blocked. The filter isn't set up yet.";
            }

            if (Filter.Mode == WebFilterMode.Provider)
            {
                var provider = _lists.Catalog.FindProvider(Filter.ProviderId);
                var name = provider?.Name ?? "Unknown provider";
                return $"Filtered DNS provider \u00b7 {name}";
            }

            var mode = "Local filter";
            var categories = EnabledCategoryCount == 1 ? "1 category" : $"{EnabledCategoryCount} categories";
            return $"{mode} \u00b7 {categories} \u00b7 {TotalDomains:N0} domains";
        }
    }

    // ── Pause ─────────────────────────────────────────────────────────────────

    public bool IsPaused => Filter.IsPaused;

    public string PauseLabel => Filter.IsPaused
        ? (Filter.PausedUntilUtc is { } u ? $"Resume (paused until {u.ToLocalTime():HH:mm})" : "Resume")
        : "Pause";

    /// <summary>Pauses filtering for the given number of minutes (or resumes if paused).</summary>
    public void TogglePause(int minutes = 30)
    {
        if (Filter.IsPaused || minutes <= 0)
        {
            Filter.PausedUntilUtc = null;
        }
        else
        {
            Filter.PausedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
        }
        _settingsStore.Save(_settings);
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
    }

    // ── Categories ────────────────────────────────────────────────────────────

    private ObservableCollection<CategoryCardViewModel> _categoryCards = new();
    public ObservableCollection<CategoryCardViewModel> CategoryCards => _categoryCards;

    /// <summary>
    /// Seeds the WebFilterSettings.Categories dictionary from the catalog defaults
    /// (run once on first enable; idempotent thereafter).
    /// </summary>
    public void SeedCategoryDefaults()
    {
        var catalog = _lists.Catalog;
        bool changed = false;

        foreach (var cat in catalog.Categories)
        {
            if (!Filter.Categories.ContainsKey(cat.Id))
            {
                Filter.Categories[cat.Id] = new CategoryRule { Enabled = cat.DefaultEnabled };
                changed = true;
            }
        }

        if (!Filter.DefaultsSeeded)
        {
            Filter.DefaultsSeeded = true;
            changed = true;
        }

        if (changed)
        {
            _settingsStore.Save(_settings);
        }
    }

    /// <summary>Builds the category card view-models from the loaded catalog.</summary>
    public void BuildCategoryCards()
    {
        var catalog = _lists.Catalog;
        _categoryCards.Clear();

        foreach (var cat in catalog.Categories)
        {
            int count = _lists.GetCategoryDomainCount(cat.Id);
            var card = new CategoryCardViewModel(_settings, _settingsStore, cat, count);

            // Mark unavailable if no list loaded and no bundled file
            if (count == 0 && cat.Sources.TrueForAll(s => string.IsNullOrEmpty(s.Bundled)))
            {
                card.MarkUnavailable("List not available");
            }

            _categoryCards.Add(card);
        }
    }

    /// <summary>Updates domain counts after list initialization completes.</summary>
    public void RefreshCategoryDomainCounts()
    {
        foreach (var card in _categoryCards)
        {
            card.SetDomainCount(_lists.GetCategoryDomainCount(card.Id));
        }
    }

    // ── Master switch ─────────────────────────────────────────────────────────

    /// <summary>Turns the master switch on or off and persists it.</summary>
    public void SetEnabled(bool enabled)
    {
        if (Filter.Enabled == enabled) return;

        Filter.Enabled = enabled;
        _settingsStore.Save(_settings);

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
    }

    /// <summary>Re-reads the status text after settings changed elsewhere on the page.</summary>
    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(EnabledCategoryCount));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseLabel));
    }

    // ── "Check a website" tool ────────────────────────────────────────────────

    [ObservableProperty]
    private string _checkInput = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isListReady;

    [ObservableProperty]
    private string _listStatus = "Block lists are loading\u2026";

    [ObservableProperty]
    private int _availableCategories;

    [ObservableProperty]
    private int _totalDomains;

    public bool CanCheck => IsListReady && !IsChecking && !string.IsNullOrWhiteSpace(CheckInput);

    partial void OnCheckInputChanged(string value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnIsListReadyChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnTotalDomainsChanged(int value) => OnPropertyChanged(nameof(StatusDetail));

    /// <summary>Loads and compiles the bundled lists the first time the page opens.</summary>
    public async Task InitializeListsAsync(CancellationToken cancellationToken = default)
    {
        if (IsListReady) return;

        try
        {
            await _lists.InitializeAsync(cancellationToken).ConfigureAwait(true);

            AvailableCategories = _lists.AvailableCategoryCount;
            TotalDomains = _lists.TotalDomainCount;

            if (_lists.IsReady)
            {
                IsListReady = true;
                ListStatus = $"{AvailableCategories} of {_lists.CategoryCount} categories ready \u00b7 {TotalDomains:N0} domains";
            }
            else
            {
                ListStatus = _lists.LastError ?? "Block lists could not be prepared.";
            }

            if (!string.IsNullOrWhiteSpace(_lists.LastError) && IsListReady)
            {
                ListStatus += " \u00b7 " + _lists.LastError;
            }

            // Build/refresh category cards now that counts are available
            BuildCategoryCards();
            RefreshCategoryDomainCounts();
            BuildServicesAndSites();
        }
        catch (OperationCanceledException)
        {
            ListStatus = "Loading block lists was cancelled.";
        }
        catch (Exception ex)
        {
            ListStatus = "Block lists could not be loaded: " + ex.Message;
        }

        OnPropertyChanged(nameof(StatusDetail));
    }

    // ── Sites Tab: Services, Always Block, Always Allow ───────────────────────

    private readonly ObservableCollection<ServiceCardViewModel> _serviceItems = new();
    public ObservableCollection<ServiceCardViewModel> ServiceItems => _serviceItems;

    private readonly ObservableCollection<DomainItemViewModel> _alwaysBlockItems = new();
    public ObservableCollection<DomainItemViewModel> AlwaysBlockItems => _alwaysBlockItems;

    private readonly ObservableCollection<DomainItemViewModel> _alwaysAllowItems = new();
    public ObservableCollection<DomainItemViewModel> AlwaysAllowItems => _alwaysAllowItems;

    private readonly ObservableCollection<TempAllowItemViewModel> _activeTempAllows = new();
    public ObservableCollection<TempAllowItemViewModel> ActiveTempAllows => _activeTempAllows;

    public void RefreshTempAllows()
    {
        Filter.TempAllows.RemoveAll(t => t.ExpiresUtc <= DateTime.UtcNow);
        _activeTempAllows.Clear();
        foreach (var t in Filter.TempAllows.OrderBy(t => t.ExpiresUtc))
        {
            _activeTempAllows.Add(new TempAllowItemViewModel(t));
        }
        OnPropertyChanged(nameof(TempAllowsCountLabel));
    }

    public string TempAllowsCountLabel => $"{Filter.TempAllows.Count} domains";

    public void RevokeTempAllow(TempAllowItemViewModel item)
    {
        Filter.TempAllows.Remove(item.Entry);
        _activeTempAllows.Remove(item);
        SaveSettings();
        OnPropertyChanged(nameof(TempAllowsCountLabel));
    }

    public void BuildServicesAndSites()
    {
        var catalog = _lists.Catalog;
        _serviceItems.Clear();

        var serviceMap = new Dictionary<string, ServiceCardViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var svc in catalog.Services)
        {
            var categoryName = catalog.Find(svc.Category)?.Name ?? svc.Category;
            var vm = new ServiceCardViewModel(_settings, _settingsStore, svc, categoryName);
            _serviceItems.Add(vm);
            serviceMap[svc.Id] = vm;
        }

        // Link social media sub-services to the social CategoryCardViewModel
        var socialCard = _categoryCards.FirstOrDefault(c => c.Id.Equals("social", StringComparison.OrdinalIgnoreCase));
        if (socialCard != null)
        {
            socialCard.SubServices.Clear();
            foreach (var svc in catalog.Services.Where(s => s.Category.Equals("social", StringComparison.OrdinalIgnoreCase)))
            {
                if (serviceMap.TryGetValue(svc.Id, out var vm))
                {
                    socialCard.SubServices.Add(vm);
                }
            }
        }

        // Populate Always Block & Always Allow
        _alwaysBlockItems.Clear();
        foreach (var entry in Filter.AlwaysBlock)
        {
            _alwaysBlockItems.Add(new DomainItemViewModel(entry, RemoveAlwaysBlock, SaveSettings));
        }

        _alwaysAllowItems.Clear();
        foreach (var entry in Filter.AlwaysAllow)
        {
            _alwaysAllowItems.Add(new DomainItemViewModel(entry, RemoveAlwaysAllow, SaveSettings));
        }

        RefreshTempAllows();

        OnPropertyChanged(nameof(AlwaysBlockCountLabel));
        OnPropertyChanged(nameof(AlwaysAllowCountLabel));
    }

    public string AlwaysBlockCountLabel => $"{Filter.AlwaysBlock.Count} domains";
    public string AlwaysAllowCountLabel => $"{Filter.AlwaysAllow.Count} domains";

    public bool AddAlwaysBlock(string input, string note, out string message)
    {
        message = string.Empty;
        if (!DomainName.TryExtractHost(input, out var host))
        {
            message = "Enter a valid domain name or website URL (e.g. example.com).";
            return false;
        }

        if (Filter.AlwaysBlock.Any(e => e.Domain.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            message = $"{host} is already in the Always Block list.";
            return false;
        }

        if (Filter.AlwaysBlock.Count >= 5000)
        {
            message = "Always Block list has reached the maximum of 5,000 entries.";
            return false;
        }

        var entry = new DomainEntry
        {
            Domain = host,
            Note = note?.Trim() ?? string.Empty,
            AddedUtc = DateTime.UtcNow
        };
        Filter.AlwaysBlock.Add(entry);
        SaveSettings();

        _alwaysBlockItems.Insert(0, new DomainItemViewModel(entry, RemoveAlwaysBlock, SaveSettings));
        OnPropertyChanged(nameof(AlwaysBlockCountLabel));
        return true;
    }

    public bool AddAlwaysAllow(string input, string note, out string message, out bool isNeverBlockInfo)
    {
        message = string.Empty;
        isNeverBlockInfo = false;

        if (!DomainName.TryExtractHost(input, out var host))
        {
            message = "Enter a valid domain name or website URL (e.g. example.com).";
            return false;
        }

        // Check if matched by Never-block list
        var neverBlockSuffixes = DomainName.EnumerateSuffixes(host);
        if (_lists.Catalog.NeverBlock.Any(nb => neverBlockSuffixes.Contains(nb, StringComparer.OrdinalIgnoreCase)))
        {
            isNeverBlockInfo = true;
            message = $"{host} is on the built-in system Never-block list (Windows essentials, update, certificates) and is always allowed automatically.";
            return false;
        }

        if (Filter.AlwaysAllow.Any(e => e.Domain.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            message = $"{host} is already in the Always Allow list.";
            return false;
        }

        if (Filter.AlwaysAllow.Count >= 5000)
        {
            message = "Always Allow list has reached the maximum of 5,000 entries.";
            return false;
        }

        var entry = new DomainEntry
        {
            Domain = host,
            Note = note?.Trim() ?? string.Empty,
            AddedUtc = DateTime.UtcNow
        };
        Filter.AlwaysAllow.Add(entry);
        SaveSettings();

        _alwaysAllowItems.Insert(0, new DomainItemViewModel(entry, RemoveAlwaysAllow, SaveSettings));
        OnPropertyChanged(nameof(AlwaysAllowCountLabel));
        return true;
    }

    public bool AddTempAllow(string domainOrUrl, int minutes, out string host, out DateTime expiresUtc)
    {
        expiresUtc = DateTime.UtcNow.AddMinutes(minutes);
        host = string.Empty;

        if (!DomainName.TryExtractHost(domainOrUrl, out host))
        {
            return false;
        }

        var targetHost = host;
        Filter.TempAllows.RemoveAll(t => t.ExpiresUtc <= DateTime.UtcNow);
        var existing = Filter.TempAllows.FirstOrDefault(t => t.Domain.Equals(targetHost, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.ExpiresUtc = expiresUtc;
        }
        else
        {
            Filter.TempAllows.Add(new TempAllow
            {
                Domain = targetHost,
                ExpiresUtc = expiresUtc
            });
        }

        SaveSettings();
        RefreshTempAllows();
        return true;
    }

    public void RemoveAlwaysBlock(DomainItemViewModel item)
    {
        Filter.AlwaysBlock.Remove(item.Entry);
        _alwaysBlockItems.Remove(item);
        SaveSettings();
        OnPropertyChanged(nameof(AlwaysBlockCountLabel));
    }

    public void RemoveAlwaysAllow(DomainItemViewModel item)
    {
        Filter.AlwaysAllow.Remove(item.Entry);
        _alwaysAllowItems.Remove(item);
        SaveSettings();
        OnPropertyChanged(nameof(AlwaysAllowCountLabel));
    }

    public int ImportAlwaysBlock(string text)
    {
        int added = 0;
        var existing = new HashSet<string>(Filter.AlwaysBlock.Select(e => e.Domain), StringComparer.OrdinalIgnoreCase);

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            if (DomainName.TryExtractHost(trimmed, out var host) && !existing.Contains(host))
            {
                if (Filter.AlwaysBlock.Count >= 5000) break;
                existing.Add(host);
                var entry = new DomainEntry { Domain = host, AddedUtc = DateTime.UtcNow };
                Filter.AlwaysBlock.Add(entry);
                _alwaysBlockItems.Insert(0, new DomainItemViewModel(entry, RemoveAlwaysBlock, SaveSettings));
                added++;
            }
        }

        if (added > 0)
        {
            SaveSettings();
            OnPropertyChanged(nameof(AlwaysBlockCountLabel));
        }
        return added;
    }

    public string ExportAlwaysBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AppLock Web Filter - Always Block List");
        sb.AppendLine($"# Exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var entry in Filter.AlwaysBlock)
        {
            if (!string.IsNullOrWhiteSpace(entry.Note))
            {
                sb.AppendLine($"{entry.Domain} # {entry.Note}");
            }
            else
            {
                sb.AppendLine(entry.Domain);
            }
        }
        return sb.ToString();
    }

    public int ImportAlwaysAllow(string text)
    {
        int added = 0;
        var existing = new HashSet<string>(Filter.AlwaysAllow.Select(e => e.Domain), StringComparer.OrdinalIgnoreCase);

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            if (DomainName.TryExtractHost(trimmed, out var host) && !existing.Contains(host))
            {
                if (Filter.AlwaysAllow.Count >= 5000) break;
                existing.Add(host);
                var entry = new DomainEntry { Domain = host, AddedUtc = DateTime.UtcNow };
                Filter.AlwaysAllow.Add(entry);
                _alwaysAllowItems.Insert(0, new DomainItemViewModel(entry, RemoveAlwaysAllow, SaveSettings));
                added++;
            }
        }

        if (added > 0)
        {
            SaveSettings();
            OnPropertyChanged(nameof(AlwaysAllowCountLabel));
        }
        return added;
    }

    public string ExportAlwaysAllow()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AppLock Web Filter - Always Allow List");
        sb.AppendLine($"# Exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var entry in Filter.AlwaysAllow)
        {
            if (!string.IsNullOrWhiteSpace(entry.Note))
            {
                sb.AppendLine($"{entry.Domain} # {entry.Note}");
            }
            else
            {
                sb.AppendLine(entry.Domain);
            }
        }
        return sb.ToString();
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
    }

    /// <summary>Runs the check tool. Returns null while another check is in progress.</summary>
    public async Task<WebFilterCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsChecking) return null;

        IsChecking = true;
        try
        {
            // In Provider mode: do a live DNS lookup through the provider's IPs
            if (Filter.Mode == WebFilterMode.Provider)
            {
                return await CheckProviderModeAsync(CheckInput, cancellationToken).ConfigureAwait(true);
            }
            return await _lists.CheckAsync(CheckInput, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task<WebFilterCheckResult> CheckProviderModeAsync(string input, CancellationToken cancellationToken)
    {
        if (!AppLock.WebFilter.Engine.DomainName.TryExtractHost(input, out var host))
        {
            return new WebFilterCheckResult(input, string.Empty, false, false, null, null, null, null);
        }

        var provider = _lists.Catalog.FindProvider(Filter.ProviderId);
        if (provider == null || provider.Ipv4.Count == 0)
        {
            return new WebFilterCheckResult(input, host, true, false, "No provider configured", null, null, null);
        }

        try
        {
            // Send a DNS query directly to the provider's first IPv4 server
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var udp = new System.Net.Sockets.UdpClient();
            udp.Client.ReceiveTimeout = 5000;
            udp.Client.SendTimeout = 5000;

            var target = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(provider.Ipv4[0]), 53);
            byte[] query = AppLock.WebFilter.Engine.DnsPacket.BuildQuery(host, AppLock.WebFilter.Engine.DnsPacket.TypeA);

            await udp.SendAsync(query, query.Length, target).ConfigureAwait(false);
            var received = await udp.ReceiveAsync(cts.Token).AsTask().ConfigureAwait(false);

            // Parse out the A record IPs
            bool blocked = IsBlockedByProvider(received.Buffer, provider);
            string reason = blocked ? $"Blocked by {provider.Name}" : $"Allowed by {provider.Name}";

            return new WebFilterCheckResult(input, host, true, blocked, reason, provider.Name, null, null);
        }
        catch (OperationCanceledException)
        {
            return new WebFilterCheckResult(input, host, true, false, "Query timed out", null, null, null);
        }
        catch (Exception ex)
        {
            return new WebFilterCheckResult(input, host, true, false, $"Check failed: {ex.Message}", null, null, null);
        }
    }

    private static bool IsBlockedByProvider(byte[] response, FilteredDnsProvider provider)
    {
        if (response.Length < AppLock.WebFilter.Engine.DnsPacket.HeaderLength) return false;

        // NXDOMAIN (RCODE 3)
        int rcode = response[3] & 0x0F;
        if (rcode == 3) return true;

        int answerCount = AppLock.WebFilter.Engine.DnsPacket.ReadU16(response, 6);
        if (answerCount == 0) return true;  // empty answer = blocked

        // Parse answer records to extract A record IPs and compare to blockIps
        int offset = AppLock.WebFilter.Engine.DnsPacket.HeaderLength;
        int questionCount = AppLock.WebFilter.Engine.DnsPacket.ReadU16(response, 4);

        // Skip questions
        for (int i = 0; i < questionCount; i++)
        {
            while (offset < response.Length)
            {
                int len = response[offset];
                if (len == 0) { offset += 5; break; }
                if ((len & 0xC0) == 0xC0) { offset += 7; break; }
                offset += len + 1;
            }
        }

        for (int i = 0; i < answerCount && offset + 10 < response.Length; i++)
        {
            // Skip name
            if ((response[offset] & 0xC0) == 0xC0) offset += 2;
            else { while (offset < response.Length && response[offset] != 0) offset++; offset++; }

            if (offset + 10 > response.Length) break;

            ushort rtype = AppLock.WebFilter.Engine.DnsPacket.ReadU16(response, offset);
            int rdLength = AppLock.WebFilter.Engine.DnsPacket.ReadU16(response, offset + 8);
            offset += 10;

            if (rtype == 1 && rdLength == 4 && offset + 4 <= response.Length)  // A record
            {
                string ip = $"{response[offset]}.{response[offset+1]}.{response[offset+2]}.{response[offset+3]}";
                if (provider.BlockIps.Any(b => string.Equals(b, ip, StringComparison.OrdinalIgnoreCase) || b == "0.0.0.0"))
                {
                    return true;
                }
            }

            offset += rdLength;
        }

        return false;
    }
}
