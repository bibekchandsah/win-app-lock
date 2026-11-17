using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using AppLock.Services;
using AppLock.Services.PcChecker;
using AppLock.Services.PcChecker.Health;
using AppLock.ViewModels.PcChecker;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AppLock.Views;

/// <summary>
/// Others → PC Checker. Read-only: it only reads system information and never changes settings,
/// installs anything or contacts the network.
/// </summary>
public sealed partial class PcCheckerPage : Page
{
    private static PcCheckerSettings Settings => ServiceContainer.Settings.PcChecker;

    private readonly InfoCollector _collector = InfoCollector.CreateDefault();
    private readonly Dictionary<string, InfoSection> _sections = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private bool _suppressSelectionHandlers;
    private DispatcherTimer? _liveTimer;
    private bool _liveRefreshing;
    private (SnapshotService.SnapshotInfo? Before, SnapshotService.SnapshotInfo? After, List<SnapshotService.Change> Changes)? _comparison;

    public PcCheckerViewModel ViewModel { get; } = new();

    public PcCheckerPage()
    {
        foreach (var category in PcCategories.All)
        {
            ViewModel.Categories.Add(category);
        }

        InitializeComponent();

        _suppressSelectionHandlers = true;
        ViewModel.HideIdentifiers = Settings.HideIdentifiers;
        HideIdentifiersToggle.IsOn = Settings.HideIdentifiers;
        DetailLevelBox.SelectedIndex = Settings.DetailLevel switch
        {
            "Basic" => 0,
            "Everything" => 2,
            _ => 1
        };
        _suppressSelectionHandlers = false;

        Loaded += PcCheckerPage_Loaded;
        Unloaded += PcCheckerPage_Unloaded;
    }

    private DetailLevel CurrentLevel => DetailLevelBox.SelectedIndex switch
    {
        0 => DetailLevel.Basic,
        2 => DetailLevel.Everything,
        _ => DetailLevel.Detailed
    };

    private async void PcCheckerPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is PcCategory current)
        {
            ViewModel.SelectedCategory = current;
            await ShowCategoryAsync(current);
        }
        else if (ViewModel.Categories.Count > 0)
        {
            CategoryList.SelectedIndex = 0;
        }
    }

    private void PcCheckerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _liveTimer?.Stop();
    }

    /// <summary>
    /// The live performance category refreshes once a second, and only while it is on screen. Values are
    /// updated in place so the list does not jump or lose its scroll position.
    /// </summary>
    private async void LiveTimer_Tick(object? sender, object e)
    {
        if (_liveRefreshing) return;
        if (ViewModel.SelectedCategory?.Id != "performance") return;
        if (ViewModel.SearchText.Trim().Length > 0) return;

        _liveRefreshing = true;
        try
        {
            var options = new CollectOptions { Level = CurrentLevel, HideIdentifiers = ViewModel.HideIdentifiers };
            var section = await _collector.CollectAsync("performance", options);
            _sections["performance"] = section;

            foreach (var item in section.AllItems())
            {
                var row = ViewModel.Rows.FirstOrDefault(r => r.Key == item.Key);
                if (row == null) continue;

                row.Update(item.Value ?? InfoItem.ReasonText(item.Missing), item.Status);
            }

            UpdateStatusText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Live refresh failed: {ex.Message}");
        }
        finally
        {
            _liveRefreshing = false;
        }
    }

    // ── Toolbar ────────────────────────────────────────────────────────────────

    private async void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionHandlers) return;
        if (CategoryList.SelectedItem is not PcCategory category) return;

        ViewModel.SelectedCategory = category;
        await ShowCategoryAsync(category);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _collector.ClearCache();
        _sections.Clear();
        if (ViewModel.SelectedCategory is { } category)
        {
            await ShowCategoryAsync(category);
        }
    }

    private void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RefreshButton_Click(this, new RoutedEventArgs());
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text ?? string.Empty;

        if (ViewModel.SelectedCategory is { } category)
        {
            await ShowCategoryAsync(category);
        }
    }

    private async void DetailLevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionHandlers) return;

        Settings.DetailLevel = CurrentLevel switch
        {
            DetailLevel.Basic => "Basic",
            DetailLevel.Everything => "Everything",
            _ => "Detailed"
        };
        SaveSettings();

        if (ViewModel.SelectedCategory is { } category)
        {
            await ShowCategoryAsync(category);
        }
    }

    private async void HideIdentifiersToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionHandlers) return;

        ViewModel.HideIdentifiers = HideIdentifiersToggle.IsOn;
        Settings.HideIdentifiers = HideIdentifiersToggle.IsOn;
        SaveSettings();

        if (ViewModel.SelectedCategory is { } category)
        {
            await ShowCategoryAsync(category);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(OthersPage));
    }

    /// <summary>Relaunches AppLock elevated through the standard UAC prompt (shared helper).</summary>
    private async void RestartAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (ElevationHelper.TryRestartElevated())
        {
            await Task.Delay(400);
            Application.Current.Exit();
        }
    }

    // ── Content ────────────────────────────────────────────────────────────────

    private async Task ShowCategoryAsync(PcCategory category)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ViewModel.IsBusy = true;
        BusyRing.IsActive = true;

        try
        {
            var search = ViewModel.SearchText.Trim();

            if (search.Length > 0)
            {
                await ShowSearchResultsAsync(search, ct);
            }
            else if (category.Id == PcCategories.OverviewId)
            {
                await EnsureSectionsAsync(category.ProviderIds, ct);
                if (ct.IsCancellationRequested) return;
                BuildOverview();
            }
            else if (category.Id == "health")
            {
                await EnsureSectionsAsync(AllProviderIds(), ct);
                if (ct.IsCancellationRequested) return;
                BuildHealth();
            }
            else if (category.Id == "reports")
            {
                BuildReports();
            }
            else if (!category.HasProviders)
            {
                ViewModel.Tiles.Clear();
                ViewModel.Rows.Clear();
                ViewModel.PlaceholderText = $"{category.Title} is not collected yet. It arrives in a later milestone.";
            }
            else
            {
                await EnsureSectionsAsync(category.ProviderIds, ct);
                if (ct.IsCancellationRequested) return;
                BuildCategory(category);
            }

            // Live data only ticks while its category is open.
            if (category.Id == "performance")
            {
                _liveTimer ??= CreateLiveTimer();
                _liveTimer.Start();
            }
            else
            {
                _liveTimer?.Stop();
            }

            ViewModel.ShowAdminHint = category.RequiresAdmin && !ElevationHelper.IsElevated;
            ViewModel.RefreshVisibility();
            UpdateStatusText();
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this one.
        }
        catch (Exception ex)
        {
            ViewModel.PlaceholderText = $"Could not load this category: {ex.Message}";
            ViewModel.RefreshVisibility();
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Category load failed: {ex}");
        }
        finally
        {
            ViewModel.IsBusy = false;
            BusyRing.IsActive = false;
        }
    }

    private async Task EnsureSectionsAsync(IEnumerable<string> providerIds, CancellationToken ct)
    {
        var missing = providerIds
            .Where(id => !_sections.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0) return;

        var options = new CollectOptions { Level = CurrentLevel, HideIdentifiers = ViewModel.HideIdentifiers };
        var collected = await _collector.CollectManyAsync(missing, options, null, ct);

        foreach (var pair in collected)
        {
            _sections[pair.Key] = pair.Value;
        }
    }

    private void BuildOverview()
    {
        ViewModel.Rows.Clear();
        ViewModel.Tiles.Clear();
        ViewModel.PlaceholderText = string.Empty;

        ViewModel.Tiles.Add(new OverviewTile
        {
            CategoryId = "os",
            Title = "Operating system",
            Glyph = PcCategories.ById("os")?.Glyph ?? "\uE774",
            Primary = ValueOr("os", "os.name") ?? "Not available",
            Secondary = Compose("Build", ValueOr("os", "os.build"))
        });

        ViewModel.Tiles.Add(new OverviewTile
        {
            CategoryId = "cpu",
            Title = "Processor",
            Glyph = PcCategories.ById("cpu")?.Glyph ?? "\uE9E9",
            Primary = ValueOr("cpu", "cpu.name") ?? "Not available",
            Secondary = Compose(
                Plural(ValueOr("cpu", "cpu.cores"), "core"),
                ValueOr("cpu", "cpu.threads") is { } threads ? $"{threads} threads" : null,
                ValueOr("cpu", "cpu.maxClock"))
        });

        ViewModel.Tiles.Add(new OverviewTile
        {
            CategoryId = "memory",
            Title = "Memory",
            Glyph = PcCategories.ById("memory")?.Glyph ?? "\uE8D2",
            Primary = ValueOr("memory", "memory.installed") ?? "Not available",
            Secondary = ValueOr("memory", "memory.inUse") is { } inUse ? $"In use {inUse}" : null
        });

        ViewModel.Tiles.Add(new OverviewTile
        {
            CategoryId = "graphics",
            Title = "Graphics",
            Glyph = PcCategories.ById("graphics")?.Glyph ?? "\uE785",
            Primary = ValueOr("gpu", "gpu.0.name") ?? "Not available",
            Secondary = Compose("Driver", ValueOr("gpu", "gpu.0.driver"))
        });

        var diskCount = ValueOr("storage", "storage.diskCount");
        var volumes = _sections.TryGetValue("storage", out var storage)
            ? storage.Groups.Find(g => g.Title == "Volumes")?.Items.Count
            : null;

        ViewModel.Tiles.Add(new OverviewTile
        {
            CategoryId = "storage",
            Title = "Storage",
            Glyph = PcCategories.ById("storage")?.Glyph ?? "\uE8B7",
            Primary = ValueOr("storage", "storage.total") ?? "Not available",
            Secondary = Compose(Plural(diskCount, "disk"), volumes is > 0 ? Plural(volumes.ToString(), "volume") : null)
        });
    }

    private void BuildCategory(PcCategory category)
    {
        ViewModel.Tiles.Clear();
        ViewModel.Rows.Clear();

        var level = CurrentLevel;
        var search = ViewModel.SearchText.Trim();
        var added = 0;

        foreach (var providerId in category.ProviderIds)
        {
            if (!_sections.TryGetValue(providerId, out var section)) continue;

            if (!string.IsNullOrEmpty(section.Error))
            {
                ViewModel.Rows.Add(new InfoItemRow
                {
                    Key = $"{providerId}.error",
                    Label = category.Title,
                    Value = $"Not available ({section.Error})",
                    Status = InfoStatus.Warning,
                    Explain = "This information could not be read from Windows right now."
                });
                added++;
                continue;
            }

            foreach (var item in section.AllItems())
            {
                if (item.Level > level) continue;

                var display = DisplayValueOf(item);
                if (search.Length > 0 &&
                    !item.Label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !display.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ViewModel.Rows.Add(ToRow(item, null));
                added++;
            }
        }

        ViewModel.PlaceholderText = added == 0
            ? $"Nothing matches “{search}” in {category.Title}." 
            : string.Empty;

        if (added == 0 && search.Length == 0)
        {
            ViewModel.PlaceholderText = "No values were reported for this category.";
        }
    }

    private async Task ShowSearchResultsAsync(string search, CancellationToken ct)
    {
        var providerIds = PcCategories.All.SelectMany(c => c.ProviderIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await EnsureSectionsAsync(providerIds, ct);
        if (ct.IsCancellationRequested) return;

        ViewModel.Tiles.Clear();
        ViewModel.Rows.Clear();

        var level = CurrentLevel;
        var added = 0;

        foreach (var providerId in providerIds)
        {
            if (!_sections.TryGetValue(providerId, out var section) || !string.IsNullOrEmpty(section.Error)) continue;

            var categoryTitle = PcCategories.ById(section.Category)?.Title
                                ?? PcCategories.ById(providerId)?.Title
                                ?? section.Title;

            foreach (var item in section.AllItems())
            {
                if (item.Level > level) continue;

                var display = DisplayValueOf(item);
                if (!item.Label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    !display.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ViewModel.Rows.Add(ToRow(item, categoryTitle));
                added++;
            }
        }

        ViewModel.PlaceholderText = added == 0 ? $"No field or value matches “{search}”." : string.Empty;
    }

    private static IEnumerable<string> AllProviderIds()
        => PcCategories.All.SelectMany(c => c.ProviderIds).Distinct(StringComparer.OrdinalIgnoreCase);

    private List<Finding> EvaluateHealth()
        => HealthRuleEngine.Evaluate(_sections, Settings);

    /// <summary>The Health check category lists every finding, worst first.</summary>
    private void BuildHealth()
    {
        ViewModel.Tiles.Clear();
        ViewModel.Rows.Clear();

        var findings = EvaluateHealth();
        foreach (var finding in findings)
        {
            ViewModel.Rows.Add(new InfoItemRow
            {
                Key = finding.Id,
                Label = finding.Title,
                Value = $"{finding.Explanation} {finding.Action}",
                Badge = PcCategories.ById(finding.CategoryId)?.Title,
                Status = finding.Severity,
                DismissId = finding.Id
            });
        }

        ViewModel.PlaceholderText = findings.Count == 0
            ? "No issues found. Everything this page can check looks healthy."
            : string.Empty;
    }

    // ── Reports & history ───────────────────────────────────────────────────────

    private void BuildReports()
    {
        ViewModel.Tiles.Clear();
        ViewModel.Rows.Clear();

        var snapshots = SnapshotService.List();
        if (snapshots.Count == 0)
        {
            ViewModel.Rows.Add(new InfoItemRow
            {
                Key = "reports.none",
                Label = "Snapshots",
                Value = "No snapshots yet. Take one from More → Take a snapshot to be able to compare later.",
                Status = InfoStatus.Info
            });
        }

        for (var i = 0; i < Math.Min(5, snapshots.Count); i++)
        {
            ViewModel.Rows.Add(new InfoItemRow
            {
                Key = $"reports.snapshot.{i}",
                Label = i == 0 ? "Latest snapshot" : $"Snapshot {i + 1}",
                Value = snapshots[i].DisplayName,
                Status = InfoStatus.Ok
            });
        }

        if (_comparison is { Before: not null, After: not null } comparison)
        {
            ViewModel.Rows.Add(new InfoItemRow
            {
                Key = "reports.compared",
                Label = "Compared",
                Value = $"{comparison.Before.DisplayName} → {comparison.After.DisplayName}",
                Badge = "History",
                Status = InfoStatus.Info
            });

            if (comparison.Changes.Count == 0)
            {
                ViewModel.Rows.Add(new InfoItemRow
                {
                    Key = "reports.noChanges",
                    Label = "Changes",
                    Value = "Nothing changed between these two snapshots.",
                    Status = InfoStatus.Ok
                });
            }

            foreach (var change in comparison.Changes.Take(120))
            {
                var before = change.Before ?? "(not present)";
                var after = change.After ?? "(removed)";
                ViewModel.Rows.Add(new InfoItemRow
                {
                    Key = $"reports.change.{change.Section}.{change.Label}",
                    Label = change.Label,
                    Value = $"{before} → {after}",
                    Badge = PcCategories.ById(change.Section)?.Title ?? change.Section,
                    Status = InfoStatus.Info
                });
            }
        }

        ViewModel.PlaceholderText = string.Empty;
        ViewModel.RefreshVisibility();
        UpdateStatusText();
    }

    private async void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        await EnsureSectionsAsync(AllProviderIds(), CancellationToken.None);
        CopyToClipboard(ReportExporter.BuildSummary(_sections.Values, new ReportExporter.Options
        {
            Level = CurrentLevel,
            RedactIdentifiers = ViewModel.HideIdentifiers
        }));
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item) return;

        var format = item.Tag as string ?? "html";
        await EnsureSectionsAsync(AllProviderIds(), CancellationToken.None);

        var options = new ReportExporter.Options
        {
            Level = CurrentLevel,
            RedactIdentifiers = ViewModel.HideIdentifiers
        };

        var sections = _sections.Values.ToList();
        string content;
        string extension;
        string filter;

        switch (format)
        {
            case "txt":
                content = ReportExporter.BuildText(sections, options);
                extension = "txt";
                filter = "Text file|*.txt|All files|*.*";
                break;
            case "json":
                content = ReportExporter.BuildJson(sections, options);
                extension = "json";
                filter = "JSON file|*.json|All files|*.*";
                break;
            case "csv":
                content = ReportExporter.BuildCsv(sections, options);
                extension = "csv";
                filter = "CSV file|*.csv|All files|*.*";
                break;
            default:
                content = ReportExporter.BuildHtml(sections, options);
                extension = "html";
                filter = "HTML file|*.html|All files|*.*";
                break;
        }

        var suggested = $"pc-checker-{DateTime.Now:yyyy-MM-dd-HHmm}.{extension}";
        var path = NativeFileDialog.Save("Save PC Checker report", filter, extension, suggested);
        if (path == null) return;

        try
        {
            System.IO.File.WriteAllText(path, content);
            await ShowNoticeAsync("Report saved", System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            await ShowNoticeAsync("Could not save the report", ex.Message);
        }
    }

    private async void TakeSnapshot_Click(object sender, RoutedEventArgs e)
    {
        await EnsureSectionsAsync(AllProviderIds(), CancellationToken.None);
        var path = SnapshotService.Save(_sections.Values);

        await ShowNoticeAsync(path == null ? "Could not save the snapshot" : "Snapshot saved",
            path == null ? "Check that the AppData folder is writable." : System.IO.Path.GetFileName(path));
    }

    private void CompareSnapshots_Click(object sender, RoutedEventArgs e)
    {
        var comparison = SnapshotService.CompareLatest();
        if (comparison.Before == null)
        {
            _ = ShowNoticeAsync("Two snapshots are needed", "Take a snapshot now, change something, then take another one.");
            return;
        }

        _comparison = comparison;
        if (PcCategories.ById("reports") is { } reports) CategoryList.SelectedItem = reports;
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void DismissFinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not InfoItemRow row) return;
        if (string.IsNullOrWhiteSpace(row.DismissId)) return;
        if (Settings.DismissedFindings.Contains(row.DismissId)) return;

        Settings.DismissedFindings.Add(row.DismissId!);
        SaveSettings();
        BuildHealth();
        ViewModel.RefreshVisibility();
        UpdateStatusText();
    }

    private InfoItemRow ToRow(InfoItem item, string? badge)
    {
        var missingText = item.Value is null && item.Missing != MissingReason.None
            ? InfoItem.ReasonText(item.Missing)
            : null;

        var masked = ViewModel.HideIdentifiers
                     && item.Sensitivity == InfoSensitivity.Identifier
                     && !string.IsNullOrWhiteSpace(item.Value);

        return new InfoItemRow
        {
            Key = item.Key,
            Label = item.Label,
            Value = item.Value ?? missingText ?? "Not available",
            Explain = item.Explain,
            Source = CurrentLevel == DetailLevel.Everything ? item.Source : null,
            Badge = badge,
            Status = item.Value is null && item.Missing != MissingReason.None ? InfoStatus.Info : item.Status,
            Masked = masked
        };
    }

    private static string DisplayValueOf(InfoItem item)
        => item.Value ?? InfoItem.ReasonText(item.Missing);

    private string? ValueOr(string sectionId, string key)
    {
        if (!_sections.TryGetValue(sectionId, out var section)) return null;
        var item = section.Find(key);
        return item?.Value;
    }

    private static string? Plural(string? count, string noun)
    {
        if (string.IsNullOrEmpty(count)) return null;
        if (!int.TryParse(count, out var number)) return $"{count} {noun}";
        return number == 1 ? $"1 {noun}" : $"{number} {noun}s";
    }

    private static string? Compose(params string?[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private void UpdateStatusText()
    {
        var parts = new List<string>();

        int sections = 0;
        DateTime? newest = null;
        foreach (var section in _sections.Values)
        {
            sections++;
            if (newest is null || section.CollectedAt > newest) newest = section.CollectedAt;
        }

        if (newest is { } scanned)
        {
            parts.Add($"Scanned {PcFormat.Since(scanned)}");
            parts.Add(sections == 1 ? "1 source" : $"{sections} sources");
        }

        // Health summary: only meaningful once a few sections have been collected.
        if (_sections.Count >= 3)
        {
            var findings = EvaluateHealth();
            var problems = findings.Count(f => f.Severity == InfoStatus.Problem);
            var warnings = findings.Count(f => f.Severity == InfoStatus.Warning);
            var notes = findings.Count - problems - warnings;

            var summary = problems > 0
                ? $"Problems ({problems})"
                : warnings > 0
                    ? $"Needs attention ({warnings})"
                    : "All good";

            if (notes > 0) summary += $" · {notes} note{(notes == 1 ? "" : "s")}";
            parts.Insert(0, summary);
        }

        parts.Add(ViewModel.HideIdentifiers ? "Identifiers hidden" : "Identifiers visible");
        parts.Add(ElevationHelper.IsElevated
            ? "Running as administrator"
            : "Standard user — some values need administrator rights");

        ViewModel.StatusText = string.Join(" · ", parts);
    }

    private DispatcherTimer CreateLiveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += LiveTimer_Tick;
        return timer;
    }

    private static void SaveSettings()
    {
        try
        {
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Saving settings failed: {ex.Message}");
        }
    }

    // ── Row actions ────────────────────────────────────────────────────────────

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not InfoItemRow row) return;
        CopyToClipboard(row.DisplayValue);
    }

    private void RevealRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not InfoItemRow row) return;
        row.IsRevealed = !row.IsRevealed;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not OverviewTile tile) return;

        foreach (var item in ViewModel.Categories)
        {
            if (!string.Equals(item.Id, tile.CategoryId, StringComparison.OrdinalIgnoreCase)) continue;

            SearchBox.Text = string.Empty;
            CategoryList.SelectedItem = item;
            return;
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text ?? string.Empty);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Clipboard copy failed: {ex.Message}");
        }
    }
}
