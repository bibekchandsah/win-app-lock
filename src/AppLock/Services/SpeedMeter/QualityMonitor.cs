using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace AppLock.Services.SpeedMeter;

/// <summary>Rolling connection quality over the last 60 pings.</summary>
public sealed class QualityStats
{
    public double LatencyMs { get; init; }
    public double JitterMs { get; init; }
    public double LossPercent { get; init; }
    public int Samples { get; init; }

    public bool HasData => Samples > 0;

    /// <summary>Excellent / Good / Fair / Poor from latency and loss.</summary>
    public string Grade
    {
        get
        {
            if (Samples == 0) return "Unknown";
            if (LossPercent >= 10 || LatencyMs > 300) return "Poor";
            if (LossPercent >= 3 || LatencyMs > 120) return "Fair";
            if (LatencyMs > 50 || JitterMs > 20) return "Good";
            return "Excellent";
        }
    }

    public static QualityStats Empty { get; } = new();
}

/// <summary>
/// Pings the default gateway on a background thread every two seconds. Gateway pings stay on the
/// local network, so measuring quality never contacts the internet.
/// </summary>
public sealed class QualityMonitor : IDisposable
{
    private const int IntervalMs = 2000;
    private const int MaxSamples = 60;

    private readonly DispatcherQueue _dispatcher;
    private readonly Queue<(double latency, bool success)> _samples = new();

    private CancellationTokenSource? _cts;
    private int _running;

    public QualityMonitor(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
    }

    /// <summary>Latest computed values; empty until the first ping completes.</summary>
    public QualityStats Latest { get; private set; } = QualityStats.Empty;

    /// <summary>Raised on the UI thread after every ping.</summary>
    public event EventHandler<QualityStats>? Updated;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PingOnceAsync(token);
                    await Task.Delay(IntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Quality ping error: {ex.Message}");
                    try { await Task.Delay(IntervalMs, token); } catch { break; }
                }
            }
        }, token);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Quality stop error: {ex.Message}");
        }

        _cts = null;
        _samples.Clear();
        Latest = QualityStats.Empty;
    }

    private async Task PingOnceAsync(CancellationToken token)
    {
        var gateway = NetworkDetailsProvider.Read().Gateway;

        if (!System.Net.IPAddress.TryParse(gateway, out var address))
        {
            Record(0, success: false);
            return;
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 1000);

            if (token.IsCancellationRequested) return;

            var ok = reply.Status == IPStatus.Success;
            Record(ok ? reply.RoundtripTime : 0, ok);
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) Record(0, success: false);
        }
    }

    private void Record(double latency, bool success)
    {
        _samples.Enqueue((latency, success));
        while (_samples.Count > MaxSamples) _samples.Dequeue();

        var all = _samples.ToList();
        var replies = all.Where(s => s.success).Select(s => s.latency).ToList();

        double jitter = 0;
        for (var i = 1; i < replies.Count; i++)
        {
            jitter += Math.Abs(replies[i] - replies[i - 1]);
        }

        if (replies.Count > 1) jitter /= replies.Count - 1;

        var stats = new QualityStats
        {
            LatencyMs = replies.Count > 0 ? replies.Average() : 0,
            JitterMs = jitter,
            LossPercent = all.Count == 0 ? 0 : (all.Count - replies.Count) * 100.0 / all.Count,
            Samples = all.Count
        };

        Latest = stats;
        _dispatcher.TryEnqueue(() => Updated?.Invoke(this, stats));
    }

    public void Dispose() => Stop();
}
