using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AppLock.Services.Vault;

/// <summary>
/// Metadata stored inside vault.lock to identify who owns the lock.
/// </summary>
public class VaultLockInfo
{
    public string MachineName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public int ProcessId { get; set; } = Environment.ProcessId;
    public DateTime LockedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents an active single-writer lock held by this process.
/// Disposing releases the file handle and deletes vault.lock.
/// </summary>
public sealed class VaultLockHandle : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _lockFilePath;
    private bool _disposed;

    public VaultLockHandle(FileStream stream, string lockFilePath)
    {
        _stream = stream;
        _lockFilePath = lockFilePath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _stream.Dispose();
        }
        catch { }

        try
        {
            if (File.Exists(_lockFilePath))
            {
                File.Delete(_lockFilePath);
            }
        }
        catch { }
    }
}

/// <summary>
/// Thrown when attempting to open a vault that is currently locked by another process or machine.
/// </summary>
public class VaultLockConflictException : Exception
{
    public VaultLockInfo? LockInfo { get; }
    public bool IsStale { get; }

    public VaultLockConflictException(VaultLockInfo? info, bool isStale, string message)
        : base(message)
    {
        LockInfo = info;
        IsStale = isStale;
    }
}

/// <summary>
/// Manages the single-writer advisory lock (vault.lock) for open vaults.
/// </summary>
public static class VaultLockFile
{
    public const string LockFileName = "vault.lock";

    /// <summary>
    /// Attempts to acquire the single-writer lock for a vault folder.
    /// If another writer holds the lock and forceTakeOver is false, throws VaultLockConflictException.
    /// </summary>
    public static VaultLockHandle Acquire(string vaultPath, bool forceTakeOver = false)
    {
        Directory.CreateDirectory(vaultPath);
        var lockFilePath = Path.Combine(vaultPath, LockFileName);

        if (File.Exists(lockFilePath) && !forceTakeOver)
        {
            VaultLockInfo? existingInfo = ReadLockInfo(lockFilePath);

            if (existingInfo != null)
            {
                bool isSameMachine = string.Equals(existingInfo.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
                bool isProcessAlive = false;

                if (isSameMachine)
                {
                    try
                    {
                        var proc = Process.GetProcessById(existingInfo.ProcessId);
                        isProcessAlive = !proc.HasExited;
                    }
                    catch
                    {
                        isProcessAlive = false;
                    }
                }

                // If process is dead on the same machine, it's a stale lock from a crash.
                // If it's on another machine or actively running on this machine, report conflict.
                if (isProcessAlive || !isSameMachine)
                {
                    var msg = !isSameMachine
                        ? $"This vault may be open on another PC ({existingInfo.MachineName})."
                        : $"This vault is currently open by process {existingInfo.ProcessId} on this PC.";

                    throw new VaultLockConflictException(existingInfo, isStale: false, msg);
                }
            }
        }

        // Attempt to create/open exclusively
        try
        {
            if (forceTakeOver && File.Exists(lockFilePath))
            {
                try { File.Delete(lockFilePath); } catch { }
            }

            var stream = new FileStream(
                lockFilePath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                FileOptions.None);

            var info = new VaultLockInfo();
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(json);
                writer.Flush();
            }
            stream.Flush(flushToDisk: true);

            return new VaultLockHandle(stream, lockFilePath);
        }
        catch (IOException ioEx)
        {
            var existingInfo = ReadLockInfo(lockFilePath);
            throw new VaultLockConflictException(
                existingInfo,
                isStale: false,
                $"The vault lock file is held by another process: {ioEx.Message}");
        }
    }

    private static VaultLockInfo? ReadLockInfo(string lockFilePath)
    {
        try
        {
            using var fs = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var content = reader.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(content))
            {
                return JsonSerializer.Deserialize<VaultLockInfo>(content);
            }
        }
        catch { }

        return null;
    }
}
