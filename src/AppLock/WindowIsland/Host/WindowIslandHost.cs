using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using AppLock.Models;
using AppLock.Services;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Windows-native Window Island host — a pure WinUI 3 / Composition notch or capsule at the
/// top of the screen.
/// 
/// Performance Architecture:
///   - Window and Composition tree are attached to the WinUI UI thread.
///   - All animations run on the GPU / DirectComposition compositor thread via Composition springs.
///   - Zero XAML layout passes or CPU reflow during animations.
///   - All data work, battery monitoring, and polling run strictly off the UI thread on
///     background worker threads and post batched updates back via DispatcherQueue.
/// </summary>
public sealed class WindowIslandHost
{
    private static readonly Lazy<WindowIslandHost> _instance = new(() => new WindowIslandHost());
    public static WindowIslandHost Instance => _instance.Value;

    private WindowIslandWindow? _window;
    private DispatcherQueue? _uiQueue;
    private readonly object _lock = new();
    private bool _started;
    private int _hotkeyId = -1;
    private int _cmdHotkeyId = -1;
    private int _throughHotkeyId = -1;

    public event Action<bool>? ThroughModeChanged;
    public event Action? CustomClocksChanged;
    public event Action? ChatSitesChanged;

    public void NotifyCustomClocksChanged() => CustomClocksChanged?.Invoke();
    public void NotifyChatSitesChanged() => ChatSitesChanged?.Invoke();

    private WindowIslandHost() { }

    public static void Log(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "window-island.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public void Start(bool ignoreEnabledSetting = false, bool useDedicatedThread = false)
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }

        var settings = ServiceContainer.Settings.WindowIsland;
        if (!ignoreEnabledSetting && !settings.Enabled)
        {
            lock (_lock) { _started = false; }
            return;
        }

        Log($"Start called (ignoreEnabled={ignoreEnabledSetting})");

        _uiQueue = DispatcherQueue.GetForCurrentThread();

        void CreateOrShowWindow()
        {
            try
            {
                if (_window != null)
                {
                    _uiQueue = _window.DispatcherQueue;
                    _window.SetIslandVisible(true);
                    _window.ApplySettings(settings);
                    Log("Existing window re-shown and settings applied");
                    return;
                }

                _window = new WindowIslandWindow();
                _uiQueue = _window.DispatcherQueue;
                _window.Show(settings);
                Log($"Window created and shown successfully on thread {Environment.CurrentManagedThreadId}");
            }
            catch (Exception ex)
            {
                Log($"Window creation failed: {ex}");
                lock (_lock) { _started = false; }
            }
        }

        if (_uiQueue != null)
        {
            CreateOrShowWindow();
        }
        else
        {
            // If called off-thread, dispatch to the UI thread
            App.MainWindowInstance?.DispatcherQueue.TryEnqueue(CreateOrShowWindow);
        }

        RegisterHotkey(settings.Hotkey, settings.ThroughModeHotkey, settings.CommandBarHotkey);
    }

    public void Stop()
    {
        lock (_lock) { _started = false; }
        UnregisterHotkey();

        var existingWindow = _window;
        if (existingWindow == null) return;

        void DoHide()
        {
            try { existingWindow.SetIslandVisible(false); } catch { }
            Log("Window hidden and paused");
        }

        if (_uiQueue != null && !_uiQueue.HasThreadAccess) _uiQueue.TryEnqueue(DoHide);
        else DoHide();
    }

    public void Shutdown()
    {
        lock (_lock) { _started = false; }
        UnregisterHotkey();

        var closingWindow = _window;
        _window = null;

        void DoClose()
        {
            try { closingWindow?.Close(); } catch { }
            Log("Window closed and stopped");
        }

        if (_uiQueue != null && !_uiQueue.HasThreadAccess) _uiQueue.TryEnqueue(DoClose);
        else DoClose();
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        void Do() => _window?.ApplySettings(settings);

        if (_uiQueue != null)
        {
            _uiQueue.TryEnqueue(Do);
        }
        else
        {
            App.MainWindowInstance?.DispatcherQueue.TryEnqueue(Do);
        }

        RegisterHotkey(settings.Hotkey, settings.ThroughModeHotkey, settings.CommandBarHotkey);
    }

    public void ToggleThroughMode()
    {
        var settings = ServiceContainer.Settings.WindowIsland;
        settings.ThroughMode = !settings.ThroughMode;
        ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);
        Log($"Through mode toggled -> {settings.ThroughMode}");

        void Do() => _window?.ApplySettings(settings);
        if (_uiQueue != null)
        {
            _uiQueue.TryEnqueue(Do);
        }
        else
        {
            App.MainWindowInstance?.DispatcherQueue.TryEnqueue(Do);
        }

        ThroughModeChanged?.Invoke(settings.ThroughMode);
    }

    public void ToggleVisibility()
    {
        if (_window == null)
        {
            Start();
            return;
        }

        void Do() => _window?.ToggleVisible();

        if (_uiQueue != null)
        {
            _uiQueue.TryEnqueue(Do);
        }
        else
        {
            App.MainWindowInstance?.DispatcherQueue.TryEnqueue(Do);
        }
    }

    public void ToggleCommandBar()
    {
        if (_window == null)
        {
            Start();
        }

        void Do() => _window?.ToggleCommandBar();

        if (_uiQueue != null)
        {
            _uiQueue.TryEnqueue(Do);
        }
        else
        {
            App.MainWindowInstance?.DispatcherQueue.TryEnqueue(Do);
        }
    }

    public void OpenCommandBar()
    {
        if (_window == null)
        {
            Start();
        }

        void Do() => _window?.OpenCommandBar();

        if (_uiQueue != null)
        {
            _uiQueue.TryEnqueue(Do);
        }
        else
        {
            App.MainWindowInstance?.DispatcherQueue.TryEnqueue(Do);
        }
    }

    public WindowIslandWindow? Window => _window;

    public async Task<string> RunBenchmarkAsync(int cycles = 50)
    {
        if (_window == null)
        {
            Start();
            await Task.Delay(500);
        }

        if (_window != null)
        {
            await _window.RunBenchmarkAsync(cycles);
            string islandDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "island");
            string benchPath = Path.Combine(islandDir, "bench.txt");
            if (File.Exists(benchPath))
            {
                return await File.ReadAllTextAsync(benchPath);
            }
        }
        return "Benchmark completed.";
    }

    // ── Hotkey ─────────────────────────────────────────────────────────────────

    private void RegisterHotkey(string hotkey, string? throughHotkey = null, string? commandBarHotkey = "Alt+Space")
    {
        UnregisterHotkey();

        // Register Command Bar hotkey (default: Alt+Space)
        string cmdHotkey = commandBarHotkey ?? "Alt+Space";
        if (!string.IsNullOrWhiteSpace(cmdHotkey))
        {
            try
            {
                var (cmdMods, cmdKey) = HotkeyService.ParseHotkey(cmdHotkey);
                _cmdHotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(cmdMods, cmdKey, ToggleCommandBar);
                Log($"Registered Command Bar hotkey {cmdHotkey} id={_cmdHotkeyId}");
            }
            catch (Exception ex)
            {
                Log($"Command Bar hotkey registration failed: {ex}");
            }
        }

        if (!string.IsNullOrWhiteSpace(throughHotkey))
        {
            try
            {
                var (tMods, tKey) = HotkeyService.ParseHotkey(throughHotkey);
                if (tKey != 0)
                {
                    _throughHotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(tMods, tKey, ToggleThroughMode);
                    Log($"Registered Through mode hotkey {throughHotkey} id={_throughHotkeyId}");
                }
            }
            catch (Exception ex)
            {
                Log($"Through mode hotkey registration failed: {ex}");
            }
        }

        if (string.IsNullOrWhiteSpace(hotkey)) return;

        try
        {
            var (modifiers, key) = HotkeyService.ParseHotkey(hotkey);
            if (key == 0) return;

            _hotkeyId = ServiceContainer.HotkeyService.RegisterHotkey(modifiers, key, ToggleVisibility);
            Log($"Registered Window Island hotkey {hotkey} id={_hotkeyId}");
        }
        catch (Exception ex)
        {
            Log($"Hotkey registration failed: {ex}");
        }
    }

    private void UnregisterHotkey()
    {
        if (_hotkeyId > 0)
        {
            try { ServiceContainer.HotkeyService.UnregisterHotkey(_hotkeyId); } catch { }
            _hotkeyId = -1;
        }

        if (_cmdHotkeyId > 0)
        {
            try { ServiceContainer.HotkeyService.UnregisterHotkey(_cmdHotkeyId); } catch { }
            _cmdHotkeyId = -1;
        }

        if (_throughHotkeyId > 0)
        {
            try { ServiceContainer.HotkeyService.UnregisterHotkey(_throughHotkeyId); } catch { }
            _throughHotkeyId = -1;
        }
    }
}
