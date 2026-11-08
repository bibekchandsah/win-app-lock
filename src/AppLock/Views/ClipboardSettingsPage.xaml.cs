using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Services;
using AppLock.ViewModels;

namespace AppLock.Views;

public sealed partial class ClipboardSettingsPage : Page
{
    private bool _isInitializing = true;
    private readonly ObservableCollection<string> _ignoredApps = new();

    public ClipboardSettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += ClipboardSettingsPage_Loaded;
        this.Unloaded += ClipboardSettingsPage_Unloaded;
    }

    private void ClipboardSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged += OnClipboardDataChanged;
        LoadSettings();
        _ = RefreshDataTabAsync();
        _ = RefreshSnippetsTabAsync();
        _ = UpdateDebugStatusAsync();
    }

    private void ClipboardSettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardStore.DataChanged -= OnClipboardDataChanged;
    }

    private void OnClipboardDataChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = RefreshDataTabAsync();
            _ = RefreshSnippetsTabAsync();
            _ = UpdateDebugStatusAsync();
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

    private void LoadSettings()
    {
        _isInitializing = true;
        try
        {
            var settings = ServiceContainer.Settings.Clipboard;

            // General tab
            EnableClipboardToggle.IsOn = settings.Enabled;

            // History limit combo
            string maxStr = settings.MaxItems.ToString();
            bool matchedPreset = false;
            foreach (ComboBoxItem item in HistoryLimitCombo.Items)
            {
                if (item.Tag?.ToString() == maxStr)
                {
                    HistoryLimitCombo.SelectedItem = item;
                    CustomLimitRow.Visibility = Visibility.Collapsed;
                    matchedPreset = true;
                    break;
                }
            }

            if (!matchedPreset)
            {
                // Select Custom
                foreach (ComboBoxItem item in HistoryLimitCombo.Items)
                {
                    if (item.Tag?.ToString() == "custom")
                    {
                        HistoryLimitCombo.SelectedItem = item;
                        CustomLimitRow.Visibility = Visibility.Visible;
                        CustomLimitNumberBox.Value = settings.MaxItems;
                        break;
                    }
                }
            }

            ClipboardHotkeyRecorder.Hotkey = settings.Hotkey;
            OverrideWinVCheckBox.IsChecked = settings.OverrideWinV;

            // Popup position
            foreach (ComboBoxItem item in PopupPositionCombo.Items)
            {
                if (item.Tag?.ToString() == settings.PopupPosition)
                {
                    PopupPositionCombo.SelectedItem = item;
                    break;
                }
            }
            if (PopupPositionCombo.SelectedItem == null && PopupPositionCombo.Items.Count > 0)
            {
                PopupPositionCombo.SelectedIndex = 0;
            }

            // Paste behavior
            string pasteTag = settings.AutoPaste ? "auto" : "copy";
            foreach (ComboBoxItem item in PasteBehaviorCombo.Items)
            {
                if (item.Tag?.ToString() == pasteTag)
                {
                    PasteBehaviorCombo.SelectedItem = item;
                    break;
                }
            }
            if (PasteBehaviorCombo.SelectedItem == null && PasteBehaviorCombo.Items.Count > 0)
            {
                PasteBehaviorCombo.SelectedIndex = 0;
            }

            StartWithWindowsToggle.IsOn = settings.StartWithWindows;

            // Theme
            foreach (ComboBoxItem item in ThemeCombo.Items)
            {
                if (item.Tag?.ToString() == settings.ThemeMode)
                {
                    ThemeCombo.SelectedItem = item;
                    break;
                }
            }
            if (ThemeCombo.SelectedItem == null && ThemeCombo.Items.Count > 0)
            {
                ThemeCombo.SelectedIndex = 0;
            }

            // Capture tab
            CaptureTextToggle.IsOn = settings.CaptureText;
            CaptureImagesToggle.IsOn = settings.CaptureImages;
            CaptureFilesToggle.IsOn = settings.CaptureFiles;
            KeepRichTextToggle.IsOn = settings.KeepRichText;
            MaxTextKbNumberBox.Value = settings.MaxTextKb;
            MaxImageMbNumberBox.Value = settings.MaxImageMb;
            StorageCapMbNumberBox.Value = settings.StorageCapMb;
            AutoDeleteDaysNumberBox.Value = settings.AutoDeleteDays;

            // Privacy tab
            EncryptHistoryToggle.IsOn = settings.EncryptHistory;
            RequireHelloToggle.IsOn = settings.RequireHello;
            DetectSensitiveToggle.IsOn = settings.DetectSensitive;
            SensitiveExpireSecondsNumberBox.Value = settings.SensitiveExpireSeconds;
            ExcludeScreenCaptureToggle.IsOn = settings.ExcludeFromScreenCapture;

            // Ignored apps list
            _ignoredApps.Clear();
            foreach (var app in settings.IgnoredApps)
            {
                _ignoredApps.Add(app);
            }
            IgnoredAppsListView.ItemsSource = _ignoredApps;

            // Data tab
            StoragePathText.Text = ServiceContainer.ClipboardStore.StoragePath;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;
        ServiceContainer.Settings.Clipboard.Clamp();
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    private void EnableClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ServiceContainer.Settings.Clipboard.Enabled = EnableClipboardToggle.IsOn;
        if (EnableClipboardToggle.IsOn)
        {
            ServiceContainer.ClipboardMonitor.Start();
        }
        else
        {
            ServiceContainer.ClipboardMonitor.Stop();
        }
        SaveSettings();
    }

    private async void HistoryLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (HistoryLimitCombo.SelectedItem is ComboBoxItem item)
        {
            string tag = item.Tag?.ToString() ?? "100";
            if (tag == "custom")
            {
                CustomLimitRow.Visibility = Visibility.Visible;
                int val = (int)CustomLimitNumberBox.Value;
                if (val < 10) val = 100;
                CustomLimitNumberBox.Value = val;
                ServiceContainer.Settings.Clipboard.MaxItems = val;
            }
            else if (int.TryParse(tag, out int limit))
            {
                CustomLimitRow.Visibility = Visibility.Collapsed;
                ServiceContainer.Settings.Clipboard.MaxItems = limit;
            }

            SaveSettings();
            await ServiceContainer.ClipboardStore.EnforceLimitAsync(ServiceContainer.Settings.Clipboard.MaxItems);
            await UpdateDebugStatusAsync();
        }
    }

    private async void CustomLimitNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing) return;
        if (!double.IsNaN(sender.Value))
        {
            int val = Math.Clamp((int)sender.Value, 10, 5000);
            ServiceContainer.Settings.Clipboard.MaxItems = val;
            SaveSettings();
            await ServiceContainer.ClipboardStore.EnforceLimitAsync(val);
            await UpdateDebugStatusAsync();
        }
    }

    private void OverrideWinVCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ServiceContainer.Settings.Clipboard.OverrideWinV = OverrideWinVCheckBox.IsChecked ?? false;
        SaveSettings();
    }

    private async void OpenWindowsClipboardSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:clipboard"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Windows clipboard settings: {ex.Message}");
        }
    }

    private void PopupPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (PopupPositionCombo.SelectedItem is ComboBoxItem item)
        {
            ServiceContainer.Settings.Clipboard.PopupPosition = item.Tag?.ToString() ?? "cursor";
            SaveSettings();
        }
    }

    private void PasteBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (PasteBehaviorCombo.SelectedItem is ComboBoxItem item)
        {
            ServiceContainer.Settings.Clipboard.AutoPaste = (item.Tag?.ToString() == "auto");
            SaveSettings();
        }
    }

    private void StartWithWindowsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ServiceContainer.Settings.Clipboard.StartWithWindows = StartWithWindowsToggle.IsOn;
        SaveSettings();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item)
        {
            string theme = item.Tag?.ToString() ?? "System";
            ServiceContainer.Settings.Clipboard.ThemeMode = theme;
            SaveSettings();

            // Apply theme dynamically to root element
            if (this.XamlRoot?.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
        }
    }

    private void CaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var s = ServiceContainer.Settings.Clipboard;
        s.CaptureText = CaptureTextToggle.IsOn;
        s.CaptureImages = CaptureImagesToggle.IsOn;
        s.CaptureFiles = CaptureFilesToggle.IsOn;
        s.KeepRichText = KeepRichTextToggle.IsOn;
        SaveSettings();
    }

    private void LimitsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing) return;
        var s = ServiceContainer.Settings.Clipboard;
        if (!double.IsNaN(MaxTextKbNumberBox.Value)) s.MaxTextKb = (int)MaxTextKbNumberBox.Value;
        if (!double.IsNaN(MaxImageMbNumberBox.Value)) s.MaxImageMb = (int)MaxImageMbNumberBox.Value;
        if (!double.IsNaN(StorageCapMbNumberBox.Value)) s.StorageCapMb = (int)StorageCapMbNumberBox.Value;
        if (!double.IsNaN(AutoDeleteDaysNumberBox.Value)) s.AutoDeleteDays = (int)AutoDeleteDaysNumberBox.Value;
        if (!double.IsNaN(SensitiveExpireSecondsNumberBox.Value)) s.SensitiveExpireSeconds = (int)SensitiveExpireSecondsNumberBox.Value;
        SaveSettings();
    }

    private void PrivacyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var s = ServiceContainer.Settings.Clipboard;
        bool prevEncrypt = s.EncryptHistory;
        s.EncryptHistory = EncryptHistoryToggle.IsOn;
        s.RequireHello = RequireHelloToggle.IsOn;
        s.DetectSensitive = DetectSensitiveToggle.IsOn;
        s.ExcludeFromScreenCapture = ExcludeScreenCaptureToggle.IsOn;
        SaveSettings();

        if (prevEncrypt != s.EncryptHistory)
        {
            _ = Task.Run(async () =>
            {
                await ServiceContainer.ClipboardStore.ReEncryptAllAsync(s.EncryptHistory);
            });
        }
    }

    private void QuickAddApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string appName)
        {
            AddIgnoredApp(appName);
        }
    }

    private void AddIgnoredApp_Click(object sender, RoutedEventArgs e)
    {
        string text = NewAppTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            AddIgnoredApp(text);
            NewAppTextBox.Text = string.Empty;
        }
    }

    private void AddIgnoredApp(string appName)
    {
        appName = appName.Trim().ToLowerInvariant();
        if (!appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            appName += ".exe";
        }

        if (!_ignoredApps.Contains(appName))
        {
            _ignoredApps.Add(appName);
            ServiceContainer.Settings.Clipboard.IgnoredApps = new System.Collections.Generic.List<string>(_ignoredApps);
            SaveSettings();
        }
    }

    private void RemoveIgnoredApp_Click(object sender, RoutedEventArgs e)
    {
        if (IgnoredAppsListView.SelectedItem is string selected)
        {
            _ignoredApps.Remove(selected);
            ServiceContainer.Settings.Clipboard.IgnoredApps = new System.Collections.Generic.List<string>(_ignoredApps);
            SaveSettings();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = ServiceContainer.ClipboardStore.StoragePath;
            ServiceContainer.ClipboardStore.EnsureDirectories();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open clipboard storage folder: {ex.Message}");
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        bool includePinned = IncludePinnedCheckBox.IsChecked ?? false;
        string msg = includePinned
            ? "Are you sure you want to delete ALL clipboard history, including pinned items? This cannot be undone."
            : "Are you sure you want to delete all unpinned clipboard items? Pinned items will be kept.";

        var dialog = new ContentDialog
        {
            Title = "Clear Clipboard History",
            Content = msg,
            PrimaryButtonText = "Clear History",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ServiceContainer.ClipboardStore.ClearHistoryAsync(includePinned);
            await RefreshDataTabAsync();
            await RefreshSnippetsTabAsync();
            await UpdateDebugStatusAsync();
        }
    }

    private async Task RefreshSnippetsTabAsync()
    {
        try
        {
            var rawSnippets = await ServiceContainer.ClipboardStore.GetSnippetsAsync();
            var list = rawSnippets.Select(s => new ClipboardItemViewModel(s)).ToList();
            SnippetsListView.ItemsSource = list;
            SnippetsEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading snippets: {ex.Message}");
        }
    }

    private async void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        await ShowSnippetDialogAsync(null);
    }

    private async void EditSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            await ShowSnippetDialogAsync(vm);
        }
    }

    private async void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            await ServiceContainer.ClipboardStore.DeleteSnippetAsync(vm.Id);
            await RefreshSnippetsTabAsync();
        }
    }

    private async Task ShowSnippetDialogAsync(ClipboardItemViewModel? existing)
    {
        var titleBox = new TextBox
        {
            Header = "Title",
            PlaceholderText = "e.g. Standard Greeting or Code Template",
            Text = existing?.SnippetTitle ?? string.Empty
        };

        var abbrevBox = new TextBox
        {
            Header = "Abbreviation (optional trigger)",
            PlaceholderText = "e.g. /greet",
            Text = existing?.SnippetAbbreviation ?? string.Empty
        };

        var contentBox = new TextBox
        {
            Header = "Snippet Text",
            PlaceholderText = "Enter reusable snippet content...",
            Text = existing?.Model.ContentString ?? existing?.PreviewText ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120
        };

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(titleBox);
        stack.Children.Add(abbrevBox);
        stack.Children.Add(contentBox);

        var dialog = new ContentDialog
        {
            Title = existing == null ? "Add Reusable Snippet" : "Edit Snippet",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            string title = titleBox.Text.Trim();
            if (string.IsNullOrEmpty(title)) title = "Snippet";
            string abbrev = abbrevBox.Text.Trim();
            string content = contentBox.Text;

            await ServiceContainer.ClipboardStore.SaveSnippetAsync(
                title,
                content,
                string.IsNullOrEmpty(abbrev) ? null : abbrev,
                existing?.Id ?? 0);

            await RefreshSnippetsTabAsync();
        }
    }

    private async void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            var targetWin = App.MainWindowInstance ?? MainWindow.Instance;
            if (targetWin != null)
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(targetWin);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("ZIP Archive", new List<string> { ".zip" });
            picker.SuggestedFileName = "AppLock_Clipboard_Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                BackupStatusText.Text = "Exporting history...";
                BackupStatusText.Visibility = Visibility.Visible;

                bool ok = await ServiceContainer.ClipboardExportService.ExportToZipAsync(file.Path);
                BackupStatusText.Text = ok ? $"Successfully exported history to {file.Name}" : "Export failed.";
            }
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"Export error: {ex.Message}";
            BackupStatusText.Visibility = Visibility.Visible;
        }
    }

    private async void ImportHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var targetWin = App.MainWindowInstance ?? MainWindow.Instance;
            if (targetWin != null)
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(targetWin);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".zip");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                BackupStatusText.Text = "Importing history...";
                BackupStatusText.Visibility = Visibility.Visible;

                int count = await ServiceContainer.ClipboardExportService.ImportFromZipAsync(file.Path);
                BackupStatusText.Text = $"Successfully imported {count} items from {file.Name}";
                await RefreshDataTabAsync();
                await RefreshSnippetsTabAsync();
                await UpdateDebugStatusAsync();
            }
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"Import error: {ex.Message}";
            BackupStatusText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshDataTabAsync()
    {
        try
        {
            long bytes = await ServiceContainer.ClipboardStore.GetStorageSizeBytesAsync();
            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            StorageUsageText.Text = $"Total disk storage used: {FormatBytes(bytes)} ({count} items in database)";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error calculating storage usage: {ex.Message}");
        }
    }

    private async Task UpdateDebugStatusAsync()
    {
        try
        {
            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            int limit = ServiceContainer.Settings.Clipboard.MaxItems;
            DebugStatusText.Text = $"Stored items: {count} | Current limit: {limit}";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating debug status: {ex.Message}");
        }
    }

    private async void AddTestItemsButton_Click(object sender, RoutedEventArgs e)
    {
        AddTestItemsButton.IsEnabled = false;
        try
        {
            await ServiceContainer.ClipboardStore.AddDebugItemsAsync(5, ServiceContainer.Settings.Clipboard.MaxItems);
            await UpdateDebugStatusAsync();
            await RefreshDataTabAsync();
        }
        finally
        {
            AddTestItemsButton.IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}

