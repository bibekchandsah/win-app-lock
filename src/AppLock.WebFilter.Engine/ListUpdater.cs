using System.Net;

namespace AppLock.WebFilter.Engine;

public enum ListUpdateStatus
{
    Updated = 0,
    NotModified = 1,
    Rejected = 2,
    Failed = 3
}

public sealed record ListUpdateResult(
    ListUpdateStatus Status,
    int DomainCount,
    string? ETag,
    DateTimeOffset? LastModified,
    string? Error)
{
    public bool IsSuccess => Status is ListUpdateStatus.Updated or ListUpdateStatus.NotModified;

    public static ListUpdateResult Rejected(string error) => new(ListUpdateStatus.Rejected, 0, null, null, error);

    public static ListUpdateResult Failed(string error) => new(ListUpdateStatus.Failed, 0, null, null, error);
}

public sealed class ListUpdaterOptions
{
    /// <summary>Hard cap for one downloaded list. The largest bundled list is a few megabytes.</summary>
    public int MaxBytes { get; init; } = 50 * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public string UserAgent { get; init; } = "AppLock-WebFilter/1.0";
}

/// <summary>
/// Downloads one block-list source over HTTPS with conditional requests and replaces the compiled
/// snapshot only when the download parses and looks sane. A bad download never replaces a good list.
/// </summary>
public sealed class ListUpdater : IDisposable
{
    /// <summary>A list must have at least this many domains before it may replace an existing one.</summary>
    public const int MinimumDomains = 100;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ListUpdaterOptions _options;

    public ListUpdater(HttpClient? httpClient = null, ListUpdaterOptions? options = null)
    {
        _options = options ?? new ListUpdaterOptions();
        _ownsHttpClient = httpClient == null;
        _http = httpClient ?? CreateHttpClient(_options);
    }

    public async Task<ListUpdateResult> UpdateAsync(
        string url,
        string destinationPath,
        int previousCount,
        string? etag,
        DateTimeOffset? lastModified,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ListUpdateResult.Rejected("Only HTTPS list URLs are allowed");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);

        if (!string.IsNullOrWhiteSpace(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        if (lastModified is { } modified)
        {
            request.Headers.IfModifiedSince = modified;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new ListUpdateResult(
                    ListUpdateStatus.NotModified,
                    previousCount,
                    etag,
                    lastModified,
                    null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ListUpdateResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0 && declaredLength > _options.MaxBytes)
            {
                return ListUpdateResult.Rejected($"List is larger than {_options.MaxBytes} bytes");
            }

            using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();

            var chunk = new byte[81920];
            while (true)
            {
                int read = await source.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read <= 0) break;

                if (buffer.Length + read > _options.MaxBytes)
                {
                    return ListUpdateResult.Rejected($"List is larger than {_options.MaxBytes} bytes");
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            var parsed = ListParser.Parse(buffer);
            int count = parsed.Domains.Count;

            if (count < MinimumDomains)
            {
                return ListUpdateResult.Rejected($"Only {count} usable domains; keeping the previous list");
            }

            if (previousCount > 0 && count < previousCount / 2)
            {
                return ListUpdateResult.Rejected(
                    $"New list has {count} domains, under half of the previous {previousCount}; keeping the previous list");
            }

            ListCompiler.CompileToFile(parsed.Domains, destinationPath);

            return new ListUpdateResult(
                ListUpdateStatus.Updated,
                count,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ListUpdateResult.Failed("Update was cancelled");
        }
        catch (OperationCanceledException)
        {
            return ListUpdateResult.Failed("The list server did not answer in time");
        }
        catch (Exception ex)
        {
            return ListUpdateResult.Failed(ex.Message);
        }
    }

    private static HttpClient CreateHttpClient(ListUpdaterOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = options.Timeout,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
