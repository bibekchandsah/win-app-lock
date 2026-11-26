using System;
using System.IO;
using System.Linq;
using AppLock.WindowIsland.Modules;
using Xunit;

namespace AppLock.Tests;

public class WindowIslandNotesAndTimerTests : IDisposable
{
    private readonly string _testDir;

    public WindowIslandNotesAndTimerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AppLockNotesTimerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Theory]
    [InlineData("45m", 45 * 60)]
    [InlineData("45 min", 45 * 60)]
    [InlineData("45 mins", 45 * 60)]
    [InlineData("1h 30m", 90 * 60)]
    [InlineData("1.5h", 90 * 60)]
    [InlineData("90s", 90)]
    [InlineData("15:00", 15 * 60)]
    [InlineData("01:30:00", 90 * 60)]
    [InlineData("25", 25 * 60)]
    public void DurationParser_ValidInputs_ReturnExpectedSeconds(string input, double expectedSeconds)
    {
        bool success = WindowIslandDurationParser.TryParse(input, out TimeSpan duration);

        Assert.True(success);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-5m")]
    [InlineData("0")]
    public void DurationParser_InvalidInputs_ReturnFalse(string? input)
    {
        bool success = WindowIslandDurationParser.TryParse(input, out TimeSpan duration);

        Assert.False(success);
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void NotesStore_CreateWithCustomTitle_PersistsTitle()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"applock_test_notes_{Guid.NewGuid():N}.dat");
        try
        {
            var store = new WindowIslandNotesStore(tempPath);
            var customNote = store.CreateNote("Project Alpha");

            Assert.NotNull(customNote);
            Assert.Equal("Project Alpha", customNote.Title);
            Assert.Contains(store.Notes, n => n.Id == customNote.Id && n.Title == "Project Alpha");

            store.DeleteNote(customNote.Id);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void NotesStore_DeleteNote_RemovesNoteAndRecreatesDefaultIfEmpty()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"applock_test_notes_{Guid.NewGuid():N}.dat");
        try
        {
            var store = new WindowIslandNotesStore(tempPath);
            var initialNotes = store.Notes.ToList();

            // Delete all existing notes
            foreach (var n in initialNotes)
            {
                store.DeleteNote(n.Id);
            }

            // When all notes deleted, store must ensure at least 1 note exists
            var notes = store.Notes;
            Assert.NotEmpty(notes);
            Assert.Equal("Note 1", notes[0].Title);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void ReminderStore_Add_WithIndividualDuration()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"applock_test_rem_{Guid.NewGuid():N}.json");
        try
        {
            using var store = new WindowIslandReminderStore(tempPath);
            var rem = store.Add("Team Standup", TimeSpan.FromMinutes(7));

            Assert.NotNull(rem);
            Assert.Equal("Team Standup", rem.Title);
            Assert.Contains(store.ActiveReminders, r => r.Id == rem.Id);

            string relTime = WindowIslandReminderStore.FormatRelativeTime(rem.DueTimeUtc);
            Assert.Equal("in 7m", relTime);

            store.Delete(rem.Id);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Theory]
    [InlineData(7.0, "in 7m")]
    [InlineData(6.95, "in 7m")]
    [InlineData(10.0, "in 10m")]
    [InlineData(9.95, "in 10m")]
    [InlineData(5.0, "in 5m")]
    [InlineData(0.5, "in 30s")]
    public void ReminderStore_FormatRelativeTime_RoundsProperly(double minutesFromNow, string expected)
    {
        DateTime due = DateTime.UtcNow.AddMinutes(minutesFromNow);
        string formatted = WindowIslandReminderStore.FormatRelativeTime(due);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void ReminderStore_Delete_RemovesReminder()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"applock_test_rem_{Guid.NewGuid():N}.json");
        try
        {
            using var store = new WindowIslandReminderStore(tempPath);
            var rem = store.AddFromPhrase("Check furnace in 20 minutes");
            Assert.NotNull(rem);

            Assert.Contains(store.ActiveReminders, r => r.Id == rem.Id);

            store.Delete(rem.Id);

            Assert.DoesNotContain(store.ActiveReminders, r => r.Id == rem.Id);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void ReminderParser_ParsesRelativeForMinutes()
    {
        var result = WindowIslandReminderParser.Parse("watch youtube for 5min");
        Assert.True(result.Success);
        Assert.True(result.HasExplicitTime);
        Assert.Equal("watch youtube", result.Title);
        Assert.True(result.DueTimeUtc > DateTime.UtcNow);
        Assert.True(result.DueTimeUtc <= DateTime.UtcNow.AddMinutes(6));

        string pill = WindowIslandReminderParser.FormatPill(result.DueTimeUtc);
        Assert.Equal("in 5 min", pill);
    }

    [Fact]
    public void ReminderParser_ParsesTomorrow()
    {
        var result = WindowIslandReminderParser.Parse("call mum tomorrow at 9");
        Assert.True(result.Success);
        Assert.True(result.HasExplicitTime);
        Assert.Equal("call mum", result.Title);
        Assert.True(result.DueTimeUtc > DateTime.UtcNow);

        string pill = WindowIslandReminderParser.FormatPill(result.DueTimeUtc);
        Assert.Contains("tomorrow", pill, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReminderStore_AddWithDateTime()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"applock_test_rem_{Guid.NewGuid():N}.json");
        try
        {
            using var store = new WindowIslandReminderStore(tempPath);
            DateTime target = DateTime.UtcNow.AddMinutes(25);
            var rem = store.Add("Oven timer", target);

            Assert.NotNull(rem);
            Assert.Equal("Oven timer", rem.Title);
            Assert.Contains(store.ActiveReminders, r => r.Id == rem.Id);

            store.Delete(rem.Id);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void ReminderParser_ParsesMonthAndDay()
    {
        var result = WindowIslandReminderParser.Parse("call mom on November 19 at 4pm");
        Assert.True(result.Success);
        Assert.True(result.HasExplicitTime);
        Assert.Equal("call mom", result.Title);
        var local = result.DueTimeUtc.ToLocalTime();
        Assert.Equal(11, local.Month);
        Assert.Equal(19, local.Day);
        Assert.Equal(16, local.Hour);
        Assert.Equal(0, local.Minute);
    }

    [Fact]
    public void ReminderParser_ParsesDayThenMonth()
    {
        var result = WindowIslandReminderParser.Parse("pay rent 3rd Dec at 10:30am");
        Assert.True(result.Success);
        Assert.Equal("pay rent", result.Title);
        var local = result.DueTimeUtc.ToLocalTime();
        Assert.Equal(12, local.Month);
        Assert.Equal(3, local.Day);
        Assert.Equal(10, local.Hour);
        Assert.Equal(30, local.Minute);
    }

    [Fact]
    public void ReminderStore_FormatRelativeTime_ShowsExactDateAfter24Hours()
    {
        DateTime dueLocal = DateTime.Now.AddDays(3).Date.AddHours(16);
        string formatted = WindowIslandReminderStore.FormatRelativeTime(dueLocal.ToUniversalTime());
        Assert.Equal(dueLocal.ToString("MMM d h:mm tt"), formatted);
    }
}
