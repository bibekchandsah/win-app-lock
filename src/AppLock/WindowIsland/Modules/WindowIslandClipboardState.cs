using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AppLock.Island.Modules;
using Microsoft.UI.Dispatching;

namespace AppLock.WindowIsland.Modules;

/// <summary>
/// Thin async wrapper around IslandClipboardTab for the Window Island Clipboard tab.
/// Loads items off the UI thread and delivers them back via the dispatcher queue.
/// </summary>
public sealed class WindowIslandClipboardState
{
    private readonly IslandClipboardTab _tab = new();
    private readonly DispatcherQueue    _dq;

    public event Action<IReadOnlyList<ClipboardRow>>? ItemsLoaded;

    public WindowIslandClipboardState(DispatcherQueue dq) => _dq = dq;

    public void Refresh(string? search = null)
    {
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

    public Task<bool> PasteAsync(string id, bool copyOnly) => _tab.PasteAsync(id, copyOnly);
    public Task<bool> PinAsync(string id)                   => _tab.PinAsync(id);
    public Task ClearAsync()                                => _tab.ClearAsync();
}
