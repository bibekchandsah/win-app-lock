using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AppLock.Island.Modules;
using AppLock.Services;
using Microsoft.UI.Dispatching;

namespace AppLock.WindowIsland.Modules;

/// <summary>
/// Thin async wrapper around IslandClipboardTab for the Window Island Clipboard tab.
/// Loads items off the UI thread and delivers them back via the dispatcher queue.
/// Syncs in real time with AppLock's core ClipboardStore.
/// </summary>
public sealed class WindowIslandClipboardState : IDisposable
{
    private const int PageSize = 60;
    private readonly IslandClipboardTab _tab = new();
    private readonly DispatcherQueue    _dq;
    private string? _lastSearch;
    private int _currentLimit = PageSize;
    private bool _loadingMore;

    public event Action<IReadOnlyList<ClipboardRow>>? ItemsLoaded;

    public WindowIslandClipboardState(DispatcherQueue dq)
    {
        _dq = dq;
        try
        {
            ServiceContainer.ClipboardStore.DataChanged += OnDataChanged;
        }
        catch { }
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        Refresh(_lastSearch);
    }

    public void Refresh(string? search = null)
    {
        if (!string.Equals(_lastSearch, search, StringComparison.Ordinal))
        {
            _currentLimit = PageSize;
        }
        _lastSearch = search;
        int limit = _currentLimit;
        _ = Task.Run(async () =>
        {
            try
            {
                var rows = await _tab.ListAsync(search, limit);
                _loadingMore = false;
                _dq.TryEnqueue(() => ItemsLoaded?.Invoke(rows));
            }
            catch (Exception ex)
            {
                _loadingMore = false;
                Debug.WriteLine($"[WindowIsland] clipboard refresh failed: {ex.Message}");
            }
        });
    }

    public void LoadMoreIfNeeded(int currentCount)
    {
        if (_loadingMore || currentCount < _currentLimit) return;
        _loadingMore = true;
        _currentLimit += PageSize;
        Refresh(_lastSearch);
    }

    public Task<bool> PasteAsync(string id, bool copyOnly, nint targetWindow = 0)
        => _tab.PasteAsync(id, copyOnly, targetWindow);
    public Task<bool> PinAsync(string id)                   => _tab.PinAsync(id);
    public Task ClearAsync()                                => _tab.ClearAsync();

    public void Dispose()
    {
        try
        {
            ServiceContainer.ClipboardStore.DataChanged -= OnDataChanged;
        }
        catch { }
    }
}
