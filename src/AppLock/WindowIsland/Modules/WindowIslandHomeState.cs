using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Island.Modules;
using AppLock.Island.Events;
using AppLock.Services.SpeedMeter;

namespace AppLock.WindowIsland.Modules;

/// <summary>Live stats the Home tab shows, batched from the background loop.</summary>
public sealed record HomeSnapshot(
    double CpuPercent,
    double RamPercent,
    double DownBytesPerSec,
    double UpBytesPerSec);

/// <summary>
/// Runs entirely on a background Task while the Home tab is visible.
/// Polls SMTC (1 Hz), CPU/RAM (1 Hz), network (1 Hz)
/// and batches the results back to the UI thread through the DispatcherQueue.
/// Idle cost: zero when stopped (island.md 4.1).
/// </summary>
public sealed class WindowIslandHomeState : IDisposable
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dq;
    private readonly NetworkStatsProvider  _network = new();
    private readonly SystemStatsProvider   _system  = new() { TrackCpu = true, TrackRam = true };
    private readonly MediaSessionSource    _media   = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Events raised on the UI thread
    public event Action<MediaSnapshot>? MediaUpdated;
    public event Action<HomeSnapshot>?  StatsUpdated;

    public bool IsRunning => _loop != null;

    public WindowIslandHomeState(Microsoft.UI.Dispatching.DispatcherQueue dq)
    {
        _dq = dq;
    }

    public void Start()
    {
        if (_loop != null) return;
        var cts = new CancellationTokenSource();
        _cts  = cts;
        _loop = Task.Run(() => LoopAsync(cts.Token));
        Debug.WriteLine("[WindowIsland] home sampling started");
    }

    public void Stop()
    {
        if (_loop == null) return;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts  = null;
        _loop = null;
        Debug.WriteLine("[WindowIsland] home sampling stopped");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        _system.Sample(); // prime CPU baseline
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(token)) break;
                await SampleAsync(token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIsland] home loop failed: {ex.Message}");
        }
    }

    private async Task SampleAsync(CancellationToken token)
    {
        try
        {
            // --- Stats (synchronous, cheap) ---
            var net = _network.Sample();
            var sys = _system.Sample();

            if (sys.CpuPercent is double cpu && !token.IsCancellationRequested)
            {
                var snap = new HomeSnapshot(cpu, sys.RamPercent ?? 0,
                    net.BytesDownPerSec, net.BytesUpPerSec);
                _dq.TryEnqueue(() => StatsUpdated?.Invoke(snap));
            }

            // --- Media (async SMTC) ---
            var media = await _media.PollAsync();
            if (media != null && !token.IsCancellationRequested)
            {
                _dq.TryEnqueue(() => MediaUpdated?.Invoke(media));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIsland] home sample failed: {ex.Message}");
        }
    }

    public Task MediaCommandAsync(string command) => _media.CommandAsync(command);

    /// <summary>Moves the player to the next app that has media (called on the UI thread).</summary>
    public void CycleMediaSession() => _media.CycleSession();

    /// <summary>Seeks the shown track to <paramref name="seconds"/> from the start.</summary>
    public Task SeekMediaAsync(double seconds) => _media.SeekAsync(seconds);

    public void Dispose()
    {
        Stop();
        _media.Dispose();
    }
}
