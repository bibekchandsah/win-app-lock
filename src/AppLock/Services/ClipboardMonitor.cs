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
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const nuint DEBOUNCE_TIMER_ID = 42;
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
    private readonly object _stateLock = new();

    private uint _cfHtml;
    private uint _cfPng;
    private uint _cfExclude;
    private uint _cfCanInclude;
    private uint _cfCanUpload;

    public event EventHandler<ClipboardItem>? ItemCaptured;

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

        readyEvent.Set();

        while (GetMessage(out MSG msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        RemoveClipboardFormatListener(_hwnd);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
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
        if (!settings.Enabled || IsPaused)
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
            for (int i = 0; i < 5; i++)
            {
                if (OpenClipboard(_hwnd))
                {
                    opened = true;
                    break;
                }
                Thread.Sleep(20);
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

                if (_cfCanUpload != 0 && IsClipboardFormatAvailable(_cfCanUpload) && GetDwordFormat(_cfCanUpload) == 0)
                {
                    Debug.WriteLine("[ClipboardMonitor] CanUploadToCloudClipboard = 0, skipping.");
                    return;
                }

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

                // Check ignored apps
                if (settings.IgnoredApps.Any(app => string.Equals(app, sourceProcess, StringComparison.OrdinalIgnoreCase)))
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

                // 2. Images capture (CF_DIB, CF_DIBV5, PNG)
                if (settings.CaptureImages && (IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBV5) || (_cfPng != 0 && IsClipboardFormatAvailable(_cfPng))))
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
            Pinned = false
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
                            return new System.Drawing.Bitmap(ms);
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
                                int biBitCount = Marshal.ReadInt16(pDib, 14);
                                int biCompression = Marshal.ReadInt32(pDib, 16);
                                int biClrUsed = Marshal.ReadInt32(pDib, 32);

                                int colorTableSize = 0;
                                if (biBitCount <= 8)
                                {
                                    colorTableSize = (biClrUsed != 0 ? biClrUsed : (1 << biBitCount)) * 4;
                                }
                                else if (biCompression == 3) // BI_BITFIELDS
                                {
                                    colorTableSize = 12;
                                }

                                uint bOffBits = 14 + 40 + (uint)colorTableSize;
                                uint bTotalSize = 14 + (uint)dibSize;

                                byte[] fullBmp = new byte[bTotalSize];
                                fullBmp[0] = 0x42; // 'B'
                                fullBmp[1] = 0x4D; // 'M'
                                BitConverter.GetBytes(bTotalSize).CopyTo(fullBmp, 2);
                                BitConverter.GetBytes(bOffBits).CopyTo(fullBmp, 10);
                                Marshal.Copy(pDib, fullBmp, 14, (int)dibSize);

                                using var ms = new MemoryStream(fullBmp);
                                return new System.Drawing.Bitmap(ms);
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

        // 3. Fallback to Windows Forms Clipboard on STA thread
        try
        {
            if (System.Windows.Forms.Clipboard.ContainsImage())
            {
                var img = System.Windows.Forms.Clipboard.GetImage();
                if (img != null) return new System.Drawing.Bitmap(img);
            }
        }
        catch { }

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

    #endregion
}

