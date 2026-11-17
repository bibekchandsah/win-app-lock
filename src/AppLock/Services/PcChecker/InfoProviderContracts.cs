using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker;

/// <summary>What kind of machine we are on, handed to providers so they can report honestly.</summary>
public sealed class SystemContext
{
    public bool IsElevated { get; init; }
    public Version OsVersion { get; init; } = Environment.OSVersion.Version;
    public int BuildNumber { get; init; } = Environment.OSVersion.Version.Build;
    public Architecture ProcessArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    public static SystemContext Create() => new() { IsElevated = ElevationHelper.IsElevated };
}

/// <summary>Options a provider collects with (detail filtering happens in the UI, this is the request).</summary>
public sealed class CollectOptions
{
    public DetailLevel Level { get; init; } = DetailLevel.Detailed;
    public bool HideIdentifiers { get; init; }

    public static CollectOptions Default => new();
}

/// <summary>
/// A small, independent source of system information. Every provider is wrapped in try/catch and a
/// timeout by <see cref="InfoCollector"/>, so one failing source never blocks the page.
/// </summary>
public interface IInfoProvider
{
    /// <summary>Stable id, e.g. "os", "cpu", "storage".</summary>
    string Id { get; }

    /// <summary>The category this provider feeds (see <see cref="Models.PcChecker.PcCategories"/>).</summary>
    string Category { get; }

    InfoTier Tier { get; }

    TimeSpan Timeout { get; }

    /// <summary>How long a collected section may be reused. Zero means "never cache".</summary>
    TimeSpan CacheLifetime { get; }

    bool IsSupported(SystemContext ctx);

    Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct);
}

/// <summary>Injected clock so caching can be tested without waiting.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Shared elevation helpers for the pages that need administrator rights.</summary>
public static class ElevationHelper
{
    private static bool? _isElevated;

    public static bool IsElevated => _isElevated ??= CheckElevation();

    private static bool CheckElevation()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches AppLock elevated through the standard UAC prompt. Returns true when a new elevated
    /// process was started (the caller then exits). Never asks for the Windows password.
    /// </summary>
    public static bool TryRestartElevated()
    {
        try
        {
            if (IsElevated) return false;

            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            // Mark this exit as authorized so the watchdog does not revive the unelevated instance.
            ProcessProtectionService.SignalAuthorizedExit();
            ProcessProtectionService.UnprotectCurrentProcess();

            return System.Diagnostics.Process.Start(psi) != null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user dismissed the UAC prompt - stay unelevated.
            return false;
        }
        catch
        {
            return false;
        }
    }
}
