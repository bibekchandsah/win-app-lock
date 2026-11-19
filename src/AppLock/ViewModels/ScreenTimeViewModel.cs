using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.ScreenTime;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Screen Time tab state. Range selection, summary cards, app list and chart data all come from
/// the aggregated SQLite tables; a refresh is cheap enough to run whenever the tab is shown.
/// </summary>
public sealed partial class ScreenTimeViewModel : ObservableObject
{
    private const int DefaultVisibleRows = 15;

    private readonly ScreenTimeService _service;
    private readonly ScreenTimeStore _store;
    private readonly LockManager _lockManager;
    private readonly IClock _clock = SystemClock.Instance;

    private List<AppUsageRowViewModel> _filtered = new();
    private int _visibleRows = DefaultVisibleRows;

    [ObservableProperty] private UsageRangeKind _rangeKind = UsageRangeKind.Today;
    [ObservableProperty] private DateOnly _anchorDate = DateOnly.FromDateTime(DateTime.Now);
    [ObservableProperty] private string _rangeTitle = string.Empty;
    [ObservableProperty] private string _totalText = "0m";
    [ObservableProperty] private string _deltaText = string.Empty;
    [ObservableProperty] private bool _showDelta;
    [ObservableProperty] private string _secondTitle = "First active";
    [ObservableProperty] private string _secondValue = "—";
    [ObservableProperty] private string _secondSubtext = "Last active —";
    [ObservableProperty] private string _mostUsedName = "—";
    [ObservableProperty] private string _mostUsedTime = string.Empty;
    [ObservableProperty] private int _mostUsedColorIndex;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _showAllButton;
    [ObservableProperty] private string _showAllText = string.Empty;
    [ObservableProperty] private string _rowCountText = string.Empty;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isTrackingEnabled = true;
    [ObservableProperty] private bool _isProtectionEnabled;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isMonthView;
    [ObservableProperty] private bool _isChartView = true;
    [ObservableProperty] private bool _showShowAll;

    public ScreenTimeViewModel()
    {
        _service = ServiceContainer.ScreenTimeService;
        _store = _service.Store;
        _lockManager = ServiceContainer.LockManager;
    }

    public ObservableCollection<AppUsageRowViewModel> Rows { get; } = new();

    public IReadOnlyList<UsageBar> Bars { get; private set; } = Array.Empty<UsageBar>();
    public IReadOnlyList<DayUsage> MonthDays { get; private set; } = Array.Empty<DayUsage>();
    public double ChartMax { get; private set; } = 3600;
    public double? AverageLine { get; private set; }
    public bool CanGoNext { get; private set; }

    public event EventHandler? ChartChanged;

    public ScreenTimeSettings Settings => _service.Settings;

    public void SetRange(UsageRangeKind kind)
    {
        if (RangeKind == kind) return;
        RangeKind = kind;
        AnchorDate = DateOnly.FromDateTime(DateTime.Now);
        _visibleRows = DefaultVisibleRows;
        Refresh();
    }

    public void Shift(int direction)
    {
        var range = new UsageRange(RangeKind, AnchorDate);
        if (direction > 0 && !range.CanGoNext(_clock.Today)) return;

        AnchorDate = range.Shift(direction).Anchor;
        _visibleRows = DefaultVisibleRows;
        Refresh();
    }

    public void SelectDay(DateOnly day)
    {
        RangeKind = UsageRangeKind.Today;
        AnchorDate = day;
        _visibleRows = DefaultVisibleRows;
        Refresh();
    }

    public void ShowAll()
    {
        _visibleRows = int.MaxValue;
        ApplyFilter();
    }

    public void ToggleShowHidden()
    {
        ShowHidden = !ShowHidden;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public void Refresh()
    {
        var range = new UsageRange(RangeKind, AnchorDate);
        var settings = Settings;
        var today = _clock.Today;

        IsTrackingEnabled = settings.TrackingEnabled;
        IsProtectionEnabled = _lockManager.IsProtectionEnabled;
        RangeTitle = range.Title;
        IsMonthView = RangeKind == UsageRangeKind.Month;
        IsChartView = !IsMonthView;
        CanGoNext = range.CanGoNext(today);

        var rows = _store.GetAppUsage(range.From, range.To);
        var storedApps = _store.GetApps().ToDictionary(a => a.AppKey, StringComparer.OrdinalIgnoreCase);
        var rules = _lockManager.GetAllRules();
        long total = rows.Sum(r => r.Seconds);

        var allRows = rows
            .Select(r => new AppUsageRowViewModel(
                r,
                storedApps.TryGetValue(r.AppKey, out var stored) ? stored : null,
                FindRule(rules, r.AppKey, storedApps.TryGetValue(r.AppKey, out var s) ? s.ExePath : null, r.DisplayName),
                settings,
                today,
                total))
            .ToList();

        var visible = allRows.Where(r => !r.IsHidden || ShowHidden).ToList();

        TotalText = UsageFormat.Duration(total);
        HasData = visible.Count > 0;

        long previous = _store.GetAppUsage(range.PreviousFrom, range.PreviousTo).Sum(r => r.Seconds);
        ShowDelta = previous > 0 && total > 0;
        if (ShowDelta)
        {
            double change = (total - previous) / (double)previous * 100.0;
            string arrow = change >= 0 ? "▲" : "▼";
            DeltaText = $"{arrow} {Math.Abs(change):0}% {range.DeltaSuffix}";
        }

        var mostUsed = visible.FirstOrDefault();
        MostUsedName = mostUsed?.DisplayName ?? "—";
        MostUsedTime = mostUsed != null ? UsageFormat.Duration(mostUsed.Seconds) : string.Empty;
        MostUsedColorIndex = mostUsed?.ColorIndex ?? 0;

        BuildSecondCard(range, today);
        BuildChart(range, visible, today);

        string retention = settings.RetentionDays <= 0 ? "History kept forever" : $"{settings.RetentionDays} days kept";
        StatusText = settings.TrackingEnabled
            ? $"Tracking is on · Data stays on this PC · {retention}"
            : "Tracking is off · Apps with limits are still enforced";

        _filtered = allRows;
        ApplyFilter();
    }

    private void BuildSecondCard(UsageRange range, DateOnly today)
    {
        if (range.Kind == UsageRangeKind.Today)
        {
            var hours = _store.GetHourUsage(range.From);
            int first = hours.FirstOrDefault(h => h.Seconds > 0)?.Hour ?? -1;
            int last = hours.LastOrDefault(h => h.Seconds > 0)?.Hour ?? -1;

            SecondTitle = "First active";
            SecondValue = first >= 0 ? $"{first:00}:00" : "—";
            SecondSubtext = last >= 0 ? $"Last active {last:00}:00" : "No activity yet";
            return;
        }

        var days = _store.GetDayTotals(range.From, range.To);
        var activeDays = days.Where(d => d.Seconds > 0).ToList();
        long average = activeDays.Count > 0 ? (long)activeDays.Average(d => d.Seconds) : 0;

        SecondTitle = "Daily average";
        SecondValue = UsageFormat.Duration(average);
        SecondSubtext = activeDays.Count == 1 ? "1 active day" : $"{activeDays.Count} active days";

        // Section 6.2: no average line when any listed app already has a limit.
        AverageLine = null;
    }

    private void BuildChart(UsageRange range, IReadOnlyList<AppUsageRowViewModel> visibleRows, DateOnly today)
    {
        if (range.Kind == UsageRangeKind.Month)
        {
            MonthDays = _store.GetDayTotals(range.From, range.To);
            Bars = Array.Empty<UsageBar>();
            ChartChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (range.Kind == UsageRangeKind.Today)
        {
            var hours = _store.GetHourUsage(range.From);
            var coverage = _store.GetCoverage(range.From);
            var bars = new List<UsageBar>(24);
            int currentHour = _clock.Now.Hour;
            bool isToday = range.From == today;

            for (int hour = 0; hour < 24; hour++)
            {
                int seconds = hours[hour].Seconds;
                bool future = isToday && hour > currentHour;
                bool incomplete = !future && coverage[hour].TrackedSeconds < 3300;

                var segments = seconds > 0
                    ? _store.GetHourStack(range.From, hour)
                        .Select(s => new BarSegment(s.AppKey, s.Label, s.Seconds, s.ColorIndex, s.Tooltip))
                        .ToList()
                    : new List<BarSegment>();

                string tooltip = $"{hour:00}:00 – {(hour + 1) % 24:00}:00 · {UsageFormat.Duration(seconds)}";
                if (segments.Count > 0)
                    tooltip += "\n" + string.Join("\n", segments.Select(s => $"· {s.Label} {UsageFormat.Duration(s.Seconds)}"));
                if (incomplete)
                    tooltip += "\nAppLock was not running for part of this hour.";
                if (future)
                    tooltip = $"{hour:00}:00 – {(hour + 1) % 24:00}:00 · not yet";

                bars.Add(new UsageBar($"{hour:00}", tooltip, seconds, segments, incomplete, future));
            }

            Bars = bars;
            ChartMax = 3600;
            AverageLine = null;
        }
        else
        {
            var days = _store.GetDayTotals(range.From, range.To);
            var bars = new List<UsageBar>(days.Count);
            foreach (var day in days)
            {
                var segments = day.Seconds > 0
                    ? new List<BarSegment> { new("day", "Total", day.Seconds, -1, UsageFormat.Duration(day.Seconds)) }
                    : new List<BarSegment>();

                string tooltip = day.Seconds > 0
                    ? $"{day.Day:ddd d MMM} · {UsageFormat.Duration(day.Seconds)}"
                    : $"{day.Day:ddd d MMM} · No data";

                bars.Add(new UsageBar(day.Day.ToString("ddd"), tooltip, day.Seconds, segments, false, day.Day > today, day.Day));
            }

            Bars = bars;

            long max = days.Count == 0 ? 0 : days.Max(d => d.Seconds);
            ChartMax = Math.Max(3600, Math.Ceiling(max / 3600.0) * 3600);

            bool anyLimit = visibleRows.Any(r => r.HasLimit);
            var activeDays = days.Where(d => d.Seconds > 0).ToList();
            AverageLine = anyLimit || activeDays.Count == 0 ? null : activeDays.Average(d => d.Seconds);
        }

        ChartChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFilter()
    {
        IEnumerable<AppUsageRowViewModel> filtered = _filtered;

        if (!ShowHidden)
            filtered = filtered.Where(r => !r.IsHidden);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string query = SearchText.Trim();
            filtered = filtered.Where(r => r.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        Rows.Clear();
        foreach (var row in list.Take(_visibleRows))
            Rows.Add(row);

        int hiddenCount = _filtered.Count(r => r.IsHidden);
        ShowAllButton = list.Count > _visibleRows;
        ShowAllText = $"Show all ({list.Count})";
        RowCountText = hiddenCount > 0 && !ShowHidden
            ? $"{list.Count} apps · {hiddenCount} hidden"
            : $"{list.Count} apps";
        HasData = list.Count > 0;
    }

    private static LockRule? FindRule(IEnumerable<LockRule> rules, string appKey, string? exePath, string displayName)
    {
        var list = rules.ToList();

        var byKey = list.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.AppKey) && r.AppKey.Equals(appKey, StringComparison.OrdinalIgnoreCase));
        if (byKey != null) return byKey;

        var byPath = list.FirstOrDefault(r =>
            !string.IsNullOrEmpty(exePath) &&
            !string.IsNullOrEmpty(r.ProcessPath) &&
            r.ProcessPath.Equals(exePath, StringComparison.OrdinalIgnoreCase));
        if (byPath != null) return byPath;

        string fileName = string.IsNullOrEmpty(exePath)
            ? string.Empty
            : System.IO.Path.GetFileNameWithoutExtension(exePath);

        return list.FirstOrDefault(r =>
            (!string.IsNullOrEmpty(fileName) && r.ProcessName.Equals(fileName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(displayName) && r.ProcessName.Equals(displayName, StringComparison.OrdinalIgnoreCase)));
    }
}
