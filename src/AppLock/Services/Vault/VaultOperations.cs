using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

public record AddFileResult(bool Success, string FileName, string? ErrorMessage = null);

public static class VaultOperations
{
    /// <summary>
    /// Adds a single file into the vault with streaming encryption and verify-before-delete.
    /// </summary>
    public static async Task<AddFileResult> AddFileAsync(
        VaultSession session,
        string sourceFilePath,
        Guid? parentId = null,
        bool deleteOriginal = false,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default,
        bool enableSizePadding = true)
    {
        if (!File.Exists(sourceFilePath))
            return new AddFileResult(false, Path.GetFileName(sourceFilePath), "Source file does not exist.");

        // Check if file is a symlink / reparse point
        var fileInfo = new FileInfo(sourceFilePath);
        if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return new AddFileResult(false, fileInfo.Name, "Symbolic links and junctions are not supported.");

        // Check space
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(session.VaultPath) ?? "C:\\");
            if (drive.AvailableFreeSpace < fileInfo.Length + (50 * 1024 * 1024)) // 50MB margin
            {
                return new AddFileResult(false, fileInfo.Name, "Insufficient disk space on vault drive.");
            }
        }
        catch { }

        var fileId = Guid.NewGuid();
        var objectId = $"{Guid.NewGuid():N}.obj";
        var finalObjPath = VaultStorage.GetObjectPath(session.VaultPath, objectId, isTrash: false);
        var tempObjPath = finalObjPath + ".tmp";

        try
        {
            progress?.Report((10, $"Encrypting {fileInfo.Name}…"));

            // 1. Encrypt source file to temp object
            long plaintextLength;
            string sha256Hex;
            var paddingMode = enableSizePadding
                ? Crypto.PaddingMode.Bucket64KiB
                : Crypto.PaddingMode.None;

            using (var src = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            using (var dst = new FileStream(tempObjPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                (plaintextLength, sha256Hex) = await Task.Run(() =>
                    FileObjectWriter.WriteObject(src, dst, session.FekWrapKey, fileId, paddingMode: paddingMode), ct);
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report((70, $"Verifying {fileInfo.Name}…"));

            // 2. Verify: decrypt to null hash stream and verify SHA-256
            using (var verifyStream = new FileStream(tempObjPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            using (var sha = SHA256.Create())
            using (var hashStream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                await Task.Run(() => FileObjectReader.DecryptAll(verifyStream, hashStream, session.FekWrapKey, plaintextLength), ct);
                hashStream.FlushFinalBlock();
                var computedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(computedHash, sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempObjPath);
                    return new AddFileResult(false, fileInfo.Name, "Verification failed: encrypted hash does not match original.");
                }
            }

            ct.ThrowIfCancellationRequested();

            // 3. Move temp object to final object
            if (File.Exists(finalObjPath)) File.Delete(finalObjPath);
            File.Move(tempObjPath, finalObjPath);

            // 4. Update index atomically
            var indexItem = new IndexItem
            {
                Id = fileId,
                ParentId = parentId,
                Name = fileInfo.Name,
                IsDirectory = false,
                Size = plaintextLength,
                CreatedTimeUtc = fileInfo.CreationTimeUtc,
                ModifiedTimeUtc = fileInfo.LastWriteTimeUtc,
                Attributes = (int)fileInfo.Attributes,
                ObjectId = objectId,
                Sha256 = sha256Hex,
                IsTrash = false
            };

            lock (session)
            {
                session.Index.Items.Add(indexItem);
                session.Index.Revision++;
                session.Index.Updated = DateTime.UtcNow;
                VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
            }

            progress?.Report((90, $"Updating index…"));

            // 5. Delete original if requested
            if (deleteOriginal)
            {
                try
                {
                    File.Delete(sourceFilePath);
                }
                catch (Exception ex)
                {
                    LogService.LogAction("Vault", $"Could not delete original '{sourceFilePath}': {ex.Message}");
                }
            }

            progress?.Report((100, $"Added {fileInfo.Name}"));
            return new AddFileResult(true, fileInfo.Name);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempObjPath)) File.Delete(tempObjPath); } catch { }
            return new AddFileResult(false, fileInfo.Name, ex.Message);
        }
    }

    /// <summary>
    /// Recursively adds a directory and all contained files into the vault.
    /// </summary>
    public static async Task<List<AddFileResult>> AddDirectoryAsync(
        VaultSession session,
        string sourceDirPath,
        Guid? parentId = null,
        bool deleteOriginal = false,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<AddFileResult>();
        var dirInfo = new DirectoryInfo(sourceDirPath);
        if (!dirInfo.Exists)
        {
            results.Add(new AddFileResult(false, dirInfo.Name, "Directory does not exist."));
            return results;
        }

        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            results.Add(new AddFileResult(false, dirInfo.Name, "Symbolic links and junctions are skipped."));
            return results;
        }

        // Create folder item in index
        var folderId = Guid.NewGuid();
        var folderItem = new IndexItem
        {
            Id = folderId,
            ParentId = parentId,
            Name = dirInfo.Name,
            IsDirectory = true,
            Size = 0,
            CreatedTimeUtc = dirInfo.CreationTimeUtc,
            ModifiedTimeUtc = dirInfo.LastWriteTimeUtc,
            Attributes = (int)dirInfo.Attributes,
            IsTrash = false
        };

        lock (session)
        {
            session.Index.Items.Add(folderItem);
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
        results.Add(new AddFileResult(true, dirInfo.Name));

        // Files in folder
        var files = dirInfo.GetFiles();
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            int pct = (int)((double)(i + 1) / files.Length * 100);
            progress?.Report((pct, $"Adding {f.Name}…"));
            var res = await AddFileAsync(session, f.FullName, folderId, deleteOriginal, progress, ct);
            results.Add(res);
        }

        // Subdirectories
        var subDirs = dirInfo.GetDirectories();
        foreach (var sub in subDirs)
        {
            ct.ThrowIfCancellationRequested();
            var subResults = await AddDirectoryAsync(session, sub.FullName, folderId, deleteOriginal, progress, ct);
            results.AddRange(subResults);
        }

        if (deleteOriginal)
        {
            try
            {
                if (Directory.GetFileSystemEntries(sourceDirPath).Length == 0)
                    Directory.Delete(sourceDirPath);
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Creates a new virtual folder in the vault index.
    /// </summary>
    public static IndexItem CreateFolder(VaultSession session, string folderName, Guid? parentId)
    {
        var item = new IndexItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = folderName.Trim(),
            IsDirectory = true,
            CreatedTimeUtc = DateTime.UtcNow,
            ModifiedTimeUtc = DateTime.UtcNow,
            IsTrash = false
        };

        lock (session)
        {
            session.Index.Items.Add(item);
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
        return item;
    }

    /// <summary>
    /// Renames an item in the vault.
    /// </summary>
    public static void RenameItem(VaultSession session, Guid itemId, string newName)
    {
        lock (session)
        {
            var item = session.Index.Items.Find(i => i.Id == itemId);
            if (item == null) throw new FileNotFoundException("Item not found in vault.");

            item.Name = newName.Trim();
            item.ModifiedTimeUtc = DateTime.UtcNow;
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
    }

    /// <summary>
    /// Moves an item to a new parent folder.
    /// </summary>
    public static void MoveItem(VaultSession session, Guid itemId, Guid? newParentId)
    {
        lock (session)
        {
            var item = session.Index.Items.Find(i => i.Id == itemId);
            if (item == null) throw new FileNotFoundException("Item not found in vault.");

            item.ParentId = newParentId;
            item.ModifiedTimeUtc = DateTime.UtcNow;
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
    }

    /// <summary>
    /// Moves an item (and all its children if a folder) to trash.
    /// </summary>
    public static void MoveToTrash(VaultSession session, Guid itemId)
    {
        lock (session)
        {
            var item = session.Index.Items.Find(i => i.Id == itemId);
            if (item == null) return;

            MoveSubtreeToTrash(session, item);
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
    }

    private static void MoveSubtreeToTrash(VaultSession session, IndexItem item)
    {
        item.IsTrash = true;
        item.DeletedTimeUtc = DateTime.UtcNow;

        if (!item.IsDirectory && !string.IsNullOrEmpty(item.ObjectId))
        {
            try
            {
                var srcPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, isTrash: false);
                var dstPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, isTrash: true);
                if (File.Exists(srcPath))
                {
                    if (File.Exists(dstPath)) File.Delete(dstPath);
                    File.Move(srcPath, dstPath);
                }
            }
            catch (Exception ex)
            {
                LogService.LogAction("Vault", $"Move to trash failed for obj '{item.ObjectId}': {ex.Message}");
            }
        }

        // If directory, move children
        if (item.IsDirectory)
        {
            var children = session.Index.Items.FindAll(i => i.ParentId == item.Id && !i.IsTrash);
            foreach (var child in children)
            {
                MoveSubtreeToTrash(session, child);
            }
        }
    }

    /// <summary>
    /// Restores an item (and its children) from trash.
    /// </summary>
    public static void RestoreFromTrash(VaultSession session, Guid itemId)
    {
        lock (session)
        {
            var item = session.Index.Items.Find(i => i.Id == itemId);
            if (item == null) return;

            RestoreSubtreeFromTrash(session, item);
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
    }

    private static void RestoreSubtreeFromTrash(VaultSession session, IndexItem item)
    {
        item.IsTrash = false;
        item.DeletedTimeUtc = null;

        if (!item.IsDirectory && !string.IsNullOrEmpty(item.ObjectId))
        {
            try
            {
                var srcPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, isTrash: true);
                var dstPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, isTrash: false);
                if (File.Exists(srcPath))
                {
                    if (File.Exists(dstPath)) File.Delete(dstPath);
                    File.Move(srcPath, dstPath);
                }
            }
            catch (Exception ex)
            {
                LogService.LogAction("Vault", $"Restore from trash failed for obj '{item.ObjectId}': {ex.Message}");
            }
        }

        if (item.IsDirectory)
        {
            var children = session.Index.Items.FindAll(i => i.ParentId == item.Id && i.IsTrash);
            foreach (var child in children)
            {
                RestoreSubtreeFromTrash(session, child);
            }
        }
    }

    /// <summary>
    /// Permanently deletes an item from trash or vault.
    /// </summary>
    public static void DeletePermanently(VaultSession session, Guid itemId)
    {
        lock (session)
        {
            var item = session.Index.Items.Find(i => i.Id == itemId);
            if (item == null) return;

            DeleteSubtreePermanently(session, item);
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
    }

    private static void DeleteSubtreePermanently(VaultSession session, IndexItem item)
    {
        if (!item.IsDirectory && !string.IsNullOrEmpty(item.ObjectId))
        {
            try
            {
                var path = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, item.IsTrash);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        if (item.IsDirectory)
        {
            var children = session.Index.Items.FindAll(i => i.ParentId == item.Id);
            foreach (var child in children)
            {
                DeleteSubtreePermanently(session, child);
            }
        }

        session.Index.Items.Remove(item);
    }

    /// <summary>
    /// Empties all items currently in trash.
    /// </summary>
    public static void EmptyTrash(VaultSession session)
    {
        lock (session)
        {
            var trashItems = session.Index.Items.FindAll(i => i.IsTrash);
            foreach (var item in trashItems)
            {
                if (!item.IsDirectory && !string.IsNullOrEmpty(item.ObjectId))
                {
                    try
                    {
                        var path = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, isTrash: true);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }
                }
            }

            session.Index.Items.RemoveAll(i => i.IsTrash);
            session.Index.Revision++;
            session.Index.Updated = DateTime.UtcNow;
            VaultStorage.SaveIndex(session.VaultPath, session.Index, session.IndexKey);
        }
    }

    /// <summary>
    /// Exports an item (file or folder) to destination directory, verifying hash upon extraction.
    /// </summary>
    public static async Task ExportItemAsync(
        VaultSession session,
        IndexItem item,
        string destDirectory,
        bool moveOut = false,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDirectory);

        if (item.IsDirectory)
        {
            var targetFolder = Path.Combine(destDirectory, item.Name);
            Directory.CreateDirectory(targetFolder);

            List<IndexItem> children;
            lock (session)
            {
                children = session.Index.Items.FindAll(i => i.ParentId == item.Id && !i.IsTrash);
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                await ExportItemAsync(session, child, targetFolder, moveOut, progress, ct);
            }

            if (moveOut)
            {
                DeletePermanently(session, item.Id);
            }
            return;
        }

        // Export file
        if (string.IsNullOrEmpty(item.ObjectId)) return;
        var objPath = VaultStorage.GetObjectPath(session.VaultPath, item.ObjectId, item.IsTrash);
        if (!File.Exists(objPath))
            throw new FileNotFoundException($"Encrypted object file for '{item.Name}' is missing on disk.");

        var targetFile = Path.Combine(destDirectory, item.Name);
        var tempTarget = targetFile + ".tmp";

        progress?.Report((20, $"Exporting {item.Name}…"));

        using (var inStream = new FileStream(objPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        using (var outStream = new FileStream(tempTarget, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        using (var sha = SHA256.Create())
        using (var hashStream = new CryptoStream(outStream, sha, CryptoStreamMode.Write))
        {
            await Task.Run(() => FileObjectReader.DecryptAll(inStream, hashStream, session.FekWrapKey, item.Size), ct);
            hashStream.FlushFinalBlock();

            // Hash verification
            var exportedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!string.IsNullOrEmpty(item.Sha256) &&
                !string.Equals(exportedHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempTarget);
                throw new CryptographicException($"Export hash verification failed for '{item.Name}'. Data may be corrupt.");
            }
        }

        if (File.Exists(targetFile)) File.Delete(targetFile);
        File.Move(tempTarget, targetFile);

        // Preserve timestamps & attributes
        try
        {
            File.SetCreationTimeUtc(targetFile, item.CreatedTimeUtc);
            File.SetLastWriteTimeUtc(targetFile, item.ModifiedTimeUtc);
        }
        catch { }

        progress?.Report((100, $"Exported {item.Name}"));

        if (moveOut)
        {
            DeletePermanently(session, item.Id);
        }
    }
}
