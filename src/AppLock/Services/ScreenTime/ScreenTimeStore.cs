using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.Data.Sqlite;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// SQLite history for Screen Time. The DB is display-only: quota enforcement always reads the
/// HMAC-protected counters in the settings file, so deleting this file never resets a limit.
///
/// All writes go through one background writer that drains a channel and writes in batches, so the
/// foreground hook and the one-second tick never touch the disk on their own thread.
/// </summary>
public sealed class ScreenTimeStore : IAsyncDisposable
{
    private const int SchemaVersion = 1;

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly TimeSpan _flushInterval;
    private readonly Channel<UsageDelta> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();
    private readonly Task _writerTask;

    private bool _initialized;

    public ScreenTimeStore(string? dbPath = null, TimeSpan? flushInterval = null)
    {
        _dbPath = dbPath ?? DefaultDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(30);
        _queue = Channel.CreateUnbounded<UsageDelta>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _writerTask = Task.Run(WriterLoopAsync);
    }

    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock",
        "screentime.db");

    public string DatabasePath => _dbPath;

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>Queues one aggregated slice. The writer batches these and flushes every 30 s.</summary>
    public void Enqueue(UsageDelta delta)
    {
        if (delta.Seconds <= 0) return;
        _queue.Writer.TryWrite(delta);
    }

    /// <summary>Writes everything queued so far, e.g. on exit, sleep or before a query in tests.</summary>
    public void Flush()
    {
        lock (_writeLock)
        {
            var pending = new List<UsageDelta>();
            while (_queue.Reader.TryRead(out var delta)) pending.Add(delta);
            if (pending.Count > 0) WriteBatch(pending);
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);

                if (DateTime.UtcNow - _lastFlushUtc < _flushInterval) continue;
                Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenTimeStore] writer stopped: {ex.Message}");
        }
        finally
        {
            try { Flush(); } catch { /* shutdown best effort */ }
        }
    }

    private DateTime _lastFlushUtc = DateTime.UtcNow;

    private void WriteBatch(IReadOnlyList<UsageDelta> batch)
    {
        EnsureInitialized();
        _lastFlushUtc = DateTime.UtcNow;

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            foreach (var delta in batch)
            {
                string day = delta.LocalTimestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                int hour = delta.LocalTimestamp.Hour;

                long appId = UpsertApp(connection, transaction, delta);

                using var usage = connection.CreateCommand();
                usage.Transaction = transaction;
                usage.CommandText = """
                    INSERT INTO usage_hour (day, hour, app_id, seconds, opens)
                    VALUES ($day, $hour, $appId, $seconds, $opens)
                    ON CONFLICT(day, hour, app_id) DO UPDATE SET
                        seconds = seconds + excluded.seconds,
                        opens   = opens + excluded.opens;
                    """;
                usage.Parameters.AddWithValue("$day", day);
                usage.Parameters.AddWithValue("$hour", hour);
                usage.Parameters.AddWithValue("$appId", appId);
                usage.Parameters.AddWithValue("$seconds", Math.Max(0, delta.Seconds));
                usage.Parameters.AddWithValue("$opens", Math.Max(0, delta.Opens));
                usage.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenTimeStore] batch write failed: {ex.Message}");
        }
    }

    private static long UpsertApp(SqliteConnection connection, SqliteTransaction transaction, UsageDelta delta)
    {
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO app (app_key, display_name, exe_path, aumid, first_seen, icon_png)
            VALUES ($key, $name, $exe, $aumid, $firstSeen, $icon)
            ON CONFLICT(app_key) DO UPDATE SET
                display_name = CASE WHEN excluded.display_name = app.app_key THEN app.display_name ELSE excluded.display_name END,
                exe_path     = COALESCE(excluded.exe_path, app.exe_path),
                aumid        = COALESCE(excluded.aumid, app.aumid),
                icon_png     = COALESCE(excluded.icon_png, app.icon_png);
            """;
        upsert.Parameters.AddWithValue("$key", delta.AppKey);
        upsert.Parameters.AddWithValue("$name", delta.DisplayName);
        upsert.Parameters.AddWithValue("$exe", (object?)delta.ExePath ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$aumid", (object?)delta.Aumid ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$firstSeen", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        upsert.Parameters.AddWithValue("$icon", (object?)delta.IconPng ?? DBNull.Value);
        upsert.ExecuteNonQuery();

        using var id = connection.CreateCommand();
        id.Transaction = transaction;
        id.CommandText = "SELECT id FROM app WHERE app_key = $key;";
        id.Parameters.AddWithValue("$key", delta.AppKey);
        return Convert.ToInt64(id.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Records how many seconds of an hour tracking actually ran (idle time included).</summary>
    public void RecordCoverage(DateOnly day, int hour, int trackedSeconds)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO coverage_hour (day, hour, tracked_seconds)
            VALUES ($day, $hour, $seconds)
            ON CONFLICT(day, hour) DO UPDATE SET
                tracked_seconds = MIN(3600, tracked_seconds + excluded.tracked_seconds);
            """;
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hour", hour);
        command.Parameters.AddWithValue("$seconds", Math.Clamp(trackedSeconds, 0, 3600));
        command.ExecuteNonQuery();
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public IReadOnlyList<AppUsageRow> GetAppUsage(DateOnly from, DateOnly to)
    {
        EnsureInitialized();
        Flush();

        var rows = new List<AppUsageRow>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.app_key, a.display_name, SUM(u.seconds) AS total, SUM(u.opens) AS opens
            FROM usage_hour u
            JOIN app a ON a.id = u.app_id
            WHERE u.day BETWEEN $from AND $to
            GROUP BY a.app_key, a.display_name
            HAVING total > 0
            ORDER BY total DESC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AppUsageRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3)));
        }

        return rows;
    }

    public IReadOnlyList<HourUsage> GetHourUsage(DateOnly day)
    {
        EnsureInitialized();
        Flush();

        var hours = new int[24];
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hour, SUM(seconds)
            FROM usage_hour
            WHERE day = $day
            GROUP BY hour;
            """;
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        while (reader.Read()) hours[reader.GetInt32(0)] = reader.GetInt32(1);

        return Enumerable.Range(0, 24).Select(h => new HourUsage(h, hours[h])).ToList();
    }

    public IReadOnlyList<DayUsage> GetDayTotals(DateOnly from, DateOnly to)
    {
        EnsureInitialized();
        Flush();

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT day, SUM(seconds)
            FROM usage_hour
            WHERE day BETWEEN $from AND $to
            GROUP BY day;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        while (reader.Read()) totals[reader.GetString(0)] = reader.GetInt32(1);

        var days = new List<DayUsage>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            days.Add(new DayUsage(day, totals.TryGetValue(key, out var seconds) ? seconds : 0));
        }

        return days;
    }

    public IReadOnlyList<CoverageHour> GetCoverage(DateOnly day)
    {
        EnsureInitialized();
        Flush();

        var hours = new int[24];
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hour, tracked_seconds FROM coverage_hour WHERE day = $day;";
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        while (reader.Read()) hours[reader.GetInt32(0)] = reader.GetInt32(1);

        return Enumerable.Range(0, 24).Select(h => new CoverageHour(h, hours[h])).ToList();
    }

    /// <summary>Every app that has a row in the database, including its path for rule creation.</summary>
    public IReadOnlyList<StoredApp> GetApps()
    {
        EnsureInitialized();

        var apps = new List<StoredApp>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_key, display_name, exe_path, aumid FROM app ORDER BY display_name;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            apps.Add(new StoredApp(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return apps;
    }

    /// <summary>Cached PNG icons for the visible list, read once per refresh.</summary>
    public IReadOnlyDictionary<string, byte[]> GetAppIcons(IEnumerable<string> appKeys)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var keys = appKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0) return result;

        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < keys.Count; i++)
        {
            string name = "$k" + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, keys[i]);
        }

        command.CommandText = $"SELECT app_key, icon_png FROM app WHERE app_key IN ({string.Join(",", names)}) AND icon_png IS NOT NULL;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (byte[])reader.GetValue(1);
        }

        return result;
    }

    /// <summary>Top apps of one hour, plus the rest as "Other", prepared for a stacked bar.</summary>
    public IReadOnlyList<BarSegment> GetHourStack(DateOnly day, int hour, int topCount = 3)
    {
        EnsureInitialized();
        Flush();

        var segments = new List<BarSegment>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.app_key, a.display_name, u.seconds
            FROM usage_hour u
            JOIN app a ON a.id = u.app_id
            WHERE u.day = $day AND u.hour = $hour
            ORDER BY u.seconds DESC;
            """;
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hour", hour);

        int other = 0;
        int index = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            string name = reader.GetString(1);
            int seconds = reader.GetInt32(2);

            if (index < topCount)
            {
                segments.Add(new BarSegment(key, name, seconds, StableColorIndex(key), $"{name} · {FormatMinutes(seconds)}"));
            }
            else
            {
                other += seconds;
            }

            index++;
        }

        if (other > 0) segments.Add(new BarSegment("other", "Other", other, -1, $"Other · {FormatMinutes(other)}"));
        return segments;
    }

    /// <summary>Stable palette index so an app keeps its color across days.</summary>
    public static int StableColorIndex(string appKey)
    {
        uint hash = 2166136261;
        foreach (char c in appKey) hash = (hash ^ c) * 16777619;
        return (int)(hash % 8);
    }

    private static string FormatMinutes(int seconds) =>
        seconds < 60 ? "under a minute" : $"{seconds / 60} min";

    // ── Maintenance ───────────────────────────────────────────────────────────

    /// <summary>Deletes history older than retentionDays. 0 means keep forever.</summary>
    public int Prune(int retentionDays, DateOnly? today = null)
    {
        if (retentionDays <= 0) return 0;

        EnsureInitialized();
        var cutoff = (today ?? DateOnly.FromDateTime(DateTime.Now)).AddDays(-retentionDays);
        string cutoffText = cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        int removed = 0;
        foreach (var table in new[] { "usage_hour", "coverage_hour", "window_title" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE day < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoffText);
            removed += command.ExecuteNonQuery();
        }

        using (var orphan = connection.CreateCommand())
        {
            orphan.Transaction = transaction;
            orphan.CommandText = "DELETE FROM app WHERE id NOT IN (SELECT DISTINCT app_id FROM usage_hour);";
            orphan.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed;
    }

    /// <summary>Deletes stored window titles when the user turns title recording off.</summary>
    public void ClearTitles()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM window_title;";
        command.ExecuteNonQuery();
    }

    /// <summary>Removes every usage row. Quota counters live in the settings file and are untouched.</summary>
    public void ClearAll()
    {
        EnsureInitialized();
        lock (_writeLock)
        {
            while (_queue.Reader.TryRead(out _)) { }
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "usage_hour", "coverage_hour", "window_title", "app" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>CSV of what the user sees: day, hour, app, seconds.</summary>
    public string ExportCsv(DateOnly from, DateOnly to, ICollection<string>? hiddenAppKeys = null)
    {
        EnsureInitialized();
        Flush();

        var builder = new StringBuilder();
        builder.AppendLine($"# AppLock screen time export,{DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine("day,hour,app,seconds");

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.day, u.hour, a.app_key, a.display_name, u.seconds
            FROM usage_hour u
            JOIN app a ON a.id = u.app_id
            WHERE u.day BETWEEN $from AND $to
            ORDER BY u.day, u.hour, u.seconds DESC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(2);
            if (hiddenAppKeys is { Count: > 0 } && hiddenAppKeys.Contains(key)) continue;

            builder.AppendLine(string.Join(",",
                reader.GetString(0),
                reader.GetInt32(1).ToString(CultureInfo.InvariantCulture),
                Quote(reader.GetString(3)),
                reader.GetInt32(4).ToString(CultureInfo.InvariantCulture)));
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ── Schema ────────────────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_writeLock)
        {
            if (_initialized) return;

            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

            using var connection = OpenConnection();
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                long current = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (current < SchemaVersion) CreateSchema(connection);
            }

            _initialized = true;
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS app (
              id            INTEGER PRIMARY KEY,
              app_key       TEXT NOT NULL UNIQUE,
              display_name  TEXT NOT NULL,
              exe_path      TEXT,
              aumid         TEXT,
              first_seen    INTEGER NOT NULL,
              icon_png      BLOB
            );

            CREATE TABLE IF NOT EXISTS usage_hour (
              day      TEXT    NOT NULL,
              hour     INTEGER NOT NULL,
              app_id   INTEGER NOT NULL REFERENCES app(id),
              seconds  INTEGER NOT NULL,
              opens    INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (day, hour, app_id)
            );

            CREATE TABLE IF NOT EXISTS coverage_hour (
              day TEXT NOT NULL,
              hour INTEGER NOT NULL,
              tracked_seconds INTEGER NOT NULL,
              PRIMARY KEY (day, hour)
            );

            CREATE TABLE IF NOT EXISTS window_title (
              day TEXT NOT NULL,
              app_id INTEGER NOT NULL,
              title TEXT NOT NULL,
              seconds INTEGER NOT NULL,
              PRIMARY KEY (day, app_id, title)
            );

            CREATE INDEX IF NOT EXISTS ix_usage_day ON usage_hour(day);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();

        try { await _writerTask.ConfigureAwait(false); } catch { /* shutdown best effort */ }

        Flush();
        _cts.Dispose();
    }
}
