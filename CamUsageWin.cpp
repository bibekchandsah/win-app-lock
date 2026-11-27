// CamUsageWin.cpp
// Windows Desktop app (Win32) that shows LIVE and recent webcam usage.
//
// Sources of information
//   1) LIVE  : Media Foundation Sensor Activity Monitor (same API Island uses).
//              Windows pushes a callback whenever a camera starts/stops streaming and tells us
//              which process IDs are streaming. WebView2 child processes are resolved to the host app.
//   2) HISTORY: HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam
//              (+ \NonPackaged for classic desktop apps).
//
// Design (optimized / robust version)
//   * Event driven: no 1-second polling. Refresh is triggered by
//         - the sensor callback (camera started/stopped),
//         - RegNotifyChangeKeyValue on the webcam registry key (dedicated watcher thread),
//         - a slow 5 s safety timer (re-checks stale entries, retries a failed sensor start).
//     Triggers are debounced (150 ms) so bursts cause only one refresh.
//   * The list view is rebuilt only when the data actually changed (no flicker, no selection loss).
//   * Registry rule identical to Island:  in use  <=>  Start != 0 && Stop == 0, both REG_QWORD.
//     A missing/invalid Stop value counts as "not in use" (the old code treated it as 0 = in use).
//   * Stale protection: a desktop app marked in-use by the registry but with no running process is "Stale?".
//   * PID-reuse protection when walking from msedgewebview2.exe up to its parent.
//
// Build: Visual Studio 2022 -> Win32 Project (Empty), add this file, Unicode, C++14 or newer,
//        Windows SDK 10.0.18362+ (for the sensor-activity interfaces).
// Original programmer: Bob Paydar

#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <tlhelp32.h>
#include <mfapi.h>     // MFStartup
#include <mfidl.h>     // IMFSensorActivityMonitor & friends (SDK 10.0.18362+)

#include <algorithm>
#include <atomic>
#include <cwctype>
#include <functional>
#include <mutex>
#include <set>
#include <string>
#include <unordered_map>
#include <vector>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "mfplat.lib")

// ---------------------- Constants & IDs ----------------------
static const wchar_t kAppClass[] = L"CamUsageWin32App";
static const wchar_t kAppTitle[] = L"Camera Usage Viewer (Win32)";

#define IDC_LIST        1001
#define IDC_REFRESH     1002
#define IDC_CURONLY     1003
#define IDC_STATUS      1004

static const UINT     WM_APP_TRIGGER  = WM_APP + 1;   // "something changed, please refresh soon"
static const UINT_PTR TIMER_SLOW      = 1;            // 5 s safety net
static const UINT_PTR TIMER_DEBOUNCE  = 2;            // 150 ms trailing-edge debounce

static const wchar_t* const REG_WEBCAM_BASE =
    L"Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\webcam";

// ---------------------- Data Model --------------------------
struct CamRow {
    std::wstring kind;          // "LIVE (sensor)" | "Packaged" | "Desktop"
    std::wstring app;           // App key or friendly name
    std::wstring exe;           // Full path (Desktop / LIVE rows)
    bool         activeNow{ false };
    bool         stale{ false };// registry says "in use" but the process is gone
    ULONGLONG    startFt{ 0 };  // FILETIME (100 ns since 1601), UTC
    ULONGLONG    stopFt{ 0 };   // FILETIME, 0 = not available / still active
};

// Globals (UI thread unless noted)
static HINSTANCE g_hInst = nullptr;
static HWND g_hList = nullptr, g_hBtnRefresh = nullptr, g_hChkCurrent = nullptr, g_hStatus = nullptr;
static std::atomic<HWND> g_hMain{ nullptr };      // read from other threads to PostMessage
static std::wstring g_lastSig, g_lastStatus;      // change detection for list / status bar

// ---------------------- Small helpers -----------------------
static std::wstring ReplaceAll(const std::wstring& s, wchar_t from, wchar_t to) {
    std::wstring r = s;
    std::replace(r.begin(), r.end(), from, to);
    return r;
}

static std::wstring LeafName(const std::wstring& path) {
    size_t pos = path.find_last_of(L"\\/");
    return (pos == std::wstring::npos) ? path : path.substr(pos + 1);
}

static std::wstring ToLower(std::wstring s) {
    std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) { return (wchar_t)::towlower(c); });
    return s;
}

static ULONGLONG NowFileTime() {
    FILETIME ft{}; GetSystemTimeAsFileTime(&ft);
    return ((ULONGLONG)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}

static std::wstring FtToLocalString(ULONGLONG ft) {
    if (ft <= 1) return L"";                       // 0 = none, 1 = our "invalid" marker
    FILETIME ftUtc{};
    ftUtc.dwLowDateTime  = static_cast<DWORD>(ft & 0xFFFFFFFFULL);
    ftUtc.dwHighDateTime = static_cast<DWORD>(ft >> 32);

    SYSTEMTIME stUtc{};
    if (!FileTimeToSystemTime(&ftUtc, &stUtc)) return L"";

    SYSTEMTIME stLocal{};
    if (!SystemTimeToTzSpecificLocalTime(nullptr, &stUtc, &stLocal)) return L"";

    wchar_t buf[64];
    swprintf_s(buf, L"%04u-%02u-%02u %02u:%02u:%02u",
        stLocal.wYear, stLocal.wMonth, stLocal.wDay,
        stLocal.wHour, stLocal.wMinute, stLocal.wSecond);
    return buf;
}

// ---------------------- Process helpers ---------------------
struct ProcInfo { DWORD parent; std::wstring exe; };
typedef std::unordered_map<DWORD, ProcInfo> ProcMap;

static ProcMap SnapshotProcesses() {
    ProcMap m;
    HANDLE h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (h == INVALID_HANDLE_VALUE) return m;
    PROCESSENTRY32W e{}; e.dwSize = sizeof(e);
    if (Process32FirstW(h, &e)) {
        do { m[e.th32ProcessID] = ProcInfo{ e.th32ParentProcessID, e.szExeFile }; }
        while (Process32NextW(h, &e));
    }
    CloseHandle(h);
    return m;
}

// Full image path of a process (handles long paths). Empty if we may not open it (e.g. elevated).
static std::wstring PidToExePath(DWORD pid) {
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return L"";
    std::wstring result;
    std::wstring buf(512, L'\0');
    for (;;) {
        DWORD len = (DWORD)buf.size();
        if (QueryFullProcessImageNameW(h, 0, &buf[0], &len)) { result.assign(buf.c_str(), len); break; }
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER || buf.size() >= 32768) break;
        buf.resize(buf.size() * 2);
    }
    CloseHandle(h);
    return result;
}

static bool GetCreateTime(DWORD pid, ULONGLONG& t) {
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!h) return false;
    FILETIME c{}, e{}, k{}, u{};
    BOOL ok = GetProcessTimes(h, &c, &e, &k, &u);
    CloseHandle(h);
    if (!ok) return false;
    t = ((ULONGLONG)c.dwHighDateTime << 32) | c.dwLowDateTime;
    return true;
}

// WebView2 apps (WhatsApp, Teams, ...) stream from a child "msedgewebview2.exe". Walk up (max 6 hops)
// to the first ancestor that is not msedgewebview2.exe. Falls back to the original PID if the chain
// is broken (parent exited) or looks like a reused PID (a parent can't be younger than its child).
static DWORD ResolveHostPid(DWORD pid, const ProcMap& procs) {
    DWORD cur = pid;
    for (int hop = 0; hop < 7; ++hop) {
        auto it = procs.find(cur);
        if (it == procs.end()) return pid;
        if (_wcsicmp(it->second.exe.c_str(), L"msedgewebview2.exe") != 0) return cur;

        DWORD parent = it->second.parent;
        if (parent == 0 || parent == cur) return pid;
        ULONGLONG tc = 0, tp = 0;
        if (GetCreateTime(cur, tc) && GetCreateTime(parent, tp) && tp > tc) return pid;   // PID reuse
        cur = parent;
    }
    return pid;
}

// Is a process with this image path running? (unreadable paths -> trust the exe name)
static bool IsDesktopAppRunning(const std::wstring& exePath, const ProcMap& procs) {
    const std::wstring leaf = LeafName(exePath);
    for (const auto& kv : procs) {
        if (_wcsicmp(kv.second.exe.c_str(), leaf.c_str()) != 0) continue;
        std::wstring p = PidToExePath(kv.first);
        if (p.empty() || _wcsicmp(p.c_str(), exePath.c_str()) == 0) return true;
    }
    return false;
}

// ---------------------- Registry (history) ------------------
static bool RegGetQword(HKEY hKey, const wchar_t* valueName, ULONGLONG& out) {
    DWORD type = 0;
    ULONGLONG val = 0;
    DWORD cb = sizeof(val);
    LONG r = RegGetValueW(hKey, nullptr, valueName, RRF_RT_QWORD, &type, &val, &cb);
    if (r == ERROR_SUCCESS && cb == sizeof(val)) { out = val; return true; }
    return false;
}

// Enumerate sub-key names. Never loops forever, tolerates unreadable entries.
template <class F>
static void ForEachSubKey(HKEY parent, F&& fn) {
    int errors = 0;
    for (DWORD i = 0;; ++i) {
        wchar_t name[256];                       // registry key names are limited to 255 chars
        DWORD len = (DWORD)_countof(name);
        LONG r = RegEnumKeyExW(parent, i, name, &len, nullptr, nullptr, nullptr, nullptr);
        if (r == ERROR_NO_MORE_ITEMS) break;
        if (r != ERROR_SUCCESS) { if (++errors > 50) break; continue; }
        fn(std::wstring(name, len));
    }
}

// Island's exact rule: failed Start read => 0, failed Stop read => "not zero" (=> not in use).
static void ReadTimes(HKEY hKey, CamRow& row) {
    ULONGLONG start = 0, stop = 0;
    bool okStart = RegGetQword(hKey, L"LastUsedTimeStart", start);
    bool okStop  = RegGetQword(hKey, L"LastUsedTimeStop", stop);
    row.startFt   = okStart ? start : 0;
    row.stopFt    = okStop ? stop : 0;           // display only
    row.activeNow = okStart && okStop && (start != 0) && (stop == 0);
}

static void LoadRegistryRows(std::vector<CamRow>& out) {
    HKEY hBase = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, REG_WEBCAM_BASE, 0, KEY_READ, &hBase) != ERROR_SUCCESS) return;

    ForEachSubKey(hBase, [&](const std::wstring& name) {
        if (_wcsicmp(name.c_str(), L"NonPackaged") == 0) {
            HKEY hNp = nullptr;
            if (RegOpenKeyExW(hBase, L"NonPackaged", 0, KEY_READ, &hNp) != ERROR_SUCCESS) return;
            ForEachSubKey(hNp, [&](const std::wstring& raw) {
                HKEY hItem = nullptr;
                if (RegOpenKeyExW(hNp, raw.c_str(), 0, KEY_READ, &hItem) != ERROR_SUCCESS) return;
                CamRow row;
                row.kind = L"Desktop";
                row.exe  = ReplaceAll(raw, L'#', L'\\');
                row.app  = LeafName(row.exe);
                ReadTimes(hItem, row);
                RegCloseKey(hItem);
                out.push_back(std::move(row));
            });
            RegCloseKey(hNp);
        }
        else {
            HKEY hChild = nullptr;
            if (RegOpenKeyExW(hBase, name.c_str(), 0, KEY_READ, &hChild) != ERROR_SUCCESS) return;
            CamRow row;
            row.kind = L"Packaged";
            row.app  = name;
            ReadTimes(hChild, row);
            RegCloseKey(hChild);
            out.push_back(std::move(row));
        }
    });
    RegCloseKey(hBase);

    std::sort(out.begin(), out.end(), [](const CamRow& a, const CamRow& b) {
        if (a.activeNow != b.activeNow) return a.activeNow > b.activeNow;
        return a.startFt > b.startFt;
    });
}

// Registry watcher thread: wakes the UI whenever anything under the webcam key changes.
static HANDLE g_hStopEvt = nullptr, g_hRegThread = nullptr;

static DWORD WINAPI RegWatchProc(LPVOID) {
    HANDLE hEvt = CreateEventW(nullptr, FALSE, FALSE, nullptr);          // auto-reset
    if (!hEvt) return 0;
    HKEY hKey = nullptr;

    for (;;) {
        if (!hKey) {
            if (RegOpenKeyExW(HKEY_CURRENT_USER, REG_WEBCAM_BASE, 0, KEY_NOTIFY | KEY_READ, &hKey) != ERROR_SUCCESS) {
                hKey = nullptr;                                          // key may not exist yet: retry later
                if (WaitForSingleObject(g_hStopEvt, 5000) == WAIT_OBJECT_0) break;
                continue;
            }
        }
        if (RegNotifyChangeKeyValue(hKey, TRUE,
                REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, hEvt, TRUE) != ERROR_SUCCESS) {
            RegCloseKey(hKey); hKey = nullptr;
            if (WaitForSingleObject(g_hStopEvt, 5000) == WAIT_OBJECT_0) break;
            continue;
        }
        HANDLE waits[2] = { g_hStopEvt, hEvt };
        DWORD w = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (w == WAIT_OBJECT_0) break;                                   // stop requested
        if (w == WAIT_OBJECT_0 + 1) {
            HWND h = g_hMain.load();
            if (h) PostMessageW(h, WM_APP_TRIGGER, 0, 0);
        }
        else break;                                                      // unexpected error
    }
    if (hKey) RegCloseKey(hKey);
    CloseHandle(hEvt);
    return 0;
}

// ---------------------- Sensor Activity Monitor (live) ------
struct LiveCam { DWORD pid; std::wstring camera; };

static bool LiveLess(const LiveCam& a, const LiveCam& b) {
    return a.pid != b.pid ? a.pid < b.pid : a.camera < b.camera;
}
static bool LiveEq(const LiveCam& a, const LiveCam& b) { return a.pid == b.pid && a.camera == b.camera; }

class SensorCallback : public IMFSensorActivitiesReportCallback {
    LONG m_ref = 1;
public:
    std::mutex mtx;
    std::vector<LiveCam> live;          // sorted; currently streaming processes
    volatile LONG reports = 0;          // diagnostics: how many reports Windows sent

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFSensorActivitiesReportCallback) ||
            riid == __uuidof(IAgileObject)) {
            *ppv = static_cast<IMFSensorActivitiesReportCallback*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override {
        ULONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return r;
    }

    // Runs on a Media Foundation worker thread. Must never throw back into Windows.
    STDMETHODIMP OnActivitiesReport(IMFSensorActivitiesReport* report) override {
        try {
            std::vector<LiveCam> now;
            ULONG nReports = 0;
            if (report && SUCCEEDED(report->GetCount(&nReports))) {
                for (ULONG i = 0; i < nReports; ++i) {
                    IMFSensorActivityReport* act = nullptr;
                    if (FAILED(report->GetActivityReport(i, &act)) || !act) continue;

                    wchar_t camName[256] = L"";
                    ULONG written = 0;
                    act->GetFriendlyName(camName, (ULONG)_countof(camName), &written);
                    camName[_countof(camName) - 1] = L'\0';

                    ULONG nProc = 0;
                    if (SUCCEEDED(act->GetProcessCount(&nProc))) {
                        for (ULONG j = 0; j < nProc; ++j) {
                            IMFSensorProcessActivity* pa = nullptr;
                            if (FAILED(act->GetProcessActivity(j, &pa)) || !pa) continue;
                            BOOL streaming = FALSE; ULONG pid = 0;
                            if (SUCCEEDED(pa->GetStreamingState(&streaming)) && streaming &&
                                SUCCEEDED(pa->GetProcessId(&pid))) {
                                now.push_back(LiveCam{ pid, camName });
                            }
                            pa->Release();
                        }
                    }
                    act->Release();
                }
            }
            std::sort(now.begin(), now.end(), LiveLess);

            bool changed;
            {
                std::lock_guard<std::mutex> lk(mtx);
                changed = !(now.size() == live.size() &&
                            std::equal(now.begin(), now.end(), live.begin(), LiveEq));
                if (changed) live.swap(now);       // replace the whole list, exactly like Island
            }
            InterlockedIncrement(&reports);
            if (changed) {
                HWND h = g_hMain.load();
                if (h) PostMessageW(h, WM_APP_TRIGGER, 0, 0);
            }
        }
        catch (...) { /* swallow: exceptions must not cross the COM boundary */ }
        return S_OK;
    }
};

typedef HRESULT(WINAPI* PFN_MFCreateSensorActivityMonitor)(IMFSensorActivitiesReportCallback*,
                                                           IMFSensorActivityMonitor**);

static SensorCallback*           g_cb = nullptr;
static IMFSensorActivityMonitor* g_monitor = nullptr;
static bool                      g_mfStarted = false;
static bool                      g_sensorSupported = true;       // false => OS lacks the API, don't retry
static HRESULT                   g_sensorHr = E_FAIL;
static const wchar_t*            g_sensorStep = L"not started";

static void StopSensorMonitor() {
    if (g_monitor) { g_monitor->Stop(); g_monitor->Release(); g_monitor = nullptr; }
    if (g_cb) { g_cb->Release(); g_cb = nullptr; }
    if (g_mfStarted) { MFShutdown(); g_mfStarted = false; }
}

static void StartSensorMonitor() {
    if (g_monitor) return;                                       // already running

    g_sensorStep = L"LoadLibrary(mfsensorgroup.dll)";
    HMODULE h = LoadLibraryW(L"mfsensorgroup.dll");
    if (!h) { g_sensorHr = HRESULT_FROM_WIN32(GetLastError()); g_sensorSupported = false; return; }

    g_sensorStep = L"GetProcAddress(MFCreateSensorActivityMonitor)";
    auto create = (PFN_MFCreateSensorActivityMonitor)GetProcAddress(h, "MFCreateSensorActivityMonitor");
    if (!create) { g_sensorHr = HRESULT_FROM_WIN32(GetLastError()); g_sensorSupported = false; return; }

    g_sensorStep = L"MFStartup";
    g_sensorHr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(g_sensorHr)) return;
    g_mfStarted = true;

    g_sensorStep = L"MFCreateSensorActivityMonitor";
    g_cb = new (std::nothrow) SensorCallback();
    if (!g_cb) { g_sensorHr = E_OUTOFMEMORY; StopSensorMonitor(); return; }
    g_sensorHr = create(g_cb, &g_monitor);
    if (FAILED(g_sensorHr)) { g_monitor = nullptr; StopSensorMonitor(); return; }

    g_sensorStep = L"IMFSensorActivityMonitor::Start";
    g_sensorHr = g_monitor->Start();
    if (FAILED(g_sensorHr)) { StopSensorMonitor(); return; }

    g_sensorStep = L"running";
}

// LIVE rows keep a stable "first seen" time, so the row doesn't change every refresh.
static std::unordered_map<std::wstring, ULONGLONG> g_firstSeen;

static void BuildLiveRows(std::vector<CamRow>& rows, const std::function<const ProcMap&()>& getProcs) {
    if (!g_cb) { g_firstSeen.clear(); return; }
    std::vector<LiveCam> snap;
    { std::lock_guard<std::mutex> lk(g_cb->mtx); snap = g_cb->live; }
    if (snap.empty()) { g_firstSeen.clear(); return; }

    const ProcMap& procs = getProcs();
    const DWORD selfPid = GetCurrentProcessId();
    const ULONGLONG now = NowFileTime();
    std::set<std::wstring> seen;

    for (const auto& c : snap) {
        DWORD host = ResolveHostPid(c.pid, procs);
        if (host == selfPid) continue;                           // ignore ourselves

        std::wstring path = PidToExePath(host);
        if (path.empty()) {                                      // elevated/protected: use snapshot name
            auto it = procs.find(host);
            if (it != procs.end()) path = it->second.exe;
        }
        std::wstring key = ToLower(path.empty() ? (L"pid:" + std::to_wstring(host)) : path) + L"|" + c.camera;
        if (!seen.insert(key).second) continue;                  // several WebView2 procs -> one row

        auto fs = g_firstSeen.find(key);
        if (fs == g_firstSeen.end()) fs = g_firstSeen.emplace(key, now).first;

        CamRow r;
        r.kind      = L"LIVE (sensor)";
        r.exe       = path;
        r.app       = path.empty() ? (L"PID " + std::to_wstring(host)) : LeafName(path);
        if (!c.camera.empty()) r.app += L"  [" + c.camera + L"]";
        r.activeNow = true;
        r.startFt   = fs->second;
        r.stopFt    = 0;
        rows.push_back(std::move(r));
    }
    for (auto it = g_firstSeen.begin(); it != g_firstSeen.end();)   // forget sessions that ended
        it = seen.count(it->first) ? std::next(it) : g_firstSeen.erase(it);
}

// ---------------------- UI ----------------------------------
static void ListView_SetupColumns(HWND hList) {
    ListView_DeleteAllItems(hList);
    while (ListView_DeleteColumn(hList, 0)) {}

    LVCOLUMNW col{};
    col.mask = LVCF_TEXT | LVCF_WIDTH | LVCF_SUBITEM;

    col.pszText = const_cast<wchar_t*>(L"Kind");       col.cx = 100; col.iSubItem = 0; ListView_InsertColumn(hList, 0, &col);
    col.pszText = const_cast<wchar_t*>(L"App");        col.cx = 260; col.iSubItem = 1; ListView_InsertColumn(hList, 1, &col);
    col.pszText = const_cast<wchar_t*>(L"EXE");        col.cx = 360; col.iSubItem = 2; ListView_InsertColumn(hList, 2, &col);
    col.pszText = const_cast<wchar_t*>(L"Active");     col.cx = 70;  col.iSubItem = 3; ListView_InsertColumn(hList, 3, &col);
    col.pszText = const_cast<wchar_t*>(L"Last Start"); col.cx = 140; col.iSubItem = 4; ListView_InsertColumn(hList, 4, &col);
    col.pszText = const_cast<wchar_t*>(L"Last Stop");  col.cx = 140; col.iSubItem = 5; ListView_InsertColumn(hList, 5, &col);
}

static void ListView_Populate(HWND hList, const std::vector<CamRow>& src, bool currentOnly) {
    SendMessageW(hList, WM_SETREDRAW, FALSE, 0);                 // no flicker while rebuilding
    ListView_DeleteAllItems(hList);

    int i = 0;
    for (const auto& r : src) {
        if (currentOnly && !r.activeNow) continue;

        std::wstring active = r.activeNow ? L"Yes" : (r.stale ? L"Stale?" : L"No");
        std::wstring startS = FtToLocalString(r.startFt);
        std::wstring stopS  = FtToLocalString(r.stopFt);

        LVITEMW item{};
        item.mask    = LVIF_TEXT;
        item.iItem   = i;
        item.pszText = const_cast<wchar_t*>(r.kind.c_str());
        int idx = ListView_InsertItem(hList, &item);
        if (idx < 0) continue;

        ListView_SetItemText(hList, idx, 1, const_cast<wchar_t*>(r.app.c_str()));
        ListView_SetItemText(hList, idx, 2, const_cast<wchar_t*>(r.exe.c_str()));
        ListView_SetItemText(hList, idx, 3, const_cast<wchar_t*>(active.c_str()));
        ListView_SetItemText(hList, idx, 4, const_cast<wchar_t*>(startS.c_str()));
        ListView_SetItemText(hList, idx, 5, const_cast<wchar_t*>(stopS.c_str()));
        ++i;
    }
    SendMessageW(hList, WM_SETREDRAW, TRUE, 0);
    InvalidateRect(hList, nullptr, TRUE);
}

static void SetStatus(const std::wstring& text) {
    if (text == g_lastStatus) return;
    g_lastStatus = text;
    int parts[1] = { -1 };
    SendMessageW(g_hStatus, SB_SETPARTS, 1, (LPARAM)parts);
    SendMessageW(g_hStatus, SB_SETTEXT, 0, (LPARAM)text.c_str());
}

static void DoRefresh(bool force) {
    try {
        // Process snapshot is taken lazily, only if something needs it.
        ProcMap procs; bool haveProcs = false;
        std::function<const ProcMap&()> getProcs = [&]() -> const ProcMap& {
            if (!haveProcs) { procs = SnapshotProcesses(); haveProcs = true; }
            return procs;
        };

        std::vector<CamRow> reg;
        LoadRegistryRows(reg);

        // Stale protection for registry entries of desktop apps
        for (auto& r : reg) {
            if (r.activeNow && r.kind == L"Desktop" && !IsDesktopAppRunning(r.exe, getProcs())) {
                r.activeNow = false;
                r.stale = true;
            }
        }

        std::vector<CamRow> rows;
        BuildLiveRows(rows, getProcs);
        rows.insert(rows.end(), reg.begin(), reg.end());

        const bool curOnly = (Button_GetCheck(g_hChkCurrent) == BST_CHECKED);

        // Rebuild the list only when something visible changed
        std::wstring sig = curOnly ? L"1" : L"0";
        for (const auto& r : rows) {
            sig += r.kind; sig += L'\x1f'; sig += r.app; sig += L'\x1f'; sig += r.exe; sig += L'\x1f';
            sig += (r.activeNow ? L'Y' : (r.stale ? L'S' : L'N')); sig += L'\x1f';
            sig += std::to_wstring(r.startFt); sig += L'\x1f'; sig += std::to_wstring(r.stopFt); sig += L'\x1e';
        }
        if (force || sig != g_lastSig) {
            g_lastSig = sig;
            ListView_Populate(g_hList, rows, curOnly);
        }

        wchar_t st[512];
        if (g_monitor)
            swprintf_s(st, L"Ready - Bob Paydar | Live sensor: running | reports: %ld | rows: %zu",
                (long)(g_cb ? g_cb->reports : 0), rows.size());
        else
            swprintf_s(st, L"Ready - Bob Paydar | Live sensor: %s (hr=0x%08X) - registry only | rows: %zu",
                g_sensorStep, (unsigned)g_sensorHr, rows.size());
        SetStatus(st);
    }
    catch (...) {
        SetStatus(L"Refresh failed (out of memory?) - will retry");
    }
}

static void ResizeLayout(HWND hWnd) {
    RECT rc{}; GetClientRect(hWnd, &rc);

    SendMessageW(g_hStatus, WM_SIZE, 0, 0);
    RECT rcStatus{}; GetWindowRect(g_hStatus, &rcStatus);
    int sbHeight = rcStatus.bottom - rcStatus.top;

    int padding = 8;
    int btnW = 100, btnH = 28;
    int chkW = 140, chkH = 24;
    int topBarH = btnH + padding * 2;

    MoveWindow(g_hBtnRefresh, padding, padding, btnW, btnH, TRUE);
    MoveWindow(g_hChkCurrent, padding + btnW + 10, padding + 2, chkW, chkH, TRUE);

    int listY = topBarH;
    int listH = std::max<int>(0, rc.bottom - listY - sbHeight);
    MoveWindow(g_hList, 0, listY, rc.right - rc.left, listH, TRUE);
}

static void InitListView(HWND hList) {
    DWORD ex = ListView_GetExtendedListViewStyle(hList);
    ex |= LVS_EX_FULLROWSELECT | LVS_EX_GRIDLINES | LVS_EX_DOUBLEBUFFER;
    ListView_SetExtendedListViewStyle(hList, ex);
    ListView_SetupColumns(hList);
}

// ---------------------- Win32 Boilerplate -------------------
static LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_CREATE:
        g_hMain.store(hWnd);

        g_hBtnRefresh = CreateWindowExW(0, L"BUTTON", L"Refresh",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            0, 0, 0, 0, hWnd, (HMENU)IDC_REFRESH, g_hInst, nullptr);

        g_hChkCurrent = CreateWindowExW(0, L"BUTTON", L"Current only",
            WS_CHILD | WS_VISIBLE | BS_AUTOCHECKBOX | WS_TABSTOP,
            0, 0, 0, 0, hWnd, (HMENU)IDC_CURONLY, g_hInst, nullptr);

        g_hList = CreateWindowExW(WS_EX_CLIENTEDGE, WC_LISTVIEWW, L"",
            WS_CHILD | WS_VISIBLE | LVS_REPORT | LVS_SHOWSELALWAYS,
            0, 0, 0, 0, hWnd, (HMENU)IDC_LIST, g_hInst, nullptr);

        g_hStatus = CreateWindowExW(0, STATUSCLASSNAMEW, L"",
            WS_CHILD | WS_VISIBLE, 0, 0, 0, 0, hWnd, (HMENU)IDC_STATUS, g_hInst, nullptr);

        InitListView(g_hList);
        ResizeLayout(hWnd);

        StartSensorMonitor();                                  // live source (push, no polling)

        g_hStopEvt = CreateEventW(nullptr, TRUE, FALSE, nullptr);   // manual-reset
        if (g_hStopEvt) g_hRegThread = CreateThread(nullptr, 0, RegWatchProc, nullptr, 0, nullptr);

        DoRefresh(true);
        SetTimer(hWnd, TIMER_SLOW, 5000, nullptr);             // safety net only
        return 0;

    case WM_APP_TRIGGER:
        SetTimer(hWnd, TIMER_DEBOUNCE, 150, nullptr);          // coalesce bursts into one refresh
        return 0;

    case WM_TIMER:
        if (wParam == TIMER_DEBOUNCE) {
            KillTimer(hWnd, TIMER_DEBOUNCE);
            DoRefresh(false);
        }
        else if (wParam == TIMER_SLOW) {
            if (!g_monitor && g_sensorSupported) StartSensorMonitor();   // retry a failed start
            DoRefresh(false);                                  // re-evaluates stale entries
        }
        return 0;

    case WM_SIZE:
        ResizeLayout(hWnd);
        return 0;

    case WM_COMMAND:
        switch (LOWORD(wParam)) {
        case IDC_REFRESH:
            DoRefresh(true);
            return 0;
        case IDC_CURONLY:
            DoRefresh(false);                                  // signature includes the filter flag
            return 0;
        }
        break;

    case WM_DESTROY:
        KillTimer(hWnd, TIMER_SLOW);
        KillTimer(hWnd, TIMER_DEBOUNCE);
        g_hMain.store(nullptr);                                // background threads stop posting
        if (g_hStopEvt) SetEvent(g_hStopEvt);
        if (g_hRegThread) { WaitForSingleObject(g_hRegThread, 3000); CloseHandle(g_hRegThread); g_hRegThread = nullptr; }
        if (g_hStopEvt) { CloseHandle(g_hStopEvt); g_hStopEvt = nullptr; }
        StopSensorMonitor();
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hWnd, msg, wParam, lParam);
}

int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE, LPWSTR, int nCmdShow) {
    g_hInst = hInstance;

    // Island initialises COM as multithreaded before starting the sensor monitor.
    // (RPC_E_CHANGED_MODE just means COM was already initialised - harmless.)
    HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    INITCOMMONCONTROLSEX icc{ sizeof(icc), ICC_LISTVIEW_CLASSES | ICC_BAR_CLASSES };
    InitCommonControlsEx(&icc);

    WNDCLASSEXW wc{ sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hIcon = LoadIcon(nullptr, IDI_APPLICATION);
    wc.hIconSm = wc.hIcon;
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = kAppClass;

    int exitCode = 0;
    if (RegisterClassExW(&wc)) {
        HWND hWnd = CreateWindowExW(0, kAppClass, kAppTitle,
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT, 1050, 560,
            nullptr, nullptr, hInstance, nullptr);

        if (hWnd) {
            ShowWindow(hWnd, nCmdShow);
            UpdateWindow(hWnd);

            MSG msg{};
            while (GetMessageW(&msg, nullptr, 0, 0) > 0) {     // > 0: also stops on -1 (error)
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            exitCode = (int)msg.wParam;
        }
    }

    if (SUCCEEDED(hrCom)) CoUninitialize();
    return exitCode;
}
