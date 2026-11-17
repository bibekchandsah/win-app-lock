using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AppLock.Models.PcChecker;
using Microsoft.UI.Xaml;

namespace AppLock.ViewModels.PcChecker;

/// <summary>One rendered label/value row. Masking of identifiers lives here so the UI stays dumb.</summary>
public sealed class InfoItemRow : INotifyPropertyChanged
{
    private bool _revealed;

    private string _value = string.Empty;
    private InfoStatus _status;

    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    /// <summary>The text shown for this value (already formatted, or a "Not available" reason).</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(DisplayValue));
        }
    }

    public string? Explain { get; init; }
    public string? Source { get; init; }
    public string? Badge { get; init; }

    public InfoStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(OkVisibility));
            OnPropertyChanged(nameof(InfoVisibility));
            OnPropertyChanged(nameof(WarningVisibility));
            OnPropertyChanged(nameof(ProblemVisibility));
        }
    }

    /// <summary>Updates a live value in place without rebuilding the whole list.</summary>
    public void Update(string value, InfoStatus status)
    {
        Value = value;
        Status = status;
    }

    /// <summary>True when "Hide identifiers" is on and this value is an identifier.</summary>
    public bool Masked { get; init; }

    public bool IsRevealed
    {
        get => _revealed;
        set
        {
            if (_revealed == value) return;
            _revealed = value;
            OnPropertyChanged(nameof(DisplayValue));
            OnPropertyChanged(nameof(EyeToolTip));
        }
    }

    public string DisplayValue => Masked && !_revealed ? "••••••••" : Value;

    public Visibility RevealVisibility => Masked ? Visibility.Visible : Visibility.Collapsed;

    public string EyeToolTip => _revealed ? "Hide this value" : "Reveal this value";

    public Visibility ExplainVisibility => string.IsNullOrWhiteSpace(Explain) ? Visibility.Collapsed : Visibility.Visible;

    public string ExplainText => Explain ?? string.Empty;

    public Visibility SourceVisibility => string.IsNullOrWhiteSpace(Source) ? Visibility.Collapsed : Visibility.Visible;

    public string SourceText => Source ?? string.Empty;

    public Visibility BadgeVisibility => string.IsNullOrWhiteSpace(Badge) ? Visibility.Collapsed : Visibility.Visible;

    public string BadgeText => Badge ?? string.Empty;

    public Visibility OkVisibility => Status == InfoStatus.Ok ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InfoVisibility => Status == InfoStatus.Info ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningVisibility => Status == InfoStatus.Warning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProblemVisibility => Status == InfoStatus.Problem ? Visibility.Visible : Visibility.Collapsed;

    public bool HasStatus => Status != InfoStatus.None;

    public string StatusText => Status switch
    {
        InfoStatus.Ok => "OK",
        InfoStatus.Info => "Information",
        InfoStatus.Warning => "Warning",
        InfoStatus.Problem => "Problem",
        _ => string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One tile on the Overview dashboard; clicking it jumps to the category.</summary>
public sealed class OverviewTile
{
    public string CategoryId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Glyph { get; init; } = "\uE946";
    public string Primary { get; init; } = "Not available";
    public string? Secondary { get; init; } = string.Empty;
    public Visibility SecondaryVisibility => string.IsNullOrWhiteSpace(Secondary) ? Visibility.Collapsed : Visibility.Visible;
    public string AutomationName => $"{Title}: {Primary}. {Secondary}".Trim();
}

/// <summary>Shell state for <see cref="Views.PcCheckerPage"/>.</summary>
public sealed class PcCheckerViewModel : INotifyPropertyChanged
{
    private string _statusText = string.Empty;
    private string _placeholderText = string.Empty;
    private bool _isBusy;
    private PcCategory? _selectedCategory;
    private string _searchText = string.Empty;
    private bool _hideIdentifiers;
    private bool _showAdminHint;

    public ObservableCollection<PcCategory> Categories { get; } = new();

    public ObservableCollection<OverviewTile> Tiles { get; } = new();

    public ObservableCollection<InfoItemRow> Rows { get; } = new();

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            if (!Set(ref _placeholderText, value)) return;
            OnPropertyChanged(nameof(PlaceholderVisibility));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public PcCategory? SelectedCategory
    {
        get => _selectedCategory;
        set => Set(ref _selectedCategory, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value);
    }

    public bool HideIdentifiers
    {
        get => _hideIdentifiers;
        set => Set(ref _hideIdentifiers, value);
    }

    /// <summary>Shown when the current category has values that need administrator rights.</summary>
    public bool ShowAdminHint
    {
        get => _showAdminHint;
        set
        {
            if (!Set(ref _showAdminHint, value)) return;
            OnPropertyChanged(nameof(AdminHintVisibility));
        }
    }

    public Visibility AdminHintVisibility
        => _showAdminHint ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlaceholderVisibility
        => string.IsNullOrEmpty(_placeholderText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TilesVisibility
        => Tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RowsVisibility
        => Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void RefreshVisibility()
    {
        OnPropertyChanged(nameof(PlaceholderVisibility));
        OnPropertyChanged(nameof(TilesVisibility));
        OnPropertyChanged(nameof(RowsVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
