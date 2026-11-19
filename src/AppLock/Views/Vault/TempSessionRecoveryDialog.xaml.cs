using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using WinRT.Interop;

namespace AppLock.Views.Vault;

public sealed partial class TempSessionRecoveryDialog : ContentDialog
{
    private readonly List<TempSessionMetadata> _sessions;

    public TempSessionRecoveryDialog(List<TempSessionMetadata> sessions)
    {
        _sessions = sessions;
        InitializeComponent();
        RecoveredListView.ItemsSource = _sessions;
    }

    private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await Task.Yield();
            // Save back to vault
            foreach (var meta in _sessions)
            {
                var reg = ServiceContainer.Settings.Vault.Vaults.Find(v => v.Id == meta.VaultId);
                var openSession = ServiceContainer.VaultSessionManager.GetSession(meta.VaultId);

                if (openSession != null && File.Exists(meta.TempFilePath))
                {
                    IndexItem? item;
                    lock (openSession)
                    {
                        item = openSession.Index.Items.Find(i => i.Id == meta.ItemId);
                    }

                    if (item != null)
                    {
                        var newObjectId = $"{Guid.NewGuid():N}.obj";
                        var finalObjPath = VaultStorage.GetObjectPath(openSession.VaultPath, newObjectId, isTrash: false);

                        long plainLen;
                        string shaHex;
                        using (var src = new FileStream(meta.TempFilePath, FileMode.Open, FileAccess.Read))
                        using (var dst = new FileStream(finalObjPath, FileMode.Create, FileAccess.Write))
                        {
                            (plainLen, shaHex) = FileObjectWriter.WriteObject(src, dst, openSession.FekWrapKey, item.Id);
                        }

                        lock (openSession)
                        {
                            item.ObjectId = newObjectId;
                            item.Size = plainLen;
                            item.Sha256 = shaHex;
                            item.ModifiedTimeUtc = DateTime.UtcNow;
                            openSession.Index.Revision++;
                            openSession.Index.Updated = DateTime.UtcNow;
                            VaultStorage.SaveIndex(openSession.VaultPath, openSession.Index, openSession.IndexKey);
                        }
                    }
                }

                SecureDelete.WipeAndDeleteDirectory(meta.SessionFolderPath);
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Recovery SaveBack error: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void SecondaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.Downloads;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                foreach (var meta in _sessions)
                {
                    if (File.Exists(meta.TempFilePath))
                    {
                        var target = Path.Combine(folder.Path, meta.OriginalName);
                        File.Copy(meta.TempFilePath, target, overwrite: true);
                    }
                    SecureDelete.WipeAndDeleteDirectory(meta.SessionFolderPath);
                }
            }
            else
            {
                args.Cancel = true;
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Recovery Export error: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CloseButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Discard all
        foreach (var meta in _sessions)
        {
            SecureDelete.WipeAndDeleteDirectory(meta.SessionFolderPath);
        }
    }
}
