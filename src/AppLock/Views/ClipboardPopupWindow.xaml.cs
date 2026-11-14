using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using AppLock.Models;
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
    private static DateTime? _lastHelloUnlockTime;
    private ClipboardItemViewModel? _editingItem;
    private ClipboardItemViewModel? _snippetSourceItem;
    private bool _suppressDataChanged;

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
        ServiceContainer.ClipboardEnabledChanged += (s, enabled) => DispatcherQueue.TryEnqueue(UpdateStatusBadges);
        ServiceContainer.ClipboardMonitor.PauseStateChanged += (s, e) => DispatcherQueue.TryEnqueue(UpdateStatusBadges);
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

    private void ApplyScreenCaptureExclusion()
    {
        if (_hwnd == nint.Zero) return;
        bool exclude = ServiceContainer.Settings.Clipboard.ExcludeFromScreenCapture;
        uint affinity = exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
        SetWindowDisplayAffinity(_hwnd, affinity);
    }

    private bool _isWindowPinned = false;
    private bool _isDragging = false;
    private POINT _dragStartCursor;
    private PointInt32 _dragStartWindowPos;

    private void ClipboardPopupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_isWindowPinned && !_isDragging)
            {
                HidePopup();
            }
        }
    }

    private void Header_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(sender as UIElement);
        if (pt.Properties.IsLeftButtonPressed && _appWindow != null)
        {
            _isDragging = true;
            (sender as UIElement)?.CapturePointer(e.Pointer);
            GetCursorPos(out _dragStartCursor);
            _dragStartWindowPos = _appWindow.Position;
            e.Handled = true;
        }
    }

    private void Header_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _appWindow != null)
        {
            GetCursorPos(out POINT currentCursor);
            int dx = currentCursor.x - _dragStartCursor.x;
            int dy = currentCursor.y - _dragStartCursor.y;

            _appWindow.Move(new PointInt32(_dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy));
            e.Handled = true;
        }
    }

    private void Header_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void Header_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
    }

    private void WindowPinButton_Click(object sender, RoutedEventArgs e)
    {
        _isWindowPinned = !_isWindowPinned;
        WindowPinIcon.Glyph = _isWindowPinned ? "\uE840" : "\uE718";
        WindowPinIcon.Foreground = _isWindowPinned
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        ToolTipService.SetToolTip(WindowPinButton, _isWindowPinned ? "Unpin window" : "Pin window open (won't close on click outside)");
    }

    private void WindowCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
    }

    private static bool IsAppLockWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Process.GetCurrentProcess().Id;
    }

    private nint GetExternalForegroundWindow()
    {
        uint currentPid = (uint)Process.GetCurrentProcess().Id;
        nint fg = GetForegroundWindow();
        if (fg != nint.Zero)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != currentPid && pid != 0)
            {
                return fg;
            }
        }

        if (_prevForegroundHwnd != nint.Zero && IsWindow(_prevForegroundHwnd))
        {
            GetWindowThreadProcessId(_prevForegroundHwnd, out uint prevPid);
            if (prevPid != currentPid && prevPid != 0)
            {
                return _prevForegroundHwnd;
            }
        }

        nint found = nint.Zero;
        EnumWindows((hwnd, lParam) =>
        {
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != currentPid && pid != 0)
                {
                    int len = GetWindowTextLength(hwnd);
                    if (len > 0)
                    {
                        found = hwnd;
                        return false;
                    }
                }
            }
            return true;
        }, nint.Zero);

        return found;
    }

    private void OnClipboardDataChanged(object? sender, EventArgs e)
    {
        if (_suppressDataChanged) return;
        if (_isVisible && HelloLockOverlay.Visibility != Visibility.Visible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_suppressDataChanged)
                {
                    _ = RefreshItemsAsync();
                }
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
        if (ServiceContainer.ClipboardMonitor.IsSessionLocked)
        {
            return;
        }

        _prevForegroundHwnd = GetExternalForegroundWindow();
        ApplyTheme();
        ApplyScreenCaptureExclusion();

        _currentSearch = string.Empty;
        SearchBox.Text = string.Empty;
        ItemsListView.SelectedItems.Clear();
        UpdateSelectionUi();

        PositionWindow();

        // Check Windows Hello authentication
        var settings = ServiceContainer.Settings.Clipboard;
        if (settings.RequireHello && !IsHelloUnlockValid())
        {
            ShowHelloLock();
        }
        else
        {
            ShowMainContent();
            _ = RefreshItemsAsync();
        }

        this.Activate();
        _isVisible = true;

        // Force topmost over Start Menu, taskbar, and search
        const nint HWND_TOPMOST = -1;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_SHOWWINDOW = 0x0040;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        BringWindowToTop(_hwnd);
        SetForegroundWindow(_hwnd);

        if (HelloLockOverlay.Visibility != Visibility.Visible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                SearchBox.Focus(FocusState.Programmatic);
            });
        }
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

    private static bool IsHelloUnlockValid()
    {
        return _lastHelloUnlockTime.HasValue && (DateTime.UtcNow - _lastHelloUnlockTime.Value) < TimeSpan.FromMinutes(1);
    }

    private void ShowHelloLock()
    {
        HelloLockOverlay.Visibility = Visibility.Visible;
        MainContentGrid.Visibility = Visibility.Collapsed;
        HelloErrorText.Visibility = Visibility.Collapsed;

        // Auto-prompt Windows Hello
        _ = TriggerHelloAuthAsync();
    }

    private void ShowMainContent()
    {
        HelloLockOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;
    }

    private async Task TriggerHelloAuthAsync()
    {
        try
        {
            HelloUnlockButton.IsEnabled = false;
            bool ok = await ServiceContainer.HelloService.RequestVerificationAsync("Unlock clipboard history");
            if (ok)
            {
                _lastHelloUnlockTime = DateTime.UtcNow;
                ShowMainContent();
                await RefreshItemsAsync();
                SearchBox.Focus(FocusState.Programmatic);
            }
            else
            {
                HelloErrorText.Text = "Authentication failed. Click Unlock to try again.";
                HelloErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            HelloErrorText.Text = $"Authentication error: {ex.Message}";
            HelloErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            HelloUnlockButton.IsEnabled = true;
        }
    }

    private void HelloUnlock_Click(object sender, RoutedEventArgs e)
    {
        _ = TriggerHelloAuthAsync();
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

    private void UpdateStatusBadges()
    {
        if (PausedBadge == null || PausedBadgeText == null || PausedBadgeIcon == null) return;

        bool enabled = ServiceContainer.Settings.Clipboard.Enabled;
        bool isPaused = ServiceContainer.ClipboardMonitor.IsPaused;

        if (!enabled)
        {
            PausedBadge.Visibility = Visibility.Visible;
            PausedBadgeText.Text = "Disabled";
            PausedBadgeIcon.Glyph = "\uE711";
            PausedBadge.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"];
            PausedBadge.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            PausedBadgeText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            PausedBadgeIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            ToolTipService.SetToolTip(PausedBadge, "Clipboard capture is disabled. Click to enable.");
        }
        else if (isPaused)
        {
            PausedBadge.Visibility = Visibility.Visible;
            var until = ServiceContainer.ClipboardMonitor.PausedUntil;
            if (until.HasValue && until.Value < DateTime.Now.AddYears(1))
            {
                var rem = until.Value - DateTime.Now;
                string remText = rem.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(rem.TotalMinutes)}m" : "<1m";
                PausedBadgeText.Text = $"Paused ({remText})";
                ToolTipService.SetToolTip(PausedBadge, $"Clipboard capture is paused until {until.Value:t}. Click to resume.");
            }
            else
            {
                PausedBadgeText.Text = "Paused";
                ToolTipService.SetToolTip(PausedBadge, "Clipboard capture is paused until resumed. Click to resume.");
            }
            PausedBadgeIcon.Glyph = "\uE769";
            PausedBadge.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"];
            PausedBadge.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            PausedBadgeText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            PausedBadgeIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
        else
        {
            PausedBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void PausedBadge_PointerPressed(object sender, PointerRoutedEventArgs e)
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
        UpdateStatusBadges();
    }

    private async Task RefreshItemsAsync()
    {
        try
        {
            UpdateStatusBadges();

            // Show the whole history the user chose to keep instead of a fixed 100-row page.
            int historyLimit = ServiceContainer.Settings.Clipboard.MaxItems;
            var rawItems = await ServiceContainer.ClipboardStore.GetItemsAsync(
                limit: historyLimit,
                filter: _currentFilter,
                search: _currentSearch);

            Items.Clear();
            for (int i = 0; i < rawItems.Count; i++)
            {
                var item = rawItems[i];
                var vm = new ClipboardItemViewModel(item);
                if (_currentFilter == "Links" && !vm.IsLink) continue;

                // Set quick paste badge 1-9 when search is empty
                if (string.IsNullOrEmpty(_currentSearch) && Items.Count < 9)
                {
                    vm.QuickPasteBadge = (Items.Count + 1).ToString();
                }

                Items.Add(vm);
            }

            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            int limit = ServiceContainer.Settings.Clipboard.MaxItems;
            FooterStatsText.Text = $"{count} / {limit} items";

            EmptyStatePanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = string.IsNullOrEmpty(_currentSearch) ? "Nothing copied yet" : "No matching items";

            UpdateSelectionUi();
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
        // 1-9 quick paste when search box is empty
        if (string.IsNullOrEmpty(_currentSearch) && !IsControlDown() && !IsShiftDown())
        {
            int quickIndex = -1;
            if (e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
            {
                quickIndex = (int)e.Key - (int)VirtualKey.Number1;
            }
            else if (e.Key >= VirtualKey.NumberPad1 && e.Key <= VirtualKey.NumberPad9)
            {
                quickIndex = (int)e.Key - (int)VirtualKey.NumberPad1;
            }

            if (quickIndex >= 0 && quickIndex < Items.Count)
            {
                e.Handled = true;
                _ = ExecutePasteAsync(Items[quickIndex], plainTextOnly: false);
                return;
            }
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (ItemsListView.SelectedItems.Count > 1)
            {
                ItemsListView.SelectedItems.Clear();
                UpdateSelectionUi();
            }
            else
            {
                HidePopup();
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.PageDown)
        {
            MoveSelection(5);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.PageUp)
        {
            MoveSelection(-5);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            bool plainText = IsShiftDown();

            if (ItemsListView.SelectedItems.Count > 1)
            {
                _ = ExecutePasteMergedAsync(plainTextOnly: plainText);
            }
            else
            {
                var targetItem = ItemsListView.SelectedItem as ClipboardItemViewModel ?? Items.FirstOrDefault();
                if (targetItem != null)
                {
                    _ = ExecutePasteAsync(targetItem, plainTextOnly: plainText);
                }
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            var targetItem = ItemsListView.SelectedItem as ClipboardItemViewModel ?? Items.FirstOrDefault();
            if (targetItem != null)
            {
                _ = DeleteItemAsync(targetItem);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.P && IsControlDown())
        {
            var targetItem = ItemsListView.SelectedItem as ClipboardItemViewModel ?? Items.FirstOrDefault();
            if (targetItem != null)
            {
                _ = TogglePinAsync(targetItem);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.C && IsControlDown())
        {
            var targetItem = ItemsListView.SelectedItem as ClipboardItemViewModel ?? Items.FirstOrDefault();
            if (targetItem != null)
            {
                _ = ExecutePasteAsync(targetItem, copyOnly: true);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.E && IsControlDown())
        {
            var targetItem = ItemsListView.SelectedItem as ClipboardItemViewModel ?? Items.FirstOrDefault();
            if (targetItem != null && targetItem.IsText)
            {
                OpenEditOverlay(targetItem);
                e.Handled = true;
            }
        }
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        int currentIndex = ItemsListView.SelectedIndex;
        int newIndex = currentIndex < 0 ? (delta > 0 ? 0 : Items.Count - 1) : currentIndex + delta;

        if (newIndex < 0) newIndex = 0;
        if (newIndex >= Items.Count) newIndex = Items.Count - 1;

        ItemsListView.SelectedIndex = newIndex;
        ItemsListView.ScrollIntoView(Items[newIndex]);
    }

    private void ItemsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (ItemsListView.SelectedItems.Count > 1)
            {
                ItemsListView.SelectedItems.Clear();
                UpdateSelectionUi();
            }
            else
            {
                HidePopup();
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            bool plainText = IsShiftDown();
            if (ItemsListView.SelectedItems.Count > 1)
            {
                _ = ExecutePasteMergedAsync(plainTextOnly: plainText);
            }
            else if (ItemsListView.SelectedItem is ClipboardItemViewModel selected)
            {
                _ = ExecutePasteAsync(selected, plainTextOnly: plainText);
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            if (ItemsListView.SelectedItems.Count > 1)
            {
                _ = DeleteSelectedItemsAsync();
            }
            else if (ItemsListView.SelectedItem is ClipboardItemViewModel selected)
            {
                _ = DeleteItemAsync(selected);
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.P && IsControlDown())
        {
            if (ItemsListView.SelectedItem is ClipboardItemViewModel selected)
            {
                _ = TogglePinAsync(selected);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.C && IsControlDown())
        {
            if (ItemsListView.SelectedItems.Count > 1)
            {
                _ = ExecutePasteMergedAsync(copyOnly: true);
            }
            else if (ItemsListView.SelectedItem is ClipboardItemViewModel selected)
            {
                _ = ExecutePasteAsync(selected, copyOnly: true);
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.E && IsControlDown())
        {
            if (ItemsListView.SelectedItem is ClipboardItemViewModel selected && selected.IsText)
            {
                OpenEditOverlay(selected);
                e.Handled = true;
            }
        }
    }

    private void ItemsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (IsControlDown())
        {
            return;
        }

        if (e.ClickedItem is ClipboardItemViewModel vm)
        {
            bool plainText = IsShiftDown();
            _ = ExecutePasteAsync(vm, plainTextOnly: plainText);
        }
    }

    private void ItemsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        int selectedCount = ItemsListView.SelectedItems.Count;
        if (selectedCount > 1)
        {
            MultiSelectBar.Visibility = Visibility.Visible;
            NormalFooter.Visibility = Visibility.Collapsed;
            MultiSelectCountText.Text = $"{selectedCount} items selected";
        }
        else
        {
            MultiSelectBar.Visibility = Visibility.Collapsed;
            NormalFooter.Visibility = Visibility.Visible;
        }
    }

    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsListView.SelectedItems.Count > 0)
        {
            ItemsListView.SelectedItems.Clear();
        }
        else if (Items.Count > 0)
        {
            ItemsListView.SelectedItems.Add(Items[0]);
            if (Items.Count > 1) ItemsListView.SelectedItems.Add(Items[1]);
        }
        UpdateSelectionUi();
    }

    private async Task ExecutePasteAsync(ClipboardItemViewModel vm, bool plainTextOnly = false, bool copyOnly = false)
    {
        nint target = _prevForegroundHwnd;
        if (target == nint.Zero || !IsWindow(target) || IsAppLockWindow(target))
        {
            target = GetExternalForegroundWindow();
        }

        // Restore focus while AppLock is still active so Windows grants foreground transition
        if (target != nint.Zero && IsWindow(target))
        {
            ClipboardPasteService.RestoreFocus(target);
        }

        HidePopup();

        await ServiceContainer.ClipboardPasteService.PasteItemAsync(
            vm.Model,
            target,
            plainTextOnly: plainTextOnly,
            copyOnly: copyOnly);
    }

    private async Task ExecutePasteMergedAsync(string separator = "\r\n", bool plainTextOnly = false, bool copyOnly = false)
    {
        var selectedList = ItemsListView.SelectedItems.OfType<ClipboardItemViewModel>().Select(x => x.Model).ToList();
        if (selectedList.Count == 0) return;

        nint target = _prevForegroundHwnd;
        if (target == nint.Zero || !IsWindow(target) || IsAppLockWindow(target))
        {
            target = GetExternalForegroundWindow();
        }

        if (target != nint.Zero && IsWindow(target))
        {
            ClipboardPasteService.RestoreFocus(target);
        }

        HidePopup();

        await ServiceContainer.ClipboardPasteService.PasteMergedAsync(
            selectedList,
            target,
            separator: separator,
            plainTextOnly: plainTextOnly,
            copyOnly: copyOnly);
    }

    private void PasteMerged_Click(object sender, RoutedEventArgs e)
    {
        _ = ExecutePasteMergedAsync(separator: "\r\n");
    }

    private void PasteMergedWithSeparator_Click(object sender, RoutedEventArgs e)
    {
        string sep = "\r\n";
        if (sender is MenuFlyoutItem item && item.Tag is string tag)
        {
            sep = tag switch
            {
                "comma" => ", ",
                "space" => " ",
                _ => "\r\n"
            };
        }
        _ = ExecutePasteMergedAsync(separator: sep);
    }

    private void MultiCopy_Click(object sender, RoutedEventArgs e)
    {
        _ = ExecutePasteMergedAsync(copyOnly: true);
    }

    private async void MultiDelete_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedItemsAsync();
    }

    private void MultiCancel_Click(object sender, RoutedEventArgs e)
    {
        ItemsListView.SelectedItems.Clear();
        UpdateSelectionUi();
    }

    private async Task DeleteSelectedItemsAsync()
    {
        var selectedIds = ItemsListView.SelectedItems.OfType<ClipboardItemViewModel>().Select(x => x.Id).ToList();
        if (selectedIds.Count > 0)
        {
            await ServiceContainer.ClipboardStore.DeleteItemsAsync(selectedIds);
            _ = RefreshItemsAsync();
        }
    }

    private async Task DeleteItemAsync(ClipboardItemViewModel vm)
    {
        _suppressDataChanged = true;
        try
        {
            Items.Remove(vm);
            await ServiceContainer.ClipboardStore.DeleteItemAsync(vm.Id);
            UpdateSelectionUi();
            int count = await ServiceContainer.ClipboardStore.GetItemCountAsync();
            int limit = ServiceContainer.Settings.Clipboard.MaxItems;
            FooterStatsText.Text = $"{count} / {limit} items";
            EmptyStatePanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressDataChanged = false;
        }
    }

    private async Task TogglePinAsync(ClipboardItemViewModel vm)
    {
        _suppressDataChanged = true;
        try
        {
            bool newPinned = !vm.Pinned;
            vm.SetPinned(newPinned);
            await ServiceContainer.ClipboardStore.TogglePinAsync(vm.Id);
            if (_currentFilter == "Pinned" && !newPinned)
            {
                Items.Remove(vm);
            }
        }
        finally
        {
            _suppressDataChanged = false;
        }
    }

    private void ItemReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            vm.ToggleReveal();
        }
    }

    private void ItemCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            _ = ExecutePasteAsync(vm, copyOnly: true);
        }
    }

    private void ItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            _ = DeleteItemAsync(vm);
        }
    }

    private void ItemPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel vm)
        {
            _ = TogglePinAsync(vm);
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
            FilterSnippetsButton.Style = filter == "Snippets" ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];

            _ = RefreshItemsAsync();
        }
    }

    private void OpenEditOverlay(ClipboardItemViewModel vm)
    {
        _editingItem = vm;
        string text = vm.Model.ContentString ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            text = vm.PreviewText;
        }
        EditTextTextBox.Text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        EditItemOverlay.Visibility = Visibility.Visible;
        MainContentGrid.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
        {
            EditTextTextBox.Focus(FocusState.Programmatic);
            EditTextTextBox.SelectAll();
        });
    }

    private async void EditOverlayPaste_Click(object sender, RoutedEventArgs e)
    {
        if (_editingItem == null) return;
        string newText = EditTextTextBox.Text;
        nint target = _prevForegroundHwnd;

        EditItemOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;
        HidePopup();

        var tempItem = new ClipboardItem
        {
            Id = _editingItem.Id,
            Type = "text",
            ContentString = newText,
            PreviewString = newText.Length > 200 ? newText[..200] : newText,
            Hash = _editingItem.Model.Hash,
            SizeBytes = newText.Length
        };

        _ = ServiceContainer.ClipboardStore.UpdateTextItemContentAsync(_editingItem.Id, newText);
        await ServiceContainer.ClipboardPasteService.PasteItemAsync(tempItem, target);
    }

    private async void EditOverlaySaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_editingItem == null) return;
        string newText = EditTextTextBox.Text;

        EditItemOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;

        await ServiceContainer.ClipboardStore.UpdateTextItemContentAsync(_editingItem.Id, newText);
        var tempItem = new ClipboardItem
        {
            Id = _editingItem.Id,
            Type = "text",
            ContentString = newText,
            PreviewString = newText.Length > 200 ? newText[..200] : newText,
            Hash = _editingItem.Model.Hash,
            SizeBytes = newText.Length
        };
        await ServiceContainer.ClipboardPasteService.SetClipboardDataAsync(tempItem);
        _ = RefreshItemsAsync();
    }

    private void EditOverlayCancel_Click(object sender, RoutedEventArgs e)
    {
        EditItemOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenSaveSnippetOverlay(ClipboardItemViewModel vm)
    {
        _snippetSourceItem = vm;
        SnippetTitleTextBox.Text = !string.IsNullOrEmpty(vm.SourceTitle) ? vm.SourceTitle : (!string.IsNullOrEmpty(vm.SourceProcess) ? vm.SourceProcess : "Snippet");
        SnippetAbbrevTextBox.Text = string.Empty;
        SaveSnippetOverlay.Visibility = Visibility.Visible;
        MainContentGrid.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
        {
            SnippetTitleTextBox.Focus(FocusState.Programmatic);
            SnippetTitleTextBox.SelectAll();
        });
    }

    private async void SaveSnippetConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_snippetSourceItem == null) return;
        string title = SnippetTitleTextBox.Text.Trim();
        if (string.IsNullOrEmpty(title)) title = "Snippet";
        string abbrev = SnippetAbbrevTextBox.Text.Trim();
        string text = _snippetSourceItem.Model.ContentString ?? _snippetSourceItem.PreviewText;

        SaveSnippetOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;

        await ServiceContainer.ClipboardStore.SaveSnippetAsync(title, text, string.IsNullOrEmpty(abbrev) ? null : abbrev);
        _ = RefreshItemsAsync();
    }

    private void SaveSnippetCancel_Click(object sender, RoutedEventArgs e)
    {
        SaveSnippetOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ItemEdit_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is ClipboardItemViewModel vm && vm.IsText)
        {
            OpenEditOverlay(vm);
        }
    }

    private void ItemSaveAsSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is ClipboardItemViewModel vm)
        {
            OpenSaveSnippetOverlay(vm);
        }
    }

    private void ItemOpenDirectLink_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is ClipboardItemViewModel vm)
        {
            string raw = (vm.Model.ContentString ?? vm.PreviewText).Trim();
            string targetUrl;

            if (vm.IsLink)
            {
                targetUrl = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + raw
                    : raw;

                if (!targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    targetUrl = "https://" + targetUrl;
                }
            }
            else
            {
                targetUrl = "https://www.google.com/search?q=" + Uri.EscapeDataString(raw);
            }

            try
            {
                Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClipboardPopup] Open direct link error: {ex.Message}");
            }

            if (!_isWindowPinned)
            {
                HidePopup();
            }
        }
    }

    private ClipboardItemViewModel? _customSearchTargetItem;

    private void BrowserSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem menuItem || menuItem.Tag is not string engine) return;
        if (GetItemFromSender(menuItem) is not ClipboardItemViewModel vm) return;

        if (engine == "custom")
        {
            _customSearchTargetItem = vm;
            CustomSearchUrlTextBox.Text = ServiceContainer.Settings.Clipboard.CustomSearchUrl ?? "https://www.google.com/search?q={query}";
            CustomSearchOverlay.Visibility = Visibility.Visible;
            MainContentGrid.Visibility = Visibility.Collapsed;
            DispatcherQueue.TryEnqueue(() =>
            {
                CustomSearchUrlTextBox.Focus(FocusState.Programmatic);
                CustomSearchUrlTextBox.SelectAll();
            });
            return;
        }

        OpenInBrowserWithEngine(vm, engine);
    }

    private void OpenInBrowserWithEngine(ClipboardItemViewModel vm, string engine)
    {
        string targetUrl = string.Empty;

        if (vm.IsImage)
        {
            targetUrl = engine switch
            {
                "bing" => "https://www.bing.com/visualsearch",
                "google" => "https://lens.google.com/",
                "tineye" => "https://tineye.com/",
                _ => "https://lens.google.com/"
            };
        }
        else
        {
            string raw = (vm.Model.ContentString ?? vm.PreviewText).Trim();
            if (vm.IsLink)
            {
                string linkUrl = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + raw
                    : raw;
                if (!linkUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !linkUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    linkUrl = "https://" + linkUrl;
                }
                targetUrl = linkUrl;
            }

            if (string.IsNullOrEmpty(targetUrl))
            {
                string encoded = Uri.EscapeDataString(raw);
                targetUrl = engine switch
                {
                    "bing" => "https://www.bing.com/search?q=" + encoded,
                    "google" => "https://www.google.com/search?q=" + encoded,
                    "duckduckgo" => "https://duckduckgo.com/?q=" + encoded,
                    "yahoo" => "https://search.yahoo.com/search?p=" + encoded,
                    "ecosia" => "https://www.ecosia.org/search?q=" + encoded,
                    _ => "https://www.google.com/search?q=" + encoded
                };
            }
        }

        if (!string.IsNullOrEmpty(targetUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClipboardPopup] Open browser error: {ex.Message}");
            }

            if (!_isWindowPinned)
            {
                HidePopup();
            }
        }
    }

    private void CustomSearchConfirm_Click(object sender, RoutedEventArgs e)
    {
        string urlTemplate = CustomSearchUrlTextBox.Text.Trim();
        if (string.IsNullOrEmpty(urlTemplate)) return;

        ServiceContainer.Settings.Clipboard.CustomSearchUrl = urlTemplate;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

        CustomSearchOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;

        if (_customSearchTargetItem != null)
        {
            string raw = (_customSearchTargetItem.Model.ContentString ?? _customSearchTargetItem.PreviewText).Trim();
            string finalUrl = urlTemplate.Contains("{query}")
                ? urlTemplate.Replace("{query}", Uri.EscapeDataString(raw))
                : (urlTemplate.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? urlTemplate : "https://" + urlTemplate);

            try
            {
                Process.Start(new ProcessStartInfo(finalUrl) { UseShellExecute = true });
            }
            catch { }

            if (!_isWindowPinned)
            {
                HidePopup();
            }
        }
    }

    private void CustomSearchCancel_Click(object sender, RoutedEventArgs e)
    {
        CustomSearchOverlay.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void ItemSaveImageAs_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is ClipboardItemViewModel vm && vm.IsImage && !string.IsNullOrEmpty(vm.ImagePath) && File.Exists(vm.ImagePath))
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
                picker.SuggestedFileName = "ClipboardImage_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    File.Copy(vm.ImagePath, file.Path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardPopup] Save image error: {ex.Message}");
            }
        }
    }

    private void ItemCopyPng_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is ClipboardItemViewModel vm && vm.IsImage)
        {
            _ = ExecutePasteAsync(vm, copyOnly: true);
        }
    }

    private async void TextAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is string action && GetItemFromSender(menuItem) is ClipboardItemViewModel vm)
        {
            string text = vm.Model.ContentString ?? vm.PreviewText;
            string transformed = action switch
            {
                "upper" => text.ToUpperInvariant(),
                "lower" => text.ToLowerInvariant(),
                "title" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower()),
                "trim" => text.Trim(),
                "removelines" => text.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "),
                "urlencode" => Uri.EscapeDataString(text),
                "urldecode" => Uri.UnescapeDataString(text),
                "b64encode" => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)),
                "b64decode" => TryBase64Decode(text),
                _ => text
            };

            nint target = _prevForegroundHwnd;
            HidePopup();

            var tempItem = new ClipboardItem
            {
                Id = vm.Id,
                Type = "text",
                ContentString = transformed,
                PreviewString = transformed.Length > 200 ? transformed[..200] : transformed,
                Hash = vm.Model.Hash,
                SizeBytes = transformed.Length
            };

            await ServiceContainer.ClipboardPasteService.PasteItemAsync(tempItem, target);
        }
    }

    private ClipboardItemViewModel? GetItemFromSender(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.Tag is ClipboardItemViewModel tagVm) return tagVm;
            if (fe.DataContext is ClipboardItemViewModel dcVm) return dcVm;
        }
        return ItemsListView.SelectedItem as ClipboardItemViewModel ?? Items.FirstOrDefault();
    }

    private static string TryBase64Decode(string input)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(input.Trim());
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return input;
        }
    }

    private void FooterPause5_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromMinutes(5));
    private void FooterPause15_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromMinutes(15));
    private void FooterPause60_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromMinutes(60));
    private void FooterPauseIndefinite_Click(object sender, RoutedEventArgs e) => SetPause(TimeSpan.FromDays(365));

    private void SetPause(TimeSpan duration)
    {
        ServiceContainer.ClipboardMonitor.SetPause(duration);
        _ = RefreshItemsAsync();
    }

    private void FooterResume_Click(object sender, RoutedEventArgs e)
    {
        ServiceContainer.ClipboardMonitor.Resume();
        _ = RefreshItemsAsync();
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

    private static bool IsShiftDown()
    {
        return (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetKeyState(0x10) & 0x8000) != 0;
    }

    private static bool IsControlDown()
    {
        return (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetKeyState(0x11) & 0x8000) != 0;
    }

    #region Win32 Interop

    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const nint HTCAPTION = 2;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    #endregion
}
