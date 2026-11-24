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
    private readonly IslandClipboardTab _tab = new();
    private readonly DispatcherQueue    _dq;
    private string? _lastSearch;

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
        _lastSearch = search;
        _ = Task.Run(async () =>
        {
            try
            {
                var rows = await _tab.ListAsync(search);
                _dq.TryEnqueue(() => ItemsLoaded?.Invoke(rows));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowIsland] clipboard refresh failed: {ex.Message}");
            }
        });
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
