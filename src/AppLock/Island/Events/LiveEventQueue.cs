using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using AppLock.Island.Bridge;

namespace AppLock.Island.Events;

/// <summary>Priority of a live card; a higher card replaces a lower one on screen (island.md 5.2).</summary>
public enum IslandCardPriority
{
    Volume = 10,
    LockKey = 20,
    Clipboard = 30,
    Screenshot = 30,
}

/// <summary>A button on a live card (island.md 7: the QR result offers Open / Copy).</summary>
public sealed record IslandCardAction(string Id, string Label, bool Primary = false);

/// <summary>One card the folded pill can show. Rendered from the bridge payload, never reflowed.</summary>
public sealed record IslandCard(
    string Kind,
    string Icon,
    string Title,
    string Detail,
    string Accent,
    IslandCardPriority Priority,
    int StayMs,
    int Value = -1,
    System.Collections.Generic.IReadOnlyList<IslandCardAction>? Actions = null);

/// <summary>
/// The single queue every live card goes through (island.md 5.2): at most one card is visible,
/// repeated events of the same kind update it in place without re-animating, a higher-priority card
/// replaces the visible one, equal or lower priority waits (max 5, oldest low priority dropped).
/// The countdown pauses while the pointer is on the island, and nothing is shown while the island
/// is animating on entry/exit so the motion keeps the frame budget.
/// </summary>
public sealed class LiveEventQueue : IDisposable
{
    private const int MaxPending = 5;
    private const int TickMs = 100;

    private readonly Action<string> _send;
    private readonly Func<bool> _paused;
    private readonly Func<bool> _canShow;
    private readonly DispatcherQueueTimer _tick;
    private readonly List<IslandCard> _pending = new();

    private IslandCard? _visible;
    private int _remainingMs;
    private long _holdUntilTicks;
    private bool _disposed;

    /// <param name="paused">Countdown pause (pointer on the island, island hidden).</param>
    /// <param name="canShow">False while the open shell is showing: cards belong to the folded pill.</param>
    public LiveEventQueue(DispatcherQueue queue, Action<string> send, Func<bool> paused, Func<bool> canShow)
    {
        _send = send;
        _paused = paused;
        _canShow = canShow;

        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(TickMs);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => OnTick();
    }

    public bool HasVisibleCard => _visible != null;

    /// <summary>Deferral window: no new card while the island is animating (the card that causes a motion is sent before it).</summary>
    public void HoldForMotion(int durationMs)
    {
        _holdUntilTicks = Environment.TickCount64 + Math.Max(0, durationMs) + 60;
    }

    private bool IsHeld => Environment.TickCount64 < _holdUntilTicks;

    public void Push(IslandCard card)
    {
        if (_disposed) return;

        if (_visible != null)
        {
            if (_visible.Kind == card.Kind)
            {
                // Coalesce: volume dragging, two copies in a row — update the card, no animation.
                Show(card, animate: false);
                return;
            }

            if (card.Priority > _visible.Priority)
            {
                Show(card, animate: false);
                return;
            }

            Enqueue(card);
            return;
        }

        // Nothing on screen: wait while the pill is mid-animation or the shell is open.
        if (IsHeld || !_canShow())
        {
            Enqueue(card);
            return;
        }

        Show(card, animate: true);
    }

    /// <summary>Drops pending and visible cards (island hidden, Quiet mode, disabled).</summary>
    public void Clear()
    {
        _pending.Clear();
        if (_visible == null) return;

        _visible = null;
        _remainingMs = 0;
        _tick.Stop();
        _send(BridgeMessages.CardClearMessage());
    }

    private void Enqueue(IslandCard card)
    {
        // Keep ticking so the queue can drain as soon as the shell folds again.
        if (!_tick.IsRunning) _tick.Start();

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

    private void Show(IslandCard card, bool animate)
    {
        _visible = card;
        _remainingMs = card.StayMs;
        _send(BridgeMessages.CardMessage(card, animate));

        if (!_tick.IsRunning) _tick.Start();
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

        if (_paused()) return;

        _remainingMs -= TickMs;
        if (_remainingMs > 0) return;

        _visible = null;
        _send(BridgeMessages.CardClearMessage());
        Pump();
    }

    private void Pump()
    {
        if (_visible != null || IsHeld || !_canShow()) return;

        IslandCard? next = null;
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
            _tick.Stop();
            return;
        }

        _pending.Remove(next);
        Show(next, animate: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _pending.Clear();
        _visible = null;
    }
}
