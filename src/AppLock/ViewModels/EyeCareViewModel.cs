using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Timers;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.EyeCare;
using Microsoft.UI.Dispatching;

namespace AppLock.ViewModels;

public class EyeCareViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly EyeCareScheduler _scheduler;
    private readonly EyeCareOverlayService _overlayService;
    private readonly DispatcherQueue _dispatcher;
    private readonly System.Timers.Timer _messageDebounceTimer;
    
    private bool _enabled;
    private int _selectedIntervalIndex;
    private string _message = "";
    private bool _scheduleEnabled;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private int _duration;
    private int _selectedTemplateIndex;
    private bool _isCustomIntervalMode;
    private int _customInterval;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Interval presets
    public ObservableCollection<int> IntervalPresets { get; } = new() { 1, 20, 25, 30, 60 };
    public ObservableCollection<string> AvailableTemplates { get; } = new();

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                OnPropertyChanged();
                UpdateEnabled(value);
            }
        }
    }

    public int SelectedIntervalIndex
    {
        get => _selectedIntervalIndex;
        set
        {
            if (_selectedIntervalIndex != value)
            {
                _selectedIntervalIndex = value;
                OnPropertyChanged();
                
                if (value >= 0 && value < IntervalPresets.Count)
                {
                    UpdateInterval(IntervalPresets[value]);
                }
            }
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message != value)
            {
                _message = value;
                OnPropertyChanged();
                DebounceMessageSave();
            }
        }
    }

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set
        {
            if (_scheduleEnabled != value)
            {
                _scheduleEnabled = value;
                OnPropertyChanged();
                UpdateScheduleEnabled(value);
            }
        }
    }

    public TimeSpan StartTime
    {
        get => _startTime;
        set
        {
            if (_startTime != value)
            {
                _startTime = value;
                OnPropertyChanged();
                UpdateStartTime(value);
            }
        }
    }

    public TimeSpan EndTime
    {
        get => _endTime;
        set
        {
            if (_endTime != value)
            {
                _endTime = value;
                OnPropertyChanged();
                UpdateEndTime(value);
            }
        }
    }

    public int Duration
    {
        get => _duration;
        set
        {
            if (_duration != value)
            {
                _duration = value;
                OnPropertyChanged();
                UpdateDuration(value);
            }
        }
    }

    public int SelectedTemplateIndex
    {
        get => _selectedTemplateIndex;
        set
        {
            if (_selectedTemplateIndex != value)
            {
                _selectedTemplateIndex = value;
                OnPropertyChanged();
                UpdateTemplate(value);
            }
        }
    }

    public bool IsCustomIntervalMode
    {
        get => _isCustomIntervalMode;
        set
        {
            if (_isCustomIntervalMode != value)
            {
                _isCustomIntervalMode = value;
                OnPropertyChanged();
            }
        }
    }

    public int CustomInterval
    {
        get => _customInterval;
        set
        {
            if (_customInterval != value)
            {
                _customInterval = value;
                OnPropertyChanged();
            }
        }
    }

    public EyeCareViewModel(
        EyeCareScheduler scheduler,
        EyeCareOverlayService overlayService,
        DispatcherQueue dispatcher)
    {
        _scheduler = scheduler;
        _overlayService = overlayService;
        _dispatcher = dispatcher;

        _messageDebounceTimer = new System.Timers.Timer(500) { AutoReset = false };
        _messageDebounceTimer.Elapsed += OnMessageDebounceElapsed;

        LoadSettings();
        RefreshTemplateList();
    }

    private void LoadSettings()
    {
        var settings = ServiceContainer.Settings.EyeCare;
        settings.Validate();

        _enabled = settings.Enabled;
        _message = settings.Message;
        _scheduleEnabled = settings.ScheduleEnabled;
        _startTime = settings.StartTime;
        _endTime = settings.EndTime;
        _duration = settings.DurationSeconds;
        _customInterval = settings.IntervalMinutes;

        // Find interval index
        var index = IntervalPresets.IndexOf(settings.IntervalMinutes);
        if (index >= 0)
        {
            _selectedIntervalIndex = index;
            _isCustomIntervalMode = false;
        }
        else
        {
            _selectedIntervalIndex = 1; // Default to 20
            _isCustomIntervalMode = true;
        }

        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(SelectedIntervalIndex));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(ScheduleEnabled));
        OnPropertyChanged(nameof(StartTime));
        OnPropertyChanged(nameof(EndTime));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(IsCustomIntervalMode));
        OnPropertyChanged(nameof(CustomInterval));
    }

    private void RefreshTemplateList()
    {
        AvailableTemplates.Clear();
        var templates = _overlayService.GetAvailableTemplates();
        foreach (var template in templates)
        {
            AvailableTemplates.Add(template);
        }

        // Select current template
        var settings = ServiceContainer.Settings.EyeCare;
        if (string.IsNullOrEmpty(settings.TemplatePath))
        {
            _selectedTemplateIndex = 0; // Default
        }
        else
        {
            var fileName = Path.GetFileName(settings.TemplatePath);
            var index = templates.FindIndex(t => t == fileName);
            _selectedTemplateIndex = index >= 0 ? index : 0;
        }
        OnPropertyChanged(nameof(SelectedTemplateIndex));
    }

    private void UpdateEnabled(bool value)
    {
        ServiceContainer.Settings.EyeCare.Enabled = value;
        SaveSettings();

        if (value)
        {
            _scheduler.Start();
        }
        else
        {
            _scheduler.Stop();
        }
    }

    private void UpdateInterval(int minutes)
    {
        ServiceContainer.Settings.EyeCare.IntervalMinutes = minutes;
        SaveSettings();
        RestartScheduler();
    }

    public void SetCustomInterval(int minutes)
    {
        if (minutes < 1 || minutes > 1440)
            return;

        ServiceContainer.Settings.EyeCare.IntervalMinutes = minutes;
        _customInterval = minutes;
        _isCustomIntervalMode = true;
        OnPropertyChanged(nameof(CustomInterval));
        OnPropertyChanged(nameof(IsCustomIntervalMode));
        SaveSettings();
        RestartScheduler();
    }

    private void DebounceMessageSave()
    {
        _messageDebounceTimer.Stop();
        _messageDebounceTimer.Start();
    }

    private void OnMessageDebounceElapsed(object? sender, ElapsedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ServiceContainer.Settings.EyeCare.Message = _message;
            SaveSettings();
        });
    }

    private void UpdateScheduleEnabled(bool value)
    {
        ServiceContainer.Settings.EyeCare.ScheduleEnabled = value;
        SaveSettings();
        RestartScheduler();
    }

    private void UpdateStartTime(TimeSpan value)
    {
        ServiceContainer.Settings.EyeCare.StartTime = value;
        SaveSettings();
    }

    private void UpdateEndTime(TimeSpan value)
    {
        ServiceContainer.Settings.EyeCare.EndTime = value;
        SaveSettings();
    }

    private void UpdateDuration(int value)
    {
        ServiceContainer.Settings.EyeCare.DurationSeconds = value;
        SaveSettings();
    }

    private void UpdateTemplate(int index)
    {
        if (index < 0 || index >= AvailableTemplates.Count)
            return;

        var selected = AvailableTemplates[index];
        if (selected == "Default (Built-in)")
        {
            ServiceContainer.Settings.EyeCare.TemplatePath = null;
        }
        else
        {
            var templatesFolder = _overlayService.GetTemplatesFolder();
            ServiceContainer.Settings.EyeCare.TemplatePath = Path.Combine(templatesFolder, selected);
        }
        SaveSettings();
    }

    public async Task BrowseForTemplateAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".html");
            
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ServiceContainer.Settings.EyeCare.TemplatePath = file.Path;
                SaveSettings();
                RefreshTemplateList();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EyeCare] Browse template error: {ex.Message}");
        }
    }

    public void OpenTemplatesFolder()
    {
        try
        {
            var folder = _overlayService.GetTemplatesFolder();
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EyeCare] Open folder error: {ex.Message}");
        }
    }

    public async Task PreviewAsync()
    {
        try
        {
            await _overlayService.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EyeCare] Preview error: {ex.Message}");
        }
    }

    private void RestartScheduler()
    {
        if (ServiceContainer.Settings.EyeCare.Enabled)
        {
            _scheduler.Start();
        }
    }

    private void SaveSettings()
    {
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _messageDebounceTimer?.Dispose();
    }
}
