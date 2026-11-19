using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

public class TempOpenServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _vaultDir;
    private readonly VaultSession _session;

    public TempOpenServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AppLock_TempOpenTest_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void SecureDelete_WipesAndRemovesFile()
    {
        var path = Path.Combine(_testDir, "wipeme.txt");
        File.WriteAllText(path, "Super confidential data that must be scrubbed");

        SecureDelete.WipeAndDeleteFile(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OpenWithDefaultApp_CreatesProtectedSessionAndGenericName()
    {
        var sample = Path.Combine(_testDir, "report.docx");
        var content = "Original report content";
        File.WriteAllText(sample, content);

        var addRes = await VaultOperations.AddFileAsync(_session, sample);
        Assert.True(addRes.Success);

        var item = _session.Index.Items.Find(i => i.Name == "report.docx")!;

        var active = await TempOpenService.OpenWithDefaultAppAsync(_session, item, useGenericName: true);
        try
        {
            Assert.NotNull(active);
            Assert.True(File.Exists(active.Metadata.TempFilePath));
            Assert.StartsWith("item-", Path.GetFileName(active.Metadata.TempFilePath));
            Assert.EndsWith(".docx", active.Metadata.TempFilePath);
            Assert.Equal(content, File.ReadAllText(active.Metadata.TempFilePath));

            // session.json exists
            var sessionJson = Path.Combine(active.Metadata.SessionFolderPath, "session.json");
            Assert.True(File.Exists(sessionJson));
        }
        finally
        {
            await TempOpenService.CloseSessionAsync(active.Metadata.SessionId);
            Assert.False(File.Exists(active.Metadata.TempFilePath));
            Assert.False(Directory.Exists(active.Metadata.SessionFolderPath));
        }
    }

    [Fact]
    public async Task CloseSession_ModifiedFile_ReencryptsChangesBackIntoVault()
    {
        var sample = Path.Combine(_testDir, "notes.txt");
        File.WriteAllText(sample, "Initial notes");

        await VaultOperations.AddFileAsync(_session, sample);
        var item = _session.Index.Items.Find(i => i.Name == "notes.txt")!;
        var oldObjId = item.ObjectId;

        var active = await TempOpenService.OpenWithDefaultAppAsync(_session, item, useGenericName: true);

        // Edit the file while open
        var editedContent = "Edited notes from external word processor!";
        File.WriteAllText(active.Metadata.TempFilePath, editedContent);

        // Close session (should write back)
        await TempOpenService.CloseSessionAsync(active.Metadata.SessionId);

        // Verify index was updated
        Assert.NotEqual(oldObjId, item.ObjectId);
        Assert.Equal(Encoding.UTF8.GetByteCount(editedContent), item.Size);

        // Decrypt from vault and verify edited content
        var exportDir = Path.Combine(_testDir, "Exported");
        await VaultOperations.ExportItemAsync(_session, item, exportDir);

        var exportedFile = Path.Combine(exportDir, "notes.txt");
        Assert.True(File.Exists(exportedFile));
        Assert.Equal(editedContent, File.ReadAllText(exportedFile));
    }

    [Fact]
    public void ScanCrashRecoverySessions_FindsLeftoverFolders()
    {
        // Setup a simulated crash folder under TempRoot
        var fakeSessionId = Guid.NewGuid();
        var fakeDir = Path.Combine(TempOpenService.TempRoot, fakeSessionId.ToString("N"));
        Directory.CreateDirectory(fakeDir);

        var fakeFile = Path.Combine(fakeDir, "item-1234.txt");
        File.WriteAllText(fakeFile, "Unsaved crash data");

        var meta = new TempSessionMetadata
        {
            SessionId = fakeSessionId,
            VaultId = _session.VaultId,
            ItemId = Guid.NewGuid(),
            OriginalName = "secret.txt",
            TempFileName = "item-1234.txt",
            TempFilePath = fakeFile,
            SessionFolderPath = fakeDir,
            InitialHash = "abc"
        };
        File.WriteAllText(Path.Combine(fakeDir, "session.json"), System.Text.Json.JsonSerializer.Serialize(meta));

        var leftovers = TempOpenService.ScanCrashRecoverySessions();
        try
        {
            Assert.Contains(leftovers, l => l.SessionId == fakeSessionId);
        }
        finally
        {
            SecureDelete.WipeAndDeleteDirectory(fakeDir);
        }
    }
}
