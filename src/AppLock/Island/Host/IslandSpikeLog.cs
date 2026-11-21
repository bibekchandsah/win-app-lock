using System;
using System.IO;

namespace AppLock.Island.Host;

/// <summary>Temporary diagnostics for the milestone 1 spike (removed with the spike entry point).</summary>
internal static class IslandSpikeLog
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "Logs", "island-spike.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
