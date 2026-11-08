using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AppLock.Models;

namespace AppLock.Services;

public sealed class ClipboardMonitor : IDisposable
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_WTSSESSION_CHANGE = 0x02B1;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint WM_APP_INSTALL_WINV = 0x8001;
    private const uint WM_APP_UNINSTALL_WINV = 0x8002;
    private const uint WM_APP_REGISTER_HOTKEY = 0x8003;

    private const int NOTIFY_FOR_THIS_SESSION = 0;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUMESUSPEND = 0x0007;

    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_V = 0x56;

    private const nuint DEBOUNCE_TIMER_ID = 42;
    private const int POPUP_HOTKEY_ID = 9001;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint CF_HDROP = 15;

    private readonly WNDPROC _wndProcDelegate;
    private Thread? _thread;
    private nint _hwnd;
    private uint _lastSequenceNumber;
    private bool _ignoreNext;
    private DateTime? _pausedUntil;
    private bool _isRunning;
    private bool _isHotkeyRegistered;
    private volatile bool _isSessionLocked;
    private nint _llHook = nint.Zero;
    private LowLevelKeyboardProc? _llProc;
    private bool _winVTriggered;
    private static Mutex? _singleInstanceMutex;
    private readonly object _stateLock = new();

    private uint _cfHtml;
    private uint _cfPng;
    private uint _cfExclude;
    private uint _cfCanInclude;
    private uint _cfCanUpload;

    public event EventHandler<ClipboardItem>? ItemCaptured;
    public event EventHandler? HotkeyTriggered;
    public event EventHandler<bool>? SessionLockChanged;

    public bool IsSessionLocked => _isSessionLocked;
    public bool IsRunning => _isRunning;

    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                if (_pausedUntil.HasValue)
                {
                    if (_pausedUntil.Value > DateTime.Now)
                    {
                        return true;
                    }
                    _pausedUntil = null;
                }
                return false;
            }
        }
    }

    public DateTime? PausedUntil
    {
        get
        {
            lock (_stateLock)
            {
                return _pausedUntil;
            }
        }
    }

    public ClipboardMonitor()
    {
        _wndProcDelegate = WndProc;
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                if (_singleInstanceMutex == null)
                {
                    _singleInstanceMutex = new Mutex(false, @"Local\AppLock_ClipboardManager_Mutex");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClipboardMonitor] Single-instance mutex check: {ex.Message}");
            }

            var readyEvent = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                try
                {
                    RunMessageLoop(readyEvent);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClipboardMonitor] Thread exception: {ex.Message}");
                }
                finally
                {
                    _isRunning = false;
                    _hwnd = nint.Zero;
                }
            })
            {
                IsBackground = true,
                Name = "ClipboardMonitorThread"
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            readyEvent.Wait(2000);
            readyEvent.Dispose();
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_isRunning) return;
            _isRunning = false;

            if (_hwnd != nint.Zero)
            {
                if (_isHotkeyRegistered)
                {
                    UnregisterHotKey(_hwnd, POPUP_HOTKEY_ID);
                    _isHotkeyRegistered = false;
                }
                UninstallWinVHook();
                PostMessage(_hwnd, WM_CLOSE, nint.Zero, nint.Zero);
            }

            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(1000);
                _thread = null;
            }
        }
    }

    public void SetPause(TimeSpan duration)
    {
        lock (_stateLock)
        {
            _pausedUntil = DateTime.Now.Add(duration);
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            _pausedUntil = null;
        }
    }

    public void IgnoreNext()
    {
        lock (_stateLock)
        {
            _ignoreNext = true;
        }
    }

    public bool RegisterHotkey(string hotkeyText)
    {
        lock (_stateLock)
        {
            if (_hwnd == nint.Zero)
            {
                return HotkeyParser.TryParse(hotkeyText, out _, out _);
            }

            if (string.IsNullOrWhiteSpace(hotkeyText))
            {
                return SendMessage(_hwnd, WM_APP_REGISTER_HOTKEY, nint.Zero, nint.Zero) != nint.Zero;
            }

            if (!HotkeyParser.TryParse(hotkeyText, out uint mods, out uint vk))
            {
                return false;
            }

            return SendMessage(_hwnd, WM_APP_REGISTER_HOTKEY, (nint)mods, (nint)vk) != nint.Zero;
        }
    }

    public void UpdateWinVHook(bool enable)
    {
        if (_hwnd != nint.Zero)
        {
            PostMessage(_hwnd, enable ? WM_APP_INSTALL_WINV : WM_APP_UNINSTALL_WINV, nint.Zero, nint.Zero);
        }
    }

    private bool RegisterHotkeyInternal(uint mods, uint vk)
    {
        if (_isHotkeyRegistered && _hwnd != nint.Zero)
        {
            UnregisterHotKey(_hwnd, POPUP_HOTKEY_ID);
            _isHotkeyRegistered = false;
        }

        if (mods == 0 && vk == 0)
        {
            return true;
        }

        bool success = RegisterHotKey(_hwnd, POPUP_HOTKEY_ID, mods | HotkeyParser.MOD_NOREPEAT, vk);
        if (success)
        {
            _isHotkeyRegistered = true;
            Debug.WriteLine($"[ClipboardMonitor] Registered global hotkey mods={mods}, vk={vk}");
            return true;
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[ClipboardMonitor] Failed to register hotkey mods={mods}, vk={vk}, error: {err}");
            return false;
        }
    }

    private void RunMessageLoop(ManualResetEventSlim readyEvent)
    {
        string className = "AppLock_ClipboardMonitor_" + Guid.NewGuid().ToString("N");

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = className
        };

        ushort regResult = RegisterClassEx(ref wndClass);
        if (regResult == 0)
        {
            Debug.WriteLine($"[ClipboardMonitor] Failed to register window class. Error: {Marshal.GetLastWin32Error()}");
            readyEvent.Set();
            return;
        }

        // HWND_MESSAGE = ((HWND)-3)
        _hwnd = CreateWindowEx(
            0,
            className,
            "AppLockClipboardMessageWindow",
            0,
            0, 0, 0, 0,
            new nint(-3),
            nint.Zero,
            nint.Zero,
            nint.Zero);

        if (_hwnd == nint.Zero)
        {
            Debug.WriteLine($"[ClipboardMonitor] Failed to create message window. Error: {Marshal.GetLastWin32Error()}");
            readyEvent.Set();
            return;
        }

        // Register custom formats
        _cfHtml = RegisterClipboardFormatW("HTML Format");
        _cfPng = RegisterClipboardFormatW("PNG");
        _cfExclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _cfCanInclude = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _cfCanUpload = RegisterClipboardFormatW("CanUploadToCloudClipboard");

        bool listenerAdded = AddClipboardFormatListener(_hwnd);
        Debug.WriteLine($"[ClipboardMonitor] Window created (HWND: {_hwnd}). AddClipboardFormatListener: {listenerAdded}");

        _lastSequenceNumber = GetClipboardSequenceNumber();

        // Register initial hotkey
        var settings = ServiceContainer.Settings.Clipboard;
        if (!string.IsNullOrEmpty(settings.Hotkey) && HotkeyParser.TryParse(settings.Hotkey, out uint mods, out uint vk))
        {
            RegisterHotkeyInternal(mods, vk);
        }

        if (settings.OverrideWinV)
        {
            InstallWinVHook();
        }

        WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);

        readyEvent.Set();

        while (GetMessage(out MSG msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        WTSUnRegisterSessionNotification(_hwnd);
        UninstallWinVHook();
        RemoveClipboardFormatListener(_hwnd);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                return nint.Zero;

            case WM_HOTKEY:
                if (wParam == (nint)POPUP_HOTKEY_ID)
                {
                    if (!_isSessionLocked)
                    {
                        HotkeyTriggered?.Invoke(this, EventArgs.Empty);
                    }
                    return nint.Zero;
                }
                break;

            case WM_WTSSESSION_CHANGE:
                if (wParam == (nint)WTS_SESSION_LOCK)
                {
                    _isSessionLocked = true;
                    Debug.WriteLine("[ClipboardMonitor] Session locked - pausing capture & hiding popup");
                    SessionLockChanged?.Invoke(this, true);
                }
                else if (wParam == (nint)WTS_SESSION_UNLOCK)
                {
                    _isSessionLocked = false;
                    Debug.WriteLine("[ClipboardMonitor] Session unlocked - resuming capture");
                    SessionLockChanged?.Invoke(this, false);
                }
                return nint.Zero;

            case WM_POWERBROADCAST:
                if (wParam == (nint)PBT_APMRESUMEAUTOMATIC || wParam == (nint)PBT_APMRESUMESUSPEND)
                {
                    Debug.WriteLine("[ClipboardMonitor] Power resume detected, refreshing listeners");
                    RemoveClipboardFormatListener(_hwnd);
                    AddClipboardFormatListener(_hwnd);
                    _lastSequenceNumber = GetClipboardSequenceNumber();

                    var s = ServiceContainer.Settings.Clipboard;
                    if (!string.IsNullOrEmpty(s.Hotkey) && HotkeyParser.TryParse(s.Hotkey, out uint m, out uint v))
                    {
                        RegisterHotkeyInternal(m, v);
                    }
                    if (s.OverrideWinV)
                    {
                        InstallWinVHook();
                    }
                }
                return nint.Zero;

            case WM_APP_REGISTER_HOTKEY:
                return RegisterHotkeyInternal((uint)wParam, (uint)lParam) ? (nint)1 : (nint)0;

            case WM_APP_INSTALL_WINV:
                InstallWinVHook();
                return nint.Zero;

            case WM_APP_UNINSTALL_WINV:
                UninstallWinVHook();
                return nint.Zero;

            case WM_TIMER:
                if (wParam == (nint)DEBOUNCE_TIMER_ID)
                {
                    KillTimer(hWnd, DEBOUNCE_TIMER_ID);
                    ReadAndProcessClipboard();
                }
                return nint.Zero;

            case WM_CLOSE:
                DestroyWindow(hWnd);
                return nint.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return nint.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnClipboardUpdate()
    {
        uint currentSeq = GetClipboardSequenceNumber();
        if (currentSeq == _lastSequenceNumber)
        {
            return;
        }
        _lastSequenceNumber = currentSeq;

        lock (_stateLock)
        {
            if (_ignoreNext)
            {
                _ignoreNext = false;
                return;
            }
        }

        var settings = ServiceContainer.Settings.Clipboard;
        if (!settings.Enabled || IsPaused || _isSessionLocked)
        {
            return;
        }

        // Reset debounce timer to ~100 ms
        SetTimer(_hwnd, DEBOUNCE_TIMER_ID, 100, nint.Zero);
    }

    private void ReadAndProcessClipboard()
    {
        try
        {
            var settings = ServiceContainer.Settings.Clipboard;
            if (!settings.Enabled || IsPaused) return;

            bool opened = false;
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(_hwnd))
                {
                    opened = true;
                    break;
                }
                Thread.Sleep(30);
            }

            if (!opened)
            {
                Debug.WriteLine("[ClipboardMonitor] Cannot open clipboard (held by another app).");
                return;
            }

            try
            {
                // Check exclusion formats (privacy / passwords)
                if (_cfExclude != 0 && IsClipboardFormatAvailable(_cfExclude))
                {
                    Debug.WriteLine("[ClipboardMonitor] ExcludeClipboardContentFromMonitorProcessing format detected, skipping.");
                    return;
                }

                if (_cfCanInclude != 0 && IsClipboardFormatAvailable(_cfCanInclude) && GetDwordFormat(_cfCanInclude) == 0)
                {
                    Debug.WriteLine("[ClipboardMonitor] CanIncludeInClipboardHistory = 0, skipping.");
                    return;
                }

                // Note: _cfCanUpload (CanUploadToCloudClipboard = 0) is intentionally NOT checked here,
                // because screen capture tools (Snipping Tool, Win+Shift+S, PowerToys) set CanUploadToCloudClipboard = 0
                // so local screenshots do not sync to cloud, but they MUST still be saved locally.

                // Identify source process
                string sourceProcess = "Unknown";
                string sourceTitle = "";
                nint ownerHwnd = GetClipboardOwner();
                if (ownerHwnd == nint.Zero)
                {
                    ownerHwnd = GetForegroundWindow();
                }

                if (ownerHwnd != nint.Zero)
                {
                    GetWindowThreadProcessId(ownerHwnd, out uint pid);
                    if (pid != 0)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            sourceProcess = proc.ProcessName + ".exe";
                            sourceTitle = proc.MainWindowTitle;
                        }
                        catch { }
                    }
                }

                // Check ignored apps (normalize with/without .exe, trim, case-insensitive)
                bool isIgnored = settings.IgnoredApps.Any(app =>
                {
                    if (string.IsNullOrWhiteSpace(app)) return false;
                    string normApp = app.Trim().ToLowerInvariant();
                    if (!normApp.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        normApp += ".exe";
                    }
                    string normSource = sourceProcess.Trim().ToLowerInvariant();
                    return string.Equals(normApp, normSource, StringComparison.OrdinalIgnoreCase);
                });

                if (isIgnored)
                {
                    Debug.WriteLine($"[ClipboardMonitor] Ignored app: {sourceProcess}, skipping.");
                    return;
                }

                // 1. Files capture (CF_HDROP)
                if (settings.CaptureFiles && IsClipboardFormatAvailable(CF_HDROP))
                {
                    if (TryCaptureFiles(sourceProcess, sourceTitle, settings))
                    {
                        return;
                    }
                }

                // 2. Images capture (CF_DIB, CF_DIBV5, PNG, CF_BITMAP)
                if (settings.CaptureImages && (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5) || (_cfPng != 0 && IsClipboardFormatAvailable(_cfPng)) || IsClipboardFormatAvailable(2 /* CF_BITMAP */)))
                {
                    if (TryCaptureImage(sourceProcess, sourceTitle, settings))
                    {
                        return;
                    }
                }

                // 3. Text capture (CF_UNICODETEXT)
                if (settings.CaptureText && IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    TryCaptureText(sourceProcess, sourceTitle, settings);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardMonitor] ReadAndProcessClipboard error: {ex.Message}");
        }
    }

    private bool TryCaptureFiles(string sourceProcess, string sourceTitle, ClipboardSettings settings)
    {
        nint hDrop = GetClipboardData(CF_HDROP);
        if (hDrop == nint.Zero) return false;

        uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        if (fileCount == 0) return false;

        var paths = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder(1024);
        for (uint i = 0; i < fileCount; i++)
        {
            sb.Clear();
            if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0)
            {
                paths.Add(sb.ToString());
            }
        }

        if (paths.Count == 0) return false;

        string json = System.Text.Json.JsonSerializer.Serialize(paths);
        using var sha = SHA256.Create();
        string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        string firstFile = Path.GetFileName(paths[0]);
        string preview = paths.Count > 1 ? $"{firstFile} (+{paths.Count - 1} more)" : firstFile;

        var fileItem = new ClipboardItem
        {
            Type = "files",
            ContentString = json,
            PreviewString = preview,
            Hash = hash,
            SizeBytes = json.Length,
            SourceProcess = sourceProcess,
            SourceTitle = sourceTitle,
            UseCount = 1,
            Pinned = false
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await ServiceContainer.ClipboardStore.InsertOrUpdateAsync(fileItem, settings.MaxItems);
                ItemCaptured?.Invoke(this, fileItem);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClipboardMonitor] Insert files error: {ex.Message}");
            }
        });
        return true;
    }

    private bool TryCaptureImage(string sourceProcess, string sourceTitle, ClipboardSettings settings)
    {
        var img = TryGetClipboardImage();
        if (img == null) return false;

        using (img)
        {
            int origW = img.Width;
            int origH = img.Height;

            using var pngMs = new MemoryStream();
            img.Save(pngMs, System.Drawing.Imaging.ImageFormat.Png);
            byte[] pngBytes = pngMs.ToArray();

            if (pngBytes.Length > (long)settings.MaxImageMb * 1024 * 1024)
            {
                Debug.WriteLine($"[ClipboardMonitor] Image size {pngBytes.Length}B exceeds limit {settings.MaxImageMb}MB, skipping.");
                return false;
            }

            using var sha = SHA256.Create();
            string hash = Convert.ToHexString(sha.ComputeHash(pngBytes)).ToLowerInvariant();

            byte[] thumbBytes = CreateThumbnailJpeg(img, 160);

            string imagesDir = Path.Combine(ServiceContainer.ClipboardStore.StoragePath, "images");
            string thumbsDir = Path.Combine(ServiceContainer.ClipboardStore.StoragePath, "thumbs");
            Directory.CreateDirectory(imagesDir);
            Directory.CreateDirectory(thumbsDir);

            string imagePath = Path.Combine(imagesDir, $"{hash}.png");
            string thumbPath = Path.Combine(thumbsDir, $"{hash}.jpg");

            if (!File.Exists(imagePath))
            {
                File.WriteAllBytes(imagePath, pngBytes);
            }
            if (!File.Exists(thumbPath))
            {
                File.WriteAllBytes(thumbPath, thumbBytes);
            }

            var imgItem = new ClipboardItem
            {
                Type = "image",
                Content = null,
                PreviewString = $"Image ({origW} × {origH})",
                Hash = hash,
                ImagePath = imagePath,
                ThumbPath = thumbPath,
                Width = origW,
                Height = origH,
                SizeBytes = pngBytes.Length,
                SourceProcess = sourceProcess,
                SourceTitle = sourceTitle,
                UseCount = 1,
                Pinned = false
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await ServiceContainer.ClipboardStore.InsertOrUpdateAsync(imgItem, settings.MaxItems);
                    ItemCaptured?.Invoke(this, imgItem);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClipboardMonitor] Insert image error: {ex.Message}");
                }
            });
            return true;
        }
    }

    private void TryCaptureText(string sourceProcess, string sourceTitle, ClipboardSettings settings)
    {
        string? text = null;
        string? html = null;

        nint hData = GetClipboardData(CF_UNICODETEXT);
        if (hData != nint.Zero)
        {
            nint pData = GlobalLock(hData);
            if (pData != nint.Zero)
            {
                try
                {
                    text = Marshal.PtrToStringUni(pData);
                }
                finally
                {
                    GlobalUnlock(hData);
                }
            }
        }

        // Read HTML format if rich text is kept
        if (settings.KeepRichText && _cfHtml != 0 && IsClipboardFormatAvailable(_cfHtml))
        {
            nint hHtml = GetClipboardData(_cfHtml);
            if (hHtml != nint.Zero)
            {
                nint pHtml = GlobalLock(hHtml);
                if (pHtml != nint.Zero)
                {
                    try
                    {
                        nuint size = GlobalSize(hHtml);
                        if (size > 0)
                        {
                            byte[] buffer = new byte[(int)size];
                            Marshal.Copy(pHtml, buffer, 0, (int)size);
                            int nullIndex = Array.IndexOf(buffer, (byte)0);
                            int len = nullIndex >= 0 ? nullIndex : buffer.Length;
                            html = Encoding.UTF8.GetString(buffer, 0, len);
                        }
                    }
                    finally
                    {
                        GlobalUnlock(hHtml);
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        if (textBytes.Length > (long)settings.MaxTextKb * 1024)
        {
            Debug.WriteLine($"[ClipboardMonitor] Text size {textBytes.Length}B exceeds limit {settings.MaxTextKb}KB, skipping.");
            return;
        }

        using var sha = SHA256.Create();
        byte[] hashBytes = sha.ComputeHash(textBytes);
        string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        string preview = text.Length > 200 ? text[..200] : text;

        bool isSensitive = false;
        long? expiresAt = null;

        if (settings.DetectSensitive && SensitiveDetector.IsSensitive(text, sourceProcess, sourceTitle))
        {
            isSensitive = true;
            if (settings.SensitiveExpireSeconds > 0)
            {
                expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (settings.SensitiveExpireSeconds * 1000L);
            }
        }

        var item = new ClipboardItem
        {
            Type = "text",
            ContentString = text,
            PreviewString = preview,
            HtmlString = html,
            Hash = hash,
            SizeBytes = textBytes.Length,
            SourceProcess = sourceProcess,
            SourceTitle = sourceTitle,
            UseCount = 1,
            Pinned = false,
            IsSensitive = isSensitive,
            ExpiresAt = expiresAt
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await ServiceContainer.ClipboardStore.InsertOrUpdateAsync(item, settings.MaxItems);
                ItemCaptured?.Invoke(this, item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClipboardMonitor] Insert text error: {ex.Message}");
            }
        });
    }

    private static uint GetDwordFormat(uint format)
    {
        nint h = GetClipboardData(format);
        if (h == nint.Zero) return 0;
        nint ptr = GlobalLock(h);
        if (ptr == nint.Zero) return 0;
        try
        {
            return (uint)Marshal.ReadInt32(ptr);
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    private System.Drawing.Image? TryGetClipboardImage()
    {
        // 1. Try PNG registered format
        if (_cfPng != 0 && IsClipboardFormatAvailable(_cfPng))
        {
            nint hPng = GetClipboardData(_cfPng);
            if (hPng != nint.Zero)
            {
                nint pData = GlobalLock(hPng);
                if (pData != nint.Zero)
                {
                    try
                    {
                        nuint size = GlobalSize(hPng);
                        if (size > 0)
                        {
                            byte[] raw = new byte[(int)size];
                            Marshal.Copy(pData, raw, 0, (int)size);
                            using var ms = new MemoryStream(raw);
                            using var temp = new System.Drawing.Bitmap(ms);
                            return new System.Drawing.Bitmap(temp);
                        }
                    }
                    catch { }
                    finally
                    {
                        GlobalUnlock(hPng);
                    }
                }
            }
        }

        // 2. Try CF_DIB / CF_DIBV5 with BITMAPFILEHEADER reconstruction
        uint[] dibFormats = new[] { CF_DIBV5, CF_DIB };
        foreach (uint fmt in dibFormats)
        {
            if (IsClipboardFormatAvailable(fmt))
            {
                nint hDib = GetClipboardData(fmt);
                if (hDib != nint.Zero)
                {
                    nint pDib = GlobalLock(hDib);
                    if (pDib != nint.Zero)
                    {
                        try
                        {
                            nuint dibSize = GlobalSize(hDib);
                            if (dibSize >= 40)
                            {
                                int biHeaderSize = Marshal.ReadInt32(pDib, 0);
                                if (biHeaderSize <= 0) biHeaderSize = 40;
                                int biBitCount = Marshal.ReadInt16(pDib, 14);
                                int biCompression = Marshal.ReadInt32(pDib, 16);
                                int biClrUsed = Marshal.ReadInt32(pDib, 32);

                                int colorTableSize = 0;
                                if (biBitCount <= 8)
                                {
                                    colorTableSize = (biClrUsed != 0 ? biClrUsed : (1 << biBitCount)) * 4;
                                }
                                else if (biCompression == 3 && biHeaderSize == 40) // BI_BITFIELDS in BITMAPINFOHEADER only
                                {
                                    colorTableSize = 12;
                                }

                                uint bOffBits = 14 + (uint)biHeaderSize + (uint)colorTableSize;
                                uint bTotalSize = 14 + (uint)dibSize;

                                byte[] fullBmp = new byte[bTotalSize];
                                fullBmp[0] = 0x42; // 'B'
                                fullBmp[1] = 0x4D; // 'M'
                                BitConverter.GetBytes(bTotalSize).CopyTo(fullBmp, 2);
                                BitConverter.GetBytes(bOffBits).CopyTo(fullBmp, 10);
                                Marshal.Copy(pDib, fullBmp, 14, (int)dibSize);

                                using var ms = new MemoryStream(fullBmp);
                                using var temp = new System.Drawing.Bitmap(ms);
                                return new System.Drawing.Bitmap(temp);
                            }
                        }
                        catch { }
                        finally
                        {
                            GlobalUnlock(hDib);
                        }
                    }
                }
            }
        }

        // 3. Fallback to CF_BITMAP (HBITMAP)
        const uint CF_BITMAP = 2;
        if (IsClipboardFormatAvailable(CF_BITMAP))
        {
            nint hBmp = GetClipboardData(CF_BITMAP);
            if (hBmp != nint.Zero)
            {
                try
                {
                    return System.Drawing.Image.FromHbitmap(hBmp);
                }
                catch { }
            }
        }

        return null;
    }

    private static byte[] CreateThumbnailJpeg(System.Drawing.Image original, int maxDim = 160)
    {
        int origW = original.Width;
        int origH = original.Height;
        float ratio = Math.Min((float)maxDim / origW, (float)maxDim / origH);
        if (ratio > 1f) ratio = 1f;

        int thumbW = Math.Max(1, (int)(origW * ratio));
        int thumbH = Math.Max(1, (int)(origH * ratio));

        using var thumbBmp = new System.Drawing.Bitmap(thumbW, thumbH);
        using (var g = System.Drawing.Graphics.FromImage(thumbBmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(System.Drawing.Color.White);
            g.DrawImage(original, 0, 0, thumbW, thumbH);
        }

        using var ms = new MemoryStream();
        thumbBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return ms.ToArray();
    }

    public void Dispose()
    {
        Stop();
    }

    #region Win32 Interop

    private delegate nint WNDPROC(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(nint hWnd, nuint uIDEvent);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(nint hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private const byte VK_LCONTROL_BYTE = 0xA2;
    private const byte SC_LCONTROL_BYTE = 0x1D;
    private const uint KEYEVENTF_KEYUP_FLAG = 0x0002;

    private void InstallWinVHook()
    {
        if (_llHook != nint.Zero) return;
        _llProc = LowLevelKeyboardHookCallback;
        nint hMod = GetModuleHandle(null);
        _llHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llProc, hMod, 0);
        Debug.WriteLine($"[ClipboardMonitor] Win+V low-level hook installed: {_llHook != nint.Zero}");
    }

    private void UninstallWinVHook()
    {
        if (_llHook != nint.Zero)
        {
            UnhookWindowsHookEx(_llHook);
            _llHook = nint.Zero;
            _llProc = null;
            Debug.WriteLine("[ClipboardMonitor] Win+V low-level hook uninstalled");
        }
    }

    private nint LowLevelKeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = kb.vkCode;

            if (vk == (uint)VK_V)
            {
                bool lWin = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0;
                bool rWin = (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                if (lWin || rWin)
                {
                    bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                    if (!ctrl && !alt)
                    {
                        if (wParam == (nint)WM_KEYDOWN || wParam == (nint)WM_SYSKEYDOWN)
                        {
                            _winVTriggered = true;
                            SuppressStartMenu();

                            if (!_isSessionLocked)
                            {
                                HotkeyTriggered?.Invoke(this, EventArgs.Empty);
                            }
                        }

                        // Swallow both key down and key up for 'V'
                        return (nint)1;
                    }
                }
            }
            else if ((vk == (uint)VK_LWIN || vk == (uint)VK_RWIN) && (wParam == (nint)WM_KEYUP || wParam == (nint)WM_SYSKEYUP))
            {
                if (_winVTriggered)
                {
                    _winVTriggered = false;
                    SuppressStartMenu();
                }
            }
        }
        return CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private static void SuppressStartMenu()
    {
        try
        {
            // Send synthetic Left-Control key-down and key-up with scan code 0x1D.
            // This is the standard Windows Shell cancellation technique that prevents
            // explorer.exe from interpreting Win key release as a Start Menu activation.
            keybd_event(VK_LCONTROL_BYTE, SC_LCONTROL_BYTE, 0, 0);
            keybd_event(VK_LCONTROL_BYTE, SC_LCONTROL_BYTE, KEYEVENTF_KEYUP_FLAG, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardMonitor] SuppressStartMenu error: {ex.Message}");
        }
    }

    #endregion
}

