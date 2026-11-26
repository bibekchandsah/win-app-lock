using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly DispatcherQueueTimer _timer;
    private volatile (int Vk, string Label)[] _watchedSnapshot = Array.Empty<(int, string)>();
    private readonly Dictionary<int, bool> _wasKeyDown = new();
    private readonly Dictionary<int, long> _lastKeyFiredMs = new();
    private readonly object _watchLock = new();

    public event Action<bool>? CapsLockChanged;
    public event Action<bool>? NumLockChanged;
    public event Action<string>? WatchedKeyPressed;

    private bool? _caps;
    private bool? _num;

    public LockKeyCardSource(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Poll();
    }

    public void SetWatchedKeys(IEnumerable<(int VirtualKey, string Label)> keys)
    {
        var list = new List<(int Vk, string Label)>();
        lock (_watchLock)
        {
            _wasKeyDown.Clear();
            foreach (var k in keys)
            {
                if (k.VirtualKey > 0 && !string.IsNullOrWhiteSpace(k.Label))
                {
                    list.Add(k);
                    // Clear any stale "pressed since last call" bit on registration
                    _ = GetAsyncKeyState(k.VirtualKey);
                }
            }
            _watchedSnapshot = list.Count == 0 ? Array.Empty<(int, string)>() : list.ToArray();
        }
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

        var snapshot = _watchedSnapshot;
        if (snapshot.Length == 0) return;

        long nowMs = Environment.TickCount64;
        foreach (var (vk, label) in snapshot)
        {
            short state = GetAsyncKeyState(vk);
            bool isDown = (state & 0x8000) != 0;
            bool tappedSinceLast = (state & 0x0001) != 0;
            _wasKeyDown.TryGetValue(vk, out bool wasDown);
            _wasKeyDown[vk] = isDown;

            if ((isDown && !wasDown) || (!isDown && tappedSinceLast))
            {
                if (!_lastKeyFiredMs.TryGetValue(vk, out long lastMs) || nowMs - lastMs >= 350)
                {
                    _lastKeyFiredMs[vk] = nowMs;
                    WatchedKeyPressed?.Invoke(label);
                }
            }
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

/// <summary>
/// Watches the user's Downloads folder for in-progress browser/app downloads (.crdownload, .part, etc.)
/// and completed downloads, reporting live size/speed and finished file details.
/// </summary>
public sealed class DownloadCardSource : IDisposable
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".opdownload", ".download", ".tmp", ".partial", ".aria2", ".!ut"
    };

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    private sealed class ActiveDownload
    {
        public string TempPath { get; }
        public long LastBytes { get; set; } = -1;
        public long LastTickMs { get; set; }
        public double SmoothedSpeed { get; set; }
        public string ResolvedName { get; set; } = string.Empty;
        public bool Resolving { get; set; }

        public ActiveDownload(string tempPath, long nowMs)
        {
            TempPath = tempPath;
            LastTickMs = nowMs;
        }
    }

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _pollTimer;
    private FileSystemWatcher? _watcher;
    private string _folder = string.Empty;
    private readonly Dictionary<string, ActiveDownload> _activeDownloads = new(StringComparer.OrdinalIgnoreCase);
    private string _lastCompletedPath = string.Empty;
    private long _lastCompletedTickMs;
    private static readonly Regex UnconfirmedRegex = new(@"^Unconfirmed\s+\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public event Action<int, string, long, double>? Progress;
    public event Action<string, long>? Completed;
    public event Action? Cancelled;

    public DownloadCardSource(DispatcherQueue queue)
    {
        _queue = queue;
        _pollTimer = queue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(650);
        _pollTimer.IsRepeating = true;
        _pollTimer.Tick += (_, _) => PollActiveTemp();
    }

    public static string GetDefaultDownloadsFolder()
    {
        try
        {
            Guid g = FolderIdDownloads;
            if (SHGetKnownFolderPath(ref g, 0, nint.Zero, out nint pPath) == 0 && pPath != nint.Zero)
            {
                try
                {
                    string? path = Marshal.PtrToStringUni(pPath);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pPath);
                }
            }
        }
        catch { }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public void Start()
    {
        Stop();
        try
        {
            _folder = GetDefaultDownloadsFolder();
            if (!Directory.Exists(_folder)) return;

            _watcher = new FileSystemWatcher(_folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Created += (_, e) => OnFsEvent(e.FullPath, isDelete: false);
            _watcher.Changed += (_, e) => OnFsEvent(e.FullPath, isDelete: false);
            _watcher.Renamed += (_, e) => OnRenamedEvent(e.OldFullPath, e.FullPath);
            _watcher.Deleted += (_, e) => OnFsEvent(e.FullPath, isDelete: true);
            _watcher.Error += (_, _) => _queue.TryEnqueue(Start);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] downloads watcher failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _activeDownloads.Clear();
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private static bool IsTempFile(string path) =>
        TempExtensions.Contains(Path.GetExtension(path));

    private static bool IsIgnoredFile(string path)
    {
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
        try
        {
            if (Directory.Exists(path)) return true;
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Hidden) != 0) return true;
        }
        catch { }
        return false;
    }

    private void OnRenamedEvent(string oldPath, string newPath)
    {
        _queue.TryEnqueue(() =>
        {
            if (IsIgnoredFile(newPath)) return;
            if (IsTempFile(newPath))
            {
                TrackTemp(newPath);
                return;
            }

            // Browser renamed temp file -> final file, or new completed file appeared
            _activeDownloads.Remove(oldPath);
            if (_activeDownloads.Count == 0) _pollTimer.Stop();
            ReportCompleted(newPath);
        });
    }

    private void OnFsEvent(string path, bool isDelete)
    {
        _queue.TryEnqueue(() =>
        {
            if (isDelete)
            {
                if (_activeDownloads.Remove(path) && _activeDownloads.Count == 0)
                {
                    _pollTimer.Stop();
                    Cancelled?.Invoke();
                }
                return;
            }

            if (IsIgnoredFile(path)) return;

            if (IsTempFile(path))
            {
                TrackTemp(path);
                return;
            }

            // Firefox creates a 0-byte target file alongside <name>.part; wait for .part to finish.
            if (File.Exists(path + ".part")) return;

            PruneMissingTempDownloads();
            ReportCompleted(path);
        });
    }

    private void TrackTemp(string path)
    {
        bool isNew = !_activeDownloads.ContainsKey(path);
        if (isNew)
        {
            _activeDownloads[path] = new ActiveDownload(path, Environment.TickCount64);
        }
        if (!_pollTimer.IsRunning) _pollTimer.Start();
        if (isNew) PollActiveTemp();
    }

    private void PruneMissingTempDownloads()
    {
        foreach (string key in _activeDownloads.Keys.ToList())
        {
            if (!File.Exists(key))
            {
                _activeDownloads.Remove(key);
            }
        }
        if (_activeDownloads.Count == 0)
        {
            _pollTimer.Stop();
        }
    }

    private void PollActiveTemp()
    {
        PruneMissingTempDownloads();
        if (_activeDownloads.Count == 0) return;

        try
        {
            long now = Environment.TickCount64;
            long totalBytes = 0;
            double totalSpeed = 0;
            var names = new List<string>();

            foreach (var dl in _activeDownloads.Values)
            {
                long bytes = new FileInfo(dl.TempPath).Length;
                if (dl.LastBytes >= 0)
                {
                    double dt = (now - dl.LastTickMs) / 1000.0;
                    if (dt >= 0.2 && bytes >= dl.LastBytes)
                    {
                        double instant = (bytes - dl.LastBytes) / dt;
                        dl.SmoothedSpeed = dl.SmoothedSpeed <= 0 ? instant : (dl.SmoothedSpeed * 0.55 + instant * 0.45);
                    }
                }
                dl.LastBytes = bytes;
                dl.LastTickMs = now;
                totalBytes += bytes;
                totalSpeed += dl.SmoothedSpeed;

                string displayName = Path.GetFileNameWithoutExtension(dl.TempPath);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = Path.GetFileName(dl.TempPath);

                if (UnconfirmedRegex.IsMatch(displayName))
                {
                    if (!string.IsNullOrWhiteSpace(dl.ResolvedName))
                    {
                        displayName = dl.ResolvedName;
                    }
                    else
                    {
                        if (!dl.Resolving)
                        {
                            dl.Resolving = true;
                            string tempPath = dl.TempPath;
                            _ = Task.Run(() =>
                            {
                                string? resolved = TryResolveChromiumTargetName(tempPath);
                                _queue.TryEnqueue(() =>
                                {
                                    if (_activeDownloads.TryGetValue(tempPath, out var active) &&
                                        !string.IsNullOrWhiteSpace(resolved))
                                    {
                                        active.ResolvedName = resolved;
                                    }
                                    if (_activeDownloads.TryGetValue(tempPath, out var a2))
                                    {
                                        a2.Resolving = false;
                                    }
                                });
                            });
                        }
                        displayName = string.Empty;
                    }
                }

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    names.Add(displayName);
                }
            }

            string combinedNames = string.Join(", ", names);
            Progress?.Invoke(_activeDownloads.Count, combinedNames, totalBytes, totalSpeed);
        }
        catch { }
    }

    private static string? TryResolveChromiumTargetName(string tempPath)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] userDataRoots =
        {
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"),
            Path.Combine(localAppData, "Vivaldi", "User Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opera Software", "Opera Stable")
        };

        var historyFiles = new List<FileInfo>();
        foreach (string root in userDataRoots)
        {
            if (!Directory.Exists(root)) continue;
            string direct = Path.Combine(root, "History");
            if (File.Exists(direct)) historyFiles.Add(new FileInfo(direct));
            string def = Path.Combine(root, "Default", "History");
            if (File.Exists(def)) historyFiles.Add(new FileInfo(def));
            try
            {
                foreach (string dir in Directory.GetDirectories(root, "Profile *"))
                {
                    string pHist = Path.Combine(dir, "History");
                    if (File.Exists(pHist)) historyFiles.Add(new FileInfo(pHist));
                }
            }
            catch { }
        }

        string tempFileName = Path.GetFileName(tempPath);
        string tmpCopy = Path.Combine(Path.GetTempPath(), $"applock_dl_{Guid.NewGuid():N}.db");
        foreach (var hf in historyFiles.OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                using (var src = new FileStream(hf.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(tmpCopy, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.CopyTo(dst);
                }

                using var conn = new SqliteConnection($"Data Source={tmpCopy};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT target_path FROM downloads WHERE current_path = @p OR current_path LIKE @like ORDER BY id DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("@p", tempPath);
                cmd.Parameters.AddWithValue("@like", "%" + tempFileName);
                var result = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(result))
                {
                    string name = Path.GetFileName(result);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch { }
            finally
            {
                try { if (File.Exists(tmpCopy)) File.Delete(tmpCopy); } catch { }
            }
        }
        return null;
    }

    private void ReportCompleted(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            long now = Environment.TickCount64;
            if (string.Equals(_lastCompletedPath, path, StringComparison.OrdinalIgnoreCase) &&
                now - _lastCompletedTickMs < 2500)
            {
                return;
            }

            long bytes = new FileInfo(path).Length;
            if (bytes <= 0) return;

            _lastCompletedPath = path;
            _lastCompletedTickMs = now;
            Completed?.Invoke(path, bytes);
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Polls Windows Explorer file operation windows (class "OperationStatusWindow")
/// to report live copy/move progress percentage and completion to the island.
/// </summary>
public sealed class FileCopyCardSource : IDisposable
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, nint lParam,
        uint fuFlags, uint uTimeout, out nint lpdwResult);

    private const uint PBM_GETPOS = 0x0408;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private static readonly Regex PercentRegex = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private bool _enabled = true;
    private nint _activeHwnd;
    private int _lastPercent;
    private string _lastOpTitle = "Copying files...";
    private long _startTickMs;
    private bool _polling;

    public event Action<string, string, int>? Progress;
    public event Action<string>? Completed;
    public event Action? Cancelled;

    public FileCopyCardSource(DispatcherQueue queue)
    {
        _queue = queue;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(550);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = PollAsync();
    }

    public void Start(bool enabled)
    {
        _enabled = enabled;
        if (enabled && !_timer.IsRunning) _timer.Start();
        else if (!enabled) Stop();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            if (!_timer.IsRunning) _timer.Start();
        }
        else
        {
            _timer.Stop();
            _activeHwnd = nint.Zero;
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _activeHwnd = nint.Zero;
    }

    private async Task PollAsync()
    {
        if (!_enabled || _polling) return;
        _polling = true;
        try
        {
            var result = await Task.Run(ScanOperationWindow).ConfigureAwait(false);
            _queue.TryEnqueue(() => ApplyScanResult(result));
        }
        catch { }
        finally
        {
            _polling = false;
        }
    }

    private (nint Hwnd, string Title, string Detail, int Percent) ScanOperationWindow()
    {
        nint foundHwnd = nint.Zero;
        string foundTitle = string.Empty;
        var sbClass = new StringBuilder(64);
        var sbText = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            sbClass.Clear();
            GetClassName(hwnd, sbClass, sbClass.Capacity);
            if (sbClass.ToString() == "OperationStatusWindow")
            {
                sbText.Clear();
                GetWindowText(hwnd, sbText, sbText.Capacity);
                foundHwnd = hwnd;
                foundTitle = sbText.ToString();
                return false;
            }
            return true;
        }, nint.Zero);

        if (foundHwnd == nint.Zero)
            return (nint.Zero, string.Empty, string.Empty, -1);

        int pct = -1;
        var m = PercentRegex.Match(foundTitle);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int parsed))
        {
            pct = Math.Clamp(parsed, 0, 100);
        }

        string opLabel = "Copying...";
        string detail = string.Empty;
        if (foundTitle.Contains("Moving", StringComparison.OrdinalIgnoreCase)) opLabel = "Moving...";
        else if (foundTitle.Contains("Deleting", StringComparison.OrdinalIgnoreCase)) opLabel = "Deleting...";
        else if (foundTitle.Contains("Paused", StringComparison.OrdinalIgnoreCase)) opLabel = "Paused";

        // Inspect child controls for progress bar or descriptive text
        EnumChildWindows(foundHwnd, (child, _) =>
        {
            sbClass.Clear();
            GetClassName(child, sbClass, sbClass.Capacity);
            string cls = sbClass.ToString();
            if (cls == "msctls_progress32" && pct < 0)
            {
                if (SendMessageTimeout(child, PBM_GETPOS, nint.Zero, nint.Zero, SMTO_ABORTIFHUNG, 40, out nint pos) != nint.Zero)
                {
                    pct = Math.Clamp((int)pos, 0, 100);
                }
            }
            else if (cls == "Static" || cls == "DirectUIHWND")
            {
                sbText.Clear();
                GetWindowText(child, sbText, sbText.Capacity);
                string txt = sbText.ToString().Trim();
                if (!string.IsNullOrEmpty(txt))
                {
                    if (txt.Contains("Moving", StringComparison.OrdinalIgnoreCase)) opLabel = "Moving...";
                    else if (txt.Contains("Copying", StringComparison.OrdinalIgnoreCase)) opLabel = "Copying...";
                    else if (detail.Length == 0 && txt.Length > 2 && !txt.Contains("complete", StringComparison.OrdinalIgnoreCase))
                    {
                        detail = txt;
                    }
                }
            }
            return true;
        }, nint.Zero);

        if (pct < 0) pct = 0;
        return (foundHwnd, opLabel, detail, pct);
    }

    private void ApplyScanResult((nint Hwnd, string Title, string Detail, int Percent) res)
    {
        if (!_enabled) return;

        if (res.Hwnd != nint.Zero)
        {
            if (_activeHwnd != res.Hwnd)
            {
                _activeHwnd = res.Hwnd;
                _startTickMs = Environment.TickCount64;
            }
            _lastPercent = res.Percent;
            _lastOpTitle = res.Title;
            Progress?.Invoke(res.Title, res.Detail, res.Percent);
        }
        else if (_activeHwnd != nint.Zero)
        {
            long elapsed = Environment.TickCount64 - _startTickMs;
            bool finished = _lastPercent >= 90 || elapsed >= 1200;
            string doneTitle = _lastOpTitle.StartsWith("Moving", StringComparison.OrdinalIgnoreCase)
                ? "Move complete"
                : "Copy complete";
            _activeHwnd = nint.Zero;
            _lastPercent = 0;

            if (finished)
                Completed?.Invoke(doneTitle);
            else
                Cancelled?.Invoke();
        }
    }

    public void Dispose()
    {
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
