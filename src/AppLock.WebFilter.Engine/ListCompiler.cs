namespace AppLock.WebFilter.Engine;

/// <summary>
/// Compiles domain lists into <see cref="ListSnapshot"/> files. The new file is written to a
/// temporary name and only moved over the old one after it is complete, so a crash or a full disk
/// can never leave a half-written list behind.
/// </summary>
public static class ListCompiler
{
    public static void CompileToFile(IEnumerable<string> domains, string destinationPath, ulong? seed = null)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ListSnapshot.Write(domains, seed ?? ListSnapshot.NewSeed(), stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file is harmless and cleaned up next time.
        }
    }
}
