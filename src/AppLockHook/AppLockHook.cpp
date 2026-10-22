/*
 * AppLockHook.dll
 *
 * Injects into the target window's process to intercept Desktop Window Manager (DWM)
 * iconic thumbnail and live preview requests:
 *   - WM_DWMSENDICONICTHUMBNAIL (0x0323)
 *   - WM_DWMSENDICONICLIVEPREVIEWBITMAP (0x0326)
 *
 * DWM requires:
 *   1. DwmSetWindowAttribute(DWMWA_FORCE_ICONIC_REPRESENTATION) must be called by the target process.
 *   2. DwmSetWindowAttribute(DWMWA_HAS_ICONIC_BITMAP) must be called by the target process.
 *   3. DwmSetIconicThumbnail must be called with a 32-bit ARGB DIBSection where Alpha = 0xFF.
 *
 * Exported functions (called from C# AppLock):
 *   BOOL InstallLockedThumbnail(HWND hwnd);
 *   void UninstallLockedThumbnail(HWND hwnd);
 */

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <dwmapi.h>
#include <commctrl.h>

#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "comctl32.lib")

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
volatile HWND  g_targets [MAX_TARGETS] = {};
volatile HHOOK g_hooksMsg[MAX_TARGETS] = {};
volatile HHOOK g_hooksCbt[MAX_TARGETS] = {};
volatile DWORD g_pids    [MAX_TARGETS] = {};
#pragma data_seg()
#pragma comment(linker, "/section:.shared,rws")

// ---------------------------------------------------------------------------
// Process-local globals
// ---------------------------------------------------------------------------
static HMODULE g_hMod = NULL;
static UINT g_wmLockTarget = 0;
static UINT g_wmUnlockTarget = 0;
static WCHAR g_dllPath[MAX_PATH] = { 0 };

// Forward declarations
static LRESULT CALLBACK LockedSubclassProc(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
static void EnsureSubclassed(HWND hwnd);

// ---------------------------------------------------------------------------
// Target table helpers
// ---------------------------------------------------------------------------
static bool IsTarget(HWND hwnd)
{
    if (!hwnd) return false;
    for (int i = 0; i < MAX_TARGETS; ++i)
    {
        if ((HWND)g_targets[i] == hwnd) return true;
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
        if (g_targets[i] == NULL) return i;
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

    // 2. Lock Icon Dimensions
    int cx = w / 2;
    int cy = h / 2 - (h > 120 ? 12 : 4);
    int iconSize = max(16, min(w, h) / 4);
    int bw = iconSize / 2;
    int bh = iconSize * 2 / 3;
    int shR = max(4, bw - 2);

    // 3. Shackle (purple curved arc)
    HPEN pen = CreatePen(PS_SOLID, max(2, iconSize / 7), RGB(168, 85, 247));
    HPEN oldPen = (HPEN)SelectObject(mdc, pen);
    HBRUSH oldBr = (HBRUSH)SelectObject(mdc, GetStockObject(NULL_BRUSH));

    int sx = cx - shR, sy = cy - bh;
    MoveToEx(mdc, sx, cy, NULL);
    LineTo(mdc, sx, sy + shR);
    MoveToEx(mdc, cx + shR, cy, NULL);
    LineTo(mdc, cx + shR, sy + shR);
    Arc(mdc, sx, sy - shR, cx + shR + 1, sy + shR + 1, sx, sy + shR, cx + shR, sy + shR);

    SelectObject(mdc, oldPen);
    SelectObject(mdc, oldBr);
    DeleteObject(pen);

    // 4. Lock Body (solid violet rect)
    HBRUSH body = CreateSolidBrush(RGB(147, 51, 234));
    RECT bodyRc = { cx - bw, cy, cx + bw, cy + bh };
    FillRect(mdc, &bodyRc, body);
    DeleteObject(body);

    // 5. Keyhole
    HBRUSH hole = CreateSolidBrush(RGB(18, 16, 28));
    int hr = max(2, bw / 4);
    Ellipse(mdc, cx - hr, cy + bh / 3, cx + hr, cy + bh / 3 + hr * 2);
    DeleteObject(hole);

    // 6. Text: "Locked"
    SetBkMode(mdc, TRANSPARENT);
    int fontSize = max(11, min(h / 7, 24));
    HFONT hFont = CreateFontW(-fontSize, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    HFONT oldF = (HFONT)SelectObject(mdc, hFont);

    SetTextColor(mdc, RGB(243, 232, 255));
    RECT textRc = { 4, cy + bh + 6, w - 4, h - 2 };
    DrawTextW(mdc, L"Locked", -1, &textRc, DT_CENTER | DT_TOP | DT_SINGLELINE);

    SelectObject(mdc, oldF);
    DeleteObject(hFont);

    // 7. Subtitle: "Protected by AppLock" (if sufficient height)
    if (h >= 130)
    {
        int subSize = max(9, h / 14);
        HFONT hSubF = CreateFontW(-subSize, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
        HFONT oldSF = (HFONT)SelectObject(mdc, hSubF);

        SetTextColor(mdc, RGB(192, 175, 215));
        RECT subRc = { 4, cy + bh + 8 + fontSize, w - 4, h - 2 };
        DrawTextW(mdc, L"Protected by AppLock", -1, &subRc, DT_CENTER | DT_TOP | DT_SINGLELINE);

        SelectObject(mdc, oldSF);
        DeleteObject(hSubF);
    }

    SelectObject(mdc, hOldBmp);
    DeleteDC(mdc);
    ReleaseDC(NULL, hdcScreen);

    // 8. MANDATORY FOR DWM: Set Alpha = 0xFF on every pixel
    // GDI drawing functions leave the alpha channel as 0x00.
    // If alpha remains 0x00, DWM treats the bitmap as completely transparent!
    DWORD* pPixels = (DWORD*)pBits;
    int total = w * h;
    for (int i = 0; i < total; ++i)
    {
        pPixels[i] |= 0xFF000000;
    }

    return hbmp;
}

static void SendLockedThumbnail(HWND hwnd, int w, int h)
{
    if (w <= 0 || h <= 0) return;
    HBITMAP bmp = RenderLockedImage(w, h);
    if (bmp)
    {
        HRESULT hr = DwmSetIconicThumbnail(hwnd, bmp, 0);
        if (FAILED(hr))
        {
            DwmSetIconicThumbnail(hwnd, bmp, 1);
        }
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
// Subclass procedure (executes inside target process)
// ---------------------------------------------------------------------------
static LRESULT CALLBACK LockedSubclassProc(
    HWND hwnd, UINT msg, WPARAM wp, LPARAM lp,
    UINT_PTR uId, DWORD_PTR dwRef)
{
    if (msg == WM_DWMSENDICONICTHUMBNAIL_MSG)
    {
        // HIWORD(lParam) = maximum allowed width, LOWORD(lParam) = maximum allowed height
        int maxW = HIWORD(lp);
        int maxH = LOWORD(lp);
        if (maxW <= 0) maxW = 200;
        if (maxH <= 0) maxH = 150;
        SendLockedThumbnail(hwnd, maxW, maxH);
        return 0; // Handled
    }

    if (msg == WM_DWMSENDICONICLIVEPREVIEWBITMAP_MSG)
    {
        SendLockedLivePreview(hwnd);
        return 0; // Handled
    }

    if (msg == g_wmUnlockTarget)
    {
        // Restore normal DWM thumbnail
        BOOL val = FALSE;
        DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, &val, sizeof(BOOL));
        DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP,           &val, sizeof(BOOL));
        DwmInvalidateIconicBitmaps(hwnd);
        RemoveWindowSubclass(hwnd, LockedSubclassProc, uId);
        return 0;
    }

    if (msg == WM_NCDESTROY)
    {
        RemoveWindowSubclass(hwnd, LockedSubclassProc, uId);
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

    DWORD_PTR dwRef = 0;
    if (GetWindowSubclass(hwnd, LockedSubclassProc, SUBCLASS_ID_VAL, &dwRef))
    {
        return; // Already installed
    }

    // Configure DWM attributes for this window
    // (SUCCEEDS because we are executing in the owning process!)
    BOOL val = TRUE;
    DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, &val, sizeof(BOOL));
    DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP,           &val, sizeof(BOOL));

    SetWindowSubclass(hwnd, LockedSubclassProc, SUBCLASS_ID_VAL, 0);
    DwmInvalidateIconicBitmaps(hwnd);
}

// ---------------------------------------------------------------------------
// Hook procedures (injected into target process by Windows hook mechanism)
// ---------------------------------------------------------------------------
static LRESULT CALLBACK HookCallWndProc(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode >= 0 && lp)
    {
        CWPSTRUCT* cwp = (CWPSTRUCT*)lp;
        if (cwp->hwnd && IsTarget(cwp->hwnd))
        {
            EnsureSubclassed(cwp->hwnd);

            if (cwp->message == WM_DWMSENDICONICTHUMBNAIL_MSG)
            {
                int maxW = HIWORD(cwp->lParam);
                int maxH = LOWORD(cwp->lParam);
                if (maxW <= 0) maxW = 200;
                if (maxH <= 0) maxH = 150;
                SendLockedThumbnail(cwp->hwnd, maxW, maxH);
            }
            else if (cwp->message == WM_DWMSENDICONICLIVEPREVIEWBITMAP_MSG)
            {
                SendLockedLivePreview(cwp->hwnd);
            }
        }
    }
    return CallNextHookEx(NULL, nCode, wp, lp);
}

static LRESULT CALLBACK HookCbtProc(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode >= 0)
    {
        HWND hwnd = (HWND)wp;
        if (hwnd && IsTarget(hwnd))
        {
            EnsureSubclassed(hwnd);
        }
    }
    return CallNextHookEx(NULL, nCode, wp, lp);
}

// ---------------------------------------------------------------------------
// DLL Injection Helper (CreateRemoteThread fallback)
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

    g_targets[slot] = hwnd;
    g_pids   [slot] = pid;

    // 1. Install thread hooks (WH_CALLWNDPROC and WH_CBT)
    HHOOK hookMsg = SetWindowsHookExW(WH_CALLWNDPROC, HookCallWndProc, g_hMod, threadId);
    HHOOK hookCbt = SetWindowsHookExW(WH_CBT,         HookCbtProc,     g_hMod, threadId);
    g_hooksMsg[slot] = hookMsg;
    g_hooksCbt[slot] = hookCbt;

    // 2. CRITICAL: Send a message to the target window so Windows immediately loads
    // the hook DLL into the target process and calls HookCallWndProc/EnsureSubclassed
    DWORD_PTR dwResult = 0;
    SendMessageTimeoutW(hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_NORMAL, 1000, &dwResult);
    DwmInvalidateIconicBitmaps(hwnd);

    // 3. Fallback: also attempt direct DLL injection for processes with restrictive hook policies
    TryInjectDll(pid);

    return TRUE;
}

extern "C" __declspec(dllexport)
void UninstallLockedThumbnail(HWND hwnd)
{
    int slot = FindSlot(hwnd);
    if (slot < 0) return;

    // 1. Tell target window subclass to restore its normal DWM thumbnail
    if (hwnd && IsWindow(hwnd))
    {
        SendMessageW(hwnd, g_wmUnlockTarget, 0, 0);
    }

    // 2. Unhook thread hooks
    if (g_hooksMsg[slot])
    {
        UnhookWindowsHookEx((HHOOK)g_hooksMsg[slot]);
        g_hooksMsg[slot] = NULL;
    }
    if (g_hooksCbt[slot])
    {
        UnhookWindowsHookEx((HHOOK)g_hooksCbt[slot]);
        g_hooksCbt[slot] = NULL;
    }

    g_targets[slot] = NULL;
    g_pids   [slot] = 0;
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

        g_wmLockTarget   = RegisterWindowMessageW(L"AppLock_LockTarget_Msg");
        g_wmUnlockTarget = RegisterWindowMessageW(L"AppLock_UnlockTarget_Msg");

        // When attached to a process, check if any target window belongs to this process
        // and subclass it immediately
        DWORD myPid = GetCurrentProcessId();
        for (int i = 0; i < MAX_TARGETS; ++i)
        {
            HWND target = (HWND)g_targets[i];
            if (target && IsWindow(target))
            {
                DWORD targetPid = 0;
                GetWindowThreadProcessId(target, &targetPid);
                if (targetPid == myPid)
                {
                    EnsureSubclassed(target);
                }
            }
        }
    }
    return TRUE;
}
