using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Island.Events;
using AppLock.Services.SpeedMeter;

namespace AppLock.Island.Modules;

/// <summary>The live numbers the Home tab shows: CPU, RAM, network speed and the volume level.</summary>
public sealed record HomeStats(double CpuPercent, double RamPercent, double DownBytesPerSec, double UpBytesPerSec, int Volume);

/// <summary>
/// Samples everything the Home tab needs once a second, on a background thread, and only while the
/// Home tab is actually on screen (island.md 4.1: no per-frame or hidden work). The host forwards the
/// results to the page through the batched bridge.
/// </summary>
public sealed class IslandHomeState : IDisposable
{
    private readonly NetworkStatsProvider _network = new();
    private readonly SystemStatsProvider _system = new() { TrackCpu = true, TrackRam = true };
    private readonly MediaSessionSource _media = new();
    private readonly IslandVolumeController _volume;
    private readonly Func<IReadOnlyList<RecentClip>> _recent;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IslandHomeState(IslandVolumeController volume, Func<IReadOnlyList<RecentClip>> recent)
    {
        _volume = volume;
        _recent = recent;
    }

    public event Action<HomeStats>? StatsSampled;

    public event Action<MediaSnapshot>? MediaSampled;

    public event Action<IReadOnlyList<RecentClip>>? RecentSampled;

    public bool IsRunning => _loop != null;

    public void Start()
    {
        if (_loop != null) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _loop = Task.Run(() => LoopAsync(cts.Token));
        Debug.WriteLine("[Island] home sampling started");
    }

    public void Stop()
    {
        if (_loop == null) return;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }

        _cts = null;
        _loop = null;
        Debug.WriteLine("[Island] home sampling stopped");
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // First pass has no CPU baseline (GetSystemTimes needs two readings), so the very first
        // sample is thrown away and the tab shows the real number a second later.
        _system.Sample();

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(token)) break;
                await SampleAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] home sampling loop failed: {ex.Message}");
        }
    }

    private async Task SampleAsync(CancellationToken token)
    {
        try
        {
            var network = _network.Sample();
            var system = _system.Sample();
            int volume = _volume.Get();

            if (system.CpuPercent is double cpu)
            {
                StatsSampled?.Invoke(new HomeStats(
                    cpu,
                    system.RamPercent ?? 0,
                    network.BytesDownPerSec,
                    network.BytesUpPerSec,
                    volume));
            }

            var media = await _media.PollAsync();
            if (media != null && !token.IsCancellationRequested) MediaSampled?.Invoke(media);

            if (!token.IsCancellationRequested) RecentSampled?.Invoke(_recent());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] home sample failed: {ex.Message}");
        }
    }

    public Task MediaCommandAsync(string command) => _media.CommandAsync(command);

    public void Dispose()
    {
        Stop();
        _media.Dispose();
    }
}
