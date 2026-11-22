using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;

namespace AppLock.WindowIsland.Events;

/// <summary>
/// Single prioritized queue for live event cards in native Window Island (island.md 5.2).
/// Features:
///   - At most one card visible at a time.
///   - Repeated events of the same kind coalesce in place without re-morphing.
///   - Higher-priority cards preempt the visible card with an in-place crossfade.
///   - Equal or lower priority cards wait in queue (max 5, oldest low priority dropped).
///   - Countdown pauses while mouse hovers the card.
///   - Held during open shell display and morph transitions.
/// </summary>
public sealed class WindowIslandEventQueue : IDisposable
{
    private const int MaxPending = 5;
    private const int TickMs = 100;

    private readonly DispatcherQueue _queue;
    private readonly Action<WindowIslandCard, bool> _onShow;
    private readonly Action _onClear;
    private readonly Func<bool> _isHovered;
    private readonly Func<bool> _canShow;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<WindowIslandCard> _pending = new();

    private WindowIslandCard? _visible;
    private int _remainingMs;
    private long _holdUntilTicks;
    private bool _disposed;

    public bool HasVisibleCard => _visible != null;
    public WindowIslandCard? CurrentCard => _visible;

    public WindowIslandEventQueue(
        DispatcherQueue queue,
        Action<WindowIslandCard, bool> onShow,
        Action onClear,
        Func<bool> isHovered,
        Func<bool> canShow)
    {
        _queue     = queue;
        _onShow    = onShow;
        _onClear   = onClear;
        _isHovered = isHovered;
        _canShow   = canShow;

        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TickMs);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();
    }

    public void HoldForMotion(int durationMs)
    {
        _holdUntilTicks = Environment.TickCount64 + Math.Max(0, durationMs) + 60;
    }

    private bool IsHeld => Environment.TickCount64 < _holdUntilTicks;

    public void Push(WindowIslandCard card)
    {
        if (_disposed) return;

        if (_visible != null)
        {
            if (_visible.Kind == card.Kind)
            {
                // Coalesce: repeated events of same type update the visible card without re-morphing
                Show(card, animate: false);
                return;
            }

            if (card.Priority > _visible.Priority)
            {
                // Preempt: higher priority replaces current card immediately with crossfade
                Show(card, animate: false);
                return;
            }

            Enqueue(card);
            return;
        }

        if (IsHeld || !_canShow())
        {
            Enqueue(card);
            return;
        }

        Show(card, animate: true);
    }

    public void Clear()
    {
        _pending.Clear();
        if (_visible == null) return;

        _visible = null;
        _remainingMs = 0;
        _timer.Stop();
        _onClear();
    }

    private void Enqueue(WindowIslandCard card)
    {
        if (!_timer.IsRunning) _timer.Start();

        if (_pending.Count >= MaxPending)
        {
            int lowest = 0;
            for (int i = 1; i < _pending.Count; i++)
            {
                if (_pending[i].Priority < _pending[lowest].Priority) lowest = i;
            }
            _pending.RemoveAt(lowest);
        }

        _pending.Add(card);
    }

    private void Show(WindowIslandCard card, bool animate)
    {
        _visible     = card;
        _remainingMs = card.StayMs;
        _onShow(card, animate);

        if (!_timer.IsRunning) _timer.Start();
    }

    private void OnTick()
    {
        if (_disposed) return;
        if (IsHeld || !_canShow()) return;

        if (_visible == null)
        {
            Pump();
            return;
        }

        // Hover pauses the countdown
        if (_isHovered()) return;

        _remainingMs -= TickMs;
        if (_remainingMs > 0) return;

        _visible = null;
        _onClear();
        Pump();
    }

    private void Pump()
    {
        if (_visible != null || IsHeld || !_canShow()) return;

        WindowIslandCard? next = null;
        int best = int.MinValue;
        for (int i = 0; i < _pending.Count; i++)
        {
            if ((int)_pending[i].Priority > best)
            {
                best = (int)_pending[i].Priority;
                next = _pending[i];
            }
        }

        if (next == null)
        {
            _timer.Stop();
            return;
        }

        _pending.Remove(next);
        Show(next, animate: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _pending.Clear();
        _visible = null;
    }
}
