using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Storage.Streams;
using AppLock.Models.Vault;
using AppLock.Services;
using AppLock.Services.Vault;
using WinRT.Interop;

namespace AppLock.Views.Vault;

public sealed partial class SecurePreviewWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private readonly VaultSession _session;
    private readonly IndexItem _item;
    private readonly nint _hwnd;

    private InMemoryRandomAccessStream? _memoryStream;
    private PdfDocument? _pdfDoc;
    private uint _pdfCurrentPageIndex;
    private double _currentZoom = 1.0;
    private double _currentAngle = 0.0;
    private string _originalText = string.Empty;

    public SecurePreviewWindow(VaultSession session, IndexItem item)
    {
        _session = session;
        _item = item;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        Title = $"{item.Name} — Secure Preview";
        FileNameText.Text = item.Name;

        // Apply screen capture protection
        if (ServiceContainer.Settings.Vault.ExcludeFromCapture)
        {
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
        }

        // Set default window size
        var appWindow = AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(960, 680));

        // Apply Mica backdrop (same as MainWindow) so the preview window
        // blends with the system wallpaper instead of using a flat grey surface.
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }

        // Sync theme with the current app theme so controls look correct
        // in both Light and Dark mode regardless of which window launched us.
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = ServiceContainer.CurrentTheme;
        }

        // Close immediately when vault locks
        ServiceContainer.VaultSessionManager.VaultLocked += OnVaultLocked;
        this.Closed += OnWindowClosed;

        _ = LoadContentAsync();
    }

    private void OnVaultLocked(object? sender, Guid vaultId)
    {
        if (vaultId == _session.VaultId)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Close();
            });
        }
    }

    private void OnWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        ServiceContainer.VaultSessionManager.VaultLocked -= OnVaultLocked;

        // Clean up in-memory streams and players
        PreviewMediaPlayer.MediaPlayer?.Dispose();
        _memoryStream?.Dispose();
        _memoryStream = null;
        _pdfDoc = null;
    }

    private async Task LoadContentAsync()
    {
        LoadingRing.IsActive = true;
        ErrorPanel.Visibility = Visibility.Collapsed;

        var category = PreviewService.GetCategory(_item.Name);

        try
        {
            switch (category)
            {
                case PreviewCategory.Image:
                    await LoadImagePreviewAsync();
                    break;
                case PreviewCategory.Text:
                    await LoadTextPreviewAsync();
                    break;
                case PreviewCategory.Pdf:
                    await LoadPdfPreviewAsync();
                    break;
                case PreviewCategory.Media:
                    await LoadMediaPreviewAsync();
                    break;
                default:
                    ShowError("Preview is not supported for this file type.");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Preview failed for '{_item.Name}': {ex.Message}");
            ShowError($"Failed to decrypt and preview: {ex.Message}");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // ---- Image Preview ------------------------------------------------

    private async Task LoadImagePreviewAsync()
    {
        ImageControlsPanel.Visibility = Visibility.Visible;
        ImageScrollViewer.Visibility = Visibility.Visible;

        _memoryStream = await PreviewService.LoadToMemoryStreamAsync(_session, _item);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(_memoryStream);
        PreviewImage.Source = bitmap;
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _currentZoom = Math.Min(_currentZoom * 1.25, 5.0);
        ImageScale.ScaleX = _currentZoom;
        ImageScale.ScaleY = _currentZoom;
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _currentZoom = Math.Max(_currentZoom / 1.25, 0.2);
        ImageScale.ScaleX = _currentZoom;
        ImageScale.ScaleY = _currentZoom;
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        _currentAngle = (_currentAngle + 90) % 360;
        ImageRotate.Angle = _currentAngle;
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        _currentZoom = 1.0;
        _currentAngle = 0.0;
        ImageScale.ScaleX = 1.0;
        ImageScale.ScaleY = 1.0;
        ImageRotate.Angle = 0;
    }

    // ---- Text Preview -------------------------------------------------

    private async Task LoadTextPreviewAsync()
    {
        TextControlsPanel.Visibility = Visibility.Visible;
        TextScrollViewer.Visibility = Visibility.Visible;

        var (text, truncated) = await PreviewService.ReadTextAsync(_session, _item);
        _originalText = text;
        PreviewTextBox.Text = text;

        if (truncated)
        {
            WarningInfoBar.Message = "This file is larger than 4 MB. Showing the first 4 MB.";
            WarningInfoBar.IsOpen = true;
        }
    }

    private void WordWrapToggle_Click(object sender, RoutedEventArgs e)
    {
        PreviewTextBox.TextWrapping = WordWrapToggle.IsChecked == true
            ? Microsoft.UI.Xaml.TextWrapping.Wrap
            : Microsoft.UI.Xaml.TextWrapping.NoWrap;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text;
        if (string.IsNullOrEmpty(q))
        {
            PreviewTextBox.Text = _originalText;
            return;
        }

        // Simple highlight / scroll to match
        int idx = _originalText.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            PreviewTextBox.Select(idx, q.Length);
        }
    }

    // ---- PDF Preview --------------------------------------------------

    private async Task LoadPdfPreviewAsync()
    {
        PdfControlsPanel.Visibility = Visibility.Visible;
        PdfScrollViewer.Visibility = Visibility.Visible;

        _memoryStream = await PreviewService.LoadToMemoryStreamAsync(_session, _item);
        _pdfDoc = await PdfDocument.LoadFromStreamAsync(_memoryStream);

        _pdfCurrentPageIndex = 0;
        await RenderPdfPageAsync(_pdfCurrentPageIndex);
    }

    private async Task RenderPdfPageAsync(uint pageIndex)
    {
        if (_pdfDoc == null || pageIndex >= _pdfDoc.PageCount) return;

        using var page = _pdfDoc.GetPage(pageIndex);
        var pageStream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(pageStream);
        pageStream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(pageStream);
        PdfImage.Source = bitmap;

        PdfPageIndicator.Text = $"Page {pageIndex + 1} of {_pdfDoc.PageCount}";
    }

    private async void PdfPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc == null || _pdfCurrentPageIndex == 0) return;
        _pdfCurrentPageIndex--;
        await RenderPdfPageAsync(_pdfCurrentPageIndex);
    }

    private async void PdfNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfDoc == null || _pdfCurrentPageIndex + 1 >= _pdfDoc.PageCount) return;
        _pdfCurrentPageIndex++;
        await RenderPdfPageAsync(_pdfCurrentPageIndex);
    }

    // ---- Media Preview ------------------------------------------------

    private async Task LoadMediaPreviewAsync()
    {
        PreviewMediaPlayer.Visibility = Visibility.Visible;

        _memoryStream = await PreviewService.LoadToMemoryStreamAsync(_session, _item);

        var ext = Path.GetExtension(_item.Name).ToLowerInvariant();
        var mime = ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            _ => "video/mp4"
        };

        var source = MediaSource.CreateFromStream(_memoryStream, mime);
        PreviewMediaPlayer.Source = source;
    }

    // ---- Common -------------------------------------------------------

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
