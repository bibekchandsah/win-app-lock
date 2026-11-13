using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.Brightness;
using Microsoft.UI.Dispatching;

namespace AppLock.ViewModels;

public class BrightnessViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BrightnessService _brightnessService;
    private readonly DispatcherQueue _dispatcher;
    private int _currentBrightness = 100;
    private string _selectedDisplayId = "All";
    private bool _isUpdatingSlider;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DisplayViewModel> Displays { get; } = new();
    public ObservableCollection<BrightnessPresetViewModel> Presets { get; } = new();

    public int CurrentBrightness
    {
        get => _currentBrightness;
        set
        {
            if (_currentBrightness != value)
            {
                _currentBrightness = value;
                OnPropertyChanged();
                
                // Apply brightness change
                if (!_isUpdatingSlider)
                {
                    _ = SetBrightnessAsync(value);
                }
            }
        }
    }

    public string SelectedDisplayId
    {
        get => _selectedDisplayId;
        set
        {
            if (_selectedDisplayId != value)
            {
                _selectedDisplayId = value;
                OnPropertyChanged();
                _ = RefreshCurrentBrightnessAsync();
            }
        }
    }

    public BrightnessViewModel(BrightnessService brightnessService, DispatcherQueue dispatcher)
    {
        _brightnessService = brightnessService;
        _dispatcher = dispatcher;
        
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await RefreshDisplaysAsync();
        await RefreshCurrentBrightnessAsync();
        LoadPresets();
    }

    private void LoadPresets()
    {
        var settings = ServiceContainer.Settings.Brightness;
        
        Presets.Clear();
        foreach (var preset in settings.Presets)
        {
            Presets.Add(new BrightnessPresetViewModel(preset, this));
        }
    }

    public async Task RefreshDisplaysAsync()
    {
        try
        {
            var displays = await Task.Run(() => _brightnessService.GetDisplays());
            
            _dispatcher.TryEnqueue(() =>
            {
                // Clearing the items makes the bound ComboBox drop its selection, which writes null
                // back through SelectedValue. Remember what to select and restore it once the items
                // are back in, otherwise the box renders with no text.
                var preferredId = string.IsNullOrEmpty(_selectedDisplayId) ? "All" : _selectedDisplayId;

                Displays.Clear();
                
                // Add "All displays" option
                Displays.Add(new DisplayViewModel
                {
                    Id = "All",
                    Name = "All displays",
                    ControlMethod = string.Empty
                });

                foreach (var display in displays)
                {
                    Displays.Add(new DisplayViewModel
                    {
                        Id = display.Id,
                        Name = $"{display.Name} · {display.Resolution}",
                        ControlMethod = display.ControlMethod
                    });
                }

                _selectedDisplayId = preferredId;
                OnPropertyChanged(nameof(SelectedDisplayId));
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshDisplays error: {ex.Message}");
        }
    }

    public async Task RefreshCurrentBrightnessAsync()
    {
        try
        {
            int brightness = await _brightnessService.GetBrightnessAsync(_selectedDisplayId);
            
            _dispatcher.TryEnqueue(() =>
            {
                _isUpdatingSlider = true;
                CurrentBrightness = brightness;
                _isUpdatingSlider = false;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshCurrentBrightness error: {ex.Message}");
        }
    }

    private async Task SetBrightnessAsync(int percent)
    {
        try
        {
            await _brightnessService.SetBrightnessAsync(percent, _selectedDisplayId, debounce: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SetBrightness error: {ex.Message}");
        }
    }

    public void AddPreset()
    {
        var preset = new BrightnessPreset { Percent = 50 };
        ServiceContainer.Settings.Brightness.Presets.Add(preset);
        Presets.Add(new BrightnessPresetViewModel(preset, this));
        SaveSettings();
    }

    public void RemovePreset(BrightnessPresetViewModel presetVm)
    {
        var preset = ServiceContainer.Settings.Brightness.Presets.FirstOrDefault(p => p.Id == presetVm.Id);
        if (preset != null)
        {
            ServiceContainer.Settings.Brightness.Presets.Remove(preset);
            Presets.Remove(presetVm);
            SaveSettings();
        }
    }

    public void ResetToDefaults()
    {
        ServiceContainer.Settings.Brightness.Presets = BrightnessSettings.DefaultPresets();
        LoadPresets();
        SaveSettings();
    }

    public void SaveSettings()
    {
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _brightnessService?.Dispose();
    }
}

public class DisplayViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ControlMethod { get; set; } = string.Empty;
}

public class BrightnessPresetViewModel : INotifyPropertyChanged
{
    private readonly BrightnessPreset _preset;
    private readonly BrightnessViewModel _parentVm;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => _preset.Id;

    public int Percent
    {
        get => _preset.Percent;
        set
        {
            if (_preset.Percent != value)
            {
                _preset.Percent = value;
                OnPropertyChanged();
                _parentVm.SaveSettings();
            }
        }
    }

    public string HotkeyDisplay => _preset.Hotkey != null 
        ? HotkeyValidator.FormatHotkey(_preset.Hotkey)
        : "Not set";

    public BrightnessPresetViewModel(BrightnessPreset preset, BrightnessViewModel parentVm)
    {
        _preset = preset;
        _parentVm = parentVm;
    }

    public void UpdateHotkey(HotkeyBinding? newHotkey)
    {
        _preset.Hotkey = newHotkey;
        OnPropertyChanged(nameof(HotkeyDisplay));
        _parentVm.SaveSettings();
    }

    public BrightnessPreset GetModel() => _preset;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
