using System;
using System.Collections.Generic;
using System.IO;
using AppLock.Services.VolumeMixer;

namespace AppLock.Tests;

/// <summary>
/// The schedule's cross-restart record: a restart during a range must not turn the schedule's own mute into the
/// user's choice, otherwise devices stay muted after the range ends.
/// </summary>
public sealed class MuteScheduleStateTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "AppLockTests", Guid.NewGuid().ToString("N"), "mute-schedule-state.json");

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch { }
    }

    [Fact]
    public void Load_returns_null_when_no_record_exists()
    {
        Assert.Null(MuteScheduleState.Load(_path, DateTime.Now));
    }

    [Fact]
    public void Save_and_Load_round_trip_both_categories()
    {
        var saved = new MuteScheduleState
        {
            SavedAt = DateTime.Now,
            Speakers = new Dictionary<string, bool> { ["speaker-a"] = false },
            Microphones = new Dictionary<string, bool> { ["mic-a"] = true },
        };
        saved.Save(_path);

        var loaded = MuteScheduleState.Load(_path, DateTime.Now);

        Assert.NotNull(loaded);
        Assert.False(loaded!.Speakers["speaker-a"]);
        Assert.True(loaded.Microphones["mic-a"]);
    }

    [Fact]
    public void Saving_an_empty_record_removes_the_file_so_nothing_is_restored_later()
    {
        new MuteScheduleState { SavedAt = DateTime.Now, Microphones = new Dictionary<string, bool> { ["mic-a"] = false } }
            .Save(_path);
        Assert.True(File.Exists(_path));

        new MuteScheduleState { SavedAt = DateTime.Now }.Save(_path);

        Assert.False(File.Exists(_path));
        Assert.Null(MuteScheduleState.Load(_path, DateTime.Now));
    }

    [Fact]
    public void Record_older_than_the_maximum_age_is_ignored_and_removed()
    {
        new MuteScheduleState
        {
            SavedAt = DateTime.Now - MuteScheduleState.MaxAge - TimeSpan.FromMinutes(1),
            Microphones = new Dictionary<string, bool> { ["mic-a"] = false },
        }.Save(_path);

        Assert.Null(MuteScheduleState.Load(_path, DateTime.Now));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Record_from_a_clock_that_jumped_forward_is_still_used()
    {
        new MuteScheduleState
        {
            SavedAt = DateTime.Now + TimeSpan.FromMinutes(2),
            Microphones = new Dictionary<string, bool> { ["mic-a"] = false },
        }.Save(_path);

        Assert.NotNull(MuteScheduleState.Load(_path, DateTime.Now));
    }

    [Fact]
    public void Corrupt_record_is_discarded_instead_of_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not json");

        Assert.Null(MuteScheduleState.Load(_path, DateTime.Now));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Empty_ids_are_dropped_when_reading_a_record()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path,
            "{\"SavedAt\":\"" + DateTime.Now.ToString("O") +
            "\",\"Speakers\":{\"\":true,\"speaker-a\":true},\"Microphones\":{}}");

        var loaded = MuteScheduleState.Load(_path, DateTime.Now);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Speakers);
        Assert.True(loaded.Speakers.ContainsKey("speaker-a"));
    }

    [Fact]
    public void HasPending_is_false_for_an_empty_record()
    {
        Assert.False(MuteScheduleState.HasPending(_path, DateTime.Now));

        new MuteScheduleState { SavedAt = DateTime.Now }.Save(_path);
        Assert.False(MuteScheduleState.HasPending(_path, DateTime.Now));

        new MuteScheduleState { SavedAt = DateTime.Now, Microphones = new Dictionary<string, bool> { ["mic-a"] = false } }
            .Save(_path);
        Assert.True(MuteScheduleState.HasPending(_path, DateTime.Now));
    }
}
