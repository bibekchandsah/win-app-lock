using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

public class PreviewServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _vaultDir;
    private readonly VaultSession _session;

    public PreviewServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AppLock_PreviewTest_" + Guid.NewGuid().ToString("N"));
        _vaultDir = Path.Combine(_testDir, "TestVault");
        VaultStorage.InitializeVaultFolder(_vaultDir);

        var vaultId = Guid.NewGuid();
        var vmk = VaultCrypto.GenerateVmk();
        var indexData = new VaultIndexData
        {
            VaultId = vaultId,
            Revision = 1,
            Items = new()
        };

        _session = new VaultSession(vaultId, _vaultDir, "TestVault", vmk, indexData);
        VaultStorage.SaveIndex(_vaultDir, indexData, _session.IndexKey);
    }

    public void Dispose()
    {
        _session.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    [Theory]
    [InlineData("photo.png", PreviewCategory.Image)]
    [InlineData("picture.jpg", PreviewCategory.Image)]
    [InlineData("notes.txt", PreviewCategory.Text)]
    [InlineData("readme.md", PreviewCategory.Text)]
    [InlineData("config.json", PreviewCategory.Text)]
    [InlineData("document.pdf", PreviewCategory.Pdf)]
    [InlineData("song.mp3", PreviewCategory.Media)]
    [InlineData("video.mp4", PreviewCategory.Media)]
    [InlineData("app.exe", PreviewCategory.Unsupported)]
    [InlineData("archive.zip", PreviewCategory.Unsupported)]
    public void GetCategory_CorrectlyClassifiesFiles(string fileName, PreviewCategory expected)
    {
        var category = PreviewService.GetCategory(fileName);
        Assert.Equal(expected, category);
    }

    [Fact]
    public async Task ReadTextAsync_StreamsDirectlyFromEncryptedObject()
    {
        var src = Path.Combine(_testDir, "test.txt");
        var expectedText = "Encrypted in-memory preview text content without writing to disk!";
        File.WriteAllText(src, expectedText);

        var addRes = await VaultOperations.AddFileAsync(_session, src);
        Assert.True(addRes.Success);

        var item = _session.Index.Items.Find(i => i.Name == "test.txt");
        Assert.NotNull(item);

        var (text, truncated) = await PreviewService.ReadTextAsync(_session, item);
        Assert.False(truncated);
        Assert.Equal(expectedText, text);
    }

    [Fact]
    public async Task OpenStream_AllowsSeekAndReadInMemory()
    {
        var src = Path.Combine(_testDir, "seek_test.txt");
        var data = new byte[1000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        File.WriteAllBytes(src, data);

        await VaultOperations.AddFileAsync(_session, src);
        var item = _session.Index.Items.Find(i => i.Name == "seek_test.txt")!;

        using var stream = PreviewService.OpenStream(_session, item);
        Assert.True(stream.CanSeek);
        Assert.Equal(1000, stream.Length);

        // Seek to offset 500
        stream.Seek(500, SeekOrigin.Begin);
        var buf = new byte[10];
        int read = stream.Read(buf, 0, 10);
        Assert.Equal(10, read);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal((byte)((500 + i) % 256), buf[i]);
        }
    }
}
