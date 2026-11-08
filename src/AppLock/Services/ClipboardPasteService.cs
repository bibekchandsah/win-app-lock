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

            // 4. Check for UIPI (Elevation / Admin Target)
            if (targetHwnd != nint.Zero && IsProcessElevated(targetHwnd))
            {
                result.Success = true;
                result.PastedViaSendInput = false;
                result.TargetIsElevated = true;
                result.Message = "Copied to clipboard. Press Ctrl+V to paste (target is running as Administrator).";

                // Surface fallback toast
                FallbackToastRequested?.Invoke(this, "Copied to clipboard. Press Ctrl+V to paste.");
                ServiceContainer.NotificationService.ShowInfo("Clipboard", "Copied to clipboard. Press Ctrl+V to paste.");
                return result;
            }

            // 5. Wait ~120ms for target window (especially Chromium / Electron apps like WhatsApp) to settle focus
            await Task.Delay(120);

            // 6. Release any held modifier keys so Shift/Alt/Ctrl/Win don't corrupt the paste
            ReleaseHeldModifiers();

            // Brief pause before SendInput
            await Task.Delay(20);

            // 7. Send Ctrl + V using SendInput
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
            var dp = new DataPackage();
            dp.SetText(mergedText);
            Clipboard.SetContent(dp);
            Clipboard.Flush();

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

            // UIPI check
            if (targetHwnd != nint.Zero && IsProcessElevated(targetHwnd))
            {
                result.Success = true;
                result.PastedViaSendInput = false;
                result.TargetIsElevated = true;
                result.Message = "Merged items copied. Press Ctrl+V to paste.";
                FallbackToastRequested?.Invoke(this, "Copied to clipboard. Press Ctrl+V to paste.");
                ServiceContainer.NotificationService.ShowInfo("Clipboard", "Copied to clipboard. Press Ctrl+V to paste.");
                return result;
            }

            await Task.Delay(120);
            ReleaseHeldModifiers();
            await Task.Delay(20);

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
    /// <summary>
    /// Writes the given ClipboardItem into Windows Clipboard using both native Win32 clipboard (for WhatsApp/Electron/Win32)
    /// and WinRT DataPackage (for UWP/modern apps).
    /// </summary>
    public async Task<bool> SetClipboardDataAsync(ClipboardItem item, bool plainTextOnly = false)
    {
        try
        {
            // Activate self-copy guard
            ServiceContainer.ClipboardMonitor.IgnoreNext();

            var dp = new DataPackage();

            if (item.Type == "text")
            {
                string text = item.ContentString ?? item.PreviewString ?? string.Empty;
                SetNativeText(text);
                dp.SetText(text);

                if (!plainTextOnly && !string.IsNullOrEmpty(item.HtmlString))
                {
                    dp.SetHtmlFormat(item.HtmlString);
                }
            }
            else if (item.Type == "image")
            {
                if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    SetNativeImage(item.ImagePath);
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
                        dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                    }
                    catch { }
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
                        var storageItems = new List<IStorageItem>();
                        foreach (var path in paths)
                        {
                            if (File.Exists(path))
                            {
                                storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
                            }
                            else if (Directory.Exists(path))
                            {
                                storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                            }
                        }

                        if (storageItems.Count > 0)
                        {
                            dp.SetStorageItems(storageItems);
                        }
                    }
                }
            }

            try
            {
                Clipboard.SetContent(dp);
                Clipboard.Flush();
            }
            catch { }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardPasteService] Error setting clipboard: {ex.Message}");
            return false;
        }
    }

    private static void SetNativeText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    EmptyClipboard();
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
            Thread.Sleep(20);
        }
    }

    private static void SetNativeImage(string imagePath)
    {
        if (!File.Exists(imagePath)) return;

        for (int i = 0; i < 5; i++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    EmptyClipboard();

                    // 1. Write CF_DIB (skipping 14-byte BMP header)
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

                    // 2. Write registered "PNG" format
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
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Brings target window to foreground reliably using SwitchToThisWindow and AttachThreadInput.
    /// </summary>
    public static void RestoreFocus(nint targetHwnd)
    {
        if (targetHwnd == nint.Zero || !IsWindow(targetHwnd)) return;

        if (IsIconic(targetHwnd))
        {
            ShowWindow(targetHwnd, SW_RESTORE);
        }

        SwitchToThisWindow(targetHwnd, true);

        uint targetThread = GetWindowThreadProcessId(targetHwnd, out _);
        uint currentThread = GetCurrentThreadId();

        if (targetThread != currentThread)
        {
            AttachThreadInput(currentThread, targetThread, true);
            SetForegroundWindow(targetHwnd);
            SetFocus(targetHwnd);
            AttachThreadInput(currentThread, targetThread, false);
        }
        else
        {
            SetForegroundWindow(targetHwnd);
            SetFocus(targetHwnd);
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
    /// Sends Ctrl+V keyboard shortcut via SendInput with valid hardware scan codes.
    /// </summary>
    public static bool SendCtrlV()
    {
        ushort ctrlScan = (ushort)MapVirtualKey(VK_CONTROL, 0);
        ushort vScan = (ushort)MapVirtualKey((uint)'V', 0);

        INPUT[] inputs = new INPUT[4];

        // 1. Ctrl Down
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_CONTROL,
                    wScan = ctrlScan,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        // 2. V Down
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)'V',
                    wScan = vScan,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        // 3. V Up
        inputs[2] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)'V',
                    wScan = vScan,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        // 4. Ctrl Up
        inputs[3] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = VK_CONTROL,
                    wScan = ctrlScan,
                    dwFlags = KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == (uint)inputs.Length;
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
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;

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

