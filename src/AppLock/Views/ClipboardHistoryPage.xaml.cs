using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
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
        ServiceContainer.ClipboardEnabledChanged += OnClipboardEnabledChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged += OnClipboardPauseStateChanged;

        UpdateStatusBanner();
        _ = LoadItemsAsync();
    }

    private void ClipboardHistoryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged -= OnClipboardDataChanged;
        ServiceContainer.ClipboardEnabledChanged -= OnClipboardEnabledChanged;
        ServiceContainer.ClipboardMonitor.PauseStateChanged -= OnClipboardPauseStateChanged;
    }

    private void OnClipboardEnabledChanged(object? sender, bool enabled)
    {
        DispatcherQueue.TryEnqueue(UpdateStatusBanner);
    }

    private void OnClipboardPauseStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateStatusBanner);
    }

    private void UpdateStatusBanner()
    {
        if (StatusBanner == null) return;

        bool enabled = ServiceContainer.Settings.Clipboard.Enabled;
        bool isPaused = ServiceContainer.ClipboardMonitor.IsPaused;

        if (!enabled)
        {
            StatusBanner.Visibility = Visibility.Visible;
            StatusBannerText.Text = "Clipboard capture is disabled";
            StatusBannerIcon.Glyph = "\uE711";
            StatusBannerActionButton.Content = "Enable Clipboard";
            StatusBanner.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"];
            StatusBanner.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            StatusBannerText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            StatusBannerIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
        else if (isPaused)
        {
            StatusBanner.Visibility = Visibility.Visible;
            var until = ServiceContainer.ClipboardMonitor.PausedUntil;
            if (until.HasValue && until.Value < DateTime.Now.AddYears(1))
            {
                var rem = until.Value - DateTime.Now;
                string remText = rem.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(rem.TotalMinutes)}m" : "<1m";
                StatusBannerText.Text = $"Clipboard capture is paused for ~{remText} (until {until.Value:t})";
            }
            else
            {
                StatusBannerText.Text = "Clipboard capture is paused until resumed";
            }
            StatusBannerIcon.Glyph = "\uE769";
            StatusBannerActionButton.Content = "Resume Capture";
            StatusBanner.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"];
            StatusBanner.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            StatusBannerText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            StatusBannerIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        else
        {
            StatusBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void StatusBannerActionButton_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ServiceContainer.Settings.Clipboard.Enabled;
        if (!enabled)
        {
            ServiceContainer.SetClipboardEnabled(true);
        }
        else if (ServiceContainer.ClipboardMonitor.IsPaused)
        {
            ServiceContainer.ClipboardMonitor.Resume();
        }
        UpdateStatusBanner();
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
                    EmptyStateMessage.Text = "Copy text, images, or files in any application to see them appear here.";
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

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            try
            {
                if (vm.IsImage)
                {
                    if (!string.IsNullOrEmpty(vm.ImagePath) && File.Exists(vm.ImagePath))
                    {
                        ServiceContainer.ClipboardMonitor.IgnoreNext();
                        var storageFile = await StorageFile.GetFileFromPathAsync(vm.ImagePath);
                        var dp = new DataPackage();
                        dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(storageFile));
                        Clipboard.SetContent(dp);
                        ShowCopyFlyout(btn, "Image copied to clipboard!");
                    }
                    else
                    {
                        ShowCopyFlyout(btn, "Image file not found on disk.");
                    }
                }
                else if (vm.IsFiles)
                {
                    if (vm.FilePaths.Count > 0)
                    {
                        ServiceContainer.ClipboardMonitor.IgnoreNext();
                        var storageItems = new List<IStorageItem>();
                        foreach (var path in vm.FilePaths)
                        {
                            if (File.Exists(path))
                            {
                                storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                            }
                            else if (Directory.Exists(path))
                            {
                                storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                            }
                        }

                        if (storageItems.Count > 0)
                        {
                            var dp = new DataPackage();
                            dp.SetStorageItems(storageItems);
                            Clipboard.SetContent(dp);
                            ShowCopyFlyout(btn, $"{storageItems.Count} file(s) copied!");
                        }
                    }
                }
                else
                {
                    string? text = vm.Model.ContentString ?? vm.PreviewText;
                    if (!string.IsNullOrEmpty(text))
                    {
                        ServiceContainer.ClipboardMonitor.IgnoreNext();
                        var dp = new DataPackage();
                        dp.SetText(text);
                        Clipboard.SetContent(dp);
                        ShowCopyFlyout(btn, "Text copied to clipboard!");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardHistoryPage] Error copying item: {ex.Message}");
                ShowCopyFlyout(btn, "Copy failed.");
            }
        }
    }

    private static void ShowCopyFlyout(FrameworkElement target, string message)
    {
        var flyout = new Flyout
        {
            Content = new TextBlock { Text = message }
        };
        flyout.ShowAt(target);
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
            Content = "Are you sure you want to clear clipboard history? Pinned items will be preserved, and associated images on disk will be deleted.",
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
