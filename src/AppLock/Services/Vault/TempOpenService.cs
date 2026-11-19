using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

public class TempSessionMetadata
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public Guid VaultId { get; set; }
    public Guid ItemId { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string TempFileName { get; set; } = string.Empty;
    public string TempFilePath { get; set; } = string.Empty;
    public string SessionFolderPath { get; set; } = string.Empty;
    public string InitialHash { get; set; } = string.Empty;
    public DateTime ExtractedTimeUtc { get; set; } = DateTime.UtcNow;
    public bool IsModified { get; set; }
}

public sealed class ActiveTempSession : IDisposable
{
    public TempSessionMetadata Metadata { get; }
    public VaultSession VaultSession { get; }
    public FileSystemWatcher? Watcher { get; set; }
    public bool IsModified => Metadata.IsModified;

    public ActiveTempSession(TempSessionMetadata metadata, VaultSession vaultSession)
    {
        Metadata = metadata;
        VaultSession = vaultSession;
    }

    public void Dispose()
    {
        Watcher?.Dispose();
        Watcher = null;
    }
}

public static class TempOpenService
{
    public static readonly string TempRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock",
        "VaultTemp");

    private static readonly Dictionary<Guid, ActiveTempSession> _activeSessions = new();
    private static readonly object _lock = new();

    public static event EventHandler<int>? OpenCountChanged;

    public static int ActiveSessionCount
    {
        get
        {
            lock (_lock) return _activeSessions.Count;
        }
    }

    public static IReadOnlyList<ActiveTempSession> GetActiveSessions()
    {
        lock (_lock) return new List<ActiveTempSession>(_activeSessions.Values);
    }

    public static IReadOnlyList<ActiveTempSession> GetActiveSessionsForVault(Guid vaultId)
    {
        lock (_lock)
        {
            var list = new List<ActiveTempSession>();
            foreach (var s in _activeSessions.Values)
            {
                if (s.Metadata.VaultId == vaultId) list.Add(s);
            }
            return list;
        }
    }

    /// <summary>
    /// Opens an item with its default application through a protected temporary decrypted copy.
    /// </summary>
    public static async Task<ActiveTempSession> OpenWithDefaultAppAsync(
        VaultSession session,
        IndexItem item,
        bool useGenericName = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(item.ObjectId))
            throw new ArgumentException("Item has no encrypted object ID.", nameof(item));

        Directory.CreateDirectory(TempRoot);

        // 1. Create unique private session folder
        var sessionId = Guid.NewGuid();
        var sessionFolder = Path.Combine(TempRoot, sessionId.ToString("N"));
        Directory.CreateDirectory(sessionFolder);

        // Mark folder not content-indexed and apply private attributes
        try
        {
            var dirInfo = new DirectoryInfo(sessionFolder);
            dirInfo.Attributes |= FileAttributes.NotContentIndexed;
        }
        catch { }

        // 2. Determine file name (generic e.g. item-3f9a.docx by default)
        var ext = Path.GetExtension(item.Name);
        var tempFileName = useGenericName
            ? $"item-{sessionId.ToString("N").Substring(0, 4)}{ext}"
            : item.Name;
        var tempFilePath = Path.Combine(sessionFolder, tempFileName);

        // 3. Decrypt into session folder
        string initialHash;
        using (var inStream = PreviewService.OpenStream(session, item))
        using (var outStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sha = SHA256.Create())
        using (var hashStream = new CryptoStream(outStream, sha, CryptoStreamMode.Write))
        {
            await inStream.CopyToAsync(hashStream, 65536, ct);
            hashStream.FlushFinalBlock();
            initialHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        // 4. Save session.json for crash recovery
        var meta = new TempSessionMetadata
        {
            SessionId = sessionId,
            VaultId = session.VaultId,
            ItemId = item.Id,
            OriginalName = item.Name,
            TempFileName = tempFileName,
            TempFilePath = tempFilePath,
            SessionFolderPath = sessionFolder,
            InitialHash = initialHash,
            ExtractedTimeUtc = DateTime.UtcNow,
            IsModified = false
        };

        var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(sessionFolder, "session.json"), metaJson);

        var activeSession = new ActiveTempSession(meta, session);

        // 5. Watch for changes
        try
        {
            var watcher = new FileSystemWatcher(sessionFolder, tempFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watcher.Changed += (s, e) =>
            {
                activeSession.Metadata.IsModified = true;
                try
                {
                    // Re-save session metadata
                    File.WriteAllText(Path.Combine(sessionFolder, "session.json"),
                        JsonSerializer.Serialize(activeSession.Metadata));
                }
                catch { }
            };

            activeSession.Watcher = watcher;
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Could not setup watcher: {ex.Message}");
        }

        lock (_lock)
        {
            _activeSessions[sessionId] = activeSession;
        }
        OpenCountChanged?.Invoke(null, ActiveSessionCount);

        // 6. Launch default app
        try
        {
            Process.Start(new ProcessStartInfo(tempFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Failed to launch default app for '{tempFilePath}': {ex.Message}");
        }

        return activeSession;
    }

    /// <summary>
    /// Closes a temp session: re-encrypts changes back into vault if modified, wipes plaintext, and removes session folder.
    /// </summary>
    public static async Task CloseSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        ActiveTempSession? active;
        lock (_lock)
        {
            if (!_activeSessions.Remove(sessionId, out active))
                return;
        }
        OpenCountChanged?.Invoke(null, ActiveSessionCount);

        active.Dispose();

        try
        {
            if (File.Exists(active.Metadata.TempFilePath))
            {
                // Check if content changed
                string currentHash;
                using (var fs = new FileStream(active.Metadata.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    currentHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                }

                if (!string.Equals(currentHash, active.Metadata.InitialHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Re-encrypt changes back into vault
                    await WriteBackAsync(active, ct);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Write-back error for session {sessionId}: {ex.Message}");
        }
        finally
        {
            // Secure delete temporary folder
            SecureDelete.WipeAndDeleteDirectory(active.Metadata.SessionFolderPath);
        }
    }

    /// <summary>
    /// Closes and flushes all sessions associated with a specific vault (e.g. on lock).
    /// </summary>
    public static async Task CloseAllSessionsForVaultAsync(Guid vaultId, CancellationToken ct = default)
    {
        var sessions = GetActiveSessionsForVault(vaultId);
        foreach (var s in sessions)
        {
            await CloseSessionAsync(s.Metadata.SessionId, ct);
        }
    }

    /// <summary>
    /// Closes and flushes all sessions across all vaults (e.g. on AppLock exit or emergency lock).
    /// </summary>
    public static async Task CloseAllSessionsAsync(CancellationToken ct = default)
    {
        var sessions = GetActiveSessions();
        foreach (var s in sessions)
        {
            await CloseSessionAsync(s.Metadata.SessionId, ct);
        }
    }

    /// <summary>
    /// Re-encrypts an edited temp file back into its vault.
    /// </summary>
    private static async Task WriteBackAsync(ActiveTempSession active, CancellationToken ct)
    {
        var session = active.VaultSession;
        var meta = active.Metadata;

        IndexItem? item;
        lock (session)
        {
            item = session.Index.Items.Find(i => i.Id == meta.ItemId);
        }
        if (item == null) return;

        var newObjectId = $"{Guid.NewGuid():N}.obj";
        var finalObjPath = VaultStorage.GetObjectPath(session.VaultPath, newObjectId, isTrash: false);
        var tempObjPath = finalObjPath + ".tmp";

        long plainLength;
        string sha256Hex;
        using (var src = new FileStream(meta.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dst = new FileStream(tempObjPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            (plainLength, sha256Hex) = await Task.Run(() =>
                FileObjectWriter.WriteObject(src, dst, session.FekWrapKey, item.Id), ct);
        }

        if (File.Exists(finalObjPath)) File.Delete(finalObjPath);
        File.Move(tempObjPath, finalObjPath);

        // Delete old object file
        if (!string.IsNullOrEmpty(item.ObjectId))
        {
            var oldPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, isTrash: false);
            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        }

        lock (session)
        {
            item.ObjectId = newObjectId;
            item.Size = plainLength;
            item.Sha256 = sha256Hex;
            item.ModifiedTimeUtc = DateTime.UtcNow;
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }

        LogService.LogAction("Vault", $"Re-encrypted edits for '{item.Name}' back into vault.");
    }

    /// <summary>
    /// Scans the TempRoot for leftover session folders from past unexpected shutdowns/crashes.
    /// </summary>
    public static List<TempSessionMetadata> ScanCrashRecoverySessions()
    {
        var recovered = new List<TempSessionMetadata>();
        if (!Directory.Exists(TempRoot)) return recovered;

        foreach (var dir in Directory.EnumerateDirectories(TempRoot))
        {
            var sessionFile = Path.Combine(dir, "session.json");
            if (File.Exists(sessionFile))
            {
                try
                {
                    var json = File.ReadAllText(sessionFile);
                    var meta = JsonSerializer.Deserialize<TempSessionMetadata>(json);
                    if (meta != null && File.Exists(meta.TempFilePath))
                    {
                        recovered.Add(meta);
                    }
                    else
                    {
                        // Stale empty session folder
                        SecureDelete.WipeAndDeleteDirectory(dir);
                    }
                }
                catch
                {
                    SecureDelete.WipeAndDeleteDirectory(dir);
                }
            }
            else
            {
                SecureDelete.WipeAndDeleteDirectory(dir);
            }
        }

        return recovered;
    }
}
