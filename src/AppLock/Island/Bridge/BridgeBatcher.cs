using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;

namespace AppLock.Island.Bridge;

/// <summary>
/// Coalesces host → UI updates to at most one message per module per 100 ms (island-webview2.md 5),
/// so a burst of provider updates can never queue up work while the page is animating.
/// The newest payload of each module wins.
/// </summary>
public sealed class BridgeBatcher : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Action<string> _send;
    private readonly Action<string, Exception>? _onError;

    public BridgeBatcher(DispatcherQueue dispatcher, Action<string> send, Action<string, Exception>? onError = null)
    {
        _send = send;
        _onError = onError;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = FlushInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Flush();
    }

    /// <summary>Queues an already-built envelope string for the given module key.</summary>
    public void Post(string module, string message)
    {
        _pending[module] = message;
        if (!_timer.IsRunning) _timer.Start();
    }

    /// <summary>Sends a message immediately (used for things that must not wait, e.g. a snapshot).</summary>
    public void SendNow(string message)
    {
        try
        {
            _send(message);
        }
        catch (Exception ex)
        {
            _onError?.Invoke("send", ex);
        }
    }

    private void Flush()
    {
        if (_pending.Count == 0)
        {
            _timer.Stop();
            return;
        }

        foreach (var (module, message) in _pending)
        {
            try
            {
                _send(message);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(module, ex);
            }
        }

        _pending.Clear();
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
