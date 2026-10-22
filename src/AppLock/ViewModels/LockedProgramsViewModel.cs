using System.Collections.ObjectModel;
using AppLock.Models;
using AppLock.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppLock.ViewModels;

public partial class LockedProgramsViewModel : ObservableObject
{
    private readonly LockManager _lockManager;

    [ObservableProperty]
    private ObservableCollection<LockRule> _lockedRules = new();

    [ObservableProperty]
    private ObservableCollection<LockRule> _filteredRules = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _lockedCount;

    public LockedProgramsViewModel(LockManager lockManager)
    {
        _lockManager = lockManager;
        _lockManager.RuleAdded += OnRuleAdded;
        _lockManager.RuleRemoved += OnRuleRemoved;
        
        LoadRules();
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterRules();
    }

    private void LoadRules()
    {
        LockedRules.Clear();
        foreach (var rule in _lockManager.GetAllRules())
        {
            if (rule.Icon == null)
            {
                rule.Icon = ServiceContainer.WindowEnumerator.GetIcon(rule.ProcessPath, rule.ProcessName);
            }
            LockedRules.Add(rule);
        }
        FilterRules();
        UpdateCount();
    }

    private void OnRuleAdded(object? sender, LockRule rule)
    {
        if (rule.Icon == null)
        {
            rule.Icon = ServiceContainer.WindowEnumerator.GetIcon(rule.ProcessPath, rule.ProcessName);
        }
        LockedRules.Add(rule);
        FilterRules();
        UpdateCount();
    }

    private void OnRuleRemoved(object? sender, Guid ruleId)
    {
        var rule = LockedRules.FirstOrDefault(r => r.Id == ruleId);
        if (rule != null)
        {
            LockedRules.Remove(rule);
            FilterRules();
            UpdateCount();
        }
    }

    private void FilterRules()
    {
        FilteredRules.Clear();

        var query = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        var filtered = string.IsNullOrEmpty(query)
            ? LockedRules
            : LockedRules.Where(r =>
                r.DisplayName.ToLowerInvariant().Contains(query) ||
                r.ProcessName.ToLowerInvariant().Contains(query));

        foreach (var rule in filtered)
        {
            FilteredRules.Add(rule);
        }
    }

    private void UpdateCount()
    {
        LockedCount = LockedRules.Count;
    }

    [RelayCommand]
    private void UnlockProgram(LockRule rule)
    {
        _lockManager.RemoveRule(rule.Id);
    }

    [RelayCommand]
    private void RefreshLocked()
    {
        LoadRules();
    }
}
