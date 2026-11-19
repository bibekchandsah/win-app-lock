using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;
using AppLock.ViewModels.Vault;
using WinRT.Interop;

namespace AppLock.Views.Vault;

public sealed partial class VaultBrowserPage : Page
{
    public VaultBrowserViewModel? ViewModel { get; private set; }

    private CancellationTokenSource? _opCts;

    public VaultBrowserPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is VaultSession session)
        {
            ViewModel = new VaultBrowserViewModel(session);
        }
        else if (e.Parameter is Guid vaultId)
        {
            var s = ServiceContainer.VaultSessionManager.GetSession(vaultId);
            if (s != null)
                ViewModel = new VaultBrowserViewModel(s);
        }

        if (ViewModel == null)
        {
            Frame.Navigate(typeof(VaultPage));
            return;
        }

        ServiceContainer.VaultSessionManager.VaultLocked += OnVaultLocked;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ServiceContainer.VaultSessionManager.VaultLocked -= OnVaultLocked;
    }

    private void OnVaultLocked(object? sender, Guid id)
    {
        if (ViewModel?.Session.VaultId == id)
        {
            DispatcherQueue.TryEnqueue(() => Frame.Navigate(typeof(VaultPage)));
        }
    }

    // ---- Function Bindings --------------------------------------------

    public Visibility GetNormalVisibility(bool isTrash) =>
        isTrash ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GetTrashVisibility(bool isTrash) =>
        isTrash ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetEmptyVisibility(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetProgressVisibility(bool running) =>
        running ? Visibility.Visible : Visibility.Collapsed;

    // ---- Navigation Handlers ------------------------------------------

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        if (ViewModel.IsTrashMode || ViewModel.CurrentFolderId.HasValue)
        {
            ViewModel.NavigateUp();
        }
        else
        {
            Frame.Navigate(typeof(VaultPage));
        }
    }

    private void LockNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        ServiceContainer.VaultSessionManager.Lock(ViewModel.Session.VaultId);
        Frame.Navigate(typeof(VaultPage));
    }

    private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (ViewModel == null) return;
        if (args.Item is BreadcrumbItem item)
        {
            ViewModel.NavigateToFolder(item.FolderId);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel?.Refresh();
    }

    private void TrashToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleTrashMode();
    }

    private void FileListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not VaultItemViewModel item || ViewModel == null) return;

        if (item.IsDirectory && !item.IsTrash)
        {
            ViewModel.NavigateToFolder(item.Id);
        }
        else if (!item.IsDirectory && !item.IsTrash && PreviewService.CanPreview(item.Name))
        {
            OpenPreview(item);
        }
    }

    private void OpenPreview(VaultItemViewModel item)
    {
        if (ViewModel == null) return;
        var win = new SecurePreviewWindow(ViewModel.Session, item.Model);
        win.Activate();
    }

    // ---- Add Files & Folders ------------------------------------------

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files != null && files.Count > 0)
            {
                await ProcessFilesAddAsync(files.Select(f => f.Path).ToList());
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"AddFiles picker error: {ex.Message}");
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                await ProcessFolderAddAsync(folder.Path);
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"AddFolder picker error: {ex.Message}");
        }
    }

    private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var box = new TextBox { PlaceholderText = "Folder name", CornerRadius = new CornerRadius(4) };
        var dialog = new ContentDialog
        {
            Title = "New folder",
            Content = box,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            VaultOperations.CreateFolder(ViewModel.Session, box.Text, ViewModel.CurrentFolderId);
            ViewModel.Refresh();
        }
    }

    private async Task ProcessFilesAddAsync(IReadOnlyList<string> filePaths)
    {
        if (ViewModel == null || filePaths.Count == 0) return;

        _opCts = new CancellationTokenSource();
        ViewModel.IsOperationRunning = true;
        ViewModel.OperationProgress = 0;
        ViewModel.OperationStatusText = "Preparing to encrypt…";

        bool deleteOriginal = ServiceContainer.Settings.Vault.DeleteOriginalsAfterVerify;

        try
        {
            for (int i = 0; i < filePaths.Count; i++)
            {
                _opCts.Token.ThrowIfCancellationRequested();
                var path = filePaths[i];
                var name = Path.GetFileName(path);

                int overallPct = (int)((double)i / filePaths.Count * 100);
                var progress = new Progress<(int Percent, string Status)>(p =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ViewModel.OperationProgress = overallPct + (p.Percent / filePaths.Count);
                        ViewModel.OperationStatusText = p.Status;
                    });
                });

                var res = await VaultOperations.AddFileAsync(
                    ViewModel.Session,
                    path,
                    ViewModel.CurrentFolderId,
                    deleteOriginal,
                    progress,
                    _opCts.Token);

                if (!res.Success && res.ErrorMessage != null)
                {
                    ViewModel.OperationErrors.Add($"{res.FileName}: {res.ErrorMessage}");
                    ViewModel.HasOperationErrors = true;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ViewModel.IsOperationRunning = false;
            ViewModel.Refresh();
        }
    }

    private async Task ProcessFolderAddAsync(string folderPath)
    {
        if (ViewModel == null) return;

        _opCts = new CancellationTokenSource();
        ViewModel.IsOperationRunning = true;
        ViewModel.OperationProgress = 0;
        ViewModel.OperationStatusText = "Adding folder…";

        bool deleteOriginal = ServiceContainer.Settings.Vault.DeleteOriginalsAfterVerify;

        try
        {
            var progress = new Progress<(int Percent, string Status)>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.OperationProgress = p.Percent;
                    ViewModel.OperationStatusText = p.Status;
                });
            });

            await VaultOperations.AddDirectoryAsync(
                ViewModel.Session,
                folderPath,
                ViewModel.CurrentFolderId,
                deleteOriginal,
                progress,
                _opCts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            ViewModel.IsOperationRunning = false;
            ViewModel.Refresh();
        }
    }

    // ---- Drag and Drop ------------------------------------------------

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel?.IsTrashMode == true)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Vault";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel?.IsTrashMode == true) return;

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var filesToAdd = new List<string>();

            foreach (var item in items)
            {
                if (item is StorageFile f)
                {
                    filesToAdd.Add(f.Path);
                }
                else if (item is StorageFolder d)
                {
                    await ProcessFolderAddAsync(d.Path);
                }
            }

            if (filesToAdd.Count > 0)
            {
                await ProcessFilesAddAsync(filesToAdd);
            }
        }
    }

    // ---- Export -------------------------------------------------------

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var selected = FileListView.SelectedItems.Cast<VaultItemViewModel>().ToList();
        if (selected.Count == 0)
        {
            var dialog = new ContentDialog
            {
                Title = "Nothing selected",
                Content = new TextBlock { Text = "Please select one or more items to export." },
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.Downloads;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            _opCts = new CancellationTokenSource();
            ViewModel.IsOperationRunning = true;
            ViewModel.OperationProgress = 0;
            ViewModel.OperationStatusText = "Exporting items…";

            for (int i = 0; i < selected.Count; i++)
            {
                _opCts.Token.ThrowIfCancellationRequested();
                var item = selected[i];
                var progress = new Progress<(int Percent, string Status)>(p =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ViewModel.OperationProgress = p.Percent;
                        ViewModel.OperationStatusText = p.Status;
                    });
                });

                await VaultOperations.ExportItemAsync(
                    ViewModel.Session,
                    item.Model,
                    folder.Path,
                    moveOut: false,
                    progress: progress,
                    ct: _opCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Export failed: {ex.Message}");
            var errDialog = new ContentDialog
            {
                Title = "Export failed",
                Content = new TextBlock { Text = ex.Message },
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot
            };
            await errDialog.ShowAsync();
        }
        finally
        {
            if (ViewModel != null) ViewModel.IsOperationRunning = false;
        }
    }

    // ---- Delete & Trash -----------------------------------------------

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var selected = FileListView.SelectedItems.Cast<VaultItemViewModel>().ToList();
        if (selected.Count == 0) return;

        if (ViewModel.IsTrashMode)
        {
            // Permanent delete confirmation
            var confirm = new ContentDialog
            {
                Title = "Permanently delete items?",
                Content = new TextBlock
                {
                    Text = $"Are you sure you want to permanently delete {selected.Count} item(s)? This cannot be undone.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Delete permanently",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                foreach (var item in selected)
                {
                    VaultOperations.DeletePermanently(ViewModel.Session, item.Id);
                }
                ViewModel.Refresh();
            }
        }
        else
        {
            // Move to trash
            foreach (var item in selected)
            {
                VaultOperations.MoveToTrash(ViewModel.Session, item.Id);
            }
            ViewModel.Refresh();
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var selected = FileListView.SelectedItems.Cast<VaultItemViewModel>().ToList();
        foreach (var item in selected)
        {
            VaultOperations.RestoreFromTrash(ViewModel.Session, item.Id);
        }
        ViewModel.Refresh();
    }

    private async void EmptyTrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        var confirm = new ContentDialog
        {
            Title = "Empty trash?",
            Content = new TextBlock
            {
                Text = "All items in the trash will be permanently deleted. This cannot be undone.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Empty trash",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            VaultOperations.EmptyTrash(ViewModel.Session);
            ViewModel.Refresh();
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        _opCts?.Cancel();
    }

    // ---- Context Menu -------------------------------------------------

    private void Item_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement elem || elem.DataContext is not VaultItemViewModel item || ViewModel == null) return;

        var flyout = new MenuFlyout();

        if (!item.IsTrash)
        {
            if (!item.IsDirectory && PreviewService.CanPreview(item.Name))
            {
                var previewItem = new MenuFlyoutItem { Text = "Preview", Icon = new SymbolIcon(Symbol.View) };
                previewItem.Click += (s, a) => OpenPreview(item);
                flyout.Items.Add(previewItem);
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            var renameItem = new MenuFlyoutItem { Text = "Rename", Icon = new SymbolIcon(Symbol.Rename) };
            renameItem.Click += async (s, a) =>
            {
                var box = new TextBox { Text = item.Name, CornerRadius = new CornerRadius(4) };
                var d = new ContentDialog
                {
                    Title = "Rename",
                    Content = box,
                    PrimaryButtonText = "Rename",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };
                if (await d.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
                {
                    VaultOperations.RenameItem(ViewModel.Session, item.Id, box.Text);
                    ViewModel.Refresh();
                }
            };
            flyout.Items.Add(renameItem);

            var deleteItem = new MenuFlyoutItem { Text = "Move to trash", Icon = new SymbolIcon(Symbol.Delete) };
            deleteItem.Click += (s, a) =>
            {
                VaultOperations.MoveToTrash(ViewModel.Session, item.Id);
                ViewModel.Refresh();
            };
            flyout.Items.Add(deleteItem);
        }
        else
        {
            var restoreItem = new MenuFlyoutItem { Text = "Restore", Icon = new SymbolIcon(Symbol.Refresh) };
            restoreItem.Click += (s, a) =>
            {
                VaultOperations.RestoreFromTrash(ViewModel.Session, item.Id);
                ViewModel.Refresh();
            };
            flyout.Items.Add(restoreItem);

            var deletePermItem = new MenuFlyoutItem { Text = "Delete permanently", Icon = new SymbolIcon(Symbol.Delete) };
            deletePermItem.Click += (s, a) =>
            {
                VaultOperations.DeletePermanently(ViewModel.Session, item.Id);
                ViewModel.Refresh();
            };
            flyout.Items.Add(deletePermItem);
        }

        flyout.ShowAt(elem);
    }
}
