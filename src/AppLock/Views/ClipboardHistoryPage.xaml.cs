using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using AppLock.Services;
using AppLock.ViewModels;

namespace AppLock.Views;

public sealed partial class ClipboardHistoryPage : Page
{
    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();

    private string _currentSearch = string.Empty;
    private string _currentFilter = "All";

    public ClipboardHistoryPage()
    {
        this.InitializeComponent();
        this.Loaded += ClipboardHistoryPage_Loaded;
        this.Unloaded += ClipboardHistoryPage_Unloaded;
    }

    private void ClipboardHistoryPage_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged += OnClipboardDataChanged;
        _ = LoadItemsAsync();
    }

    private void ClipboardHistoryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged -= OnClipboardDataChanged;
    }

    private void OnClipboardDataChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = LoadItemsAsync();
        });
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(OthersPage));
        }
    }

    private async Task LoadItemsAsync()
    {
        try
        {
            var rawItems = await ServiceContainer.ClipboardStore.GetItemsAsync(
                limit: 200,
                filter: _currentFilter,
                search: _currentSearch);

            Items.Clear();
            foreach (var item in rawItems)
            {
                // In Milestone 2, filter "Links" manually if filter is Links
                var vm = new ClipboardItemViewModel(item);
                if (_currentFilter == "Links" && !vm.IsLink)
                {
                    continue;
                }
                Items.Add(vm);
            }

            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            int limit = ServiceContainer.Settings.Clipboard.MaxItems;
            StatsSubtext.Text = $"Showing {Items.Count} items • Stored: {count} / {limit}";

            if (Items.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                if (!string.IsNullOrWhiteSpace(_currentSearch))
                {
                    EmptyStateTitle.Text = "No matching items";
                    EmptyStateMessage.Text = $"No clipboard items match '{_currentSearch}'.";
                }
                else
                {
                    EmptyStateTitle.Text = "No clipboard history yet";
                    EmptyStateMessage.Text = "Copy text or data in any application (e.g. Notepad, browser, Word) to see it appear here.";
                }
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardHistoryPage] Error loading items: {ex.Message}");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentSearch = SearchBox.Text.Trim();
        _ = LoadItemsAsync();
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterComboBox.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Tag?.ToString() ?? "All";
            _ = LoadItemsAsync();
        }
    }

    private void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            string? text = vm.Model.ContentString ?? vm.PreviewText;
            if (!string.IsNullOrEmpty(text))
            {
                ServiceContainer.ClipboardMonitor.IgnoreNext();
                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);

                // Show notification flyout
                var flyout = new Flyout
                {
                    Content = new TextBlock { Text = "Copied to clipboard!" }
                };
                flyout.ShowAt(btn);
            }
        }
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            await ServiceContainer.ClipboardStore.DeleteItemAsync(vm.Id);
            _ = LoadItemsAsync();
        }
    }

    private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear All Clipboard History",
            Content = "Are you sure you want to clear clipboard history? Pinned items will be preserved.",
            PrimaryButtonText = "Clear History",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ServiceContainer.ClipboardStore.ClearHistoryAsync(includePinned: false);
            _ = LoadItemsAsync();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ClipboardSettingsPage));
    }
}

