using System.Net;
using System.Net.Http.Headers;
using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

public class ListUpdaterTests
{
    private const string Url = "https://lists.example/blocklist.txt";

    [Fact]
    public async Task UpdatesAndCompilesAGoodList()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        using var handler = new StubHandler(_ => TextResponse(DomainsText(200)));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 150, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Updated, result.Status);
        Assert.Equal(200, result.DomainCount);

        using var snapshot = ListSnapshot.Open(path);
        Assert.Equal(200, snapshot.Count);
        Assert.True(snapshot.Contains("host0.example.com"));
    }

    [Fact]
    public async Task NotModifiedLeavesTheListUntouched()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");
        ListCompiler.CompileToFile(Domains(120), path, seed: 1);

        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 120, etag: "\"abc\"", lastModified: null);

        Assert.Equal(ListUpdateStatus.NotModified, result.Status);
        Assert.Equal(120, result.DomainCount);

        using var snapshot = ListSnapshot.Open(path);
        Assert.Equal(120, snapshot.Count);
    }

    [Fact]
    public async Task SendsConditionalHeaders()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        var handler = new StubHandler(request =>
        {
            Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "\"abc\"");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 0, etag: "\"abc\"", lastModified: null);

        Assert.Equal(ListUpdateStatus.NotModified, result.Status);
    }

    [Fact]
    public async Task RejectsATinyListAndKeepsTheOldSnapshot()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");
        ListCompiler.CompileToFile(Domains(120), path, seed: 1);
        var before = File.ReadAllBytes(path);

        using var handler = new StubHandler(_ => TextResponse(DomainsText(10)));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 120, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Rejected, result.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RejectsAListThatShrinksByMoreThanHalf()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        using var handler = new StubHandler(_ => TextResponse(DomainsText(400)));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 1000, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Rejected, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AFailedDownloadKeepsTheOldSnapshot()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");
        ListCompiler.CompileToFile(Domains(120), path, seed: 1);

        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 120, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Failed, result.Status);

        using var snapshot = ListSnapshot.Open(path);
        Assert.Equal(120, snapshot.Count);
    }

    [Fact]
    public async Task JunkThatParsesToNothingIsRejected()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        using var handler = new StubHandler(_ => TextResponse("# comments only\n\nnot a domain\n127.0.0.1\n"));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 0, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Rejected, result.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RejectsAnOversizedList()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        using var handler = new StubHandler(_ => TextResponse(DomainsText(500)));
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http, new ListUpdaterOptions { MaxBytes = 1024 });

        var result = await updater.UpdateAsync(Url, path, previousCount: 0, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Rejected, result.Status);
        Assert.Contains("larger", result.Error);
    }

    [Theory]
    [InlineData("http://lists.example/blocklist.txt")]
    [InlineData("ftp://lists.example/blocklist.txt")]
    [InlineData("not a url")]
    public async Task RejectsNonHttpsUrls(string url)
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        int calls = 0;
        using var handler = new StubHandler(_ =>
        {
            calls++;
            return TextResponse(DomainsText(200));
        });
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(url, path, previousCount: 0, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Rejected, result.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReturnsTheNewEtagForTheNextRequest()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "list.snapshot");

        using var handler = new StubHandler(_ =>
        {
            var response = TextResponse(DomainsText(200));
            response.Headers.ETag = new EntityTagHeaderValue("\"new-tag\"");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
            return response;
        });
        using var http = new HttpClient(handler);
        using var updater = new ListUpdater(http);

        var result = await updater.UpdateAsync(Url, path, previousCount: 0, etag: null, lastModified: null);

        Assert.Equal(ListUpdateStatus.Updated, result.Status);
        Assert.Equal("\"new-tag\"", result.ETag);
        Assert.NotNull(result.LastModified);
    }

    private static string DomainsText(int count)
        => string.Join("\n", Domains(count).Select(d => $"0.0.0.0 {d}"));

    private static IEnumerable<string> Domains(int count)
        => Enumerable.Range(0, count).Select(i => $"host{i}.example.com");

    private static HttpResponseMessage TextResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
