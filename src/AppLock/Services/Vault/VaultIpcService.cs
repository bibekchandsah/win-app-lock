using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AppLock.Services.Vault;

/// <summary>
/// Handles IPC handoff when user invokes "Add to AppLock Vault" (via Explorer context menu or SendTo)
/// while AppLock is already running.
/// </summary>
public static class VaultIpcService
{
    public const string VaultAddEventName = @"Local\AppLock_VaultAdd_Event";
    private static readonly string PendingAddFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock",
        "pending_vault_add.json");

    /// <summary>
    /// Writes paths to the pending add queue and signals the running AppLock instance.
    /// </summary>
    public static bool SignalPendingAdd(IEnumerable<string> paths)
    {
        try
        {
            var dir = Path.GetDirectoryName(PendingAddFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var list = new List<string>(paths);
            lock (PendingAddFilePath)
            {
                // If there are already pending paths, merge them
                if (File.Exists(PendingAddFilePath))
                {
                    try
                    {
                        var json = File.ReadAllText(PendingAddFilePath);
                        var existing = JsonSerializer.Deserialize<List<string>>(json);
                        if (existing != null)
                        {
                            list.InsertRange(0, existing);
                        }
                    }
                    catch { }
                }

                File.WriteAllText(PendingAddFilePath, JsonSerializer.Serialize(list));
            }

            // Signal running instance
            if (EventWaitHandle.TryOpenExisting(VaultAddEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"SignalPendingAdd failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Reads and clears all pending files to add.
    /// </summary>
    public static List<string> ReadAndClearPendingAdd()
    {
        var result = new List<string>();
        try
        {
            if (File.Exists(PendingAddFilePath))
            {
                lock (PendingAddFilePath)
                {
                    if (File.Exists(PendingAddFilePath))
                    {
                        var json = File.ReadAllText(PendingAddFilePath);
                        var list = JsonSerializer.Deserialize<List<string>>(json);
                        if (list != null)
                        {
                            result.AddRange(list);
                        }
                        File.Delete(PendingAddFilePath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"ReadAndClearPendingAdd failed: {ex.Message}");
        }
        return result;
    }
}
