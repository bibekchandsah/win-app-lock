# Detecting which app is using the camera (Windows) — as done by Island

Source analysed: `Island-setup.exe` (NSIS installer → `island.exe`, a Tauri/Rust app, ~13 MB).
Method: static analysis only (installer unpacked, strings + imports of `island.exe` inspected; nothing was executed).

> **Confidence note.** The binary is stripped Rust, so I did **not** disassemble the control flow.
> What is *confirmed* from the binary is listed under "Evidence". The exact decision logic
> (e.g. "Stop == 0 means in use", `#` → `\` decoding) is the documented behaviour of the Windows
> ConsentStore and is what the evidence implies — treat it as well-founded inference, not a copy of Island's source.

---

## 1. Evidence found in `island.exe`

| Finding | What it means |
|---|---|
| String `Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore` | Registry root it reads |
| Strings `webcam`, `microphone`, `LastUsedTimeStart`, `LastUsedTimeStop` (adjacent in the binary) | Sub-keys per capability + the two value names it reads. **These are exactly the values in your screenshot.** |
| Imports `RegOpenKeyExW`, `RegEnumKeyExW`, `RegQueryValueExW`, `RegGetValueW` | Enumerates sub-keys (one per app) and reads the values |
| Runtime-loaded `mfsensorgroup.dll` + `MFCreateSensorActivityMonitor`, plus `MFStartup`/`MFShutdown` | Uses the Media Foundation **Sensor Activity Monitor** (the same thing Windows 11 uses for the camera privacy indicator) to get notified when a camera starts/stops streaming |
| `shell:AppsFolder\` | Used to resolve/launch Store (packaged) apps by identity |
| `Microsoft.WindowsCamera`, `Microsoft.WindowsSoundRecorder` / "Sound Recorder", `msedgewebview2.exe` | Special-cased app names (built-in Camera / Sound Recorder apps and Island's own WebView2 helper — most likely friendly-name mapping or ignore list) |
| `OpenProcess`, `QueryFullProcessImageNameW`, `CreateToolhelp32Snapshot`, `GetProcessId` | Resolving a PID to an exe path/name |

## 2. How it works (two cooperating mechanisms)

### A. Registry ConsentStore — *who* is/was using the camera

Windows records every app's camera access under:

```
HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam
├── <PackageFamilyName>                 ← Store/UWP apps, e.g. Microsoft.WindowsCamera_8wekyb3d8bbwe
└── NonPackaged
    └── C:#Program Files#Google#Chrome#Application#chrome.exe   ← Win32 apps; '\' is replaced by '#'
```

(There is also a machine-wide copy under `HKLM\...\ConsentStore\webcam`; per-user data is in HKCU.)

Each app key has these values (your screenshot):

| Value | Type | Meaning |
|---|---|---|
| `LastUsedTimeStart` | REG_QWORD | FILETIME when the app last **started** using the camera |
| `LastUsedTimeStop` | REG_QWORD | FILETIME when it **stopped**. **`0` while the camera is still in use** |
| `Value` | REG_SZ | `Allow` / `Deny` (permission) |

**Rule: an app is using the camera right now when `LastUsedTimeStart > 0` and `LastUsedTimeStop == 0`**
(in your screenshot both are non-zero, so that app had already stopped).

Use `microphone` instead of `webcam` for the mic — same layout.

### B. Sensor Activity Monitor — *when* it changes (and the PID)

`MFCreateSensorActivityMonitor` calls you back whenever any camera's streaming state changes, and
the report lists each process using each camera (PID, streaming state, shared/controller mode).
Island uses this as the event source so it doesn't have to poll; the registry then gives the friendly app identity.
You can implement either mechanism alone. **Registry polling is the simplest and enough for most cases.**

## 3. Implementation (C# / .NET 8) — Option 1: registry (recommended starting point)

```csharp
using Microsoft.Win32;

public sealed record CameraUser(string Name, string Path, bool IsPackaged, DateTime? Started);

public static class CameraUsage
{
    private const string Root =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
        // use ...\ConsentStore\microphone for the mic

    public static List<CameraUser> GetActiveUsers()
    {
        var result = new List<CameraUser>();
        using var root = Registry.CurrentUser.OpenSubKey(Root);
        if (root is null) return result;

        foreach (var name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var np = root.OpenSubKey(name);
                if (np is null) continue;
                foreach (var appKey in np.GetSubKeyNames())
                    Check(np, appKey, packaged: false, result);
            }
            else
            {
                Check(root, name, packaged: true, result);
            }
        }
        return result;
    }

    private static void Check(RegistryKey parent, string keyName, bool packaged, List<CameraUser> result)
    {
        using var k = parent.OpenSubKey(keyName);
        if (k is null) return;

        long start = ToLong(k.GetValue("LastUsedTimeStart"));
        long stop  = ToLong(k.GetValue("LastUsedTimeStop"));

        // In use right now: started, and no stop time recorded yet
        if (start > 0 && stop == 0)
        {
            string path = packaged ? keyName : keyName.Replace('#', '\\');
            result.Add(new CameraUser(FriendlyName(path, packaged), path, packaged,
                                      DateTime.FromFileTimeUtc(start).ToLocalTime()));
        }
    }

    private static long ToLong(object? v) => v switch
    {
        long l => l,
        int i  => i,
        _      => 0
    };

    private static string FriendlyName(string path, bool packaged)
    {
        if (packaged) return path.Split('_')[0];              // e.g. Microsoft.WindowsCamera
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(vi.FileDescription)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : vi.FileDescription!;
        }
        catch { return System.IO.Path.GetFileNameWithoutExtension(path); }
    }
}
```

Polling loop (1 s is fine; the check is a handful of registry reads):

```csharp
var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
var previous = new HashSet<string>();
while (await timer.WaitForNextTickAsync(ct))
{
    var now = CameraUsage.GetActiveUsers();
    var nowSet = now.Select(u => u.Path).ToHashSet();

    foreach (var u in now.Where(u => !previous.Contains(u.Path)))
        OnCameraStarted(u);                       // show your indicator / notification

    foreach (var p in previous.Where(p => !nowSet.Contains(p)))
        OnCameraStopped(p);

    previous = nowSet;
}
```

**Event-driven instead of polling (optional):** P/Invoke `RegNotifyChangeKeyValue` on the `webcam` key with
`bWatchSubtree = true` and `REG_NOTIFY_CHANGE_LAST_SET`, wait on the event handle, then call `GetActiveUsers()`.
Windows updates these values as soon as an app opens/closes the camera.

## 4. Option 2: Sensor Activity Monitor (gives PID + instant events)

Requires Windows 10 1903+. Flow:

1. `MFStartup(MF_VERSION)` (from `mfplat.dll`).
2. `MFCreateSensorActivityMonitor(callback, out monitor)` — exported from **`mfsensorgroup.dll`** (load it dynamically, as Island does).
3. `monitor.Start()`.
4. In `IMFSensorActivitiesReportCallback.OnActivitiesReport(report)`:
   - `report.GetCount()` → for each `i`: `report.GetActivityReport(i)` → `IMFSensorActivityReport`
   - `GetFriendlyName()`, `GetSymbolicLink()` → which camera
   - `GetProcessCount()` → for each: `GetProcessActivity(j)` → `IMFSensorProcessActivity`
   - `GetProcessId()`, `GetStreamingState()` (true = streaming), `GetStreamingMode()` (Controller/Shared)
5. Map PID → exe with `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW` (that is why Island imports those).
6. `monitor.Stop()`, then `MFShutdown()` on exit.

The interfaces are declared in the Windows SDK header `mfidl.h` (section "Sensor activity"). In C# you need to
declare them as `[ComImport]` interfaces — **copy the IIDs/method order from the SDK header rather than from memory**,
because a wrong vtable order crashes silently. Easiest paths:

- **CsWin32** source generator: add `MFCreateSensorActivityMonitor`, `IMFSensorActivityMonitor`,
  `IMFSensorActivitiesReportCallback`, `IMFSensorActivitiesReport`, `IMFSensorActivityReport`,
  `IMFSensorProcessActivity` to `NativeMethods.txt`, or
- use it in a small C++/WinRT helper DLL and call that from your app.

Practical tip: use this monitor only as a *trigger* ("camera state changed → re-run `GetActiveUsers()`"),
and keep Option 1 as the source of truth for the app identity. That is the combination Island appears to use.

## 5. Gotchas

- **Stop = 0 can get stuck** if an app crashes while streaming. Cross-check that the process still exists
  (for non-packaged apps, `Process.GetProcesses()` where `MainModule.FileName` equals the path) before showing "in use".
- Non-packaged keys use `#` instead of `\` — a literal `#` in a real path is ambiguous but extremely rare.
- Packaged apps are keyed by **PackageFamilyName**, not exe path; resolve a display name through the package manager
  or launch via `shell:AppsFolder\<AUMID>`.
- Browsers/Electron/WebView2 apps show up as the **host exe** (e.g. `chrome.exe`, `msedgewebview2.exe`),
  not the website/tab. Island special-cases `msedgewebview2.exe` — so should you (skip your own process).
- Per-user data lives in HKCU; services/elevated apps may also appear under HKLM.
- Values are FILETIME (UTC, 100 ns ticks since 1601) — convert with `DateTime.FromFileTimeUtc`.
- Mic and camera are independent keys (`microphone`, `webcam`) — run the same code against both.
- This relies on undocumented-but-stable registry behaviour (the data behind Settings ▸ Privacy ▸ Camera
  "recently used"). Microsoft could change it; the Sensor Activity Monitor API is the officially documented route.
