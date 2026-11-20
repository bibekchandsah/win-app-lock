using AppLock.WebFilter.Engine;

namespace AppLock.Tests.WebFilter;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "applock-wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // A still-mapped snapshot can keep the directory alive briefly; the OS cleans temp later.
        }
    }
}

public class ListSnapshotTests
{
    [Fact]
    public void MatchesTheNameAndItsSubdomains()
    {
        using var snapshot = ListSnapshot.FromDomains(new[] { "example.com", "ads.example.net" }, seed: 42);

        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.TryMatch("example.com", out var exact));
        Assert.Equal("example.com", exact);
        Assert.True(snapshot.TryMatch("www.example.com", out var sub));
        Assert.Equal("example.com", sub);
        Assert.False(snapshot.Contains("notexample.com"));
        Assert.False(snapshot.Contains("example.org"));
    }

    [Fact]
    public void ReturnsTheMostSpecificMatchingEntry()
    {
        using var snapshot = ListSnapshot.FromDomains(new[] { "example.com", "ads.example.com" }, seed: 7);

        Assert.True(snapshot.TryMatch("x.ads.example.com", out var matched));
        Assert.Equal("ads.example.com", matched);
    }

    [Fact]
    public void SkipsInvalidDomainsWhenCompiling()
    {
        using var snapshot = ListSnapshot.FromDomains(
            new[] { "example.com", "localhost", "1.2.3.4", "bad..com", "EXAMPLE.com." },
            seed: 1);

        Assert.Equal(1, snapshot.Count);
    }

    [Fact]
    public void RoundTripsThroughACompiledFile()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "test.snapshot");

        ListCompiler.CompileToFile(new[] { "example.com", "ads.example.net" }, path, seed: 1234);

        using var snapshot = ListSnapshot.Open(path);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(1234ul, snapshot.Seed);
        Assert.True(snapshot.Contains("www.example.com"));
        Assert.True(snapshot.Contains("deep.ads.example.net"));
        Assert.False(snapshot.Contains("example.org"));
    }

    [Fact]
    public void RejectsACorruptSnapshot()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "bad.snapshot");
        File.WriteAllBytes(path, new byte[100]);

        Assert.Throws<InvalidDataException>(() => ListSnapshot.Open(path));
    }

    [Fact]
    public void RejectsASnapshotWhoseCountDoesNotMatchItsLength()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "bad.snapshot");

        ListCompiler.CompileToFile(new[] { "example.com" }, path, seed: 5);

        // Truncate the payload but keep the valid header: the count no longer matches the length.
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 4).ToArray());

        Assert.Throws<InvalidDataException>(() => ListSnapshot.Open(path));
    }

    [Fact]
    public void CompiledFileIsCompact()
    {
        using var directory = new TempDirectory();
        string path = System.IO.Path.Combine(directory.Path, "size.snapshot");

        var domains = Enumerable.Range(0, 10_000).Select(i => $"host{i}.example.com");
        ListCompiler.CompileToFile(domains, path, seed: 9);

        var info = new FileInfo(path);
        Assert.Equal(ListSnapshot.HeaderSize + 10_000L * sizeof(ulong), info.Length);
        Assert.True(info.Length < 100 * 1024);
    }
}
