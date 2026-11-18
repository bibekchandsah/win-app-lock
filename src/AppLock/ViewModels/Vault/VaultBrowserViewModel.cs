using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using AppLock.Models.Vault;
using AppLock.Services.Vault;

namespace AppLock.ViewModels.Vault;

public record BreadcrumbItem(string Name, Guid? FolderId);

public partial class VaultBrowserViewModel : ObservableObject
{
    public VaultSession Session { get; }

    [ObservableProperty]
    private string _vaultName = string.Empty;

    [ObservableProperty]
    private Guid? _currentFolderId = null;

    [ObservableProperty]
    private ObservableCollection<BreadcrumbItem> _breadcrumbs = new();

    [ObservableProperty]
    private ObservableCollection<VaultItemViewModel> _items = new();

    [ObservableProperty]
    private bool _isTrashMode = false;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _statusSummary = string.Empty;

    [ObservableProperty]
    private bool _showEncryptionBanner = true;

    [ObservableProperty]
    private bool _isOperationRunning = false;

    [ObservableProperty]
    private int _operationProgress = 0;

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _operationErrors = new();

    [ObservableProperty]
    private bool _hasOperationErrors = false;

    public VaultBrowserViewModel(VaultSession session)
    {
        Session = session;
        VaultName = session.VaultName;
        Refresh();
    }

    public void Refresh()
    {
        Items.Clear();

        List<IndexItem> sourceItems;
        lock (Session)
        {
            if (IsTrashMode)
            {
                sourceItems = Session.Index.Items.Where(i => i.IsTrash).ToList();
            }
            else
            {
                sourceItems = Session.Index.Items
                    .Where(i => !i.IsTrash && i.ParentId == CurrentFolderId)
                    .ToList();
            }
        }

        // Apply search query filter if set
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            sourceItems = sourceItems
                .Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Folders first, then alphabetically
        var sorted = sourceItems
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var item in sorted)
        {
            Items.Add(new VaultItemViewModel(item));
        }

        UpdateBreadcrumbs();
        UpdateStatusSummary();
    }

    public void NavigateToFolder(Guid? folderId)
    {
        IsTrashMode = false;
        CurrentFolderId = folderId;
        SearchQuery = string.Empty;
        Refresh();
    }

    public void NavigateUp()
    {
        if (IsTrashMode)
        {
            NavigateToFolder(null);
            return;
        }

        if (!CurrentFolderId.HasValue) return;

        lock (Session)
        {
            var current = Session.Index.Items.Find(i => i.Id == CurrentFolderId.Value);
            CurrentFolderId = current?.ParentId;
        }
        Refresh();
    }

    public void ToggleTrashMode()
    {
        IsTrashMode = !IsTrashMode;
        SearchQuery = string.Empty;
        Refresh();
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem(VaultName, null));

        if (IsTrashMode)
        {
            Breadcrumbs.Add(new BreadcrumbItem("Trash", null));
            return;
        }

        if (!CurrentFolderId.HasValue) return;

        var pathStack = new Stack<BreadcrumbItem>();
        var curr = CurrentFolderId;

        lock (Session)
        {
            while (curr.HasValue)
            {
                var folder = Session.Index.Items.Find(i => i.Id == curr.Value);
                if (folder == null) break;
                pathStack.Push(new BreadcrumbItem(folder.Name, folder.Id));
                curr = folder.ParentId;
            }
        }

        while (pathStack.Count > 0)
        {
            Breadcrumbs.Add(pathStack.Pop());
        }
    }

    private void UpdateStatusSummary()
    {
        int count = Items.Count;
        long totalSize = Items.Where(i => !i.IsDirectory).Sum(i => i.Size);

        StatusSummary = IsTrashMode
            ? $"{count} items in trash"
            : $"{count} items  •  {FormatFileSize(totalSize)}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
