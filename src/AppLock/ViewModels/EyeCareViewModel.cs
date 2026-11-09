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
    private bool _isRefreshingTemplates;
    private bool _isUpdatingFromExternal;

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
                if (!_isUpdatingFromExternal)
                {
                    UpdateEnabled(value);
                }
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
                
                if (!_isUpdatingFromExternal && value >= 0 && value < IntervalPresets.Count)
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
                
                if (!_isRefreshingTemplates)
                {
                    UpdateTemplate(value);
                }
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

        // Sync when tray (or any external source) changes eye care state
        ServiceContainer.EyeCareEnabledChanged += OnEyeCareEnabledChanged;
        ServiceContainer.EyeCareIntervalChanged += OnEyeCareIntervalChanged;
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
        _isRefreshingTemplates = true;
        try
        {
            AvailableTemplates.Clear();
            var templates = _overlayService.GetAvailableTemplates();

            var settings = ServiceContainer.Settings.EyeCare;
            if (!string.IsNullOrEmpty(settings.TemplatePath) && File.Exists(settings.TemplatePath))
            {
                var fileName = Path.GetFileName(settings.TemplatePath);
                if (!templates.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    templates.Add(fileName);
                }
            }

            foreach (var template in templates)
            {
                AvailableTemplates.Add(template);
            }

            int targetIndex = 0;
            if (!string.IsNullOrEmpty(settings.TemplatePath))
            {
                var fileName = Path.GetFileName(settings.TemplatePath);
                var index = AvailableTemplates.ToList().FindIndex(t => string.Equals(t, fileName, StringComparison.OrdinalIgnoreCase));
                targetIndex = index >= 0 ? index : 0;
            }

            _selectedTemplateIndex = targetIndex;
            OnPropertyChanged(nameof(SelectedTemplateIndex));
        }
        finally
        {
            _isRefreshingTemplates = false;
        }
    }

    private void UpdateEnabled(bool value)
    {
        ServiceContainer.SetEyeCareEnabled(value);
    }

    private void UpdateInterval(int minutes)
    {
        ServiceContainer.SetEyeCareInterval(minutes);
    }

    public void SetCustomInterval(int minutes)
    {
        if (minutes < 1 || minutes > 1440)
            return;

        _customInterval = minutes;
        _isCustomIntervalMode = true;
        OnPropertyChanged(nameof(CustomInterval));
        OnPropertyChanged(nameof(IsCustomIntervalMode));
        ServiceContainer.SetEyeCareInterval(minutes);
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
        if (_isRefreshingTemplates)
            return;

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
            var inFolder = Path.Combine(templatesFolder, selected);
            if (File.Exists(inFolder))
            {
                ServiceContainer.Settings.EyeCare.TemplatePath = inFolder;
            }
            else if (!string.IsNullOrEmpty(ServiceContainer.Settings.EyeCare.TemplatePath) &&
                     string.Equals(Path.GetFileName(ServiceContainer.Settings.EyeCare.TemplatePath), selected, StringComparison.OrdinalIgnoreCase) &&
                     File.Exists(ServiceContainer.Settings.EyeCare.TemplatePath))
            {
                // Retain current valid custom path
            }
            else
            {
                ServiceContainer.Settings.EyeCare.TemplatePath = inFolder;
            }
        }
        SaveSettings();
    }

    public async Task BrowseForTemplateAsync()
    {
        try
        {
            Debug.WriteLine("[EyeCare] BrowseForTemplateAsync started");
            
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".html");
            picker.FileTypeFilter.Add(".htm");
            
            Debug.WriteLine($"[EyeCare] App.MainWindowInstance is null: {App.MainWindowInstance == null}");
            
            if (App.MainWindowInstance == null)
            {
                Debug.WriteLine("[EyeCare] ERROR: MainWindowInstance is null!");
                throw new InvalidOperationException("Main window instance is not available");
            }
            
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            Debug.WriteLine($"[EyeCare] Window handle: {hWnd}");
            
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            Debug.WriteLine("[EyeCare] Picker initialized, showing dialog...");

            var file = await picker.PickSingleFileAsync();
            Debug.WriteLine($"[EyeCare] File picked: {file?.Path ?? "null"}");
            
            if (file != null)
            {
                var templatesFolder = _overlayService.GetTemplatesFolder();
                Directory.CreateDirectory(templatesFolder);
                var targetPath = Path.Combine(templatesFolder, file.Name);

                try
                {
                    if (!string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(file.Path, targetPath, overwrite: true);
                    }
                    ServiceContainer.Settings.EyeCare.TemplatePath = targetPath;
                }
                catch (Exception copyEx)
                {
                    Debug.WriteLine($"[EyeCare] Failed to copy to templates folder: {copyEx.Message}");
                    ServiceContainer.Settings.EyeCare.TemplatePath = file.Path;
                }

                SaveSettings();
                RefreshTemplateList();
                Debug.WriteLine("[EyeCare] Template path saved and list refreshed");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EyeCare] Browse template error: {ex.Message}");
            Debug.WriteLine($"[EyeCare] Stack trace: {ex.StackTrace}");
            throw; // Re-throw so the UI handler can catch it
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
            Debug.WriteLine("[EyeCare] PreviewAsync started");
            Debug.WriteLine($"[EyeCare] Overlay service is null: {_overlayService == null}");
            
            if (_overlayService == null)
            {
                throw new InvalidOperationException("Overlay service is not available");
            }
            
            Debug.WriteLine("[EyeCare] Calling ShowAsync...");
            await _overlayService.ShowAsync();
            Debug.WriteLine("[EyeCare] ShowAsync completed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EyeCare] Preview error: {ex.Message}");
            Debug.WriteLine($"[EyeCare] Stack trace: {ex.StackTrace}");
            throw; // Re-throw so the UI handler can catch it
        }
    }

    private void RestartScheduler()
    {
        if (ServiceContainer.Settings.EyeCare.Enabled)
        {
            _scheduler.Start();
        }
    }

    private void OnEyeCareEnabledChanged(object? sender, bool enabled)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_enabled == enabled) return;
            _isUpdatingFromExternal = true;
            try
            {
                Enabled = enabled;
            }
            finally
            {
                _isUpdatingFromExternal = false;
            }
        });
    }

    private void OnEyeCareIntervalChanged(object? sender, int minutes)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _customInterval = minutes;
            var idx = IntervalPresets.IndexOf(minutes);
            if (idx >= 0)
            {
                _isCustomIntervalMode = false;
                if (_selectedIntervalIndex != idx)
                {
                    _isUpdatingFromExternal = true;
                    try
                    {
                        SelectedIntervalIndex = idx;
                    }
                    finally
                    {
                        _isUpdatingFromExternal = false;
                    }
                }
            }
            else
            {
                _isCustomIntervalMode = true;
            }
            OnPropertyChanged(nameof(CustomInterval));
            OnPropertyChanged(nameof(IsCustomIntervalMode));
        });
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
        ServiceContainer.EyeCareEnabledChanged -= OnEyeCareEnabledChanged;
        ServiceContainer.EyeCareIntervalChanged -= OnEyeCareIntervalChanged;
        _messageDebounceTimer?.Dispose();
    }
}
