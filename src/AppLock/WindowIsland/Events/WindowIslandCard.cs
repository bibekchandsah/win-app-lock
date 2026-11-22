using System;

namespace AppLock.WindowIsland.Events;

/// <summary>
/// Priority of a live card; a higher priority replaces a lower one on screen immediately (island.md 5.2).
/// Priority (high -> low):
/// Reminder/Timer (90) > Notification (70) > VPN (60) > Rain (50) > Battery (40) > Clipboard/Screenshot (30) > CapsLock (20) > Volume (10).
/// </summary>
public enum WindowIslandCardPriority
{
    Volume = 10,
    CapsLock = 20,
    Clipboard = 30,
    Screenshot = 30,
    Battery = 40,
    Rain = 50,
    Vpn = 60,
    Notification = 70,
    ReminderTimer = 90
}

/// <summary>
/// Model for a live event card displayed in the native Window Island notch.
/// </summary>
public sealed record WindowIslandCard(
    string Kind,
    string Icon,
    string Title,
    string Detail,
    string AccentHex,
    WindowIslandCardPriority Priority,
    int StayMs,
    int Value = -1
);
