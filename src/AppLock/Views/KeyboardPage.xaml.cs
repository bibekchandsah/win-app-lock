using AppLock.Services;
using AppLock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppLock.Views
{
    public sealed partial class KeyboardPage : Page
    {
        private readonly KeyboardViewModel _viewModel;
        private readonly KeyboardService _keyboardService;
        private Microsoft.UI.Xaml.DispatcherTimer? _stateTimer;
        private bool _confirmingToggle;

        public KeyboardPage()
        {
            this.InitializeComponent();
            _keyboardService = ServiceContainer.KeyboardService;
            _viewModel = ServiceContainer.KeyboardViewModel;

            Loaded += KeyboardPage_Loaded;
            Unloaded += KeyboardPage_Unloaded;

            LoadSettings();
        }

        private void KeyboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            ServiceContainer.KeyboardEnabledChanged -= OnKeyboardEnabledChanged;
            ServiceContainer.KeyboardEnabledChanged += OnKeyboardEnabledChanged;

            LoadSettings();

            // The input state can also change without an event (another surface, a failed API call).
            // A light poll keeps the switch and the status line honest while the page is visible.
            _stateTimer ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.5) };
            _stateTimer.Tick -= OnStateTimerTick;
            _stateTimer.Tick += OnStateTimerTick;
            _stateTimer.Start();
        }

        private void OnStateTimerTick(object? sender, object e) => SyncFromHardware();

        /// <summary>Re-reads the real input state and updates the switch only when it actually differs.</summary>
        private void SyncFromHardware()
        {
            if (_confirmingToggle) return;

            var actual = _keyboardService.IsKeyboardEnabled();
            if (KeyboardToggle.IsOn == actual)
            {
                // Status text can still be stale after a failed API call; refresh it cheaply.
                if (_viewModel.IsAdmin && _viewModel.StatusText != $"Input is currently: {(actual ? "Enabled" : "Disabled")}")
                {
                    _viewModel.RefreshStatus();
                    UpdateStatus();
                }

                return;
            }

            KeyboardToggle.Toggled -= KeyboardToggle_Toggled;
            KeyboardToggle.IsOn = actual;
            KeyboardToggle.Toggled += KeyboardToggle_Toggled;

            _viewModel.RefreshStatus();
            UpdateStatus();
        }

        private void KeyboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            ServiceContainer.KeyboardEnabledChanged -= OnKeyboardEnabledChanged;
            _stateTimer?.Stop();
        }

        /// <summary>Mirrors a change made from the quick-settings switch on the Others page.</summary>
        private void OnKeyboardEnabledChanged(object? sender, bool enabled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                KeyboardToggle.Toggled -= KeyboardToggle_Toggled;
                KeyboardToggle.IsOn = enabled;
                KeyboardToggle.Toggled += KeyboardToggle_Toggled;

                UpdateStatus();
            });
        }

        private void LoadSettings()
        {
            // Re-read the live input state first: the view model is a singleton, so its cached value
            // is stale after the state was changed from the Others card.
            _viewModel.RefreshStatus();

            // Update toggles without triggering events
            KeyboardToggle.Toggled -= KeyboardToggle_Toggled;
            KeyboardToggle.IsOn = _viewModel.Enabled;
            KeyboardToggle.Toggled += KeyboardToggle_Toggled;

            LockKeyIndicatorToggle.Toggled -= LockKeyIndicatorToggle_Toggled;
            LockKeyIndicatorToggle.IsOn = ServiceContainer.Settings.LockKeyIndicator.Enabled;
            LockKeyIndicatorToggle.Toggled += LockKeyIndicatorToggle_Toggled;

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (!_viewModel.IsAdmin)
            {
                StatusText.Text = "Administrator privileges required";
                StatusIcon.Glyph = "\uE7BA"; // Warning icon
                StatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                
                WarningCard.Visibility = Visibility.Visible;
                
                KeyboardToggle.IsEnabled = false;
            }
            else
            {
                // Re-read the state so the status line follows what was just applied instead of the
                // value captured when the (singleton) view model was created.
                _viewModel.RefreshStatus();

                StatusText.Text = _viewModel.StatusText;
                StatusIcon.Glyph = _viewModel.Enabled ? "\uE73E" : "\uE711"; // Checkmark or Cancel
                StatusIcon.Foreground = _viewModel.Enabled 
                    ? new SolidColorBrush(Microsoft.UI.Colors.Green)
                    : new SolidColorBrush(Microsoft.UI.Colors.Red);
                
                WarningCard.Visibility = Visibility.Collapsed;
                KeyboardToggle.IsEnabled = true;
            }
        }

        private async void KeyboardToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.IsAdmin)
            {
                // Revert toggle
                KeyboardToggle.Toggled -= KeyboardToggle_Toggled;
                KeyboardToggle.IsOn = _viewModel.Enabled;
                KeyboardToggle.Toggled += KeyboardToggle_Toggled;
                return;
            }

            var newState = KeyboardToggle.IsOn;
            _confirmingToggle = true;

            // Confirm with user
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                // ContentDialogs use the application theme by default, which is a dark plate in a
                // light-themed window; follow the page's actual theme instead.
                RequestedTheme = this.ActualTheme,
                Title = newState ? "Enable Input?" : "Disable Input?",
                Content = $"Are you sure you want to {(newState ? "enable" : "disable")} keyboard and mouse input?\n\nThis will take effect immediately.",
                PrimaryButtonText = "Yes",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            _confirmingToggle = false;

            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    // The shared entry point applies the state, saves it and notifies the quick
                    // switch on the Others page.
                    if (!ServiceContainer.SetKeyboardEnabled(newState))
                    {
                        throw new Exception("BlockInput API call failed");
                    }

                    UpdateStatus();
                }
                catch (System.Exception ex)
                {
                    // Show error dialog
                    var errorDialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = this.ActualTheme,
                        Title = "Error",
                        Content = $"Failed to change input state: {ex.Message}",
                        CloseButtonText = "OK"
                    };
                    await errorDialog.ShowAsync();

                    // Revert toggle
                    KeyboardToggle.Toggled -= KeyboardToggle_Toggled;
                    KeyboardToggle.IsOn = _viewModel.Enabled;
                    KeyboardToggle.Toggled += KeyboardToggle_Toggled;
                }
            }
            else
            {
                // User cancelled - revert toggle
                KeyboardToggle.Toggled -= KeyboardToggle_Toggled;
                KeyboardToggle.IsOn = _viewModel.Enabled;
                KeyboardToggle.Toggled += KeyboardToggle_Toggled;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate back to Others page
            Frame.Navigate(typeof(OthersPage));
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Reload current status from registry
            _viewModel.Enabled = _keyboardService.IsKeyboardEnabled();
            LoadSettings();
        }

        /// <summary>
        /// Relaunches AppLock elevated so the keyboard service (BlockInput) can be used, mirroring
        /// the BitLocker page's restart action.
        /// </summary>
        private async void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_viewModel.IsAdmin) return;

                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;

                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };

                // Mark this exit as authorized so the watchdog does not revive the non-elevated instance.
                ProcessProtectionService.SignalAuthorizedExit();
                ProcessProtectionService.UnprotectCurrentProcess();

                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    // Give the elevated instance a moment to come up, then close this one.
                    await Task.Delay(400);
                    Environment.Exit(0);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user dismissed the UAC prompt - keep running unelevated.
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KeyboardPage] Restart as administrator failed: {ex.Message}");
            }
        }

        private void LockKeyIndicatorToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var isEnabled = LockKeyIndicatorToggle.IsOn;
            ServiceContainer.SetLockKeyIndicatorEnabled(isEnabled);
        }
    }
}
