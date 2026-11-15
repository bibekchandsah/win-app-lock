using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.Firewall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.Dialogs;

public sealed partial class KnownPortsDialog : ContentDialog
{
    private readonly ObservableCollection<KnownPort> _items = new();
    private readonly FirewallAuthService _authService = new();

    public KnownPortsDialog()
    {
        InitializeComponent();
        KnownPortsListView.ItemsSource = _items;
        LoadKnownPorts();
    }

    private void LoadKnownPorts()
    {
        _items.Clear();
        var known = ServiceContainer.Settings.ListeningPorts.KnownPorts ?? new List<KnownPort>();
        foreach (var port in known.OrderBy(p => p.Port).ThenBy(p => p.Protocol))
        {
            _items.Add(port);
        }

        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        KnownPortsListView.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveKnownPorts()
    {
        ServiceContainer.Settings.ListeningPorts.KnownPorts = _items.ToList();
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        UpdateEmptyState();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddErrorText.Visibility = Visibility.Collapsed;

        var portText = AddPortBox.Text?.Trim() ?? "";
        if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
        {
            AddErrorText.Text = "Enter a valid port between 1 and 65535.";
            AddErrorText.Visibility = Visibility.Visible;
            return;
        }

        var proto = (AddProtocolCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TCP";
        var appPath = AddAppPathBox.Text?.Trim() ?? "";

        if (_items.Any(p => p.Port == port &&
                            string.Equals(p.Protocol, proto, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.AppPath, appPath, StringComparison.OrdinalIgnoreCase)))
        {
            AddErrorText.Text = "This port is already in the known list.";
            AddErrorText.Visibility = Visibility.Visible;
            return;
        }

        var entry = new KnownPort
        {
            Port = port,
            Protocol = proto.ToUpperInvariant(),
            AppPath = appPath,
            Added = DateTime.Now
        };

        _items.Add(entry);
        SaveKnownPorts();

        AddPortBox.Text = "";
        AddAppPathBox.Text = "";
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is KnownPort port)
        {
            if (_authService.RequiresAuthForUnblock)
            {
                bool authenticated = await _authService.AuthenticateForUnblockAsync(this.XamlRoot, "remove a known port");
                if (!authenticated)
                    return;
            }

            _items.Remove(port);
            SaveKnownPorts();
        }
    }

    private async void ExportJson_Click(object sender, RoutedEventArgs e)
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
            picker.FileTypeChoices.Add("JSON Document", new List<string> { ".json" });
            picker.SuggestedFileName = $"AppLock_KnownPorts_{DateTime.Now:yyyyMMdd_HHmmss}";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KnownPortsDialog] Export error: {ex.Message}");
        }
    }

    private async void ImportJson_Click(object sender, RoutedEventArgs e)
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
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var content = await File.ReadAllTextAsync(file.Path);
                var imported = JsonSerializer.Deserialize<List<KnownPort>>(content);
                if (imported != null)
                {
                    int added = 0;
                    foreach (var p in imported)
                    {
                        if (p.Port >= 1 && p.Port <= 65535 &&
                            !_items.Any(existing => existing.Port == p.Port &&
                                                    string.Equals(existing.Protocol, p.Protocol, StringComparison.OrdinalIgnoreCase) &&
                                                    string.Equals(existing.AppPath, p.AppPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            _items.Add(p);
                            added++;
                        }
                    }

                    if (added > 0)
                    {
                        SaveKnownPorts();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KnownPortsDialog] Import error: {ex.Message}");
        }
    }
}
