using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AppLock.Services.SpeedMeter;

/// <summary>One speed test result.</summary>
public sealed class SpeedTestResult
{
    public DateTime When { get; init; }
    public double DownMbps { get; init; }
    public double UpMbps { get; init; }
}

/// <summary>
/// Opt-in speed test. It only runs when the user presses the button, downloads 10 MB and uploads
/// 5 MB from/to Cloudflare's public speed-test endpoints, and keeps the last 20 results in memory.
/// </summary>
public static class SpeedTestService
{
    private const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=10000000";
    private const string UploadUrl = "https://speed.cloudflare.com/__up";
    private const int UploadBytes = 5 * 1024 * 1024;
    private const int MaxHistory = 20;

    private static readonly List<SpeedTestResult> History = new();

    public static IReadOnlyList<SpeedTestResult> Results => History;

    /// <summary>Fetches the public IP once. Only called when the user asks for it.</summary>
    public static async Task<string> GetPublicIpAsync(CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return (await http.GetStringAsync("https://api.ipify.org", token)).Trim();
    }

    public static async Task<SpeedTestResult> RunAsync(IProgress<string>? progress, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        progress?.Report("Measuring download…");

        long downloaded = 0;
        var watch = Stopwatch.StartNew();

        using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                downloaded += read;
            }
        }

        watch.Stop();

        var downMbps = watch.Elapsed.TotalSeconds > 0
            ? downloaded * 8 / watch.Elapsed.TotalSeconds / 1_000_000
            : 0;

        progress?.Report("Measuring upload…");

        using var payload = new ByteArrayContent(new byte[UploadBytes]);
        watch.Restart();

        using (var response = await http.PostAsync(UploadUrl, payload, token))
        {
            response.EnsureSuccessStatusCode();
        }

        watch.Stop();

        var upMbps = watch.Elapsed.TotalSeconds > 0
            ? UploadBytes * 8 / watch.Elapsed.TotalSeconds / 1_000_000
            : 0;

        var result = new SpeedTestResult
        {
            When = DateTime.Now,
            DownMbps = downMbps,
            UpMbps = upMbps
        };

        History.Insert(0, result);
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);

        return result;
    }
}
