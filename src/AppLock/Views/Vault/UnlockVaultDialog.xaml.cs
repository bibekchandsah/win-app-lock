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

    private async Task<bool> TryUnlockAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        SetUnlockingState(true);

        _cts = new CancellationTokenSource();
        try
        {
            var mgr = ServiceContainer.VaultSessionManager;

            VaultSession session;
            if (_useRecoveryKey)
            {
                var key = RecoveryKeyBox.Text.Trim();
                if (string.IsNullOrEmpty(key))
                {
                    ShowError("Please enter your recovery key.");
                    return false;
                }
                session = await mgr.UnlockWithRecoveryKeyAsync(_registration, key, _cts.Token);
            }
            else
            {
                var pw = PasswordBox.Password;
                if (string.IsNullOrEmpty(pw))
                {
                    ShowError("Please enter your password.");
                    return false;
                }
                session = await mgr.UnlockWithPasswordAsync(_registration, pw, _cts.Token);
                // Clear password box immediately
                PasswordBox.Password = "";
            }

            UnlockedSession = session;

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
