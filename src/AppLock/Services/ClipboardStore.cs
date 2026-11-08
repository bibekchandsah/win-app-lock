using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.Data.Sqlite;

namespace AppLock.Services;

public sealed class ClipboardStore
{
    private static readonly byte[] s_dpapiHeader = new byte[] { 0x44, 0x50, 0x41, 0x50 }; // "DPAP"
    private static readonly byte[] s_optionalEntropy = new byte[] { 0x41, 0x70, 0x70, 0x4C, 0x6F, 0x63, 0x6B, 0x43, 0x6C, 0x69, 0x70 }; // "AppLockClip"

    private static readonly string BaseDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock",
        "Clipboard");

    private static readonly string DatabasePath = Path.Combine(BaseDirectory, "clipboard.db");
    private static readonly string ImagesDirectory = Path.Combine(BaseDirectory, "images");
    private static readonly string ThumbsDirectory = Path.Combine(BaseDirectory, "thumbs");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _expireTimer;
    private bool _isInitialized;

    public string StoragePath => BaseDirectory;
    public string DbFilePath => DatabasePath;

    public event EventHandler? DataChanged;

    public ClipboardStore()
    {
        // Periodic check to delete expired sensitive items and enforce storage cap / auto-delete
        _expireTimer = new Timer(async _ =>
        {
            try
            {
                await DeleteExpiredItemsAsync();
                await EnforceStorageCapAndAutoDeleteAsync();
            }
            catch { }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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
                bool encrypt = ServiceContainer.Settings.Clipboard.EncryptHistory;
                byte[]? contentBytes = encrypt && item.Content != null ? EncryptBytes(item.Content) : item.Content;
                byte[]? previewBytes = encrypt && item.Preview != null ? EncryptBytes(item.Preview) : item.Preview;
                byte[]? htmlBytes = encrypt && item.Html != null ? EncryptBytes(item.Html) : item.Html;

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
                insertCmd.Parameters.AddWithValue("@content", (object?)contentBytes ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@preview", (object?)previewBytes ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@html", (object?)htmlBytes ?? DBNull.Value);
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

            // Purge expired sensitive items first
            await DeleteExpiredInternalAsync(conn);

            using var cmd = conn.CreateCommand();

            var whereClauses = new List<string>();
            if (filter == "Snippets")
            {
                whereClauses.Add("is_snippet = 1");
            }
            else
            {
                whereClauses.Add("is_snippet = 0");

                if (!string.IsNullOrEmpty(filter) && filter != "All")
                {
                    if (filter == "Text") whereClauses.Add("type = 'text'");
                    else if (filter == "Images") whereClauses.Add("type = 'image'");
                    else if (filter == "Files") whereClauses.Add("type = 'files'");
                    else if (filter == "Pinned") whereClauses.Add("pinned = 1");
                }
            }

            string where = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
            int fetchLimit = string.IsNullOrWhiteSpace(search) ? limit : Math.Max(limit * 5, 500);

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
            cmd.Parameters.AddWithValue("@limit", fetchLimit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                byte[]? rawContent = reader.IsDBNull(2) ? null : (byte[])reader["content"];
                byte[]? rawPreview = reader.IsDBNull(3) ? null : (byte[])reader["preview"];
                byte[]? rawHtml = reader.IsDBNull(4) ? null : (byte[])reader["html"];

                var item = new ClipboardItem
                {
                    Id = reader.GetInt64(0),
                    Type = reader.GetString(1),
                    Content = DecryptBytes(rawContent),
                    Preview = DecryptBytes(rawPreview),
                    Html = DecryptBytes(rawHtml),
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

            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim();
                list = list.FindAll(item =>
                    (item.PreviewString != null && item.PreviewString.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (item.ContentString != null && item.ContentString.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (item.SourceProcess != null && item.SourceProcess.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (item.SourceTitle != null && item.SourceTitle.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                    (item.Label != null && item.Label.Contains(s, StringComparison.OrdinalIgnoreCase))
                );

                if (list.Count > limit)
                {
                    list = list.Take(limit).ToList();
                }
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

    public async Task<int> DeleteExpiredItemsAsync()
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        int deleted = 0;
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();
            deleted = await DeleteExpiredInternalAsync(conn);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Error deleting expired items: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }

        if (deleted > 0)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        return deleted;
    }

    private static async Task<int> DeleteExpiredInternalAsync(SqliteConnection conn)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var filesToDelete = new List<string>();

        using (var findCmd = conn.CreateCommand())
        {
            findCmd.CommandText = "SELECT image_path, thumb_path FROM items WHERE expires_at IS NOT NULL AND expires_at <= @now;";
            findCmd.Parameters.AddWithValue("@now", now);
            using var reader = await findCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0)) filesToDelete.Add(reader.GetString(0));
                if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
            }
        }

        int deletedCount = 0;
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM items WHERE expires_at IS NOT NULL AND expires_at <= @now;";
            delCmd.Parameters.AddWithValue("@now", now);
            deletedCount = await delCmd.ExecuteNonQueryAsync();
        }

        foreach (var path in filesToDelete)
        {
            DeleteFileSafe(path);
        }

        return deletedCount;
    }

    public async Task ReEncryptAllAsync(bool encrypt)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            var updates = new List<(long Id, byte[]? Content, byte[]? Preview, byte[]? Html)>();

            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = "SELECT id, content, preview, html FROM items;";
                using var reader = await readCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(0);
                    byte[]? content = reader.IsDBNull(1) ? null : (byte[])reader["content"];
                    byte[]? preview = reader.IsDBNull(2) ? null : (byte[])reader["preview"];
                    byte[]? html = reader.IsDBNull(3) ? null : (byte[])reader["html"];

                    byte[]? newContent = content;
                    byte[]? newPreview = preview;
                    byte[]? newHtml = html;

                    if (encrypt)
                    {
                        if (content != null && !IsEncrypted(content)) newContent = EncryptBytes(content);
                        if (preview != null && !IsEncrypted(preview)) newPreview = EncryptBytes(preview);
                        if (html != null && !IsEncrypted(html)) newHtml = EncryptBytes(html);
                    }
                    else
                    {
                        if (content != null && IsEncrypted(content)) newContent = DecryptBytes(content);
                        if (preview != null && IsEncrypted(preview)) newPreview = DecryptBytes(preview);
                        if (html != null && IsEncrypted(html)) newHtml = DecryptBytes(html);
                    }

                    updates.Add((id, newContent, newPreview, newHtml));
                }
            }

            using var trans = conn.BeginTransaction();
            foreach (var u in updates)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = trans;
                updateCmd.CommandText = "UPDATE items SET content = @c, preview = @p, html = @h WHERE id = @id;";
                updateCmd.Parameters.AddWithValue("@c", (object?)u.Content ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@p", (object?)u.Preview ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@h", (object?)u.Html ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@id", u.Id);
                await updateCmd.ExecuteNonQueryAsync();
            }
            await trans.CommitAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Re-encryption error: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public static byte[] EncryptBytes(byte[]? data)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();
        try
        {
            byte[] encrypted = ProtectedData.Protect(data, s_optionalEntropy, DataProtectionScope.CurrentUser);
            byte[] result = new byte[s_dpapiHeader.Length + encrypted.Length];
            Buffer.BlockCopy(s_dpapiHeader, 0, result, 0, s_dpapiHeader.Length);
            Buffer.BlockCopy(encrypted, 0, result, s_dpapiHeader.Length, encrypted.Length);
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] DPAPI Protect error: {ex.Message}");
            return data;
        }
    }

    public static byte[] DecryptBytes(byte[]? data)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();

        if (IsEncrypted(data))
        {
            try
            {
                byte[] encrypted = new byte[data.Length - s_dpapiHeader.Length];
                Buffer.BlockCopy(data, s_dpapiHeader.Length, encrypted, 0, encrypted.Length);
                return ProtectedData.Unprotect(encrypted, s_optionalEntropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardStore] DPAPI Unprotect error: {ex.Message}");
                return data;
            }
        }

        return data;
    }

    public static bool IsEncrypted(byte[]? data)
    {
        if (data == null || data.Length <= s_dpapiHeader.Length) return false;
        return data[0] == s_dpapiHeader[0] &&
               data[1] == s_dpapiHeader[1] &&
               data[2] == s_dpapiHeader[2] &&
               data[3] == s_dpapiHeader[3];
    }

    public async Task<List<ClipboardItem>> GetSnippetsAsync()
    {
        return await GetItemsAsync(limit: 500, filter: "Snippets");
    }

    public async Task<long> SaveSnippetAsync(string title, string text, string? abbreviation = null, long id = 0)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        long snippetId = id;
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            bool encrypt = ServiceContainer.Settings.Clipboard.EncryptHistory;
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] contentBytes = encrypt ? EncryptBytes(textBytes) : textBytes;
            string prev = text.Length > 200 ? text[..200] : text;
            byte[] previewBytes = encrypt ? EncryptBytes(System.Text.Encoding.UTF8.GetBytes(prev)) : System.Text.Encoding.UTF8.GetBytes(prev);

            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(textBytes)).ToLowerInvariant();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (id > 0)
            {
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = @"
UPDATE items 
SET label = @title,
    content = @content,
    preview = @preview,
    source_title = @abbrev,
    last_used_at = @now,
    size_bytes = @size
WHERE id = @id;
";
                updateCmd.Parameters.AddWithValue("@title", title);
                updateCmd.Parameters.AddWithValue("@content", (object?)contentBytes ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@preview", (object?)previewBytes ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@abbrev", (object?)abbreviation ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@now", now);
                updateCmd.Parameters.AddWithValue("@size", textBytes.Length);
                updateCmd.Parameters.AddWithValue("@id", id);
                await updateCmd.ExecuteNonQueryAsync();
            }
            else
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = @"
INSERT INTO items (
  type, content, preview, hash, size_bytes, source_process, source_title,
  created_at, last_used_at, use_count, pinned, is_snippet, label
) VALUES (
  'text', @content, @preview, @hash, @size, 'Snippet', @abbrev,
  @now, @now, 0, 1, 1, @title
);
SELECT last_insert_rowid();
";
                insertCmd.Parameters.AddWithValue("@content", (object?)contentBytes ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@preview", (object?)previewBytes ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@hash", hash);
                insertCmd.Parameters.AddWithValue("@size", textBytes.Length);
                insertCmd.Parameters.AddWithValue("@abbrev", (object?)abbreviation ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@now", now);
                insertCmd.Parameters.AddWithValue("@title", title);

                var res = await insertCmd.ExecuteScalarAsync();
                snippetId = Convert.ToInt64(res);
            }
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        return snippetId;
    }

    public async Task DeleteSnippetAsync(long id)
    {
        await DeleteItemAsync(id);
    }

    public async Task UpdateTextItemContentAsync(long id, string newText)
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            bool encrypt = ServiceContainer.Settings.Clipboard.EncryptHistory;
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(newText);
            byte[] contentBytes = encrypt ? EncryptBytes(textBytes) : textBytes;
            string previewStr = newText.Length > 200 ? newText[..200] : newText;
            byte[] previewBytes = encrypt ? EncryptBytes(System.Text.Encoding.UTF8.GetBytes(previewStr)) : System.Text.Encoding.UTF8.GetBytes(previewStr);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE items 
SET content = @content, 
    preview = @preview, 
    size_bytes = @size, 
    last_used_at = @now 
WHERE id = @id;
";
            cmd.Parameters.AddWithValue("@content", (object?)contentBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@preview", (object?)previewBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@size", textBytes.Length);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnforceStorageCapAndAutoDeleteAsync()
    {
        await InitializeAsync();
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(GetConnectionString());
            await conn.OpenAsync();

            var settings = ServiceContainer.Settings.Clipboard;

            // 1. Auto-delete after N days (unpinned non-snippets only; 0 = never)
            if (settings.AutoDeleteDays > 0)
            {
                long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ((long)settings.AutoDeleteDays * 86400000L);
                var filesToDelete = new List<string>();

                using (var findCmd = conn.CreateCommand())
                {
                    findCmd.CommandText = "SELECT image_path, thumb_path FROM items WHERE pinned = 0 AND is_snippet = 0 AND last_used_at < @cutoff;";
                    findCmd.Parameters.AddWithValue("@cutoff", cutoff);
                    using var reader = await findCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) filesToDelete.Add(reader.GetString(0));
                        if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                    }
                }

                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.CommandText = "DELETE FROM items WHERE pinned = 0 AND is_snippet = 0 AND last_used_at < @cutoff;";
                    delCmd.Parameters.AddWithValue("@cutoff", cutoff);
                    await delCmd.ExecuteNonQueryAsync();
                }

                foreach (var file in filesToDelete)
                {
                    DeleteFileSafe(file);
                }
            }

            // 2. Storage cap enforcement (MB)
            if (settings.StorageCapMb > 0)
            {
                long capBytes = (long)settings.StorageCapMb * 1024 * 1024;
                long currentUsage = await GetStorageSizeBytesAsync();

                if (currentUsage > capBytes)
                {
                    var filesToDelete = new List<string>();
                    var idsToDelete = new List<long>();

                    using (var findCmd = conn.CreateCommand())
                    {
                        findCmd.CommandText = @"
SELECT id, image_path, thumb_path, size_bytes 
FROM items 
WHERE pinned = 0 AND is_snippet = 0 
ORDER BY last_used_at ASC;
";
                        using var reader = await findCmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync() && currentUsage > capBytes)
                        {
                            idsToDelete.Add(reader.GetInt64(0));
                            if (!reader.IsDBNull(1)) filesToDelete.Add(reader.GetString(1));
                            if (!reader.IsDBNull(2)) filesToDelete.Add(reader.GetString(2));
                            long size = reader.GetInt64(3);
                            currentUsage -= size;
                        }
                    }

                    if (idsToDelete.Count > 0)
                    {
                        using (var delCmd = conn.CreateCommand())
                        {
                            delCmd.CommandText = $"DELETE FROM items WHERE id IN ({string.Join(",", idsToDelete)});";
                            await delCmd.ExecuteNonQueryAsync();
                        }

                        foreach (var file in filesToDelete)
                        {
                            DeleteFileSafe(file);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardStore] Error enforcing cap/auto-delete: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
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

