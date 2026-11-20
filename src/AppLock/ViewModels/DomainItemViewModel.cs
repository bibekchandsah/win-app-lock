using System;
using AppLock.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.ViewModels;

/// <summary>
/// Backs one item in the Always Block or Always Allow list.
/// </summary>
public sealed partial class DomainItemViewModel : ObservableObject
{
    private readonly DomainEntry _entry;
    private readonly Action<DomainItemViewModel> _onDelete;
    private readonly Action _onSave;

    public DomainItemViewModel(DomainEntry entry, Action<DomainItemViewModel> onDelete, Action onSave)
    {
        _entry = entry;
        _onDelete = onDelete;
        _onSave = onSave;
    }

    public DomainEntry Entry => _entry;

    public string Domain => _entry.Domain;

    public string Note
    {
        get => _entry.Note;
        set
        {
            if (_entry.Note == value) return;
            _entry.Note = value ?? string.Empty;
            _onSave();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_entry.Note);

    public DateTime AddedUtc => _entry.AddedUtc;

    public string AddedDateFormatted => _entry.AddedUtc.ToLocalTime().ToString("MMM d, yyyy");

    public void RequestDelete() => _onDelete(this);
}

/// <summary>
/// Backs one item in the Temporary Allow list.
/// </summary>
public sealed class TempAllowItemViewModel
{
    public TempAllow Entry { get; }

    public string Domain => Entry.Domain;

    public string ExpiresFormatted => $"Expires at {Entry.ExpiresUtc.ToLocalTime():HH:mm} ({FormatRemaining()})";

    public TempAllowItemViewModel(TempAllow entry)
    {
        Entry = entry;
    }

    private string FormatRemaining()
    {
        var diff = Entry.ExpiresUtc - DateTime.UtcNow;
        if (diff.TotalMinutes < 1) return "< 1 min left";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m left";
        return $"{(int)diff.TotalHours}h {diff.Minutes}m left";
    }
}
