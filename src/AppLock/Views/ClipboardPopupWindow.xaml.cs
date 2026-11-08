using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;
using AppLock.Services;
using AppLock.ViewModels;

namespace AppLock.Views;

public sealed partial class ClipboardPopupWindow : Window
{
    private static ClipboardPopupWindow? _instance;
    private AppWindow? _appWindow;
    private nint _hwnd;
    private nint _prevForegroundHwnd;
    private bool _isVisible;
    private string _currentFilter = "All";
    private string _currentSearch = string.Empty;

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();

    public static ClipboardPopupWindow Instance => _instance ??= new ClipboardPopupWindow();

    public ClipboardPopupWindow()
    {
        _instance = this;
        this.InitializeComponent();

        SetupWindow();
        ApplyTheme();

        this.Activated += ClipboardPopupWindow_Activated;
        ServiceContainer.ClipboardStore.DataChanged += OnClipboardDataChanged;
    }

    private void SetupWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow != null)
        {
            _appWindow.IsShownInSwitchers = false;
            _appWindow.Resize(new SizeInt32(420, 540));

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }

        ExtendsContentIntoTitleBar = true;
    }

    private void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            string theme = ServiceContainer.Settings.Clipboard.ThemeMode;
            root.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void ClipboardPopupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            HidePopup();
        }
    }

    private void OnClipboardDataChanged(object? sender, EventArgs e)
    {
        if (_isVisible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _ = RefreshItemsAsync();
            });
        }
    }

    public static void Toggle()
    {
        if (Instance._isVisible)
        {
            Instance.HidePopup();
        }
        else
        {
            Instance.ShowPopup();
        }
    }

    public void ShowPopup()
    {
        _prevForegroundHwnd = GetForegroundWindow();
        ApplyTheme();
        _currentSearch = string.Empty;
        SearchBox.Text = string.Empty;

        PositionWindow();

        _ = RefreshItemsAsync();

        this.Activate();
        _isVisible = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            SearchBox.Focus(FocusState.Programmatic);
        });
    }

    public void HidePopup()
    {
        if (!_isVisible) return;
        _isVisible = false;

        if (_appWindow != null)
        {
            _appWindow.Hide();
        }
    }

    private void PositionWindow()
    {
        const int width = 420;
        const int height = 540;

        string posMode = ServiceContainer.Settings.Clipboard.PopupPosition;

        GetCursorPos(out POINT mousePt);
        nint hMon = MonitorFromPoint(mousePt, MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);

        int x = mousePt.x - 20;
        int y = mousePt.y + 10;

        if (posMode == "center")
        {
            x = mi.rcWork.left + ((mi.rcWork.right - mi.rcWork.left) - width) / 2;
            y = mi.rcWork.top + ((mi.rcWork.bottom - mi.rcWork.top) - height) / 2;
        }
        else if (posMode == "caret" && _prevForegroundHwnd != nint.Zero)
        {
            bool foundCaret = false;
            uint threadId = GetWindowThreadProcessId(_prevForegroundHwnd, out _);
            GUITHREADINFO gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };

            if (GetGUIThreadInfo(threadId, ref gui) && gui.hwndCaret != nint.Zero && gui.rcCaret.right > gui.rcCaret.left)
            {
                POINT caretPt = new POINT { x = gui.rcCaret.left, y = gui.rcCaret.bottom };
                if (ClientToScreen(gui.hwndCaret, ref caretPt))
                {
                    x = caretPt.x;
                    y = caretPt.y + 8;
                    foundCaret = true;
                }
            }

            if (!foundCaret)
            {
                x = mousePt.x - 20;
                y = mousePt.y + 10;
            }
        }

        // Clamp to monitor work area
        if (x + width > mi.rcWork.right) x = mi.rcWork.right - width - 10;
        if (x < mi.rcWork.left) x = mi.rcWork.left + 10;
        if (y + height > mi.rcWork.bottom) y = mi.rcWork.bottom - height - 10;
        if (y < mi.rcWork.top) y = mi.rcWork.top + 10;

        _appWindow?.Move(new PointInt32(x, y));
    }

    private async Task RefreshItemsAsync()
    {
        try
        {
            var rawItems = await ServiceContainer.ClipboardStore.GetItemsAsync(
                limit: 100,
                filter: _currentFilter,
                search: _currentSearch);

            Items.Clear();
            foreach (var item in rawItems)
            {
                var vm = new ClipboardItemViewModel(item);
                if (_currentFilter == "Links" && !vm.IsLink) continue;
                Items.Add(vm);
            }

            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            int limit = ServiceContainer.Settings.Clipboard.MaxItems;
            FooterStatsText.Text = $"{count} / {limit} items";

            EmptyStatePanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = string.IsNullOrEmpty(_currentSearch) ? "Nothing copied yet" : "No matching items";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardPopupWindow] Error loading items: {ex.Message}");
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _currentSearch = SearchBox.Text.Trim();
        _ = RefreshItemsAsync();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HidePopup();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down)
        {
            if (Items.Count > 0)
            {
                ItemsListView.SelectedIndex = 0;
                ItemsListView.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.Enter)
        {
            if (Items.Count > 0)
            {
                SelectAndCopyItem(Items[0]);
                e.Handled = true;
            }
        }
    }

    private void ItemsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HidePopup();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            if (ItemsListView.SelectedItem is ClipboardItemViewModel selected)
            {
                SelectAndCopyItem(selected);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.Delete)
        {
            if (ItemsListView.SelectedItem is ClipboardItemViewModel selected)
            {
                _ = ServiceContainer.ClipboardStore.DeleteItemAsync(selected.Id);
                e.Handled = true;
            }
        }
    }

    private void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ClipboardItemViewModel vm)
        {
            SelectAndCopyItem(vm);
        }
    }

    private async void SelectAndCopyItem(ClipboardItemViewModel vm)
    {
        try
        {
            ServiceContainer.ClipboardMonitor.IgnoreNext();

            if (vm.IsImage)
            {
                if (!string.IsNullOrEmpty(vm.ImagePath) && File.Exists(vm.ImagePath))
                {
                    var file = await StorageFile.GetFileFromPathAsync(vm.ImagePath);
                    var dp = new DataPackage();
                    dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    Clipboard.SetContent(dp);
                }
            }
            else if (vm.IsFiles)
            {
                if (vm.FilePaths.Count > 0)
                {
                    var storageItems = new List<IStorageItem>();
                    foreach (var path in vm.FilePaths)
                    {
                        if (File.Exists(path)) storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                        else if (Directory.Exists(path)) storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    }
                    if (storageItems.Count > 0)
                    {
                        var dp = new DataPackage();
                        dp.SetStorageItems(storageItems);
                        Clipboard.SetContent(dp);
                    }
                }
            }
            else
            {
                string? text = vm.Model.ContentString ?? vm.PreviewText;
                if (!string.IsNullOrEmpty(text))
                {
                    var dp = new DataPackage();
                    dp.SetText(text);
                    Clipboard.SetContent(dp);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardPopupWindow] Error copying: {ex.Message}");
        }

        HidePopup();
    }

    private void ItemCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            SelectAndCopyItem(vm);
        }
    }

    private async void ItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            await ServiceContainer.ClipboardStore.DeleteItemAsync(vm.Id);
            _ = RefreshItemsAsync();
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filter)
        {
            _currentFilter = filter;

            FilterAllButton.Style = filter == "All" ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
            FilterTextButton.Style = filter == "Text" ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
            FilterImagesButton.Style = filter == "Images" ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
            FilterLinksButton.Style = filter == "Links" ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
            FilterPinnedButton.Style = filter == "Pinned" ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];

            _ = RefreshItemsAsync();
        }
    }

    private async void FooterClearAll_Click(object sender, RoutedEventArgs e)
    {
        await ServiceContainer.ClipboardStore.ClearHistoryAsync(includePinned: false);
        _ = RefreshItemsAsync();
    }

    private void FooterSettings_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
        MainWindow.Instance?.Activate();
        if (MainWindow.Instance?.Content is FrameworkElement root)
        {
            var frame = FindDescendantFrame(root);
            frame?.Navigate(typeof(ClipboardSettingsPage));
        }
    }

    private static Frame? FindDescendantFrame(DependencyObject parent)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Frame frame) return frame;
            var sub = FindDescendantFrame(child);
            if (sub != null) return sub;
        }
        return null;
    }

    #region Win32 Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    #endregion
}

