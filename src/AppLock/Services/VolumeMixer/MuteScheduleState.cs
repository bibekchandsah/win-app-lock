using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AppLock.Services.VolumeMixer;

/// <summary>
/// Remembers, across restarts, which devices the auto-mute schedule muted and what each one was doing before.
/// Without it a restart during an active range sees an already-muted device, treats "muted" as the user's own
/// choice, and restores it to muted when the range ends — the device would stay muted forever.
/// The file is deliberately short-lived: anything older than <see cref="MaxAge"/> is stale and ignored, so an
/// old record can never unmute a device the user muted by hand much later.
/// </summary>
public sealed class MuteScheduleState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>A record older than this is discarded instead of being applied.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public DateTime SavedAt { get; set; }

    public Dictionary<string, bool> Speakers { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, bool> Microphones { get; set; } = new(StringComparer.Ordinal);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock",
        "mute-schedule-state.json");

    public bool IsEmpty => Speakers.Count == 0 && Microphones.Count == 0;

    /// <summary>True when the schedule currently holds at least one device.</summary>
    public static bool HasPending(string path, DateTime now) => Load(path, now) is { IsEmpty: false };

    /// <summary>
    /// Reads the record written by a previous run. Returns null when there is none, it cannot be read, or it is
    /// older than <see cref="MaxAge"/> (a stale record is deleted).
    /// </summary>
    public static MuteScheduleState? Load(string path, DateTime now)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var state = JsonSerializer.Deserialize<MuteScheduleState>(File.ReadAllText(path), JsonOptions);
            if (state == null) return null;

            if (now - state.SavedAt > MaxAge || state.SavedAt - now > TimeSpan.FromMinutes(5))
            {
                TryDelete(path);
                return null;
            }

            state.Speakers = Copy(state.Speakers);
            state.Microphones = Copy(state.Microphones);
            return state;
        }
        catch
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>Writes the record, or removes it when the schedule is no longer holding anything.</summary>
    public void Save(string path)
    {
        try
        {
            if (IsEmpty)
            {
                TryDelete(path);
                return;
            }

            if (SavedAt == default) SavedAt = DateTime.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write beside the target and move over it, so a crash mid-write cannot leave a half-written record.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Diagnostics only; failing to persist must not stop the muting itself.
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static Dictionary<string, bool> Copy(Dictionary<string, bool>? source)
    {
        var copy = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (source == null) return copy;

        foreach (var (id, muted) in source)
        {
            if (!string.IsNullOrEmpty(id)) copy[id] = muted;
        }

        return copy;
    }
}
