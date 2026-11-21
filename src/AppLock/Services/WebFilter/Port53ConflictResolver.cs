using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AppLock.Services.WebFilter;

/// <summary>
/// Detects services that hold UDP port 53 on 0.0.0.0 (which blocks AppLock from binding on
/// any loopback address) and temporarily stops them while the DNS filter is running.
/// Services are restarted when the filter stops, only if we stopped them ourselves.
/// Uses sc.exe to avoid a dependency on System.ServiceProcess (not available in WinAppSDK projects).
/// </summary>
internal sealed class Port53ConflictResolver : IDisposable
{
    // Windows services known to grab UDP port 53 on 0.0.0.0
    private static readonly string[] ConflictingServices =
    {
        "SharedAccess",   // Internet Connection Sharing (ICS)
        "dns",            // Windows DNS Server role (Server editions)
    };

    private readonly List<string> _stoppedServices = new();
    private bool _disposed;

    /// <summary>
    /// Returns true if UDP port 53 is currently held by another process, preventing loopback bind.
    /// </summary>
    public static bool IsPort53Conflicted()
    {
        try
        {
            using var probe = new UdpClient();
            probe.Client.Bind(new IPEndPoint(IPAddress.Loopback, 53));
            return false; // successfully bound — no conflict
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// Stops any known conflicting Windows services holding port 53.
    /// Returns true when port 53 is free after the operation.
    /// </summary>
    public async Task<bool> ResolveConflictsAsync()
    {
        if (!IsPort53Conflicted()) return true;

        foreach (var serviceName in ConflictingServices)
        {
            try
            {
                string status = QueryServiceStatus(serviceName);
                if (status.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[Port53ConflictResolver] Stopping service '{serviceName}' to free port 53...");
                    bool stopped = await StopServiceAsync(serviceName).ConfigureAwait(false);
                    if (stopped)
                    {
                        _stoppedServices.Add(serviceName);
                        Debug.WriteLine($"[Port53ConflictResolver] Service '{serviceName}' stopped successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Port53ConflictResolver] Could not stop service '{serviceName}': {ex.Message}");
            }
        }

        // Give the OS a moment to release the socket binding
        await Task.Delay(600).ConfigureAwait(false);

        bool resolved = !IsPort53Conflicted();
        Debug.WriteLine($"[Port53ConflictResolver] Port 53 conflict resolved: {resolved}");
        return resolved;
    }

    /// <summary>
    /// Restarts any services that were stopped by this instance.
    /// </summary>
    public void RestoreServices()
    {
        foreach (var serviceName in _stoppedServices)
        {
            try
            {
                Debug.WriteLine($"[Port53ConflictResolver] Restarting service '{serviceName}'...");
                StartService(serviceName);
                Debug.WriteLine($"[Port53ConflictResolver] Service '{serviceName}' restart requested.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Port53ConflictResolver] Could not restart service '{serviceName}': {ex.Message}");
            }
        }
        _stoppedServices.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreServices();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string QueryServiceStatus(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {serviceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            string output = proc?.StandardOutput.ReadToEnd() ?? string.Empty;
            proc?.WaitForExit(3000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Task<bool> StopServiceAsync(string serviceName)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", $"stop {serviceName}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
                return proc?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });
    }

    private static void StartService(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"start {serviceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Port53ConflictResolver] sc start {serviceName} failed: {ex.Message}");
        }
    }
}
