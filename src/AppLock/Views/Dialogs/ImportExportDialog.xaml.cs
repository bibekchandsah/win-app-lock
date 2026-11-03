using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AppLock.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.Views.Dialogs;

public sealed partial class ImportExportDialog : ContentDialog
{
    public ImportDialogViewModel ViewModel { get; }

    public ImportExportDialog(XamlRoot xamlRoot, List<NetworkRuleEntry> newRules, List<NetworkRuleEntry> conflicts)
    {
        this.InitializeComponent();
        XamlRoot = xamlRoot;
        RequestedTheme = ((FrameworkElement)xamlRoot.Content).ActualTheme;
        ViewModel = new ImportDialogViewModel(newRules, conflicts);
        RulesList.ItemsSource = ViewModel.AllRules;
        
        // Select all new rules by default
        foreach (var rule in newRules)
        {
            RulesList.SelectedItems.Add(rule);
        }
    }

    public List<NetworkRuleEntry> SelectedRules => 
        RulesList.SelectedItems.Cast<NetworkRuleEntry>().ToList();

    public int NewCount => ViewModel.NewCount;
    public int ConflictCount => ViewModel.ConflictCount;
    public int SelectedCount => ViewModel.SelectedCount;
    public bool HasSelection => ViewModel.HasSelection;

    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedCount = RulesList.SelectedItems.Count;
    }
}

public partial class ImportDialogViewModel : ObservableObject
{
    private readonly List<NetworkRuleEntry> _newRules;
    private readonly List<NetworkRuleEntry> _conflicts;

    [ObservableProperty]
    private int _selectedCount;

    public List<NetworkRuleEntry> AllRules { get; }
    public int NewCount => _newRules.Count;
    public int ConflictCount => _conflicts.Count;
    public bool HasSelection => SelectedCount > 0;

    public ImportDialogViewModel(List<NetworkRuleEntry> newRules, List<NetworkRuleEntry> conflicts)
    {
        _newRules = newRules;
        _conflicts = conflicts;

        // Mark status for UI
        foreach (var rule in newRules)
        {
            rule.ImportStatus = "New";
        }

        foreach (var rule in conflicts)
        {
            rule.ImportStatus = "Conflict";
        }

        AllRules = newRules.Concat(conflicts).ToList();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
    }
}
