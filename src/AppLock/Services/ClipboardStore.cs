using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.Data.Sqlite;

namespace AppLock.Services;

public sealed class ClipboardStore
{
    private static readonly string BaseDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock",
        "Clipboard");

    private static readonly string DatabasePath = Path.Combine(BaseDirectory, "clipboard.db");
    private static readonly string ImagesDirectory = Path.Combine(BaseDirectory, "images");
    private static readonly string ThumbsDirectory = Path.Combine(BaseDirectory, "thumbs");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isInitialized;

    public string StoragePath => BaseDirectory;
    public string DbFilePath => DatabasePath;

    public event EventHandler? DataChanged;

    public ClipboardStore()
    {
    }

    public void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(BaseDirectory);
            Directory.CreateDirectory(ImagesDirectory);
            Directory.CreateDirectory(ThumbsDirectory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Error creating directories: {ex.Message}");
        }
    }

    private string GetConnectionString()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return builder.ToString();
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_isInitialized) return;
            EnsureDirectories();

            try
            {
                await RunMigrationsAsync();
            }
            catch (SqliteException ex) when (IsCorruptionException(ex))
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardStore] SQLite corrupted. Recovering: {ex.Message}");
                RecoverFromCorruption();
                await RunMigrationsAsync();
            }

            _isInitialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool IsCorruptionException(SqliteException ex)
    {
        // SQLite error 11 is SQLITE_CORRUPT, 26 is SQLITE_NOTADB
        return ex.SqliteErrorCode == 11 || 
               ex.SqliteErrorCode == 26 || 
               ex.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase);
    }

    private void RecoverFromCorruption()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(DatabasePath))
            {
                string corruptPath = Path.Combine(BaseDirectory, $"clipboard.corrupt.{DateTime.Now:yyyyMMddHHmmss}.db");
                File.Move(DatabasePath, corruptPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Failed to rename corrupt database: {ex.Message}");
            try
            {
                if (File.Exists(DatabasePath))
                {
                    File.Delete(DatabasePath);
                }
            }
            catch { }
        }
    }

    private async Task RunMigrationsAsync()
    {
        using var conn = new SqliteConnection(GetConnectionString());
        await conn.OpenAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            long currentVersion = (long)(await cmd.ExecuteScalarAsync() ?? 0L);

            if (currentVersion < 1)
            {
                using var trans = conn.BeginTransaction();
                cmd.Transaction = trans;

                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS items (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  type          TEXT NOT NULL,
  content       BLOB,
  preview       BLOB,
  html          BLOB,
  hash          TEXT NOT NULL,
  image_path    TEXT,
  thumb_path    TEXT,
  width         INTEGER,
  height        INTEGER,
  size_bytes    INTEGER NOT NULL,
  source_process TEXT,
  source_title  TEXT,
  created_at    INTEGER NOT NULL,
  last_used_at  INTEGER NOT NULL,
  use_count     INTEGER NOT NULL DEFAULT 0,
  pinned        INTEGER NOT NULL DEFAULT 0,
  is_snippet    INTEGER NOT NULL DEFAULT 0,
  label         TEXT,
  is_sensitive  INTEGER NOT NULL DEFAULT 0,
  expires_at    INTEGER
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_items_hash ON items(hash) WHERE is_snippet = 0;
CREATE INDEX IF NOT EXISTS ix_items_last_used ON items(last_used_at DESC);

PRAGMA user_version = 1;
";
                await cmd.ExecuteNonQueryAsync();
                await trans.CommitAsync();
            }
        }
    }

    public async Task<long> InsertOrUpdateAsync(ClipboardItem item, int maxItems = 100)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        long insertedId = 0;
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (item.CreatedAt <= 0) item.CreatedAt = now;
            item.LastUsedAt = now;

            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT id, use_count FROM items WHERE hash = @hash AND is_snippet = 0 LIMIT 1;";
                checkCmd.Parameters.AddWithValue("@hash", item.Hash);

                using var reader = await checkCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    insertedId = reader.GetInt64(0);
                    int currentUse = reader.GetInt32(1);

                    using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = @"
UPDATE items 
SET last_used_at = @now,
    use_count = @useCount,
    source_process = COALESCE(@source_process, source_process),
    source_title = COALESCE(@source_title, source_title)
WHERE id = @id;
";
                    updateCmd.Parameters.AddWithValue("@now", now);
                    updateCmd.Parameters.AddWithValue("@useCount", currentUse + 1);
                    updateCmd.Parameters.AddWithValue("@source_process", (object?)item.SourceProcess ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@source_title", (object?)item.SourceTitle ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@id", insertedId);
                    await updateCmd.ExecuteNonQueryAsync();
                }
            }

            if (insertedId == 0)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = @"
INSERT INTO items (
  type, content, preview, html, hash, image_path, thumb_path,
  width, height, size_bytes, source_process, source_title,
  created_at, last_used_at, use_count, pinned, is_snippet,
  label, is_sensitive, expires_at
) VALUES (
  @type, @content, @preview, @html, @hash, @image_path, @thumb_path,
  @width, @height, @size_bytes, @source_process, @source_title,
  @created_at, @last_used_at, @use_count, @pinned, @is_snippet,
  @label, @is_sensitive, @expires_at
);
SELECT last_insert_rowid();
";
                insertCmd.Parameters.AddWithValue("@type", item.Type);
                insertCmd.Parameters.AddWithValue("@content", (object?)item.Content ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@preview", (object?)item.Preview ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@html", (object?)item.Html ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@hash", item.Hash);
                insertCmd.Parameters.AddWithValue("@image_path", (object?)item.ImagePath ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@thumb_path", (object?)item.ThumbPath ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@width", (object?)item.Width ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@height", (object?)item.Height ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@size_bytes", item.SizeBytes);
                insertCmd.Parameters.AddWithValue("@source_process", (object?)item.SourceProcess ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@source_title", (object?)item.SourceTitle ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@created_at", item.CreatedAt);
                insertCmd.Parameters.AddWithValue("@last_used_at", item.LastUsedAt);
                insertCmd.Parameters.AddWithValue("@use_count", item.UseCount);
                insertCmd.Parameters.AddWithValue("@pinned", item.Pinned ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@is_snippet", item.IsSnippet ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@label", (object?)item.Label ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@is_sensitive", item.IsSensitive ? 1 : 0);
                insertCmd.Parameters.AddWithValue("@expires_at", (object?)item.ExpiresAt ?? DBNull.Value);

                var scalar = await insertCmd.ExecuteScalarAsync();
                insertedId = Convert.ToInt64(scalar);
            }

            // Enforce limit
            await EnforceLimitInternalAsync(conn, maxItems);
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        return insertedId;
    }

    public async Task EnforceLimitAsync(int maxItems)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();
            await EnforceLimitInternalAsync(conn, maxItems);
        }
        finally
        {
            _lock.Release();
        }
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EnforceLimitInternalAsync(SqliteConnection conn, int maxItems)
    {
        if (maxItems < 10) maxItems = 10;

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM items WHERE is_snippet = 0;";
        long totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync() ?? 0L);

        if (totalCount <= maxItems) return;

        long excess = totalCount - maxItems;

        // Query oldest unpinned non-snippet items to delete
        var idsToDelete = new List<long>();
        var filesToDelete = new List<string>();

        using (var findCmd = conn.CreateCommand())
        {
            findCmd.CommandText = @"
SELECT id, image_path, thumb_path 
FROM items 
WHERE pinned = 0 AND is_snippet = 0 
ORDER BY last_used_at ASC 
LIMIT @excess;
";
            findCmd.Parameters.AddWithValue("@excess", excess);
            using var reader = await findCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                idsToDelete.Add(reader.GetInt64(0));
                if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                if (!reader.IsDBNull(2)) filesToDelete.Add(reader.GetString(2));
            }
        }

        if (idsToDelete.Count > 0)
        {
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = $"DELETE FROM items WHERE id IN ({string.Join(",", idsToDelete)});";
            await delCmd.ExecuteNonQueryAsync();

            // Delete files on disk
            foreach (var path in filesToDelete)
            {
                DeleteFileSafe(path);
            }
        }
    }

    public async Task<int> GetItemCountAsync()
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE is_snippet = 0;";
            var res = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(res ?? 0);
        }
        catch
        {
            return 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ClipboardItem>> GetItemsAsync(int limit = 100, string? filter = null, string? search = null)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        var list = new List<ClipboardItem>();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();

            var whereClauses = new List<string>();
            whereClauses.Add("is_snippet = 0");

            if (!string.IsNullOrEmpty(filter) && filter != "All")
            {
                if (filter == "Text") whereClauses.Add("type = 'text'");
                else if (filter == "Images") whereClauses.Add("type = 'image'");
                else if (filter == "Files") whereClauses.Add("type = 'files'");
                else if (filter == "Pinned") whereClauses.Add("pinned = 1");
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                whereClauses.Add("(source_process LIKE @search OR source_title LIKE @search OR label LIKE @search)");
                cmd.Parameters.AddWithValue("@search", $"%{search.Trim()}%");
            }

            string where = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            cmd.CommandText = $@"
SELECT id, type, content, preview, html, hash, image_path, thumb_path,
       width, height, size_bytes, source_process, source_title,
       created_at, last_used_at, use_count, pinned, is_snippet,
       label, is_sensitive, expires_at
FROM items
{where}
ORDER BY last_used_at DESC
LIMIT @limit;
";
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var item = new ClipboardItem
                {
                    Id = reader.GetInt64(0),
                    Type = reader.GetString(1),
                    Content = reader.IsDBNull(2) ? null : (byte[])reader["content"],
                    Preview = reader.IsDBNull(3) ? null : (byte[])reader["preview"],
                    Html = reader.IsDBNull(4) ? null : (byte[])reader["html"],
                    Hash = reader.GetString(5),
                    ImagePath = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ThumbPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Width = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    Height = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    SizeBytes = reader.GetInt64(10),
                    SourceProcess = reader.IsDBNull(11) ? null : reader.GetString(11),
                    SourceTitle = reader.IsDBNull(12) ? null : reader.GetString(12),
                    CreatedAt = reader.GetInt64(13),
                    LastUsedAt = reader.GetInt64(14),
                    UseCount = reader.GetInt32(15),
                    Pinned = reader.GetInt32(16) == 1,
                    IsSnippet = reader.GetInt32(17) == 1,
                    Label = reader.IsDBNull(18) ? null : reader.GetString(18),
                    IsSensitive = reader.GetInt32(19) == 1,
                    ExpiresAt = reader.IsDBNull(20) ? null : reader.GetInt64(20)
                };
                list.Add(item);
            }
        }
        finally
        {
            _lock.Release();
        }
        return list;
    }

    public async Task<bool> TogglePinAsync(long id)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        bool newPinned = false;
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE items SET pinned = CASE WHEN pinned = 1 THEN 0 ELSE 1 END WHERE id = @id;";
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }

            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT pinned FROM items WHERE id = @id;";
                checkCmd.Parameters.AddWithValue("@id", id);
                var res = await checkCmd.ExecuteScalarAsync();
                if (res != null && res != DBNull.Value)
                {
                    newPinned = Convert.ToInt32(res) == 1;
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        return newPinned;
    }

    public async Task UpdateUsageAsync(long id)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE items 
SET use_count = use_count + 1, 
    last_used_at = @now 
WHERE id = @id;
";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Error updating usage for {id}: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteItemsAsync(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        await InitializeAsync();
        await _lock.WaitAsync();
        var filesToDelete = new List<string>();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            string inClause = string.Join(",", idList);
            using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = $"SELECT image_path, thumb_path FROM items WHERE id IN ({inClause});";
                using var reader = await findCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) filesToDelete.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                }
            }

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = $"DELETE FROM items WHERE id IN ({inClause});";
                await delCmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            _lock.Release();
        }

        foreach (var path in filesToDelete)
        {
            DeleteFileSafe(path);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteItemAsync(long id)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        string? imagePath = null;
        string? thumbPath = null;
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = "SELECT image_path, thumb_path FROM items WHERE id = @id;";
                findCmd.Parameters.AddWithValue("@id", id);
                using var reader = await findCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) imagePath = reader.GetString(0);
                    if (!reader.IsDBNull(1)) thumbPath = reader.GetString(1);
                }
            }

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM items WHERE id = @id;";
                delCmd.Parameters.AddWithValue("@id", id);
                await delCmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            _lock.Release();
        }

        if (imagePath != null) DeleteFileSafe(imagePath);
        if (thumbPath != null) DeleteFileSafe(thumbPath);

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearHistoryAsync(bool includePinned = false)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        var filesToDelete = new List<string>();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            string where = includePinned ? "WHERE is_snippet = 0" : "WHERE pinned = 0 AND is_snippet = 0";

            using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = $"SELECT image_path, thumb_path FROM items {where};";
                using var reader = await findCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0)) filesToDelete.Add(reader.GetString(0));
                    if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                }
            }

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = $"DELETE FROM items {where};";
                await delCmd.ExecuteNonQueryAsync();
            }

            using (var vacCmd = conn.CreateCommand())
            {
                vacCmd.CommandText = "VACUUM;";
                await vacCmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            _lock.Release();
        }

        foreach (var path in filesToDelete)
        {
            DeleteFileSafe(path);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<long> GetStorageSizeBytesAsync()
    {
        return await Task.Run(() =>
        {
            long total = 0;
            try
            {
                if (File.Exists(DatabasePath))
                {
                    total += new FileInfo(DatabasePath).Length;
                }

                if (Directory.Exists(ImagesDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(ImagesDirectory, "*.*", SearchOption.AllDirectories))
                    {
                        total += new FileInfo(file).Length;
                    }
                }

                if (Directory.Exists(ThumbsDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(ThumbsDirectory, "*.*", SearchOption.AllDirectories))
                    {
                        total += new FileInfo(file).Length;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Error getting storage size: {ex.Message}");
            }
            return total;
        });
    }

    public async Task AddDebugItemsAsync(int count, int maxItems = 100)
    {
        for (int i = 0; i < count; i++)
        {
            string text = $"Test clipboard item #{Guid.NewGuid().ToString("N")[..6]} - created at {DateTime.Now:HH:mm:ss.fff}";
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
            string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var item = new ClipboardItem
            {
                Type = "text",
                ContentString = text,
                PreviewString = text,
                Hash = hash,
                SizeBytes = text.Length,
                SourceProcess = "AppLock.exe",
                SourceTitle = "AppLock Debug",
                UseCount = 1,
                Pinned = false
            };

            await InsertOrUpdateAsync(item, maxItems);
            // Brief pause between inserts so timestamps differ
            await Task.Delay(20);
        }
    }

    private static void DeleteFileSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }
}

