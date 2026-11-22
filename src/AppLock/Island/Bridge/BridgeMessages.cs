using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AppLock.Island.Events;
using AppLock.Island.Modules;
using AppLock.Models;

namespace AppLock.Island.Bridge;

/// <summary>Colours the UI needs, resolved from the theme and the opacity setting.</summary>
public readonly record struct IslandPalette(string Fill, string Text, string DimText, string FaintText, bool IsDark);

/// <summary>
/// Builds the host → UI messages of island-webview2.md section 5. Only this class decides the
/// wire format, so the UI and the host cannot drift apart.
/// </summary>
public static class BridgeMessages
{
    public const string SettingsSnapshot = "settings.snapshot";
    public const string SettingsChanged = "settings.changed";
    public const string StateTime = "state.time";
    public const string StateBattery = "state.battery";
    public const string StateHover = "state.hover";
    public const string AnimPause = "anim.pause";
    public const string AnimResume = "anim.resume";
    public const string EventCard = "event.card";
    public const string EventCardClear = "event.card.clear";
    public const string StateMedia = "state.media";
    public const string StateStats = "state.stats";
    public const string StateVolume = "state.volume";
    public const string StateRecent = "state.recent";
    public const string ListClipboard = "list.clipboard";
    public const string ListShelf = "list.shelf";

    public static string Envelope(string type, object? payload)
    {
        return JsonSerializer.Serialize(new { v = 1, type, payload });
    }

    /// <summary>Resolves the island palette. The Island has its own colours; AppLock pages keep theme resources.</summary>
    public static IslandPalette ResolvePalette(IslandSettings settings, bool systemUsesLightTheme)
    {
        bool dark = settings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => !systemUsesLightTheme,
        };

        double alpha = Math.Clamp(settings.OpacityPercent, 40, 100) / 100d;

        return dark
            ? new IslandPalette(
                Fill: $"rgba(17, 17, 20, {alpha:0.###})",
                Text: "#FFFFFFFF",
                DimText: "rgba(255, 255, 255, 0.72)",
                FaintText: "rgba(255, 255, 255, 0.5)",
                IsDark: true)
            : new IslandPalette(
                Fill: $"rgba(250, 250, 252, {Math.Max(alpha, 0.92):0.###})",
                Text: "#FF16161A",
                DimText: "rgba(22, 22, 26, 0.68)",
                FaintText: "rgba(22, 22, 26, 0.48)",
                IsDark: false);
    }

    public static string SettingsSnapshotMessage(IslandSettings settings, IslandPalette palette, string appVersion)
    {
        return Envelope(SettingsSnapshot, new
        {
            enabled = settings.Enabled,
            position = settings.Position,
            shape = settings.Shape,
            theme = settings.Theme,
            dark = palette.IsDark,
            sizePercent = Math.Clamp(settings.SizePercent, 70, 150),
            opacityPercent = Math.Clamp(settings.OpacityPercent, 40, 100),
            hardwareAcceleration = settings.HardwareAcceleration,
            showClock = settings.ShowClock,
            showBattery = settings.ShowBattery,
            cards = new
            {
                volume = settings.ShowVolumeCard,
                capsLock = settings.ShowCapsLockCard,
                clipboard = settings.ShowClipboardCard,
                screenshot = settings.ShowScreenshotCard,
                stayMs = settings.CardStayMs,
            },
            hotkey = settings.Hotkey,
            fill = palette.Fill,
            text = palette.Text,
            dimText = palette.DimText,
            faintText = palette.FaintText,
            version = appVersion,
        });
    }

    public static string TimeMessage(DateTime now)
    {
        return Envelope(StateTime, new
        {
            hhmm = now.ToString("HH:mm"),
            seconds = now.Second,
        });
    }

    public static string BatteryMessage(int percent, bool charging, bool present)
    {
        return Envelope(StateBattery, new { percent, charging, present });
    }

    public static string HoverMessage(bool inside)
    {
        return Envelope(StateHover, new { inside });
    }

    /// <summary>A live card for the folded pill. <paramref name="animate"/> is false when an existing card is updated in place.</summary>
    public static string CardMessage(IslandCard card, bool animate)
    {
        return Envelope(EventCard, new
        {
            id = card.Kind,
            kind = card.Kind,
            icon = card.Icon,
            title = card.Title,
            detail = card.Detail,
            accent = card.Accent,
            value = card.Value,
            stayMs = card.StayMs,
            priority = (int)card.Priority,
            animate,
            actions = card.Actions?.Select(a => new { id = a.Id, label = a.Label, primary = a.Primary }).ToArray(),
        });
    }

    public static string CardClearMessage() => Envelope(EventCardClear, null);

    public static string MediaMessage(MediaSnapshot media)
    {
        return Envelope(StateMedia, new
        {
            hasSession = media.HasSession,
            playing = media.Playing,
            title = media.Title,
            artist = media.Artist,
            app = media.App,
            position = Math.Round(media.PositionSeconds, 2),
            duration = Math.Round(media.DurationSeconds, 2),
            art = media.ArtFile,
        });
    }

    public static string StatsMessage(HomeStats stats)
    {
        return Envelope(StateStats, new
        {
            cpu = Math.Round(stats.CpuPercent, 0),
            ram = Math.Round(stats.RamPercent, 0),
            down = Math.Round(stats.DownBytesPerSec, 0),
            up = Math.Round(stats.UpBytesPerSec, 0),
            volume = stats.Volume,
        });
    }

    public static string VolumeMessage(int percent, IReadOnlyList<VolumeDeviceInfo> devices)
    {
        return Envelope(StateVolume, new
        {
            percent,
            devices = devices.Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }).ToArray(),
        });
    }

    public static string RecentMessage(IReadOnlyList<RecentClip> items)
    {
        return Envelope(StateRecent, new
        {
            items = items
                .Select(item => new
                {
                    kind = item.Kind,
                    preview = item.Preview,
                    app = item.App,
                    age = AgeText(item.CapturedAt),
                })
                .ToArray(),
        });
    }

    /// <summary>The Clipboard tab: one row per history item (island.md 9.1).</summary>
    public static string ClipboardListMessage(IReadOnlyList<ClipboardRow> rows)
    {
        return Envelope(ListClipboard, new
        {
            items = rows
                .Select(row => new
                {
                    id = row.Id,
                    type = row.Type,
                    preview = row.Preview,
                    source = row.Source,
                    age = row.Age,
                    chars = row.Chars,
                    pinned = row.Pinned,
                    thumb = row.Thumb,
                })
                .ToArray(),
        });
    }

    /// <summary>The Shelf tab: the files the user parked there (island.md 9.2).</summary>
    public static string ShelfListMessage(IReadOnlyList<ShelfRow> rows)
    {
        return Envelope(ListShelf, new
        {
            items = rows
                .Select(row => new
                {
                    path = row.Path,
                    name = row.Name,
                    icon = row.Icon,
                    exists = row.Exists,
                })
                .ToArray(),
        });
    }

    private static string AgeText(DateTime capturedAt)
    {
        TimeSpan age = DateTime.Now - capturedAt;
        if (age.TotalSeconds < 45) return "now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} d";
    }
}
