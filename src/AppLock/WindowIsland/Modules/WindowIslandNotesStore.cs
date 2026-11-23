using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppLock.WindowIsland.Modules;

public sealed class IslandNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Note 1";
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists notes encrypted with Windows DPAPI in notes.dat (island.md Section 9.5).
/// Supports multiple notes, auto-save, copy, delete.
/// </summary>
public sealed class WindowIslandNotesStore
{
    private readonly string _filePath;
    private readonly List<IslandNote> _notes = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public WindowIslandNotesStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "notes.dat");

        Load();

        lock (_lock)
        {
            if (_notes.Count == 0)
            {
                _notes.Add(new IslandNote { Title = "Note 1", Content = string.Empty });
                Save();
            }
        }
    }

    public IReadOnlyList<IslandNote> Notes
    {
        get
        {
            lock (_lock) return _notes.ToList();
        }
    }

    public IslandNote CreateNote(string? title = null)
    {
        IslandNote note;
        lock (_lock)
        {
            int nextNum = _notes.Count + 1;
            note = new IslandNote
            {
                Title = string.IsNullOrWhiteSpace(title) ? $"Note {nextNum}" : title.Trim(),
                Content = string.Empty
            };
            _notes.Add(note);
        }

        Save();
        Changed?.Invoke();
        return note;
    }

    public void UpdateNote(string id, string content, string? title = null)
    {
        bool changed = false;
        lock (_lock)
        {
            var note = _notes.FirstOrDefault(n => n.Id == id);
            if (note != null)
            {
                if (note.Content != content)
                {
                    note.Content = content;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(title) && note.Title != title.Trim())
                {
                    note.Title = title.Trim();
                    changed = true;
                }
                if (changed) note.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        // An unchanged save must not re-encrypt the file or raise Changed; doing so fed the
        // editor's reload/save feedback loop.
        if (!changed) return;

        Save();
        Changed?.Invoke();
    }

    public void DeleteNote(string id)
    {
        lock (_lock)
        {
            _notes.RemoveAll(n => n.Id == id);
            if (_notes.Count == 0)
            {
                _notes.Add(new IslandNote { Title = "Note 1", Content = string.Empty });
            }
        }

        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            byte[] encrypted = File.ReadAllBytes(_filePath);
            if (encrypted.Length == 0) return;

            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);
            var loaded = JsonSerializer.Deserialize<List<IslandNote>>(json);
            if (loaded != null)
            {
                lock (_lock)
                {
                    _notes.Clear();
                    _notes.AddRange(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandNotesStore] Load error: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            List<IslandNote> copy;
            lock (_lock)
            {
                copy = _notes.ToList();
            }
            string json = JsonSerializer.Serialize(copy);
            byte[] raw = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, encrypted);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandNotesStore] Save error: {ex.Message}");
        }
    }
}
