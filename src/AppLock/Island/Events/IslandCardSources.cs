using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using AppLock.Services;
using AppLock.Services.Audio;

namespace AppLock.Island.Events;

/// <summary>
/// Caps Lock (and Num/Scroll Lock) state changes. Polls <c>GetKeyState</c> at 10 Hz while the
/// island is enabled, which island.md 5.2 explicitly allows and which avoids a second global
/// keyboard hook next to the one AppLock already installs.
/// </summary>
public sealed class LockKeyCardSource : IDisposable
{
    private readonly DispatcherQueueTimer _timer;

    public event Action<bool>? CapsLockChanged;
    public event Action<bool>? NumLockChanged;

    private bool? _caps;
    private bool? _num;

    public LockKeyCardSource(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _caps = LockKeyIndicatorService.IsCapsLockOn();
        _num = LockKeyIndicatorService.IsNumLockOn();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _caps = null;
        _num = null;
    }

    private void Poll()
    {
        bool caps = LockKeyIndicatorService.IsCapsLockOn();
        if (_caps != caps)
        {
            _caps = caps;
            CapsLockChanged?.Invoke(caps);
        }

        bool num = LockKeyIndicatorService.IsNumLockOn();
        if (_num != num)
        {
            _num = num;
            NumLockChanged?.Invoke(num);
        }
    }

    public void Dispose() => Stop();
}

/// <summary>The text or image description of a fresh clipboard item, for the "Copied" card.</summary>
public readonly record struct ClipboardCardInfo(string Kind, string Title, string Detail);

/// <summary>
/// Minimal clipboard listener for the island card: it does not store anything (the clipboard
/// history owns that) and honours the same privacy flags as <see cref="ClipboardMonitor"/> —
/// items flagged <c>ExcludeClipboardContentFromMonitorProcessing</c> or with
/// <c>CanIncludeInClipboardHistory = 0</c> (password managers) never produce a card.
/// </summary>
public sealed class ClipboardCardSource : IDisposable
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _debounce;
    private readonly uint _cfExclude;
    private readonly uint _cfCanInclude;
    private readonly uint _cfPng;

    private bool _attached;
    private long _lastReadMs;

    public event Action<ClipboardCardInfo>? Captured;

    public ClipboardCardSource(DispatcherQueue queue, IntPtr hwnd)
    {
        _hwnd = hwnd;
        _cfExclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _cfCanInclude = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _cfPng = RegisterClipboardFormatW("PNG");

        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(250);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Read();
    }

    public void Start()
    {
        if (_attached) return;
        _attached = AddClipboardFormatListener(_hwnd);
        if (!_attached) Debug.WriteLine("[Island] AddClipboardFormatListener failed");
    }

    /// <summary>Called from the window procedure; true when the message was a clipboard update.</summary>
    public bool HandleMessage(uint message)
    {
        if (message != WM_CLIPBOARDUPDATE) return false;

        // Clipboard owners usually set their formats a moment after the update is posted.
        _debounce.Stop();
        _debounce.Start();
        return true;
    }

    private void Read()
    {
        // Two updates within a few milliseconds describe the same copy.
        long now = Environment.TickCount64;
        if (now - _lastReadMs < 200) return;
        _lastReadMs = now;

        if (!OpenClipboard(_hwnd)) return;
        try
        {
            if (_cfExclude != 0 && IsClipboardFormatAvailable(_cfExclude)) return;
            if (_cfCanInclude != 0 && IsClipboardFormatAvailable(_cfCanInclude) && ReadDword(_cfCanInclude) == 0) return;

            if (IsClipboardFormatAvailable(CF_UNICODETEXT) && TryReadText(out string text))
            {
                Captured?.Invoke(new ClipboardCardInfo("clipboard.text", "Copied", text));
                return;
            }

            if (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5)
                || IsClipboardFormatAvailable(CF_BITMAP)
                || (_cfPng != 0 && IsClipboardFormatAvailable(_cfPng)))
            {
                Captured?.Invoke(new ClipboardCardInfo("clipboard.image", "Image copied", string.Empty));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] clipboard read failed: {ex.Message}");
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static uint ReadDword(uint format)
    {
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return 1;

        IntPtr data = GlobalLock(handle);
        if (data == IntPtr.Zero) return 1;

        try
        {
            return unchecked((uint)Marshal.ReadInt32(data));
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool TryReadText(out string text)
    {
        text = string.Empty;
        IntPtr handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return false;

        IntPtr data = GlobalLock(handle);
        if (data == IntPtr.Zero) return false;

        try
        {
            string? raw = Marshal.PtrToStringUni(data);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // One line, collapsed whitespace, clipped: the card is a one-line preview.
            var builder = new StringBuilder(Math.Min(raw.Length, 120));
            bool lastWasSpace = false;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                    builder.Append(' ');
                }
                else
                {
                    lastWasSpace = false;
                    builder.Append(c);
                }

                if (builder.Length >= 80) break;
            }

            text = builder.ToString().Trim();
            return text.Length > 0;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    public void Dispose()
    {
        _debounce.Stop();
        if (_attached)
        {
            RemoveClipboardFormatListener(_hwnd);
            _attached = false;
        }
    }
}

/// <summary>Watches the screenshots folder and reports the file that just appeared.</summary>
public sealed class ScreenshotCardSource : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _debounce;
    private FileSystemWatcher? _watcher;
    private string _pendingName = string.Empty;
    private string _folder = DefaultFolder;
    private bool _watchingFolder;

    public event Action<string>? Saved;

    public ScreenshotCardSource(DispatcherQueue queue)
    {
        _queue = queue;
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(400);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) =>
        {
            if (_pendingName.Length == 0) return;
            string name = _pendingName;
            _pendingName = string.Empty;
            Saved?.Invoke(name);
        };
    }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public void Start(string? folder)
    {
        Stop();

        try
        {
            string path = Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder);
            _folder = path;

            // The screenshots folder is created by Windows on the first screenshot, so before that
            // the parent is watched and every event is filtered down to the folder itself.
            _watchingFolder = Directory.Exists(path);
            string root = _watchingFolder ? path : Path.GetDirectoryName(path) ?? path;
            if (!Directory.Exists(root)) return;

            _watcher = new FileSystemWatcher(root)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Deleted += OnDeleted;
            _watcher.Error += (_, _) => _queue.TryEnqueue(() => Start(_folder));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] screenshots watcher failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => Report(e.FullPath);

    /// <summary>The folder can be deleted while the island runs; fall back to watching its parent.</summary>
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!Directory.Exists(_folder)) Start(_folder);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // The folder may have just been created (first screenshot on this machine): move the
        // watcher into it and pick up anything already written there.
        if (!_watchingFolder && Directory.Exists(_folder))
        {
            Start(_folder);
            ReportNewestInFolder();
            return;
        }

        Report(e.FullPath);
    }

    private void ReportNewestInFolder()
    {
        try
        {
            var info = new DirectoryInfo(_folder);
            FileInfo? newest = null;
            foreach (FileInfo file in info.GetFiles())
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(5)) continue;
                if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = file;
            }

            if (newest != null) Report(newest.FullName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] screenshot scan failed: {ex.Message}");
        }
    }

    private void Report(string path)
    {
        // When the folder itself did not exist yet, the watcher may be on the parent.
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null && !directory.Equals(_folder, StringComparison.OrdinalIgnoreCase)) return;

        string extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _queue.TryEnqueue(() =>
        {
            _pendingName = Path.GetFileName(path);
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public void Dispose()
    {
        _debounce.Stop();
        Stop();
    }
}

/// <summary>System volume through the existing NAudio wrapper, one default-device call at a time.</summary>
public sealed class IslandVolumeController : IDisposable
{
    private SpeakerVolumeService? _service;
    private bool _failed;

    private SpeakerDevice? DefaultSpeaker()
    {
        if (_failed) return null;

        try
        {
            _service ??= new SpeakerVolumeService();
            if (!_service.Initialize() && _service.GetSpeakers().Count == 0) return null;

            foreach (SpeakerDevice speaker in _service.GetSpeakers())
            {
                if (speaker.IsDefault) return speaker;
            }

            return null;
        }
        catch (Exception ex)
        {
            _failed = true;
            Debug.WriteLine($"[Island] volume unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Current volume in percent, or -1 when there is no output device.</summary>
    public int Get()
    {
        var speaker = DefaultSpeaker();
        if (speaker == null) return -1;

        try
        {
            return Math.Clamp((int)Math.Round(speaker.GetVolume() * 100), 0, 100);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Adds a step to the current volume and returns the new value, or -1.</summary>
    public int Step(int delta)
    {
        var speaker = DefaultSpeaker();
        if (speaker == null) return -1;

        try
        {
            float current = speaker.GetVolume();
            float next = Math.Clamp(current + (delta / 100f), 0f, 1f);
            speaker.SetVolume(next);
            return Math.Clamp((int)Math.Round(next * 100), 0, 100);
        }
        catch
        {
            return -1;
        }
    }

    public int Set(int percent)
    {
        var speaker = DefaultSpeaker();
        if (speaker == null) return -1;

        try
        {
            float next = Math.Clamp(percent, 0, 100) / 100f;
            speaker.SetVolume(next);
            return Math.Clamp(percent, 0, 100);
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
    }
}
