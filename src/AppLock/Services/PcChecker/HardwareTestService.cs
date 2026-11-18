using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.Services.PcChecker;

/// <summary>
/// The measurable hardware tests behind <see cref="Views.HardwareTestsPage"/>: a CPU/RAM benchmark that only
/// needs the runtime, a file-based disk read/write test and a download-based network test. Everything reports
/// real measured numbers, never an estimate dressed up as a result.
/// </summary>
public static class HardwareTestService
{
    public sealed record CpuResult(double SingleThreadOps, double MultiThreadOps, TimeSpan Elapsed);
    public sealed record RamResult(double GigabytesPerSecond, long BytesCopied);
    public sealed record DiskResult(double WriteMegabytesPerSecond, double ReadMegabytesPerSecond, long Bytes, string Folder);
    public sealed record NetworkResult(double MegabitsPerSecond, long Bytes, TimeSpan Elapsed, string Host);

    /// <summary>Runs a fixed 400 ms of arithmetic on one thread, then on every core, and reports both rates.</summary>
    public static CpuResult RunCpuTest(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        double single = RunWorkload(1, 400, ct);
        int threads = Math.Max(1, Environment.ProcessorCount);
        double multi = RunWorkload(threads, 600, ct);

        watch.Stop();
        return new CpuResult(single, multi, watch.Elapsed);
    }

    private static double RunWorkload(int threads, int milliseconds, CancellationToken ct)
    {
        long total = 0;
        var watch = Stopwatch.StartNew();

        Parallel.For(0, threads, new ParallelOptions { CancellationToken = ct }, _ =>
        {
            long local = 0;
            var inner = Stopwatch.StartNew();
            while (inner.ElapsedMilliseconds < milliseconds)
            {
                for (int i = 0; i < 100_000; i++)
                {
                    local += (long)Math.Sqrt(i) + ((i * 31) % 7);
                }
            }
            Interlocked.Add(ref total, local);
        });

        return total / Math.Max(0.001, watch.Elapsed.TotalSeconds);
    }

    /// <summary>Copies a 64 MB block repeatedly and reports the achieved memory bandwidth.</summary>
    public static RamResult RunRamTest(CancellationToken ct)
    {
        const int size = 64 * 1024 * 1024;
        var source = new byte[size];
        var destination = new byte[size];
        Random.Shared.NextBytes(source);

        // Warm the pages so the first-touch page faults are not counted as slow memory.
        Buffer.BlockCopy(source, 0, destination, 0, size);

        const int rounds = 12;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            ct.ThrowIfCancellationRequested();
            Buffer.BlockCopy(source, 0, destination, 0, size);
        }
        watch.Stop();

        // Read the destination so the copies cannot be optimised away.
        if (destination[0] == 0xFF && destination[size - 1] == 0xFF && source[1] == 0xFE)
        {
            Debug.WriteLine("[HardwareTest] RAM test touched the buffer");
        }

        long bytes = (long)size * rounds;
        double gigabytesPerSecond = bytes / (1024.0 * 1024.0 * 1024.0) / Math.Max(0.001, watch.Elapsed.TotalSeconds);
        return new RamResult(gigabytesPerSecond, bytes);
    }

    /// <summary>
    /// Writes and reads back a temporary file of <paramref name="bytes"/> on the selected drive. The file is
    /// always deleted, even when the test is cancelled; nothing else on the drive is touched.
    /// </summary>
    public static async Task<DiskResult> RunDiskTestAsync(
        string folder, long bytes, int bufferSize, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"AppLock-disk-test-{Guid.NewGuid():N}.tmp");
        var buffer = new byte[bufferSize];
        Random.Shared.NextBytes(buffer);

        try
        {
            double writeSpeed;

            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize, FileOptions.WriteThrough))
            {
                var watch = Stopwatch.StartNew();
                long written = 0;
                while (written < bytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int chunk = (int)Math.Min(bufferSize, bytes - written);
                    await stream.WriteAsync(buffer.AsMemory(0, chunk), ct);
                    written += chunk;
                    progress?.Report(0.5 * written / bytes);
                }

                await stream.FlushAsync(ct);
                stream.Flush(true);
                watch.Stop();
                writeSpeed = written / 1_000_000.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds);
            }

            double readSpeed;

            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, FileOptions.SequentialScan))
            {
                var watch = Stopwatch.StartNew();
                long read = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    read += count;
                    progress?.Report(0.5 + 0.5 * read / bytes);
                }
                watch.Stop();
                readSpeed = read / 1_000_000.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds);
            }

            progress?.Report(1);
            return new DiskResult(writeSpeed, readSpeed, bytes, folder);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Downloads a fixed number of bytes and reports the achieved throughput. This measures the whole path to
    /// the internet (link, router, server), not just the local adapter.
    /// </summary>
    public static async Task<NetworkResult> RunNetworkTestAsync(
        HttpClient client, long bytes, IProgress<double>? progress, CancellationToken ct)
    {
        const string host = "speed.cloudflare.com";

        using var response = await client.GetAsync($"https://{host}/__down?bytes={bytes}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var watch = Stopwatch.StartNew();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            progress?.Report(Math.Min(1, (double)total / bytes));
        }
        watch.Stop();

        double megabitsPerSecond = total * 8.0 / 1_000_000.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds);
        return new NetworkResult(megabitsPerSecond, total, watch.Elapsed, host);
    }
}
