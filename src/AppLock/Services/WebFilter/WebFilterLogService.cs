using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.Data.Sqlite;

namespace AppLock.Services.WebFilter;

public sealed record WebFilterStats(int BlockedToday, int BlockedThisWeek, string TopBlockedCategory);

public sealed record TopBlockedDomain(string Domain, int Count);

public sealed record WebFilterLogGroup(
    string Domain,
    string Reason,
    DateTime LatestTimestampUtc,
    int Count);

public sealed class WebFilterLogService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private bool _initialized;

    public event EventHandler? LogChanged;

    public WebFilterLogService(AppSettings settings, string? dbPath = null)
    {
        _settings = settings;

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "AppLock");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "webfilter.db");
        }
        else
        {
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            await using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS activity_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    domain TEXT NOT NULL,
    blocked INTEGER NOT NULL,
    reason TEXT,
    timestamp_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_activity_time ON activity_log(timestamp_utc);
CREATE INDEX IF NOT EXISTS idx_activity_domain ON activity_log(domain);
CREATE INDEX IF NOT EXISTS idx_activity_blocked ON activity_log(blocked);
";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _initialized = true;

            await PruneInternalAsync(conn).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterLog] InitializeAsync failed: {ex.Message}");
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task LogQueryAsync(string domain, bool blocked, string? reason, DateTime timestampUtc)
    {
        if (blocked && !_settings.WebFilter.LogBlocked) return;
        if (!blocked && !_settings.WebFilter.LogAllowed) return;

        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            await _dbGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO activity_log (domain, blocked, reason, timestamp_utc)
VALUES (@domain, @blocked, @reason, @time);
";
                cmd.Parameters.AddWithValue("@domain", domain);
                cmd.Parameters.AddWithValue("@blocked", blocked ? 1 : 0);
                cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@time", timestampUtc.ToString("o"));

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                _dbGate.Release();
            }

            LogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterLog] LogQueryAsync failed: {ex.Message}");
        }
    }

    public async Task ClearLogAsync()
    {
        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            await _dbGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM activity_log;";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                _dbGate.Release();
            }

            LogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterLog] ClearLogAsync failed: {ex.Message}");
        }
    }

    public async Task<WebFilterStats> GetStatsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        int todayCount = 0;
        int weekCount = 0;
        string topCategory = "None";

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var todayStartUtc = DateTime.Today.ToUniversalTime().ToString("o");
            var weekStartUtc = DateTime.UtcNow.AddDays(-7).ToString("o");

            // Today blocked
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM activity_log WHERE blocked = 1 AND timestamp_utc >= @start;";
                cmd.Parameters.AddWithValue("@start", todayStartUtc);
                var val = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                todayCount = Convert.ToInt32(val);
            }

            // Week blocked
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM activity_log WHERE blocked = 1 AND timestamp_utc >= @start;";
                cmd.Parameters.AddWithValue("@start", weekStartUtc);
                var val = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                weekCount = Convert.ToInt32(val);
            }

            // Top category this week
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT reason, COUNT(*) as cnt
FROM activity_log
WHERE blocked = 1 AND reason IS NOT NULL AND reason != '' AND timestamp_utc >= @start
GROUP BY reason
ORDER BY cnt DESC
LIMIT 1;
";
                cmd.Parameters.AddWithValue("@start", weekStartUtc);
                await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    topCategory = reader.GetString(0);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterLog] GetStatsAsync failed: {ex.Message}");
        }
        finally
        {
            _dbGate.Release();
        }

        return new WebFilterStats(todayCount, weekCount, topCategory);
    }

    public async Task<List<WebFilterLogGroup>> GetRecentBlockedGroupsAsync(int limit = 100)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        var rawList = new List<(string Domain, string Reason, DateTime Time)>();

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            // Fetch up to 500 recent blocked events to group
            cmd.CommandText = @"
SELECT domain, reason, timestamp_utc
FROM activity_log
WHERE blocked = 1
ORDER BY timestamp_utc DESC
LIMIT 500;
";
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                string domain = reader.GetString(0);
                string reason = reader.IsDBNull(1) ? "Blocked" : reader.GetString(1);
                string timeStr = reader.GetString(2);
                if (DateTime.TryParse(timeStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                {
                    rawList.Add((domain, reason, dt));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterLog] GetRecentBlockedGroupsAsync failed: {ex.Message}");
        }
        finally
        {
            _dbGate.Release();
        }

        // Group repeats: one row per domain per minute with count badge (spec 7.2)
        var groups = new List<WebFilterLogGroup>();
        var seen = new Dictionary<string, (DateTime LatestTime, string Reason, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in rawList)
        {
            // Bucket key: domain + minute timestamp
            var minuteBucket = new DateTime(entry.Time.Year, entry.Time.Month, entry.Time.Day, entry.Time.Hour, entry.Time.Minute, 0, DateTimeKind.Utc);
            string key = $"{entry.Domain}@@{minuteBucket:yyyyMMddHHmm}@@{entry.Reason}";

            if (seen.TryGetValue(key, out var val))
            {
                seen[key] = (val.LatestTime > entry.Time ? val.LatestTime : entry.Time, val.Reason, val.Count + 1);
            }
            else
            {
                seen[key] = (entry.Time, entry.Reason, 1);
            }
        }

        foreach (var pair in seen.OrderByDescending(p => p.Value.LatestTime).Take(limit))
        {
            string domain = pair.Key.Split("@@")[0];
            groups.Add(new WebFilterLogGroup(domain, pair.Value.Reason, pair.Value.LatestTime, pair.Value.Count));
        }

        return groups;
    }

    public async Task<List<TopBlockedDomain>> GetTopBlockedDomainsAsync(int days = 7, int limit = 10)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        var list = new List<TopBlockedDomain>();

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var cutoff = DateTime.UtcNow.AddDays(-days).ToString("o");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT domain, COUNT(*) as cnt
FROM activity_log
WHERE blocked = 1 AND timestamp_utc >= @cutoff
GROUP BY domain
ORDER BY cnt DESC
LIMIT @limit;
";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            cmd.Parameters.AddWithValue("@limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                string domain = reader.GetString(0);
                int count = reader.GetInt32(1);
                list.Add(new TopBlockedDomain(domain, count));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebFilterLog] GetTopBlockedDomainsAsync failed: {ex.Message}");
        }
        finally
        {
            _dbGate.Release();
        }

        return list;
    }

    public async Task PruneOldEntriesAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        await _dbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await PruneInternalAsync(conn).ConfigureAwait(false);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    private async Task PruneInternalAsync(SqliteConnection conn)
    {
        int retentionDays = _settings.WebFilter.LogRetentionDays;
        if (retentionDays <= 0) retentionDays = 30;

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM activity_log WHERE timestamp_utc < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _dbGate.Dispose();
    }
}
