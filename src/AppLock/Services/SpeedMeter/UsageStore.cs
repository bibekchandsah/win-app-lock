using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AppLock.Services.SpeedMeter;

/// <summary>One day of traffic for one adapter.</summary>
public sealed class UsageRow
{
    public string Date { get; set; } = string.Empty;      // yyyy-MM-dd
    public string AdapterId { get; set; } = string.Empty;
    public string AdapterName { get; set; } = string.Empty;
    public long BytesDown { get; set; }
    public long BytesUp { get; set; }
}

/// <summary>A grouped value for the history chart.</summary>
public sealed class UsagePoint
{
    public string Label { get; init; } = string.Empty;
    public long BytesDown { get; init; }
    public long BytesUp { get; init; }
    public long Total => BytesDown + BytesUp;
}

/// <summary>
/// Local-only data usage history: one row per day and adapter, kept for 12 months, written
/// atomically to %APPDATA%\AppLock\SpeedMeter\usage.json. Nothing here ever leaves the PC.
/// </summary>
public sealed class UsageStore
{
    private const int RetentionMonths = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly List<UsageRow> _rows = new();
    private readonly string _path;
    private readonly string _directory;

    private DateTime _lastFlush = DateTime.MinValue;
    private bool _dirty;

    public UsageStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppLock", "SpeedMeter");

        _path = Path.Combine(_directory, "usage.json");
        Load();
    }

    /// <summary>Last cap warning that fired, encoded as "period|threshold".</summary>
    public string? LastDailyCapAlert { get; set; }

    public string? LastMonthlyCapAlert { get; set; }

    public string FilePath => _path;

    public void AddBytes(string adapterId, string adapterName, long down, long up, DateTime when)
    {
        if (down <= 0 && up <= 0) return;

        lock (_gate)
        {
            var date = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var row = _rows.FirstOrDefault(r => r.Date == date && r.AdapterId == adapterId);

            if (row is null)
            {
                row = new UsageRow { Date = date, AdapterId = adapterId, AdapterName = adapterName };
                _rows.Add(row);
            }

            row.BytesDown += Math.Max(0, down);
            row.BytesUp += Math.Max(0, up);
            if (adapterName.Length > 0) row.AdapterName = adapterName;

            _dirty = true;
            Prune(when);
        }
    }

    /// <summary>Writes the file at most once a minute; <paramref name="force"/> for exit/reset.</summary>
    public void FlushIfDue(DateTime now, bool force = false)
    {
        if (!_dirty) return;

        lock (_gate)
        {
            if (!force && now - _lastFlush < TimeSpan.FromSeconds(60)) return;

            SaveLocked();
        }
    }

    public (long Down, long Up) Total(DateTime fromInclusive, DateTime toExclusive, string? adapterId = null)
    {
        lock (_gate)
        {
            long down = 0, up = 0;

            foreach (var row in _rows)
            {
                if (!TryParseDate(row.Date, out var date)) continue;
                if (date < fromInclusive.Date || date >= toExclusive.Date) continue;
                if (adapterId is not null && row.AdapterId != adapterId) continue;

                down += row.BytesDown;
                up += row.BytesUp;
            }

            return (down, up);
        }
    }

    public (long Down, long Up) Today(string? adapterId = null)
    {
        var today = DateTime.Today;
        return Total(today, today.AddDays(1), adapterId);
    }

    /// <summary>Daily buckets for the last <paramref name="days"/> days, oldest first.</summary>
    public List<UsagePoint> Daily(int days, string? adapterId = null)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-(days - 1));
        var points = new List<UsagePoint>();

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var (down, up) = Total(day, day.AddDays(1), adapterId);
            points.Add(new UsagePoint { Label = day.ToString("d MMM", CultureInfo.CurrentCulture), BytesDown = down, BytesUp = up });
        }

        return points;
    }

    /// <summary>Weekly buckets for the last <paramref name="weeks"/> weeks, oldest first.</summary>
    public List<UsagePoint> Weekly(int weeks, string? adapterId = null)
    {
        var end = DateTime.Today;
        var points = new List<UsagePoint>();

        for (var index = weeks - 1; index >= 0; index--)
        {
            var weekEnd = end.AddDays(-7 * index);
            var weekStart = weekEnd.AddDays(-6);

            var (down, up) = Total(weekStart, weekEnd.AddDays(1), adapterId);
            points.Add(new UsagePoint
            {
                Label = $"{weekStart:d MMM}",
                BytesDown = down,
                BytesUp = up
            });
        }

        return points;
    }

    /// <summary>Monthly buckets for the last <paramref name="months"/> months, oldest first.</summary>
    public List<UsagePoint> Monthly(int months, string? adapterId = null)
    {
        var points = new List<UsagePoint>();
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-(months - 1));

        for (var index = 0; index < months; index++)
        {
            var start = first.AddMonths(index);
            var (down, up) = Total(start, start.AddMonths(1), adapterId);

            points.Add(new UsagePoint
            {
                Label = start.ToString("MMM yy", CultureInfo.CurrentCulture),
                BytesDown = down,
                BytesUp = up
            });
        }

        return points;
    }

    /// <summary>Start of the current "month" using the user's monthly reset day (1–28).</summary>
    public static DateTime MonthStart(DateTime today, int resetDay)
    {
        resetDay = Math.Clamp(resetDay, 1, 28);

        var day = Math.Min(resetDay, DateTime.DaysInMonth(today.Year, today.Month));
        var start = new DateTime(today.Year, today.Month, day);

        if (today.Date < start) start = start.AddMonths(-1);

        return start;
    }

    public void MarkDailyCapAlert(string key)
    {
        lock (_gate)
        {
            LastDailyCapAlert = key;
            _dirty = true;
        }
    }

    public void MarkMonthlyCapAlert(string key)
    {
        lock (_gate)
        {
            LastMonthlyCapAlert = key;
            _dirty = true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            LastDailyCapAlert = null;
            LastMonthlyCapAlert = null;
            _dirty = true;
            SaveLocked();
        }
    }

    /// <summary>CSV for spreadsheet export; one line per stored day and adapter.</summary>
    public string BuildCsv(string? adapterId = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("date,adapterId,adapterName,downBytes,upBytes");

        lock (_gate)
        {
            foreach (var row in _rows
                         .Where(r => adapterId is null || r.AdapterId == adapterId)
                         .OrderBy(r => r.Date, StringComparer.Ordinal)
                         .ThenBy(r => r.AdapterName, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(string.Join(',',
                    row.Date,
                    Escape(row.AdapterId),
                    Escape(row.AdapterName),
                    row.BytesDown.ToString(CultureInfo.InvariantCulture),
                    row.BytesUp.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private void Prune(DateTime now)
    {
        // Keep whole months so the monthly chart never starts in the middle of a month.
        var cutoff = new DateTime(now.Year, now.Month, 1).AddMonths(-(RetentionMonths - 1));

        _rows.RemoveAll(row =>
            !TryParseDate(row.Date, out var date) || date < cutoff);
    }

    private static bool TryParseDate(string value, out DateTime date)
        => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var document = JsonSerializer.Deserialize<UsageDocument>(File.ReadAllText(_path), JsonOptions);
            if (document is null) return;

            _rows.AddRange(document.Rows ?? new List<UsageRow>());
            LastDailyCapAlert = document.LastDailyCapAlert;
            LastMonthlyCapAlert = document.LastMonthlyCapAlert;

            Prune(DateTime.Now);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Usage load error: {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var document = new UsageDocument
            {
                Rows = _rows,
                LastDailyCapAlert = LastDailyCapAlert,
                LastMonthlyCapAlert = LastMonthlyCapAlert
            };

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temp = _path + ".tmp";

            // Write-then-replace keeps the previous file intact if the app dies mid-save.
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);

            _dirty = false;
            _lastFlush = DateTime.Now;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Usage save error: {ex.Message}");
        }
    }

    private sealed class UsageDocument
    {
        public List<UsageRow>? Rows { get; set; }
        public string? LastDailyCapAlert { get; set; }
        public string? LastMonthlyCapAlert { get; set; }
    }
}
