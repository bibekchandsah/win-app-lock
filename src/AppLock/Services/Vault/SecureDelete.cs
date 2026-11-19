using System;
using System.IO;
using System.Security.Cryptography;

namespace AppLock.Services.Vault;

public static class SecureDelete
{
    /// <summary>
    /// Overwrites file data before deletion (best effort).
    /// Flushes to disk and deletes the file.
    /// </summary>
    public static void WipeAndDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.IsReadOnly)
                fileInfo.IsReadOnly = false;

            long length = fileInfo.Length;
            if (length > 0)
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                byte[] zeros = new byte[Math.Min(length, 65536)];
                long written = 0;
                while (written < length)
                {
                    int toWrite = (int)Math.Min(zeros.Length, length - written);
                    fs.Write(zeros, 0, toWrite);
                    written += toWrite;
                }
                fs.Flush(flushToDisk: true);
                fs.SetLength(0);
            }
        }
        catch
        {
            // Even if overwrite fails (e.g. sharing violation), attempt delete below
        }

        try
        {
            File.Delete(filePath);
        }
        catch { }
    }

    /// <summary>
    /// Recursively wipes and deletes a temporary session directory.
    /// </summary>
    public static void WipeAndDeleteDirectory(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                WipeAndDeleteFile(file);
            }

            Directory.Delete(dirPath, recursive: true);
        }
        catch { }
    }
}
