using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;

namespace AppLock.Views.Vault;

public sealed partial class UnlockVaultDialog : ContentDialog
{
    private readonly VaultRegistration _registration;
    private bool _useRecoveryKey;
    private int _failedAttempts;
    private CancellationTokenSource? _cts;

    /// <summary>Set on successful unlock; caller reads this.</summary>
    public VaultSession? UnlockedSession { get; private set; }

    public UnlockVaultDialog(VaultRegistration registration)
    {
        _registration = registration;
        InitializeComponent();
        VaultNameText.Text = registration.Name;

        if (registration.AutoLockMinutes == 0)
            KeepUnlockedBox.SelectedIndex = 0;
        else if (registration.AutoLockMinutes == 5)
            KeepUnlockedBox.SelectedIndex = 1;
        else if (registration.AutoLockMinutes == 15)
            KeepUnlockedBox.SelectedIndex = 2;
        else if (registration.AutoLockMinutes == 30)
            KeepUnlockedBox.SelectedIndex = 3;

        Loaded += async (_, _) => await InitHelloButtonAsync();
    }

    private void ApplyKeepUnlockedSetting()
    {
        switch (KeepUnlockedBox.SelectedIndex)
        {
            case 0:
                _registration.AutoLockMinutes = 0;
                break;
            case 1:
                _registration.AutoLockMinutes = 5;
                break;
            case 2:
                _registration.AutoLockMinutes = 15;
                break;
            case 3:
                _registration.AutoLockMinutes = 30;
                break;
        }
    }

    private async Task InitHelloButtonAsync()
    {
        try
        {
            bool helloAvailable = await HelloKeyService.IsAvailableAsync();
            bool helloEnrolled  = HelloKeyService.IsEnrolledForVault(_registration.Id);
            HelloButton.Visibility = (helloAvailable && helloEnrolled)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            HelloButton.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Mode toggle --------------------------------------------------

    private void UseRecoveryKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _useRecoveryKey = true;
        PasswordPanel.Visibility = Visibility.Collapsed;
        RecoveryKeyPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void UsePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _useRecoveryKey = false;
        RecoveryKeyPanel.Visibility = Visibility.Collapsed;
        PasswordPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void PasswordBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = TryUnlockAsync();
    }

    private void RecoveryKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
    }

    // ---- Windows Hello ------------------------------------------------

    private async void HelloButton_Click(object sender, RoutedEventArgs e)
    {
        await TryHelloUnlockAsync();
    }

    private async Task TryHelloUnlockAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        SetUnlockingState(true);
        _cts = new CancellationTokenSource();
        try
        {
            var mgr = ServiceContainer.VaultSessionManager;
            var session = await mgr.UnlockWithHelloAsync(_registration, ct: _cts.Token);

            // Finalize session (mirror logic from TryUnlockAsync)
            UnlockedSession = session;
            _ = ServiceContainer.VaultAutoLockService;
            ApplyKeepUnlockedSetting();
            _registration.LastOpened = DateTime.UtcNow;
            if (session.Index.Revision > _registration.HighestIndexRevision)
                _registration.HighestIndexRevision = session.Index.Revision;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            Hide();
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            ShowError($"Windows Hello failed: {ex.Message}\nUse password instead.");
            LogService.LogAction("Vault", $"Hello unlock failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            // cancelled — do nothing
        }
        catch (Exception ex)
        {
            ShowError($"Unlock error: {ex.Message}");
        }
        finally
        {
            SetUnlockingState(false);
        }
    }

    // ---- Unlock -------------------------------------------------------

    private async void UnlockButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            bool success = await TryUnlockAsync();
            if (!success)
                args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<bool> TryUnlockAsync(bool forceTakeOver = false)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        SetUnlockingState(true);

        _cts = new CancellationTokenSource();
        var mgr = ServiceContainer.VaultSessionManager;
        try
        {
            VaultSession session;
            if (_useRecoveryKey)
            {
                var key = RecoveryKeyBox.Text.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    ShowError("Please enter your recovery key.");
                    return false;
                }
                session = await mgr.UnlockWithRecoveryKeyAsync(_registration, key, forceTakeOver, _cts.Token);
            }
            else
            {
                var pw = PasswordBox.Password;
                if (string.IsNullOrEmpty(pw))
                {
                    ShowError("Please enter your password.");
                    return false;
                }
                session = await mgr.UnlockWithPasswordAsync(_registration, pw, forceTakeOver, _cts.Token);
                // Clear password box immediately
                PasswordBox.Password = "";
            }

            UnlockedSession = session;

            // Ensure auto-lock service is running
            _ = ServiceContainer.VaultAutoLockService;

            ApplyKeepUnlockedSetting();

            // Update highest revision in registration
            _registration.LastOpened = DateTime.UtcNow;
            if (session.Index.Revision > _registration.HighestIndexRevision)
                _registration.HighestIndexRevision = session.Index.Revision;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (VaultLockConflictException ex)
        {
            SetUnlockingState(false);
            var ownerInfo = ex.LockInfo != null
                ? $" (opened by {ex.LockInfo.MachineName} at {ex.LockInfo.LockedAtUtc:g})"
                : "";
            var conflictDialog = new ContentDialog
            {
                Title = "Vault may be open elsewhere",
                Content = new TextBlock
                {
                    Text = ex.Message + ownerInfo + "\n\nDo you want to take over the lock (open in read/write mode anyway)?",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                },
                PrimaryButtonText = "Take over",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            var conflictResult = await conflictDialog.ShowAsync();
            if (conflictResult == ContentDialogResult.Primary)
            {
                return await TryUnlockAsync(forceTakeOver: true);
            }
            return false;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _failedAttempts++;
            PasswordBox.Password = "";
            RecoveryKeyBox.Text = "";

            string msg = _useRecoveryKey
                ? "Recovery key was not accepted. Check for typos."
                : "Incorrect password.";

            if (_failedAttempts >= 3)
                msg += $" ({_failedAttempts} failed attempts)";

            ShowError(msg);
            LogService.LogAction("Vault", $"Unlock failed attempt {_failedAttempts}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ShowError($"Unlock failed: {ex.Message}");
            LogService.LogAction("Vault", $"Unlock error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            SetUnlockingState(false);
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetUnlockingState(bool unlocking)
    {
        UnlockingPanel.Visibility = unlocking ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled   = !unlocking;
        IsSecondaryButtonEnabled = !unlocking;
        PasswordBox.IsEnabled    = !unlocking;
        RecoveryKeyBox.IsEnabled = !unlocking;
        PasswordPanel.IsHitTestVisible = !unlocking;
        RecoveryKeyPanel.IsHitTestVisible = !unlocking;
    }
}
