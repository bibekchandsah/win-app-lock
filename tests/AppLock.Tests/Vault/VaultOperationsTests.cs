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

public class VaultOperationsTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _vaultDir;
    private readonly VaultSession _session;

    public VaultOperationsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AppLock_VaultOpTest_" + Guid.NewGuid().ToString("N"));
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
    public async Task AddFile_EncryptsAndVerifiesOriginalContent()
    {
        var sampleFile = Path.Combine(_testDir, "sample.txt");
        var content = "This is a secret document that belongs inside the encrypted vault!";
        File.WriteAllText(sampleFile, content);

        var result = await VaultOperations.AddFileAsync(_session, sampleFile, deleteOriginal: false);
        Assert.True(result.Success);

        // Check index
        var item = _session.Index.Items.Find(i => i.Name == "sample.txt");
        Assert.NotNull(item);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), item.Size);
        Assert.False(string.IsNullOrEmpty(item.ObjectId));

        // Check object file on disk
        var objPath = VaultStorage.GetObjectPath(_vaultDir, item.ObjectId!, isTrash: false);
        Assert.True(File.Exists(objPath));

        // Export and verify plaintext
        var exportDir = Path.Combine(_testDir, "Exported");
        await VaultOperations.ExportItemAsync(_session, item, exportDir);

        var exportedFile = Path.Combine(exportDir, "sample.txt");
        Assert.True(File.Exists(exportedFile));
        Assert.Equal(content, File.ReadAllText(exportedFile));
    }

    [Fact]
    public async Task AddFile_DeleteOriginal_RemovesSourceFile()
    {
        var sampleFile = Path.Combine(_testDir, "will_delete.txt");
        File.WriteAllText(sampleFile, "Goodbye plaintext original!");

        var result = await VaultOperations.AddFileAsync(_session, sampleFile, deleteOriginal: true);
        Assert.True(result.Success);
        Assert.False(File.Exists(sampleFile), "Original file should have been deleted after verified encryption.");
    }

    [Fact]
    public async Task CreateFolder_Rename_And_MoveItem()
    {
        // 1. Create Folder
        var folder = VaultOperations.CreateFolder(_session, "Documents", null);
        Assert.NotNull(folder);
        Assert.True(folder.IsDirectory);

        // 2. Add file inside folder
        var srcFile = Path.Combine(_testDir, "doc.txt");
        File.WriteAllText(srcFile, "Inside documents");
        var res = await VaultOperations.AddFileAsync(_session, srcFile, parentId: folder.Id);
        Assert.True(res.Success);

        var docItem = _session.Index.Items.Find(i => i.Name == "doc.txt");
        Assert.NotNull(docItem);
        Assert.Equal(folder.Id, docItem.ParentId);

        // 3. Rename
        VaultOperations.RenameItem(_session, docItem.Id, "renamed_doc.txt");
        Assert.Equal("renamed_doc.txt", docItem.Name);

        // 4. Move to root
        VaultOperations.MoveItem(_session, docItem.Id, null);
        Assert.Null(docItem.ParentId);
    }

    [Fact]
    public async Task Trash_Restore_And_DeletePermanently()
    {
        var sampleFile = Path.Combine(_testDir, "trash_test.txt");
        File.WriteAllText(sampleFile, "Trash me");
        await VaultOperations.AddFileAsync(_session, sampleFile);

        var item = _session.Index.Items.Find(i => i.Name == "trash_test.txt");
        Assert.NotNull(item);
        var objectId = item.ObjectId!;

        // 1. Move to Trash
        VaultOperations.MoveToTrash(_session, item.Id);
        Assert.True(item.IsTrash);
        Assert.NotNull(item.DeletedTimeUtc);
        Assert.True(File.Exists(VaultStorage.GetObjectPath(_vaultDir, objectId, isTrash: true)));
        Assert.False(File.Exists(VaultStorage.GetObjectPath(_vaultDir, objectId, isTrash: false)));

        // 2. Restore
        VaultOperations.RestoreFromTrash(_session, item.Id);
        Assert.False(item.IsTrash);
        Assert.Null(item.DeletedTimeUtc);
        Assert.True(File.Exists(VaultStorage.GetObjectPath(_vaultDir, objectId, isTrash: false)));

        // 3. Delete Permanently
        VaultOperations.DeletePermanently(_session, item.Id);
        Assert.Null(_session.Index.Items.Find(i => i.Id == item.Id));
        Assert.False(File.Exists(VaultStorage.GetObjectPath(_vaultDir, objectId, isTrash: false)));
    }

    [Fact]
    public async Task EmptyTrash_RemovesAllTrashObjects()
    {
        var f1 = Path.Combine(_testDir, "t1.txt");
        var f2 = Path.Combine(_testDir, "t2.txt");
        File.WriteAllText(f1, "1");
        File.WriteAllText(f2, "2");

        await VaultOperations.AddFileAsync(_session, f1);
        await VaultOperations.AddFileAsync(_session, f2);

        var i1 = _session.Index.Items.Find(i => i.Name == "t1.txt")!;
        var i2 = _session.Index.Items.Find(i => i.Name == "t2.txt")!;

        VaultOperations.MoveToTrash(_session, i1.Id);
        VaultOperations.MoveToTrash(_session, i2.Id);

        VaultOperations.EmptyTrash(_session);

        Assert.Empty(_session.Index.Items.FindAll(i => i.IsTrash));
        Assert.False(File.Exists(VaultStorage.GetObjectPath(_vaultDir, i1.ObjectId!, isTrash: true)));
        Assert.False(File.Exists(VaultStorage.GetObjectPath(_vaultDir, i2.ObjectId!, isTrash: true)));
    }
}
