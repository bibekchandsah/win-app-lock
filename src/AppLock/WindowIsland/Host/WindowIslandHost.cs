using System;
using System.IO;
using Microsoft.UI.Dispatching;
using AppLock.Models;
using AppLock.Services;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Windows-native Window Island host — a pure WinUI 3 / Composition notch or capsule at the
/// top of the screen. This is completely separate from the WebView2 "Island" feature.
/// </summary>
public sealed class WindowIslandHost
{
    private static readonly Lazy<WindowIslandHost> _instance = new(() => new WindowIslandHost());
    public static WindowIslandHost Instance => _instance.Value;

    private WindowIslandWindow? _window;
    private readonly object _lock = new();
    private bool _started;

    private WindowIslandHost() { }

    private static void Log(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLock", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "window-island.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public void Start(bool ignoreEnabledSetting = false)
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

        Log("Start called (ignoreEnabled=" + ignoreEnabledSetting + ")");

        void DoStart()
        {
            try
            {
                _window = new WindowIslandWindow();
                _window.Show(settings);
                Log("Window created and shown successfully");
            }
            catch (Exception ex)
            {
                Log("Window creation failed: " + ex);
                lock (_lock) { _started = false; }
            }
        }

        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq != null)
        {
            DoStart();
        }
        else
        {
            var controller = DispatcherQueueController.CreateOnCurrentThread();
            controller.DispatcherQueue.TryEnqueue(DoStart);
        }
    }

    public void Stop()
    {
        lock (_lock) { _started = false; }

        var dq = DispatcherQueue.GetForCurrentThread();
        void DoStop()
        {
            try { _window?.Close(); }
            catch { }
            _window = null;
            Log("Window closed and stopped");
        }

        if (dq != null) DoStop();
        else DoStop();
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        var dq = DispatcherQueue.GetForCurrentThread();
        void Do() => _window?.ApplySettings(settings);
        if (dq != null) dq.TryEnqueue(Do);
    }
}
