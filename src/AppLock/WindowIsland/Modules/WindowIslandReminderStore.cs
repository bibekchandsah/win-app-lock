using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace AppLock.WindowIsland.Modules;

public sealed class IslandReminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateTime DueTimeUtc { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists reminders in reminders.json, monitors due times, handles snooze and completion.
/// </summary>
public sealed class WindowIslandReminderStore : IDisposable
{
    private readonly string _filePath;
    private readonly List<IslandReminder> _reminders = new();
    private readonly HashSet<string> _triggeredIds = new();
    private readonly Timer _checkTimer;
    private readonly object _lock = new();

    public event Action<IslandReminder>? ReminderTriggered;
    public event Action? Changed;

    public WindowIslandReminderStore(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "reminders.json");
        }
        else
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            _filePath = filePath;
        }

        Load();

        // Check reminders every 1 second
        _checkTimer = new Timer(CheckDueReminders, null, 1000, 1000);

        // Fire missed reminders on startup (within last 3 days)
        CheckMissedReminders();
    }

    public IReadOnlyList<IslandReminder> ActiveReminders
    {
        get
        {
            lock (_lock)
            {
                return _reminders
                    .Where(r => !r.IsCompleted)
                    .OrderBy(r => r.DueTimeUtc)
                    .ToList();
            }
        }
    }

    public IslandReminder Add(string title, TimeSpan duration)
    {
        var reminder = new IslandReminder
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Reminder" : title.Trim(),
            DueTimeUtc = DateTime.UtcNow.Add(duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(10))
        };
        lock (_lock)
        {
            _reminders.Add(reminder);
        }
        Save();
        Changed?.Invoke();
        return reminder;
    }

    public IslandReminder Add(string title, DateTime dueTimeUtc)
    {
        var reminder = new IslandReminder
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Reminder" : title.Trim(),
            DueTimeUtc = dueTimeUtc > DateTime.UtcNow ? dueTimeUtc : DateTime.UtcNow.AddMinutes(1)
        };
        lock (_lock)
        {
            _reminders.Add(reminder);
        }
        Save();
        Changed?.Invoke();
        return reminder;
    }

    public IslandReminder? AddFromPhrase(string phrase)
    {
        var result = WindowIslandReminderParser.Parse(phrase);
        if (!result.Success) return null;

        var reminder = new IslandReminder
        {
            Title = result.Title,
            DueTimeUtc = result.DueTimeUtc
        };

        lock (_lock)
        {
            _reminders.Add(reminder);
        }

        Save();
        Changed?.Invoke();
        return reminder;
    }

    public void Complete(string id)
    {
        lock (_lock)
        {
            var item = _reminders.FirstOrDefault(r => r.Id == id);
            if (item != null)
            {
                item.IsCompleted = true;
            }
            _triggeredIds.Remove(id);
        }
        Save();
        Changed?.Invoke();
    }

    public void Snooze(string id, int minutes = 10)
    {
        lock (_lock)
        {
            var item = _reminders.FirstOrDefault(r => r.Id == id);
            if (item != null)
            {
                item.IsCompleted = false;
                item.DueTimeUtc = DateTime.UtcNow.AddMinutes(minutes);
            }
            _triggeredIds.Remove(id);
        }
        Save();
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _reminders.RemoveAll(r => r.Id == id);
            _triggeredIds.Remove(id);
        }
        Save();
        Changed?.Invoke();
    }

    public static string FormatRelativeTime(DateTime dueTimeUtc)
    {
        var diff = dueTimeUtc - DateTime.UtcNow;
        if (diff.TotalSeconds <= 0) return "due now";
        if (diff.TotalSeconds < 60) return $"in {Math.Max(1, (int)Math.Ceiling(diff.TotalSeconds))}s";
        if (diff.TotalMinutes < 60) return $"in {Math.Max(1, (int)Math.Ceiling(diff.TotalMinutes))}m";
        if (diff.TotalHours < 24) return $"in {Math.Max(1, (int)Math.Ceiling(diff.TotalHours))}h";
        return $"in {Math.Max(1, (int)Math.Ceiling(diff.TotalDays))}d";
    }

    private void CheckDueReminders(object? state)
    {
        List<IslandReminder> dueItems = new();
        DateTime now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var r in _reminders)
            {
                if (!r.IsCompleted && r.DueTimeUtc <= now && _triggeredIds.Add(r.Id))
                {
                    dueItems.Add(r);
                }
            }
        }

        foreach (var due in dueItems)
        {
            ReminderTriggered?.Invoke(due);
        }
    }

    private void CheckMissedReminders()
    {
        DateTime now = DateTime.UtcNow;
        DateTime cutoff = now.AddDays(-3);
        List<IslandReminder> missed = new();

        lock (_lock)
        {
            foreach (var r in _reminders)
            {
                if (!r.IsCompleted && r.DueTimeUtc <= now && r.DueTimeUtc >= cutoff && _triggeredIds.Add(r.Id))
                {
                    missed.Add(r);
                }
            }
        }

        foreach (var m in missed)
        {
            ReminderTriggered?.Invoke(m);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            string json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<IslandReminder>>(json);
            if (loaded != null)
            {
                lock (_lock)
                {
                    _reminders.Clear();
                    _reminders.AddRange(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandReminderStore] Load error: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            List<IslandReminder> copy;
            lock (_lock)
            {
                copy = _reminders.ToList();
            }
            File.WriteAllText(_filePath, JsonSerializer.Serialize(copy));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandReminderStore] Save error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _checkTimer.Dispose();
    }
}
