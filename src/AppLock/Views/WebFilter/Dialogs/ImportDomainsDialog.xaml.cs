using System;
using System.Collections.Generic;
using System.IO;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.WebFilter.Dialogs;

public sealed partial class ImportDomainsDialog : ContentDialog
{
    public string ImportedText { get; private set; } = string.Empty;

    public ImportDomainsDialog(string listName)
    {
        InitializeComponent();
        Title = $"Import into {listName}";
        InputTextBox.TextChanged += (s, e) => UpdatePreview();

        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            RequestedTheme = ServiceContainer.CurrentTheme;
        }
    }

    private void UpdatePreview()
    {
        var lines = InputTextBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int valid = 0;
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (!string.IsNullOrEmpty(t) && !t.StartsWith('#')) valid++;
        }
        PreviewCountText.Text = valid > 0 ? $"{valid} domain(s) ready to import." : string.Empty;
    }

    private async void BrowseFileButton_Click(object sender, RoutedEventArgs e)
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
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var text = await File.ReadAllTextAsync(file.Path);
                InputTextBox.Text = text;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebFilter] Import file pick failed: {ex.Message}");
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ImportedText = InputTextBox.Text;
    }
}
