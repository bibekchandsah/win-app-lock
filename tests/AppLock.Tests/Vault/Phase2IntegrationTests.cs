using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using AppLock.Models.Vault;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using Xunit;

namespace AppLock.Tests.Vault;

/// <summary>
/// Unit tests for ExplorerContextMenuService (registry read/write).
/// These tests only run on Windows. They write under HKCU and self-clean.
/// </summary>
public class ExplorerContextMenuServiceTests : IDisposable
{
    private const string TestKeyPathFiles = @"Software\Classes\*\shell\AppLock.AddVault.UnitTest";
    private const string TestKeyPathFolders = @"Software\Classes\Directory\shell\AppLock.AddVault.UnitTest";

    public ExplorerContextMenuServiceTests()
    {
        ExplorerContextMenuService.MenuKeyPathFiles = TestKeyPathFiles;
        ExplorerContextMenuService.MenuKeyPathFolders = TestKeyPathFolders;
        // Ensure clean state before each test
        ExplorerContextMenuService.Unregister();
    }

    public void Dispose()
    {
        ExplorerContextMenuService.Unregister();
        ExplorerContextMenuService.MenuKeyPathFiles = ExplorerContextMenuService.DefaultMenuKeyPathFiles;
        ExplorerContextMenuService.MenuKeyPathFolders = ExplorerContextMenuService.DefaultMenuKeyPathFolders;
    }

    [Fact]
    public void IsEnabled_WhenNotRegistered_ReturnsFalse()
    {
        ExplorerContextMenuService.Unregister();
        Assert.False(ExplorerContextMenuService.IsEnabled());
    }

    [Fact]
    public void Register_CreatesExpectedKeys_IsEnabledReturnsTrue()
    {
        ExplorerContextMenuService.Register();
        Assert.True(ExplorerContextMenuService.IsEnabled());
    }

    [Fact]
    public void Unregister_RemovesKeys_IsEnabledReturnsFalse()
    {
        ExplorerContextMenuService.Register();
        Assert.True(ExplorerContextMenuService.IsEnabled());

        ExplorerContextMenuService.Unregister();
        Assert.False(ExplorerContextMenuService.IsEnabled());
    }

    [Fact]
    public void SetEnabled_True_RegistersMenu()
    {
        ExplorerContextMenuService.SetEnabled(true);
        Assert.True(ExplorerContextMenuService.IsEnabled());
    }

    [Fact]
    public void SetEnabled_False_UnregistersMenu()
    {
        ExplorerContextMenuService.Register();
        ExplorerContextMenuService.SetEnabled(false);
        Assert.False(ExplorerContextMenuService.IsEnabled());
    }

    [Fact]
    public void Unregister_WhenNotRegistered_DoesNotThrow()
    {
        // Should be idempotent
        ExplorerContextMenuService.Unregister();
        ExplorerContextMenuService.Unregister();
        Assert.False(ExplorerContextMenuService.IsEnabled());
    }
}

/// <summary>
/// Unit tests for VaultIpcService — pending file queue write / read / clear.
/// </summary>
public class VaultIpcServiceTests : IDisposable
{
    private readonly string _pendingFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "pending_vault_add.json");

    public VaultIpcServiceTests()
    {
        // Clean slate
        if (File.Exists(_pendingFile)) File.Delete(_pendingFile);
    }

    public void Dispose()
    {
        if (File.Exists(_pendingFile)) File.Delete(_pendingFile);
    }

    [Fact]
    public void ReadAndClear_WhenNoPendingFile_ReturnsEmptyList()
    {
        var result = VaultIpcService.ReadAndClearPendingAdd();
        Assert.Empty(result);
    }

    [Fact]
    public void ReadAndClear_AfterSignalPendingAdd_ReturnsPaths_AndClearsFile()
    {
        var paths = new List<string> { @"C:\users\user\documents\secret.pdf", @"D:\photos\image.jpg" };
        // Write directly to pending file (simulate signal without IPC event)
        var dir = Path.GetDirectoryName(_pendingFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_pendingFile, System.Text.Json.JsonSerializer.Serialize(paths));

        var result = VaultIpcService.ReadAndClearPendingAdd();

        Assert.Equal(2, result.Count);
        Assert.Contains(@"C:\users\user\documents\secret.pdf", result);
        Assert.Contains(@"D:\photos\image.jpg", result);

        // File must be deleted after reading
        Assert.False(File.Exists(_pendingFile));
    }

    [Fact]
    public void ReadAndClear_CalledTwice_SecondCallReturnsEmpty()
    {
        var paths = new List<string> { @"C:\test.txt" };
        var dir = Path.GetDirectoryName(_pendingFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_pendingFile, System.Text.Json.JsonSerializer.Serialize(paths));

        var first = VaultIpcService.ReadAndClearPendingAdd();
        var second = VaultIpcService.ReadAndClearPendingAdd();

        Assert.Single(first);
        Assert.Empty(second);
    }
}
