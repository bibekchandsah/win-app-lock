using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AppLock.Services;
using AppLock.Services.Vault;

namespace AppLock.Views.Vault;

public sealed partial class VerifyVaultDialog : ContentDialog
{
    private readonly VaultSession _session;
    private VaultVerifyReport? _lastReport;
    private CancellationTokenSource? _cts;

    public VerifyVaultDialog(VaultSession session)
    {
        _session = session;
        InitializeComponent();
        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (this.XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            this.RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            this.RequestedTheme = ServiceContainer.CurrentTheme;
        }
    }

    private async void VerifyButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Cancel normal close — this button triggers the verification
        args.Cancel = true;

        IsPrimaryButtonEnabled = false;
        DeepVerifyCheck.IsEnabled = false;
        ProgressSection.Visibility = Visibility.Visible;
        ResultsBorder.Visibility = Visibility.Collapsed;
        ErrorDetailsBox.Visibility = Visibility.Collapsed;

        bool deep = DeepVerifyCheck.IsChecked == true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<(int current, int total, string currentFile)>(info =>
        {
            if (info.total > 0)
            {
                VerifyProgressBar.Value = (double)info.current / info.total * 100.0;
                ProgressStatusText.Text = $"Verifying ({info.current}/{info.total}): {info.currentFile}";
            }
        });

        try
        {
            _lastReport = await VaultManager.VerifyVaultAsync(_session, deep, progress, _cts.Token);
            DisplayReport(_lastReport);
        }
        catch (OperationCanceledException)
        {
            ProgressStatusText.Text = "Verification cancelled.";
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = $"Verification failed: {ex.Message}";
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            DeepVerifyCheck.IsEnabled = true;
            PrimaryButtonText = "Re-verify";
        }
    }

    private void DisplayReport(VaultVerifyReport report)
    {
        ProgressSection.Visibility = Visibility.Collapsed;
        ResultsBorder.Visibility = Visibility.Visible;

        OverallStatusText.Text = report.IsSuccess
            ? "✓ Vault integrity verified successfully"
            : "⚠ Integrity issues detected";

        ConfigStatusText.Text = report.ConfigValid ? "Valid (Authenticated MAC)" : "Failed";
        IndexStatusText.Text  = report.IndexValid ? $"Valid ({report.ItemCount} items)" : "Failed";
        FilesStatusText.Text  = $"{report.VerifiedFilesCount} / {report.TotalFiles} files intact";

        int badCount = report.MissingFiles.Count + report.CorruptedFiles.Count;
        CorruptStatusText.Text = badCount == 0 ? "None" : $"{badCount} issue(s)";

        OrphansStatusText.Text = report.OrphanFiles.Count == 0 ? "None" : $"{report.OrphanFiles.Count} unreferenced object(s)";
        CleanOrphansButton.Visibility = report.OrphanFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (report.Errors.Count > 0)
        {
            ErrorDetailsBox.Visibility = Visibility.Visible;
            ErrorDetailsBox.Text = string.Join(Environment.NewLine, report.Errors);
        }
    }

    private void CleanOrphansButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastReport?.OrphanFiles == null) return;

        int deleted = 0;
        foreach (var file in _lastReport.OrphanFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch { }
        }

        CleanOrphansButton.IsEnabled = false;
        OrphansStatusText.Text = $"Cleaned {deleted} orphan object(s).";
        LogService.LogAction("Vault", $"Cleaned {deleted} orphan objects from vault '{_session.VaultName}'.");
    }
}
