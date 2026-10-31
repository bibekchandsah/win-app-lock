/*
 * AppLockHook.dll
 *
 * Injects into the target window's process to intercept Desktop Window Manager (DWM)
 * iconic thumbnail and live preview requests:
 *   - WM_DWMSENDICONICTHUMBNAIL (0x0323)
 *   - WM_DWMSENDICONICLIVEPREVIEWBITMAP (0x0326)
 *
 * DWM POSTS these messages to the owning window thread. WH_GETMESSAGE intercepts
 * messages from the thread queue (both posted AND sent), making it the reliable
 * choice. WH_CALLWNDPROC is also installed so that SendMessageTimeout in
 * InstallLockedThumbnail immediately loads the hook DLL into the target process
 * and calls EnsureSubclassed before DWM makes its first thumbnail request.
 * WH_CBT provides an additional early-load trigger on window activation.
 *
 * DWM requires (all in the owning process):
 *   1. DwmSetWindowAttribute(DWMWA_FORCE_ICONIC_REPRESENTATION)
 *   2. DwmSetWindowAttribute(DWMWA_HAS_ICONIC_BITMAP)
 *   3. DwmSetIconicThumbnail with a 32-bit ARGB DIBSection where Alpha = 0xFF.
 *
 * Exported functions:
 *   BOOL InstallLockedThumbnail(HWND hwnd);
 *   void UninstallLockedThumbnail(HWND hwnd);
 *   void UninstallAllLockedThumbnails();
 *   void InvalidateLockedThumbnail(HWND hwnd);
 */

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <dwmapi.h>
#include <commctrl.h>
#include <stdio.h>
#include <stdlib.h>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "comctl32.lib")

static void HookLog(const wchar_t* fmt, ...)
{
    WCHAR buf[512];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buf, 512, _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(buf);
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------
#define MAX_TARGETS 32
#define WM_DWMSENDICONICTHUMBNAIL_MSG         0x0323U
#define WM_DWMSENDICONICLIVEPREVIEWBITMAP_MSG 0x0326U
#define SUBCLASS_ID_VAL                       ((UINT_PTR)0xA70C4C)

// ---------------------------------------------------------------------------
// Shared data segment (shared across all processes loading this DLL)
// ---------------------------------------------------------------------------
#pragma data_seg(".shared")
volatile HWND  g_targets   [MAX_TARGETS] = {};
volatile HHOOK g_hooksCall [MAX_TARGETS] = {}; // WH_CALLWNDPROC hooks (forces immediate DLL load via SendMessage)
volatile HHOOK g_hooksGet  [MAX_TARGETS] = {}; // WH_GETMESSAGE hooks (catches posted DWM messages)
volatile HHOOK g_hooksCbt  [MAX_TARGETS] = {}; // WH_CBT hooks (subclass trigger on activation)
volatile DWORD g_pids      [MAX_TARGETS] = {};
volatile DWORD g_appLockPid = 0;              // AppLock.exe main PID for dead-process detection
#pragma data_seg()
#pragma comment(linker, "/section:.shared,rws")

// ---------------------------------------------------------------------------
// Process-local globals
// ---------------------------------------------------------------------------
static HMODULE g_hMod = NULL;
static UINT g_wmUnlockTarget = 0;
static WCHAR g_dllPath[MAX_PATH] = { 0 };

// Forward declarations
static LRESULT CALLBACK LockedSubclassProc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
static void EnsureSubclassed(HWND hwnd);
static void RestoreWindowDwmState(HWND hwnd);

// ---------------------------------------------------------------------------
// Liveness check for AppLock.exe (only called on timer)
// ---------------------------------------------------------------------------
static bool IsAppLockAlive()
{
    DWORD pid = g_appLockPid;
    if (pid == 0) return true; // Not set yet or in AppLock process itself
    if (GetCurrentProcessId() == pid) return true;

    HANDLE hProc = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!hProc)
    {
        DWORD err = GetLastError();
        // ERROR_ACCESS_DENIED means the process exists and is running, but security policy prevents opening a handle
        if (err == ERROR_ACCESS_DENIED)
        {
            return true;
        }
        // Only if the PID does not exist is it truly dead
        return false;
    }

    DWORD waitRes = WaitForSingleObject(hProc, 0);
    CloseHandle(hProc);
    return (waitRes != WAIT_OBJECT_0);
}

// ---------------------------------------------------------------------------
// Target table helpers
// ---------------------------------------------------------------------------
static bool IsTarget(HWND hwnd)
{
    if (!hwnd) return false;
    for (int i = 0; i < MAX_TARGETS; ++i)
    {
        if ((HWND)g_targets[i] == hwnd)
        {
            if (!IsWindow(hwnd))
            {
                g_targets[i] = NULL;
                g_pids   [i] = 0;
                return false;
            }
            return true;
        }
    }
    return false;
}

static int FindSlot(HWND hwnd)
{
    for (int i = 0; i < MAX_TARGETS; ++i)
    {
        if ((HWND)g_targets[i] == hwnd) return i;
    }
    return -1;
}

static int FindEmpty()
{
    for (int i = 0; i < MAX_TARGETS; ++i)
    {
        if (g_targets[i] == NULL || !IsWindow((HWND)g_targets[i]))
        {
            if (g_targets[i] != NULL)
            {
                // Stale entry — clean up its hooks
                if (g_hooksCall[i]) { UnhookWindowsHookEx((HHOOK)g_hooksCall[i]); g_hooksCall[i] = NULL; }
                if (g_hooksGet [i]) { UnhookWindowsHookEx((HHOOK)g_hooksGet [i]); g_hooksGet [i] = NULL; }
                if (g_hooksCbt [i]) { UnhookWindowsHookEx((HHOOK)g_hooksCbt [i]); g_hooksCbt [i] = NULL; }
                g_targets[i] = NULL;
                g_pids   [i] = 0;
            }
            return i;
        }
    }
    return -1;
}

// ---------------------------------------------------------------------------
// 32-bit ARGB DIBSection creation and drawing
// ---------------------------------------------------------------------------
static HBITMAP CreateLockedDIBSection(int w, int h, void** ppBits)
{
    if (w <= 0 || h <= 0 || !ppBits) return NULL;

    BITMAPINFO bmi = {};
    bmi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth       = w;
    bmi.bmiHeader.biHeight      = -h; // Top-down DIB
    bmi.bmiHeader.biPlanes      = 1;
    bmi.bmiHeader.biBitCount    = 32;
    bmi.bmiHeader.biCompression = BI_RGB;
    bmi.bmiHeader.biSizeImage   = (DWORD)(w * h * 4);

    HDC hdc = GetDC(NULL);
    HBITMAP hbmp = CreateDIBSection(hdc, &bmi, DIB_RGB_COLORS, ppBits, NULL, 0);
    ReleaseDC(NULL, hdc);

    return hbmp;
}

static HBITMAP RenderLockedImage(int w, int h)
{
    void* pBits = NULL;
    HBITMAP hbmp = CreateLockedDIBSection(w, h, &pBits);
    if (!hbmp || !pBits) return NULL;

    HDC hdcScreen = GetDC(NULL);
    HDC mdc = CreateCompatibleDC(hdcScreen);
    HBITMAP hOldBmp = (HBITMAP)SelectObject(mdc, hbmp);

    // 1. Dark purple/slate background
    HBRUSH bgBrush = CreateSolidBrush(RGB(18, 16, 28));
    RECT rc = { 0, 0, w, h };
    FillRect(mdc, &rc, bgBrush);
    DeleteObject(bgBrush);

    // 2. Proportional layout sizing
    bool isLivePreview = (h > 250);
    bool showSub = isLivePreview || (h >= 130);

    int badgeSize = isLivePreview ? 80 : max(32, min(h / 3, 52));
    int badgeR    = isLivePreview ? 20 : max(8, badgeSize / 4);
    int titleSize = isLivePreview ? 22 : max(11, min(h / 9, 14));
    int subSize   = isLivePreview ? 13 : max(9, min(h / 14, 11));
    int gap1      = isLivePreview ? 14 : 6;
    int gap2      = isLivePreview ? 6 : 3;

    int totalH = badgeSize + gap1 + titleSize + (showSub ? (gap2 + subSize) : 0);
    int startY = max(4, (h - totalH) / 2);
    int bx = (w - badgeSize) / 2;
    int by = startY;

    // 3. Rounded purple accent badge
    HBRUSH badgeBrush = CreateSolidBrush(RGB(138, 43, 226));
    HPEN badgePen     = CreatePen(PS_SOLID, 1, RGB(168, 85, 247));
    HPEN oldPen       = (HPEN)SelectObject(mdc, badgePen);
    HBRUSH oldBr      = (HBRUSH)SelectObject(mdc, badgeBrush);

    RoundRect(mdc, bx, by, bx + badgeSize, by + badgeSize, badgeR * 2, badgeR * 2);

    SelectObject(mdc, oldPen);
    SelectObject(mdc, oldBr);
    DeleteObject(badgeBrush);
    DeleteObject(badgePen);

    // 4. Lock icon glyph (0xE72E) in white inside badge
    int glyphSize = (int)(badgeSize * 0.52f);
    HFONT hGlyphFont = CreateFontW(
        -glyphSize, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe Fluent Icons");
    if (!hGlyphFont)
    {
        hGlyphFont = CreateFontW(
            -glyphSize, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_DONTCARE, L"Segoe MDL2 Assets");
    }

    SetBkMode(mdc, TRANSPARENT);
    HFONT oldF = (HFONT)SelectObject(mdc, hGlyphFont);
    SetTextColor(mdc, RGB(255, 255, 255));
    WCHAR glyph[2] = { (WCHAR)0xE72E, 0 };
    RECT glyphRc = { bx, by, bx + badgeSize, by + badgeSize };
    DrawTextW(mdc, glyph, 1, &glyphRc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    SelectObject(mdc, oldF);
    DeleteObject(hGlyphFont);

    // 5. Title: "Locked"
    HFONT hTitleFont = CreateFontW(
        -titleSize, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    oldF = (HFONT)SelectObject(mdc, hTitleFont);
    SetTextColor(mdc, RGB(245, 240, 255));
    int textY = by + badgeSize + gap1;
    RECT titleRc = { 2, textY, w - 2, textY + titleSize + 4 };
    DrawTextW(mdc, L"Locked", -1, &titleRc, DT_CENTER | DT_TOP | DT_SINGLELINE);
    SelectObject(mdc, oldF);
    DeleteObject(hTitleFont);

    // 6. Subtitle (if space permits)
    if (showSub)
    {
        HFONT hSubFont = CreateFontW(
            -subSize, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
        oldF = (HFONT)SelectObject(mdc, hSubFont);
        SetTextColor(mdc, RGB(180, 165, 210));
        int subY = textY + titleSize + gap2;
        RECT subRc = { 2, subY, w - 2, subY + subSize + 4 };
        DrawTextW(mdc, L"Protected by AppLock", -1, &subRc, DT_CENTER | DT_TOP | DT_SINGLELINE);
        SelectObject(mdc, oldF);
        DeleteObject(hSubFont);
    }

    SelectObject(mdc, hOldBmp);
    DeleteDC(mdc);
    ReleaseDC(NULL, hdcScreen);

    // MANDATORY FOR DWM: Set Alpha = 0xFF on every pixel after all GDI drawing.
    // GdiFlush() ensures GDI has finished writing before DWM reads the bitmap.
    DWORD* pPixels = (DWORD*)pBits;
    int total = w * h;
    for (int i = 0; i < total; ++i)
        pPixels[i] |= 0xFF000000;
    GdiFlush();

    return hbmp;
}

static void SendLockedThumbnail(HWND hwnd, int w, int h)
{
    if (w <= 0 || h <= 0) return;

    HBITMAP bmp = RenderLockedImage(w, h);
    if (bmp)
    {
        DwmSetIconicThumbnail(hwnd, bmp, 0);
        DeleteObject(bmp);
    }
}

static void SendLockedLivePreview(HWND hwnd)
{
    RECT rc = { 0 };
    if (!GetWindowRect(hwnd, &rc))
    {
        rc.right = 800;
        rc.bottom = 600;
    }
    int w = rc.right - rc.left;
    int h = rc.bottom - rc.top;
    if (w <= 0) w = 800;
    if (h <= 0) h = 600;

    HBITMAP bmp = RenderLockedImage(w, h);
    if (bmp)
    {
        DwmSetIconicLivePreviewBitmap(hwnd, bmp, NULL, 0);
        DeleteObject(bmp);
    }
}

// ---------------------------------------------------------------------------
// Restore normal DWM state on target window
// ---------------------------------------------------------------------------
static void RestoreWindowDwmState(HWND hwnd)
{
    if (!hwnd || !IsWindow(hwnd)) return;

    BOOL val = FALSE;
    DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, &val, sizeof(BOOL));
    DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP,           &val, sizeof(BOOL));
    DwmInvalidateIconicBitmaps(hwnd);

    RemoveWindowSubclass(hwnd, LockedSubclassProc, SUBCLASS_ID_VAL);

    // Force DWM to re-render the live preview
    SetWindowPos(hwnd, NULL, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
    RedrawWindow(hwnd, NULL, NULL,
        RDW_INVALIDATE | RDW_UPDATENOW | RDW_FRAME | RDW_ALLCHILDREN);
}

// ---------------------------------------------------------------------------
// Subclass procedure (executes inside target process)
// ---------------------------------------------------------------------------
static LRESULT CALLBACK LockedSubclassProc(
    HWND hwnd, UINT msg, WPARAM wp, LPARAM lp,
    UINT_PTR uId, DWORD_PTR dwRef)
{
    if (msg == WM_DWMSENDICONICTHUMBNAIL_MSG)
    {
        // If AppLock has been terminated, clean up so DWM can use the live preview
        if (!IsAppLockAlive())
        {
            RestoreWindowDwmState(hwnd);
            return DefSubclassProc(hwnd, msg, wp, lp);
        }
        int maxW = HIWORD(lp);
        int maxH = LOWORD(lp);
        if (maxW <= 0) maxW = 200;
        if (maxH <= 0) maxH = 150;
        SendLockedThumbnail(hwnd, maxW, maxH);
        return 0;
    }

    if (msg == WM_DWMSENDICONICLIVEPREVIEWBITMAP_MSG)
    {
        if (!IsAppLockAlive())
        {
            RestoreWindowDwmState(hwnd);
            return DefSubclassProc(hwnd, msg, wp, lp);
        }
        SendLockedLivePreview(hwnd);
        return 0;
    }

    if (msg == g_wmUnlockTarget)
    {
        RestoreWindowDwmState(hwnd);
        int slot = FindSlot(hwnd);
        if (slot >= 0)
        {
            g_targets[slot] = NULL;
            g_pids[slot]    = 0;
        }
        return 0;
    }

    if (msg == WM_DESTROY || msg == WM_NCDESTROY)
    {
        RemoveWindowSubclass(hwnd, LockedSubclassProc, SUBCLASS_ID_VAL);
        int slot = FindSlot(hwnd);
        if (slot >= 0)
        {
            g_targets[slot] = NULL;
            g_pids[slot]    = 0;
        }
    }

    return DefSubclassProc(hwnd, msg, wp, lp);
}

// ---------------------------------------------------------------------------
// In-target configuration
// ---------------------------------------------------------------------------
static void EnsureSubclassed(HWND hwnd)
{
    if (!hwnd || !IsWindow(hwnd)) return;
    if (!IsTarget(hwnd)) return;

    // Allow the unlock message through UIPI filter (cross-integrity-level delivery)
    if (g_wmUnlockTarget != 0)
    {
        typedef BOOL (WINAPI *pfnChangeFilter)(HWND, UINT, DWORD, PCHANGEFILTERSTRUCT);
        HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
        if (hUser32)
        {
            pfnChangeFilter pChange = (pfnChangeFilter)GetProcAddress(hUser32, "ChangeWindowMessageFilterEx");
            if (pChange)
                pChange(hwnd, g_wmUnlockTarget, 1 /* MSGFLT_ALLOW */, NULL);
        }
    }

    DWORD_PTR dwRef = 0;
    if (GetWindowSubclass(hwnd, LockedSubclassProc, SUBCLASS_ID_VAL, &dwRef))
        return; // Already installed — DWM attributes already set

    // Set DWM iconic-thumbnail attributes (succeeds because we are in the owning process)
    BOOL val = TRUE;
    DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, &val, sizeof(BOOL));
    DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP,           &val, sizeof(BOOL));

    SetWindowSubclass(hwnd, LockedSubclassProc, SUBCLASS_ID_VAL, 0);

    DwmInvalidateIconicBitmaps(hwnd);
}

// ---------------------------------------------------------------------------
// Hook procedures — loaded into target process by Windows hook mechanism
// ---------------------------------------------------------------------------

// WH_CALLWNDPROC — fires for SENT messages.
// Primary purpose: gets the hook DLL loaded into the target thread immediately
// when we call SendMessageTimeout(WM_NULL) during InstallLockedThumbnail.
static LRESULT CALLBACK HookCallWndProc(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode >= 0 && lp)
    {
        CWPSTRUCT* cwp = (CWPSTRUCT*)lp;
        if (cwp->hwnd)
        {
            HWND target = IsTarget(cwp->hwnd) ? cwp->hwnd : GetAncestor(cwp->hwnd, GA_ROOT);
            if (target && IsTarget(target))
                EnsureSubclassed(target);
        }
    }
    return CallNextHookEx(NULL, nCode, wp, lp);
}

// WH_GETMESSAGE — fires for messages retrieved via GetMessage/PeekMessage.
// DWM posts WM_DWMSENDICONICTHUMBNAIL and WM_DWMSENDICONICLIVEPREVIEWBITMAP,
// so this hook is the primary one that catches those requests.
static LRESULT CALLBACK HookGetMsgProc(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode >= 0 && lp)
    {
        MSG* msg = (MSG*)lp;
        if (msg->hwnd)
        {
            HWND target = IsTarget(msg->hwnd) ? msg->hwnd : GetAncestor(msg->hwnd, GA_ROOT);
            if (target && IsTarget(target))
            {
                if (msg->message == g_wmUnlockTarget)
                {
                    RestoreWindowDwmState(target);
                    msg->message = WM_NULL;
                    return 0;
                }

                // Ensure subclass is installed every time we see a message from this window
                EnsureSubclassed(target);

                if (msg->message == WM_DWMSENDICONICTHUMBNAIL_MSG && msg->hwnd == target)
                {
                    int maxW = HIWORD(msg->lParam);
                    int maxH = LOWORD(msg->lParam);
                    if (maxW <= 0) maxW = 200;
                    if (maxH <= 0) maxH = 150;
                    SendLockedThumbnail(target, maxW, maxH);
                    msg->message = WM_NULL;
                }
                else if (msg->message == WM_DWMSENDICONICLIVEPREVIEWBITMAP_MSG && msg->hwnd == target)
                {
                    SendLockedLivePreview(target);
                    msg->message = WM_NULL;
                }
            }
        }
    }
    return CallNextHookEx(NULL, nCode, wp, lp);
}

// WH_CBT — fires on window activation/creation events, good trigger for EnsureSubclassed
static LRESULT CALLBACK HookCbtProc(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode >= 0)
    {
        HWND hwnd = (HWND)wp;
        if (hwnd)
        {
            HWND target = IsTarget(hwnd) ? hwnd : GetAncestor(hwnd, GA_ROOT);
            if (target && IsTarget(target))
            {
                EnsureSubclassed(target);
            }
        }
    }
    return CallNextHookEx(NULL, nCode, wp, lp);
}

// ---------------------------------------------------------------------------
// DLL Injection Helper (CreateRemoteThread fallback for non-UWP processes)
// ---------------------------------------------------------------------------
static void TryInjectDll(DWORD pid)
{
    if (pid == 0 || pid == GetCurrentProcessId()) return;
    if (g_dllPath[0] == L'\0') return;

    HANDLE hProc = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | PROCESS_QUERY_INFORMATION,
        FALSE, pid);
    if (!hProc) return;

    SIZE_T bytes = (wcslen(g_dllPath) + 1) * sizeof(WCHAR);
    LPVOID pRemoteMem = VirtualAllocEx(hProc, NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (pRemoteMem)
    {
        if (WriteProcessMemory(hProc, pRemoteMem, g_dllPath, bytes, NULL))
        {
            LPTHREAD_START_ROUTINE pLoadLibrary = (LPTHREAD_START_ROUTINE)GetProcAddress(
                GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
            if (pLoadLibrary)
            {
                HANDLE hThread = CreateRemoteThread(hProc, NULL, 0, pLoadLibrary, pRemoteMem, 0, NULL);
                if (hThread)
                {
                    WaitForSingleObject(hThread, 500);
                    CloseHandle(hThread);
                }
            }
        }
    }
    CloseHandle(hProc);
}

// ---------------------------------------------------------------------------
// Exported API
// ---------------------------------------------------------------------------
extern "C" __declspec(dllexport)
BOOL InstallLockedThumbnail(HWND hwnd)
{
    if (!hwnd || !IsWindow(hwnd)) return FALSE;
    if (IsTarget(hwnd)) return TRUE; // Already tracked

    int slot = FindEmpty();
    if (slot < 0) return FALSE; // Table full

    DWORD pid = 0;
    DWORD threadId = GetWindowThreadProcessId(hwnd, &pid);
    if (!threadId) return FALSE;

    g_appLockPid = GetCurrentProcessId();

    g_targets[slot] = hwnd;
    g_pids[slot]    = pid;

    // 1. Install all three hook types on the target thread.
    //    WH_CALLWNDPROC: forces DLL load into target when we SendMessage below.
    //    WH_GETMESSAGE:  catches posted DWM thumbnail/live-preview messages.
    //    WH_CBT:         subclasses window on activation events.
    HHOOK hookCall = SetWindowsHookExW(WH_CALLWNDPROC, HookCallWndProc, g_hMod, threadId);
    HHOOK hookGet  = SetWindowsHookExW(WH_GETMESSAGE,  HookGetMsgProc,  g_hMod, threadId);
    HHOOK hookCbt  = SetWindowsHookExW(WH_CBT,         HookCbtProc,     g_hMod, threadId);

    // Fallback: if per-thread hooks failed (AppContainer / UIPI boundary), try global scope
    if (!hookCall) hookCall = SetWindowsHookExW(WH_CALLWNDPROC, HookCallWndProc, g_hMod, 0);
    if (!hookGet)  hookGet  = SetWindowsHookExW(WH_GETMESSAGE,  HookGetMsgProc,  g_hMod, 0);
    if (!hookCbt)  hookCbt  = SetWindowsHookExW(WH_CBT,         HookCbtProc,     g_hMod, 0);

    g_hooksCall[slot] = hookCall;
    g_hooksGet [slot] = hookGet;
    g_hooksCbt [slot] = hookCbt;

    // 2. Send a synchronous WM_NULL to force Windows to load our hook DLL into the
    //    target thread RIGHT NOW. WH_CALLWNDPROC fires during this call, which means
    //    EnsureSubclassed (and therefore DWMWA attributes) are set before we return.
    DWORD_PTR dwResult = 0;
    SendMessageTimeoutW(hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_NORMAL, 1000, &dwResult);

    // Also post so the WH_GETMESSAGE hook fires on the next pump cycle
    PostMessageW(hwnd, WM_NULL, 0, 0);

    // 3. Fallback direct injection for processes with restrictive hook policies (UWP AppContainer)
    TryInjectDll(pid);

    // 4. Ask DWM to request a fresh thumbnail (will trigger WM_DWMSENDICONICTHUMBNAIL)
    DwmInvalidateIconicBitmaps(hwnd);

    return TRUE;
}

extern "C" __declspec(dllexport)
void UninstallLockedThumbnail(HWND hwnd)
{
    int slot = FindSlot(hwnd);
    if (slot < 0) return;

    HHOOK hCall = (HHOOK)g_hooksCall[slot];
    HHOOK hGet  = (HHOOK)g_hooksGet [slot];
    HHOOK hCbt  = (HHOOK)g_hooksCbt [slot];

    // Clear from shared table FIRST so hook callbacks won't re-subclass
    g_targets[slot]   = NULL;
    g_pids[slot]      = 0;
    g_hooksCall[slot] = NULL;
    g_hooksGet [slot] = NULL;
    g_hooksCbt [slot] = NULL;

    // Tell the subclass (inside target process) to restore normal DWM thumbnail
    if (hwnd && IsWindow(hwnd))
    {
        DWORD_PTR dwResult = 0;
        SendMessageTimeoutW(hwnd, g_wmUnlockTarget, 0, 0, SMTO_ABORTIFHUNG | SMTO_NORMAL, 500, &dwResult);
        PostMessageW(hwnd, g_wmUnlockTarget, 0, 0);
        PostMessageW(hwnd, WM_NULL, 0, 0); // flush queue so unlock is processed
    }

    if (hCall) UnhookWindowsHookEx(hCall);
    if (hGet)  UnhookWindowsHookEx(hGet);
    if (hCbt)  UnhookWindowsHookEx(hCbt);
}

extern "C" __declspec(dllexport)
void UninstallAllLockedThumbnails()
{
    for (int i = 0; i < MAX_TARGETS; ++i)
    {
        HWND hwnd = (HWND)g_targets[i];
        if (hwnd)
        {
            UninstallLockedThumbnail(hwnd);
        }
    }
}

// Called from the managed side ~600 ms after install to re-request a thumbnail
// in case DWM asked before EnsureSubclassed completed inside the target process.
extern "C" __declspec(dllexport)
void InvalidateLockedThumbnail(HWND hwnd)
{
    if (!hwnd || !IsWindow(hwnd)) return;
    if (!IsTarget(hwnd)) return;

    // Only invalidate — do NOT call EnsureSubclassed cross-process (it is a no-op there)
    DwmInvalidateIconicBitmaps(hwnd);
}

// ---------------------------------------------------------------------------
// DllMain
// ---------------------------------------------------------------------------
BOOL WINAPI DllMain(HMODULE hMod, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hMod = hMod;
        DisableThreadLibraryCalls(hMod);
        GetModuleFileNameW(hMod, g_dllPath, MAX_PATH);

        g_wmUnlockTarget = RegisterWindowMessageW(L"AppLock_UnlockTarget_Msg");

        DWORD myPid = GetCurrentProcessId();

        // When loaded into a target process (by hook injection or CreateRemoteThread),
        // immediately subclass any windows in this process that are in the target table.
        for (int i = 0; i < MAX_TARGETS; ++i)
        {
            HWND target = (HWND)g_targets[i];
            if (target && IsWindow(target))
            {
                DWORD targetPid = 0;
                GetWindowThreadProcessId(target, &targetPid);
                if (targetPid == myPid)
                    EnsureSubclassed(target);
            }
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // If we are being unloaded from a TARGET process (not AppLock itself),
        // restore any locked windows in this process so they don't stay broken.
        DWORD myPid = GetCurrentProcessId();
        if (myPid != g_appLockPid)
        {
            for (int i = 0; i < MAX_TARGETS; ++i)
            {
                HWND target = (HWND)g_targets[i];
                if (target && IsWindow(target))
                {
                    DWORD targetPid = 0;
                    GetWindowThreadProcessId(target, &targetPid);
                    if (targetPid == myPid)
                        RestoreWindowDwmState(target);
                }
            }
        }
    }
    return TRUE;
}
