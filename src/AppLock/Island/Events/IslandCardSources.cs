using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
public readonly record struct ClipboardCardInfo(
    string Kind,
    string Title,
    string Detail,
    string App,
    string Text,
    int Width,
    int Height,
    byte[]? ImageBytes = null);

/// <summary>One entry of the Home tab's "Recently copied" list (last three items, never persisted).</summary>
public readonly record struct RecentClip(string Kind, string Preview, string App, DateTime CapturedAt);

/// <summary>One playback device for the Home tab's output dropdown.</summary>
public readonly record struct VolumeDeviceInfo(string Id, string Name, bool IsDefault);

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

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _debounce;
    private readonly uint _cfExclude;
    private readonly uint _cfCanInclude;
    private readonly uint _cfPng;
    private readonly List<RecentClip> _recent = new();

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

    /// <summary>The last three copies, newest first, for the Home tab (memory only, nothing stored).</summary>
    public IReadOnlyList<RecentClip> Recent
    {
        get
        {
            lock (_recent) return _recent.ToArray();
        }
    }

    private void Read()
    {
        // Two updates within a few milliseconds describe the same copy.
        long now = Environment.TickCount64;
        if (now - _lastReadMs < 200) return;
        _lastReadMs = now;

        // The owner is read before AppLock takes the clipboard: OpenClipboard makes this window
        // the owner, and the source app would be lost.
        string app = SourceAppName();

        if (!OpenClipboard(_hwnd)) return;
        try
        {
            if (_cfExclude != 0 && IsClipboardFormatAvailable(_cfExclude)) return;
            if (_cfCanInclude != 0 && IsClipboardFormatAvailable(_cfCanInclude) && ReadDword(_cfCanInclude) == 0) return;

            if (IsClipboardFormatAvailable(CF_UNICODETEXT) && TryReadText(out string text))
            {
                AddRecent(new RecentClip("clipboard.text", text, app, DateTime.Now));
                string detail = app.Length > 0 ? $"{text} \u00B7 {app}" : text;
                Captured?.Invoke(new ClipboardCardInfo("clipboard.text", "Copied", detail, app, text, 0, 0));
                return;
            }

            if (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5)
                || IsClipboardFormatAvailable(CF_BITMAP)
                || (_cfPng != 0 && IsClipboardFormatAvailable(_cfPng)))
            {
                TryGetImageSize(out int width, out int height);
                byte[]? imageBytes = TryReadImageBytes(_cfPng);
                string size = width > 0 ? $"{width}\u00D7{height}" : "Image";
                string detail = app.Length > 0 ? $"{size} \u00B7 {app}" : size;
                AddRecent(new RecentClip("clipboard.image", size, app, DateTime.Now));
                Captured?.Invoke(new ClipboardCardInfo("clipboard.image", "Image copied", detail, app, string.Empty, width, height, imageBytes));
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

    private void AddRecent(RecentClip clip)
    {
        lock (_recent)
        {
            _recent.Insert(0, clip);
            while (_recent.Count > 3) _recent.RemoveAt(_recent.Count - 1);
        }
    }

    /// <summary>The app that copied the item, e.g. "msedge.exe". Empty when Windows does not say.</summary>
    private static string SourceAppName()
    {
        try
        {
            IntPtr owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return string.Empty;

            GetWindowThreadProcessId(owner, out uint processId);
            if (processId == 0) return string.Empty;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception)
        {
            // Elevated or already-exited process: the card just shows no source.
            return string.Empty;
        }
    }

    /// <summary>Width and height from the DIB header (the clipboard image card shows "268×125").</summary>
    private static bool TryGetImageSize(out int width, out int height)
    {
        width = 0;
        height = 0;

        uint format = IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : CF_DIB;
        IntPtr handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return false;

        IntPtr data = GlobalLock(handle);
        if (data == IntPtr.Zero) return false;

        try
        {
            var header = Marshal.PtrToStructure<BITMAPINFOHEADER>(data);
            if (header.biSize < 40 || header.biWidth <= 0 || header.biHeight == 0) return false;

            width = header.biWidth;
            height = Math.Abs(header.biHeight);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? TryReadImageBytes(uint cfPng)
    {
        try
        {
            if (cfPng != 0 && IsClipboardFormatAvailable(cfPng))
            {
                IntPtr pngHandle = GetClipboardData(cfPng);
                if (pngHandle != IntPtr.Zero)
                {
                    int size = (int)GlobalSize(pngHandle);
                    IntPtr ptr = GlobalLock(pngHandle);
                    if (ptr != IntPtr.Zero && size > 0 && size <= 16 * 1024 * 1024)
                    {
                        try
                        {
                            var png = new byte[size];
                            Marshal.Copy(ptr, png, 0, size);
                            return png;
                        }
                        finally { GlobalUnlock(pngHandle); }
                    }
                }
            }

            uint fmt = IsClipboardFormatAvailable(CF_DIB) ? CF_DIB : (IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : 0);
            if (fmt == 0) return null;

            IntPtr dibHandle = GetClipboardData(fmt);
            if (dibHandle == IntPtr.Zero) return null;
            int dibSize = (int)GlobalSize(dibHandle);
            if (dibSize < 40 || dibSize > 16 * 1024 * 1024) return null;

            IntPtr dibPtr = GlobalLock(dibHandle);
            if (dibPtr == IntPtr.Zero) return null;
            try
            {
                var header = Marshal.PtrToStructure<BITMAPINFOHEADER>(dibPtr);
                uint colorsUsed = (uint)Math.Max(0, header.biClrUsed);
                if (colorsUsed == 0 && header.biBitCount <= 8)
                {
                    colorsUsed = 1u << header.biBitCount;
                }
                uint extraMasks = (header.biSize == 40 && header.biCompression == 3) ? 12u : 0u;
                int offset = 14 + (int)header.biSize + (int)extraMasks + (int)(colorsUsed * 4);
                int totalSize = 14 + dibSize;

                var bmp = new byte[totalSize];
                bmp[0] = (byte)'B';
                bmp[1] = (byte)'M';
                BitConverter.GetBytes(totalSize).CopyTo(bmp, 2);
                BitConverter.GetBytes(offset).CopyTo(bmp, 10);
                Marshal.Copy(dibPtr, bmp, 14, dibSize);
                return bmp;
            }
            finally { GlobalUnlock(dibHandle); }
        }
        catch
        {
            return null;
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
    private bool _initialized;

    private SpeakerDevice? DefaultSpeaker()
    {
        if (_failed) return null;

        try
        {
            // Enumerating devices reads a property store per endpoint, so it is paid once per
            // controller; GetSpeakers() afterwards is a cached list.
            _service ??= new SpeakerVolumeService();
            if (!_initialized)
            {
                _initialized = true;
                if (!_service.Initialize() && _service.GetSpeakers().Count == 0) return null;
            }
            else
            {
                // Only the default flag is re-checked (one cheap lookup, no enumeration) so a
                // headset switch is picked up without paying for the full device list again.
                _service.RefreshDefaultFlags();
            }

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
            // SpeakerDevice.GetVolume/SetVolume both work in percent; mixing a 0-1 scalar in here
            // clamped every step to 1, which is why a wheel/hotkey nudge used to drop to ~1%.
            float current = speaker.GetVolume();
            float next = Math.Clamp(current + delta, 0f, 100f);
            speaker.SetVolume(next);
            return Math.Clamp((int)Math.Round(next), 0, 100);
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

    /// <summary>Playback devices for the Home tab's output dropdown (volume-mixer.md service).</summary>
    public IReadOnlyList<VolumeDeviceInfo> Devices()
    {
        if (DefaultSpeaker() == null || _service == null) return Array.Empty<VolumeDeviceInfo>();

        try
        {
            _service.RefreshDefaultFlags();
            return _service.GetSpeakers()
                .Select(s => new VolumeDeviceInfo(s.DeviceId, s.FriendlyName, s.IsDefault))
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] speaker enumeration failed: {ex.Message}");
            return Array.Empty<VolumeDeviceInfo>();
        }
    }

    /// <summary>Makes an already-enumerated device the Windows default; the id must match one.</summary>
    public bool SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || _service == null) return false;

        try
        {
            if (!_service.GetSpeakers().Any(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal)))
            {
                return false;
            }

            bool changed = _service.SetDefaultSpeaker(deviceId);
            _service.RefreshDefaultFlags();
            return changed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] default speaker change failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
        _initialized = false;
    }
}
