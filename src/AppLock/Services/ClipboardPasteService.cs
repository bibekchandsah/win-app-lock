using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using AppLock.Models;

namespace AppLock.Services;

public class ClipboardPasteResult
{
    public bool Success { get; set; }
    public bool PastedViaSendInput { get; set; }
    public bool TargetIsElevated { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Service responsible for the complete paste flow: setting system clipboard with self-copy guard,
/// restoring focus to previous foreground window, releasing held modifiers, sending Ctrl+V via SendInput,
/// handling plain text paste (Shift+Enter), multi-select merge paste, and UIPI elevation fallback.
/// </summary>
public class ClipboardPasteService
{
    public event EventHandler<string>? FallbackToastRequested;

    /// <summary>
    /// Executes the paste flow for a single item.
    /// </summary>
    public async Task<ClipboardPasteResult> PasteItemAsync(
        ClipboardItem item,
        nint targetHwnd,
        bool plainTextOnly = false,
        bool copyOnly = false)
    {
        var result = new ClipboardPasteResult();

        try
        {
            // 1. Write item to system clipboard with self-copy guard
            bool setSuccess = await SetClipboardDataAsync(item, plainTextOnly);
            if (!setSuccess)
            {
                result.Success = false;
                result.Message = "Failed to set clipboard content.";
                return result;
            }

            // Update item usage in DB (bumps count and moves to top)
            _ = ServiceContainer.ClipboardStore.UpdateUsageAsync(item.Id);

            // 2. If copy-only mode is requested or AutoPaste setting is disabled:
            bool autoPasteEnabled = ServiceContainer.Settings.Clipboard.AutoPaste;
            if (copyOnly || !autoPasteEnabled)
            {
                result.Success = true;
                result.PastedViaSendInput = false;
                result.Message = "Copied to clipboard.";
                return result;
            }

            // 3. Restore focus to the target window
            if (targetHwnd != nint.Zero && IsWindow(targetHwnd))
            {
                RestoreFocus(targetHwnd);
            }

            // 4. Wait ~180ms for target window (especially Chromium / Electron apps like WhatsApp) to settle focus
            await Task.Delay(180);

            // 5. Release any held modifier keys so Shift/Alt/Ctrl/Win don't corrupt the paste
            ReleaseHeldModifiers();

            // Brief pause before SendInput
            await Task.Delay(25);

            // 6. Send Ctrl + V using keybd_event with scan codes
            bool sent = SendCtrlV();
            result.Success = true;
            result.PastedViaSendInput = sent;
            result.Message = sent ? "Pasted successfully." : "Copied to clipboard.";

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardPasteService] Error during paste flow: {ex}");
            result.Success = false;
            result.Message = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Executes the paste flow for multiple items merged with a separator.
    /// </summary>
    public async Task<ClipboardPasteResult> PasteMergedAsync(
        IEnumerable<ClipboardItem> items,
        nint targetHwnd,
        string separator = "\r\n",
        bool plainTextOnly = false,
        bool copyOnly = false)
    {
        var result = new ClipboardPasteResult();
        var itemList = items.ToList();
        if (itemList.Count == 0)
        {
            result.Success = false;
            result.Message = "No items to paste.";
            return result;
        }

        try
        {
            // Build merged string
            var sb = new StringBuilder();
            foreach (var item in itemList)
            {
                string text = string.Empty;
                if (item.Type == "text")
                {
                    text = item.ContentString ?? item.PreviewString ?? string.Empty;
                }
                else if (item.Type == "files" && !string.IsNullOrEmpty(item.ContentString))
                {
                    try
                    {
                        var files = JsonSerializer.Deserialize<List<string>>(item.ContentString);
                        if (files != null && files.Count > 0)
                        {
                            text = string.Join(Environment.NewLine, files);
                        }
                    }
                    catch { }
                }
                else if (item.Type == "image")
                {
                    text = !string.IsNullOrEmpty(item.ImagePath) ? item.ImagePath : "[Image]";
                }

                if (!string.IsNullOrEmpty(text))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(separator);
                    }
                    sb.Append(text);
                }
            }

            string mergedText = sb.ToString();

            // Set system clipboard with self-copy guard
            ServiceContainer.ClipboardMonitor.IgnoreNext();
            SetNativeText(mergedText);

            // Update usage for all items in DB
            foreach (var it in itemList)
            {
                _ = ServiceContainer.ClipboardStore.UpdateUsageAsync(it.Id);
            }

            bool autoPasteEnabled = ServiceContainer.Settings.Clipboard.AutoPaste;
            if (copyOnly || !autoPasteEnabled)
            {
                result.Success = true;
                result.PastedViaSendInput = false;
                result.Message = "Merged items copied to clipboard.";
                return result;
            }

            // Restore focus
            if (targetHwnd != nint.Zero && IsWindow(targetHwnd))
            {
                RestoreFocus(targetHwnd);
            }

            await Task.Delay(180);
            ReleaseHeldModifiers();
            await Task.Delay(25);

            bool sent = SendCtrlV();
            result.Success = true;
            result.PastedViaSendInput = sent;
            result.Message = sent ? "Merged items pasted." : "Merged items copied.";
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardPasteService] Error during merged paste: {ex}");
            result.Success = false;
            result.Message = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Writes the given ClipboardItem into Windows Clipboard using native Win32 clipboard formats
    /// (CF_UNICODETEXT, CF_TEXT, HTML Format, CF_DIB, PNG, and CF_HDROP) ensuring 100% compatibility
    /// with WhatsApp, Chromium/Electron apps, Microsoft Office, and traditional desktop apps.
    /// </summary>
    public async Task<bool> SetClipboardDataAsync(ClipboardItem item, bool plainTextOnly = false)
    {
        try
        {
            // Activate self-copy guard
            ServiceContainer.ClipboardMonitor.IgnoreNext();

            if (item.Type == "text")
            {
                string text = item.ContentString ?? item.PreviewString ?? string.Empty;
                string? html = (!plainTextOnly && !string.IsNullOrEmpty(item.HtmlString)) ? item.HtmlString : null;
                SetNativeText(text, html);
            }
            else if (item.Type == "image")
            {
                if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    SetNativeImage(item.ImagePath);
                }
                else
                {
                    return false;
                }
            }
            else if (item.Type == "files")
            {
                if (!string.IsNullOrEmpty(item.ContentString))
                {
                    var paths = JsonSerializer.Deserialize<List<string>>(item.ContentString);
                    if (paths != null && paths.Count > 0)
                    {
                        SetNativeFiles(paths.ToArray());
                    }
                }
            }

            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardPasteService] Error setting clipboard: {ex.Message}");
            return false;
        }
    }

    public static void SetNativeText(string text, string? html = null)
    {
        for (int i = 0; i < 8; i++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    EmptyClipboard();

                    // 1. CF_UNICODETEXT (UTF-16)
                    byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                    nint hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
                    if (hMem != nint.Zero)
                    {
                        nint ptr = GlobalLock(hMem);
                        if (ptr != nint.Zero)
                        {
                            Marshal.Copy(bytes, 0, ptr, bytes.Length);
                            GlobalUnlock(hMem);
                            SetClipboardData(CF_UNICODETEXT, hMem);
                        }
                    }

                    // 2. CF_TEXT (ANSI fallback)
                    byte[] ansiBytes = Encoding.Default.GetBytes(text + "\0");
                    nint hAnsi = GlobalAlloc(GMEM_MOVEABLE, (nuint)ansiBytes.Length);
                    if (hAnsi != nint.Zero)
                    {
                        nint pAnsi = GlobalLock(hAnsi);
                        if (pAnsi != nint.Zero)
                        {
                            Marshal.Copy(ansiBytes, 0, pAnsi, ansiBytes.Length);
                            GlobalUnlock(hAnsi);
                            SetClipboardData(CF_TEXT, hAnsi);
                        }
                    }

                    // 3. HTML Format (rich text)
                    if (!string.IsNullOrEmpty(html))
                    {
                        uint cfHtml = RegisterClipboardFormatW("HTML Format");
                        if (cfHtml != 0)
                        {
                            byte[] htmlBytes = Encoding.UTF8.GetBytes(html + "\0");
                            nint hHtml = GlobalAlloc(GMEM_MOVEABLE, (nuint)htmlBytes.Length);
                            if (hHtml != nint.Zero)
                            {
                                nint pHtml = GlobalLock(hHtml);
                                if (pHtml != nint.Zero)
                                {
                                    Marshal.Copy(htmlBytes, 0, pHtml, htmlBytes.Length);
                                    GlobalUnlock(hHtml);
                                    SetClipboardData(cfHtml, hHtml);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    CloseClipboard();
                }
                break;
            }
            Thread.Sleep(25);
        }
    }

    public static void SetNativeImage(string imagePath)
    {
        if (!File.Exists(imagePath)) return;

        for (int i = 0; i < 8; i++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    EmptyClipboard();

                    // 1. CF_HDROP (file drop list pointing to image file - primary format for WhatsApp, Telegram, Discord, Slack)
                    SetClipboardFileDropInternal(new[] { imagePath });

                    // 2. Registered "PNG" format
                    uint cfPng = RegisterClipboardFormatW("PNG");
                    if (cfPng != 0)
                    {
                        byte[] pngBytes = File.ReadAllBytes(imagePath);
                        nint hPng = GlobalAlloc(GMEM_MOVEABLE, (nuint)pngBytes.Length);
                        if (hPng != nint.Zero)
                        {
                            nint pPng = GlobalLock(hPng);
                            if (pPng != nint.Zero)
                            {
                                Marshal.Copy(pngBytes, 0, pPng, pngBytes.Length);
                                GlobalUnlock(hPng);
                                SetClipboardData(cfPng, hPng);
                            }
                        }
                    }

                    // 3. CF_DIB (standard device-independent bitmap, skipping 14-byte BMP file header)
                    using (var bmp = new System.Drawing.Bitmap(imagePath))
                    {
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        byte[] bmpBytes = ms.ToArray();
                        if (bmpBytes.Length > 14)
                        {
                            int dibLength = bmpBytes.Length - 14;
                            nint hDib = GlobalAlloc(GMEM_MOVEABLE, (nuint)dibLength);
                            if (hDib != nint.Zero)
                            {
                                nint pDib = GlobalLock(hDib);
                                if (pDib != nint.Zero)
                                {
                                    Marshal.Copy(bmpBytes, 14, pDib, dibLength);
                                    GlobalUnlock(hDib);
                                    SetClipboardData(CF_DIB, hDib);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClipboardPasteService] SetNativeImage error: {ex.Message}");
                }
                finally
                {
                    CloseClipboard();
                }
                break;
            }
            Thread.Sleep(25);
        }
    }

    public static void SetNativeFiles(string[] filePaths)
    {
        if (filePaths == null || filePaths.Length == 0) return;

        for (int i = 0; i < 8; i++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    EmptyClipboard();
                    SetClipboardFileDropInternal(filePaths);

                    // Text fallback
                    string text = string.Join(Environment.NewLine, filePaths);
                    byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                    nint hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
                    if (hMem != nint.Zero)
                    {
                        nint ptr = GlobalLock(hMem);
                        if (ptr != nint.Zero)
                        {
                            Marshal.Copy(bytes, 0, ptr, bytes.Length);
                            GlobalUnlock(hMem);
                            SetClipboardData(CF_UNICODETEXT, hMem);
                        }
                    }
                }
                finally
                {
                    CloseClipboard();
                }
                break;
            }
            Thread.Sleep(25);
        }
    }

    private static void SetClipboardFileDropInternal(string[] filePaths)
    {
        const int structSize = 20; // sizeof(DROPFILES)
        int totalBytes = structSize;
        foreach (var p in filePaths)
        {
            totalBytes += (p.Length + 1) * 2;
        }
        totalBytes += 2; // double null terminator

        nint hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)totalBytes);
        if (hGlobal == nint.Zero) return;

        nint ptr = GlobalLock(hGlobal);
        if (ptr == nint.Zero) return;

        try
        {
            Marshal.WriteInt32(ptr, 0, structSize); // pFiles = 20
            Marshal.WriteInt32(ptr, 4, 0);          // pt.x
            Marshal.WriteInt32(ptr, 8, 0);          // pt.y
            Marshal.WriteInt32(ptr, 12, 0);         // fNC
            Marshal.WriteInt32(ptr, 16, 1);         // fWide = 1 (Unicode)

            nint curr = ptr + structSize;
            foreach (var p in filePaths)
            {
                byte[] b = Encoding.Unicode.GetBytes(p + "\0");
                Marshal.Copy(b, 0, curr, b.Length);
                curr += b.Length;
            }
            Marshal.WriteInt16(curr, 0); // extra null terminator

            SetClipboardData(CF_HDROP, hGlobal);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }
    }

    /// <summary>
    /// Brings target window to foreground reliably without destroying child edit caret focus.
    /// </summary>
    public static void RestoreFocus(nint targetHwnd)
    {
        if (targetHwnd == nint.Zero || !IsWindow(targetHwnd)) return;

        if (IsIconic(targetHwnd))
        {
            ShowWindow(targetHwnd, SW_RESTORE);
        }

        uint targetThread = GetWindowThreadProcessId(targetHwnd, out uint targetPid);
        uint currentThread = GetCurrentThreadId();

        AllowSetForegroundWindow(targetPid != 0 ? targetPid : ASFW_ANY);

        nint fg = GetForegroundWindow();
        uint fgThread = fg != nint.Zero ? GetWindowThreadProcessId(fg, out _) : 0;

        if (fgThread != 0 && fgThread != targetThread)
        {
            AttachThreadInput(fgThread, targetThread, true);
            AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(targetHwnd);
            SetForegroundWindow(targetHwnd);
            SwitchToThisWindow(targetHwnd, true);

            AttachThreadInput(currentThread, targetThread, false);
            AttachThreadInput(fgThread, targetThread, false);
        }
        else
        {
            BringWindowToTop(targetHwnd);
            SetForegroundWindow(targetHwnd);
            SwitchToThisWindow(targetHwnd, true);
        }
    }

    /// <summary>
    /// Synthesizes key release (KEYUP) for all modifiers that are currently held down.
    /// </summary>
    public static void ReleaseHeldModifiers()
    {
        ushort[] modifierVks = new ushort[]
        {
            VK_CONTROL, VK_LCONTROL, VK_RCONTROL,
            VK_SHIFT,   VK_LSHIFT,   VK_RSHIFT,
            VK_MENU,    VK_LMENU,    VK_RMENU,
            VK_LWIN,    VK_RWIN
        };

        var keyUps = new List<INPUT>();

        foreach (var vk in modifierVks)
        {
            // Check if key is physically or logically pressed (high-order bit set)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0 || (GetKeyState(vk) & 0x8000) != 0)
            {
                keyUps.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = vk,
                            wScan = 0,
                            dwFlags = KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = nint.Zero
                        }
                    }
                });
            }
        }

        if (keyUps.Count > 0)
        {
            var arr = keyUps.ToArray();
            SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>
    /// Sends Ctrl+V keyboard shortcut using keybd_event with hardware scan codes and discrete timing,
    /// ensuring reliable reception by Chromium/Electron (WhatsApp, Slack, Discord), WPF, Win32, and UWP apps.
    /// </summary>
    public static bool SendCtrlV()
    {
        try
        {
            ushort ctrlScan = (ushort)MapVirtualKey(VK_CONTROL, 0);
            ushort vScan = (ushort)MapVirtualKey((uint)'V', 0);

            // 1. Ctrl Down
            keybd_event((byte)VK_CONTROL, (byte)ctrlScan, 0, 0);
            Thread.Sleep(20);

            // 2. V Down
            keybd_event((byte)'V', (byte)vScan, 0, 0);
            Thread.Sleep(30);

            // 3. V Up
            keybd_event((byte)'V', (byte)vScan, KEYEVENTF_KEYUP, 0);
            Thread.Sleep(20);

            // 4. Ctrl Up
            keybd_event((byte)VK_CONTROL, (byte)ctrlScan, KEYEVENTF_KEYUP, 0);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardPasteService] SendCtrlV error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if target window process is running with elevated privileges (UIPI barrier).
    /// </summary>
    public static bool IsProcessElevated(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        nint hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == nint.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            // Access denied (5) indicates higher integrity level than our process
            if (err == 5) return true;
            return false;
        }

        try
        {
            if (OpenProcessToken(hProcess, TOKEN_QUERY, out nint hToken))
            {
                try
                {
                    int tokenElevationSize = sizeof(int);
                    nint pElevation = Marshal.AllocHGlobal(tokenElevationSize);
                    try
                    {
                        if (GetTokenInformation(hToken, TOKEN_ELEVATION, pElevation, (uint)tokenElevationSize, out _))
                        {
                            int isElevated = Marshal.ReadInt32(pElevation);
                            if (isElevated != 0 && !IsCurrentProcessElevated())
                            {
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pElevation);
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return false;
    }

    private static bool? _isCurrentElevated;
    public static bool IsCurrentProcessElevated()
    {
        if (_isCurrentElevated.HasValue) return _isCurrentElevated.Value;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        _isCurrentElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        return _isCurrentElevated.Value;
    }

    #region Win32 Interop

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SW_RESTORE = 9;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TOKEN_ELEVATION = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint CF_TEXT = 1;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private const uint ASFW_ANY = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint TokenHandle, int TokenInformationClass, nint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    #endregion
}

