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
        var store = new WindowIslandNotesStore();
        var customNote = store.CreateNote("Project Alpha");

        Assert.NotNull(customNote);
        Assert.Equal("Project Alpha", customNote.Title);
        Assert.Contains(store.Notes, n => n.Id == customNote.Id && n.Title == "Project Alpha");

        store.DeleteNote(customNote.Id);
    }

    [Fact]
    public void NotesStore_DeleteNote_RemovesNoteAndRecreatesDefaultIfEmpty()
    {
        var store = new WindowIslandNotesStore();
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

    [Fact]
    public void ReminderStore_Delete_RemovesReminder()
    {
        using var store = new WindowIslandReminderStore();
        var rem = store.AddFromPhrase("Check furnace in 20 minutes");
        Assert.NotNull(rem);

        Assert.Contains(store.ActiveReminders, r => r.Id == rem.Id);

        store.Delete(rem.Id);

        Assert.DoesNotContain(store.ActiveReminders, r => r.Id == rem.Id);
    }
}
