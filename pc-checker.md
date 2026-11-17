# Others → PC Checker — Feature Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, elevation helper, auth rules and Fluent look as the other **Others** pages.

## 1. Idea

**PC Checker** is a read-only, all-in-one system information tool inside AppLock. It shows every hardware and software detail Windows can report about this PC, from the big things (CPU, RAM, GPU, storage) to the minor ones (BIOS date, memory module part numbers, display EDID data, driver dates, PATH problems), so the user does not need CPU-Z, GPU-Z, HWiNFO, CrystalDiskInfo, Speccy, msinfo32, Device Manager or Task Manager just to look something up.

It also turns the raw data into useful answers: a health summary with plain-language issues, a "what changed" history, a Windows 11 readiness check, a requirement checker for games and apps, shareable reports, and a few simple built-in tests.

Light / Dark / System theme like every other page (theme resources only).

### 1.1 Coverage goal (be honest about "everything")
"Everything" means **everything Windows exposes to a normal app, plus optional extras that need administrator rights or a hardware driver**. Every field falls into one of three tiers, and the UI says which one when a value is missing:

| Tier | Needs | Examples |
|---|---|---|
| **1 — Standard** | Nothing special (default) | OS, CPU model/cores/cache, RAM modules, disks, GPU/driver, displays, network, installed software, services |
| **2 — Administrator** | AppLock running elevated | Disk SMART details, some firmware tables, full process details, some event logs |
| **3 — Driver-based sensors (optional)** | Admin + a hardware-access driver, off by default, explicit consent | CPU/GPU/board temperatures, fan speeds, voltages, power draw |

A value that can't be read shows **"Not available"** with the reason ("needs administrator rights", "the device doesn't report this", "needs the sensor driver", "not supported on this Windows version"), never a blank or a guess.

## 2. Scope

**In scope:** reading and displaying system information, health checks and recommendations, live performance view, stability history (crashes, errors), reports/export, snapshots and comparison, compatibility checks, and simple opt-in tests (benchmarks, display/keyboard/mouse/gamepad/audio tests).

**Out of scope, do not build:** overclocking, fan control, BIOS/firmware flashing, installing or updating drivers, registry "cleaners" or "optimizers", uninstalling apps, uploading hardware data anywhere, online benchmark comparisons, full Windows product-key recovery, saved Wi-Fi passwords, reading personal files, browser data, or message contents. The page is **read-only** except for explicit exports and tests the user starts.

## 3. Navigation and placement

- New card in the **Others** card registry: **PC Checker** (a computer/chip glyph from Segoe Fluent Icons), description "Every detail about your hardware and software, with a health check", status text like "Healthy · 2 notes · scanned 5 min ago". Registry order value **80** (… Speed Meter 70, **PC Checker 80**).
- Clicking the card opens the **PC Checker page** inside the Others frame with a `BreadcrumbBar` (Others › PC Checker) and back button.
- Inside the page: a **category rail** on the left (collapses to a dropdown on narrow windows) and the content area on the right, with a **global search box** at the top that searches every field, value and table across all categories (for example "serial", "AVX2", "DDR5", "KB5034").
- Works with the main window closed to the tray only for background snapshots (see 9.3); otherwise nothing runs when the page isn't open.

### 3.1 Category rail (in this order)
1. **Overview** 2. **Operating system** 3. **Motherboard & firmware** 4. **Processor** 5. **Memory** 6. **Storage** 7. **Graphics** 8. **Displays** 9. **Audio** 10. **Network** 11. **Battery & power** 12. **Devices & peripherals** 13. **Sensors** 14. **Software** 15. **Security** 16. **Performance (live)** 17. **Stability & events** 18. **Health check** 19. **Tools** 20. **Compatibility** 21. **Reports & history**

### 3.2 Common controls (every category)
- **Refresh** (F5), **Copy section** (as text), **Export section**, and a **Details level** selector: *Basic* (the important fields), *Detailed* (default), *Everything* (adds minor fields, raw values and IDs).
- **Hide identifiers** toggle (serial numbers, UUIDs, MAC addresses, IP addresses, user and computer names): when on, they're masked with a click-to-reveal eye icon, which makes screenshots safe. Default **off**, remembered.
- Each property shows: label, value, a **copy** button, a status icon when relevant (OK / Info / Warning / Problem, with text for accessibility), and a **"?"** tooltip that explains what it means in one plain sentence, plus **source** (for example "Source: WMI Win32_BIOS") in the *Everything* level.
- Lists (apps, devices, services, drivers…) are sortable, filterable tables with **Copy row**, **Export CSV**, and column chooser.

## 4. Page content: what each category shows

Every field below is a requirement for the **Everything** level; the **Basic** level shows the bold fields only.

### 4.1 Overview
A dashboard of tiles (click to jump to the category): **Operating system**, **Processor**, **Memory**, **Graphics**, **Storage**, **Displays**, **Network**, **Battery**, **Security**, **Uptime & last boot**. At the top, a **health summary** (*All good* / *Needs attention (N)* / *Problems (N)*) with the issue list from section 8, and quick actions: **Rescan**, **Copy summary** (a short text block for pasting into a chat or forum), **Save report**, **Compare with last snapshot**.

### 4.2 Operating system
**Windows name and edition**, **version (e.g. 24H2) and full build number with revision (UBR)**, **architecture**, **install date**, **last boot time and uptime**, **activation status** (licensed or not; only the **last 5 characters** of the product key, like `slmgr /dli`; never the full key), computer name, domain/workgroup, current user and whether it's an administrator, other local user accounts (names and enabled state only), system locale, UI language, time zone and clock sync status, keyboard layouts, boot mode (**UEFI/Legacy**), Secure Boot state, pending reboot (and why), Windows Update status (last check, last install, pending updates, active hours), installed updates/hotfixes (KB number, date, description, install result), optional Windows features enabled (Hyper-V, WSL, Windows Sandbox, .NET, SMB…), WSL distributions and versions, Developer Mode, long-path support, Windows Experience (WinSAT results if present), power plan, hibernation and Fast Startup state, supported sleep states (`powercfg /a` equivalent), system restore status and restore points, page file settings and usage, special folders and install paths, system environment variables, system DPI/scaling.

### 4.3 Motherboard & firmware
**Manufacturer, model, version**, board serial (identifier), **BIOS/UEFI vendor, version, release date**, SMBIOS version, firmware type, system manufacturer/model/SKU/family, system UUID (identifier), **chassis type** (desktop, laptop, all-in-one, mini PC, tablet…), **chipset** (from PCI devices), **TPM** (present, version 1.2/2.0, manufacturer, firmware version, enabled/activated/ready), Secure Boot and Platform Key state, firmware virtualization state (VT-x/AMD-V, IOMMU/VT-d when reported), PCIe slots and their usage (SMBIOS), memory slot map, onboard devices (SMBIOS type 10/41), ACPI table names/OEM IDs, Dell/HP/Lenovo-specific asset fields when present in SMBIOS (asset tag, service tag as identifier).

### 4.4 Processor
**Name, vendor, family/model/stepping, microarchitecture/codename** (best-effort from a bundled table; labeled as a guess when unsure), **socket**, **physical cores and logical processors**, **hybrid layout** (performance vs efficiency cores via processor efficiency class), **base, max boost and current clock** (current is per core in the live view), bus speed, **cache sizes** (L1 instruction/data, L2, L3 and sharing), processor groups and NUMA nodes, **instruction set support** (SSE up to SSE4.2, AVX, AVX2, AVX-512 variants, AES-NI, SHA, FMA, BMI1/2, POPCNT, RDRAND/RDSEED, VT-x/AMD-V, SLAT, NX/DEP, Hyper-V presence), CPUID vendor/brand string and feature leaves in *Everything*, microcode revision, **virtualization enabled in firmware**, security mitigations status (Spectre/Meltdown/MDS/etc. as reported by the OS), hypervisor present, power/thermal state if reported, TDP and process node only when the bundled table is confident, per-core usage in the live view.

### 4.5 Memory
**Total installed, usable, in use, available**, committed/limit, cached, standby, modified, paged and non-paged pool, hardware reserved, compression, page-file usage. **Per module (slot by slot):** **capacity, type (DDR3/4/5, LPDDR), form factor (DIMM/SODIMM/soldered), rated speed and configured speed**, manufacturer, **part number**, serial (identifier), voltage (min/max/configured), rank, data width/total width (ECC), bank and slot locator, channel layout (**single vs dual channel**, estimated), memory-profile hint (XMP/EXPO likely off when configured speed is below rated speed), maximum capacity supported by the board and number of free slots (SMBIOS), last Windows Memory Diagnostic result from the event log.

### 4.6 Storage
**Physical disks:** **model, size, type (NVMe SSD / SATA SSD / HDD / USB / virtual), interface and bus**, firmware version, serial (identifier), partition style (GPT/MBR), removable/read-only, spindle speed (HDD), PCIe link speed and lanes for NVMe when reported, write-cache and TRIM state, disk number and Windows device path. **Partitions and volumes:** drive letter, label, file system, **size, used, free**, cluster size, boot/system/recovery roles, BitLocker state (with a link to the BitLocker page), compression, indexing, optimization (defrag/TRIM) state and last run, mount points, network and mapped drives, optical drives, VHD/VHDX mounts, Storage Spaces pools. **Health (SMART/NVMe log; Tier 2):** overall health, **temperature**, **power-on hours**, power cycles, **percentage used / wear level**, available spare, **total data written/read**, reallocated and pending sectors, read/write errors, unsafe shutdowns, critical warning flags, each with a plain-language reading ("Wear 3%: very healthy"). Free-space and volume-growth notes in the health check.

### 4.7 Graphics
For each GPU (integrated and discrete): **name, vendor, device and revision IDs (PCI)**, **driver version, date and provider**, WDDM version, **dedicated VRAM, shared memory, total**, live VRAM use, DirectX version and **Direct3D feature level**, Direct3D 12 capabilities (resource binding, ray tracing tier, mesh shaders, variable-rate shading, DirectML/compute), **Vulkan** (instance/API version, device list), OpenGL and OpenCL versions when available, **hardware-accelerated GPU scheduling** (on/off), per-app GPU preference list, video encode/decode codec support (H.264, HEVC, VP9, AV1), HDR support, outputs connected, PCIe link generation/width when reported, live usage per engine (3D, compute, copy, video decode/encode). Temperatures, fan, clocks and power appear only in **Sensors** (4.13).

### 4.8 Displays
For each monitor, from EDID and the display configuration: **name, manufacturer, model**, EDID version, **native resolution**, **current resolution, refresh rate, color depth**, list of supported modes, physical size and **diagonal in inches**, pixel density (PPI), scaling %, orientation, **connection type** (HDMI/DisplayPort/eDP/USB-C…), **HDR and wide-color support and state**, variable refresh rate/adaptive sync flags, color profile (ICC) in use, manufacture week/year, serial (identifier), position in a multi-monitor layout (drawn as a mini map), primary monitor, Night Light state. A **Display test** tool lives under Tools (see 4.19).

### 4.9 Audio
Playback and recording devices (MMDevice API): **name, default/default-communications marking, state**, endpoint type (speakers, headphones, HDMI, Bluetooth), driver/provider/version, sound controller/codec, format (sample rate, bit depth, channels), supported formats, exclusive-mode setting, spatial-sound mode, current volume and mute state, microphone privacy permission state. A short **Audio test** lives under Tools.

### 4.10 Network
**Adapters:** name, type (Ethernet/Wi-Fi/Bluetooth/VPN/virtual), **status, link speed**, **MAC (identifier)**, driver/version/date, **IPv4/IPv6 addresses, subnet, gateway, DNS servers, DHCP server and lease times**, MTU, metered state, connection profile and network category (Public/Private). **Wi-Fi:** SSID, BSSID (identifier), PHY type (Wi-Fi 5/6/6E/7), band, channel and width, signal strength, security type, link speed, country code, available-networks count, saved profile **names only** (never passwords). **Bluetooth adapter:** radio version/LMP, LE and LE Audio support, state. Also: proxy settings, hosts-file entry count, firewall profiles state, routing table, ARP/neighbor table, DNS cache count, listening-ports and Network-devices shortcuts (links to the Internet Access page tabs). Public IP lookup is a button that works **only when the user presses it** (it makes one network request, with a note saying so). Simple tools (ping, trace route, DNS lookup) run only when the user starts them.

### 4.11 Battery & power
Present/absent, **chemistry, manufacturer**, serial (identifier), **design capacity, full-charge capacity, wear %** (with reading: "Wear 8%: healthy"), **cycle count** (when reported), current charge %, charging/discharging state, **charge/discharge rate**, voltage, estimated time remaining, AC adapter status and rated wattage when reported, battery-saver state, power mode (best efficiency / balanced / best performance), power plan, modern-standby support, lid/power-button actions, battery usage history and sleep-study drainers parsed from the Windows battery report (optional button that generates the report). Desktop PCs show UPS devices if present, or "No battery".

### 4.12 Devices & peripherals
A **Device Manager equivalent**: all Plug and Play devices grouped by class with **status, problem code and its explanation**, manufacturer, **driver provider, version, date, signed or not**, hardware IDs, install date, location, resources (IRQ, memory ranges where available), power-management settings, parent/child tree view. **Problem devices** (error codes, disabled, unknown devices) are listed at the top and feed the health check. Specific views: **USB** (tree with controllers, hubs, devices, VID/PID, **USB version and negotiated speed**, power draw, serial as identifier), **Thunderbolt/USB4 ports**, **Bluetooth** (paired devices, connected state, class), **HID** (keyboards, mice, touchpads incl. Precision Touchpad, touchscreens, pens, game controllers), **cameras** (name, driver, supported resolutions; no camera preview here), **printers and scanners** (driver, port, state), **biometric/Windows Hello devices**, **smart-card readers**, **sensors** (accelerometer, ambient light, gyro), docks, SD card readers.

### 4.13 Sensors (Tier 3, optional)
Off by default. Turning it on shows a clear consent screen: sensor readings need administrator rights and a hardware-access driver; **AppLock never installs a driver silently**; the driver can be removed again from the same screen. When enabled: **CPU package and per-core temperatures, CPU power, per-core clocks and voltages, GPU temperature/clock/fan/power (via vendor libraries such as NVML where installed), motherboard and chipset temperatures, fan speeds, voltages, disk temperatures, battery temperature**, each with **current, min, max and average over the session**, live charts, thresholds that warn (default CPU 90 °C, GPU 90 °C, disk 70 °C, configurable), alerts through the shared AppLock toast, CSV logging, and a °C/°F setting. Without the driver the section still shows what Windows reports without one (disk temperature via SMART, battery temperature, GPU engine usage, ACPI thermal-zone readings labeled "approximate") and explains the limit.

### 4.14 Software
- **Installed apps:** name, version, publisher, **install date, size, install location, architecture (x86/x64/ARM64)**, source (classic installer, Microsoft Store/MSIX, winget, portable if detectable). Search, sort, filter ("recent", "large", "no publisher"). *Read-only*; the **Uninstall** button opens Windows' own Installed apps settings page for that app.
- **Startup items:** registry Run keys (machine and user), Startup folders, logon scheduled tasks, auto-start services, with **enabled/disabled state and startup impact** (from Windows' own startup-impact data when available). Read-only in version 1, with a button that opens the Task Manager startup tab.
- **Services:** name, display name, **state, start type, logon account, binary path**, description, dependencies, flagged when the binary is outside `Windows` and `Program Files` or has no valid signature.
- **Drivers:** kernel and user drivers with version, date, provider, signed status; **unsigned or very old drivers** are flagged.
- **Processes (live):** name, PID, **CPU, memory, disk, network**, path, user, parent, start time, threads, handles, publisher, signed status. Read-only; **Open file location** and **Copy**. (An **End task** button is *phase 2*, with confirmation, critical-process protection and the AppLock password.)
- **Scheduled tasks:** name, path, trigger, last run and result, author; non-Microsoft tasks highlighted.
- **Runtimes and developer tools:** .NET SDKs/runtimes, Visual C++ redistributables, **Java, Python, Node.js, Git, Docker, WSL, PowerShell versions, Android SDK/platform-tools/ADB/emulator, Visual Studio and VS Code versions**, DirectX runtime, **WebView2 runtime**, Windows App SDK runtime, winget version. A **PATH checker** lists each PATH entry and flags entries that don't exist, duplicates and very long PATHs.
- **Updates:** Windows update history (KB, date, result code and meaning, including failed updates).
- **Other:** installed fonts (count and list), codecs/Media Foundation transforms, Windows features, default browser, environment variables (user and system, secret-looking values masked).
- **Never** shown: browser extensions/history, document names, credentials, or any file contents.

### 4.15 Security
**Windows Security status:** Defender real-time protection, signature version and age, last scan, tamper protection, other registered antivirus products; **firewall** state per profile; SmartScreen; **UAC level**; **Secure Boot**; **TPM**; **BitLocker/Device Encryption** per volume (link to the BitLocker page); **memory integrity (HVCI), virtualization-based security, Credential Guard, Kernel DMA protection**; Windows Hello availability; update status; **local administrator accounts and the guest account state**; Remote Desktop and Remote Assistance state; SMBv1 state; AutoPlay; open network shares; speculative-execution mitigations. Each item has OK / Warning / Problem status and a one-line recommendation (see section 8). Shortcuts to the Internet Access (firewall, listening ports, network devices) and BitLocker pages.

### 4.16 Performance (live)
Live charts (last 60 seconds, 1 s default) updating **only while the page is visible**: **CPU** (total and per core, with current clock per core, boost/throttle indication), **memory** (in use, committed, cached, compression), **disk** (per physical disk: throughput, IOPS, latency, queue length, active time), **network** (per adapter), **GPU** (per engine and VRAM), plus uptime, process and thread counts, handle count, context switches, and a **top 5 processes** panel by CPU, memory, disk and network. Source: PDH counters, `GetSystemTimes`, `GlobalMemoryStatusEx`, IP Helper, DXGI/PDH GPU counters (reuse providers from `speed-meter.md` when present).

### 4.17 Stability & events
A plain-language **reliability timeline** (days on the x-axis, each day with a stability index and markers) built from: Windows Reliability history (`Win32_ReliabilityRecords`), event logs and minidumps. Lists with filters and **explanations**: **blue screens (BugCheck code with its name, e.g. `0x0000009F DRIVER_POWER_STATE_FAILURE`, plus faulting driver when known)**, **unexpected shutdowns (Kernel-Power 41)**, **application crashes and hangs**, **hardware errors (WHEA-Logger: CPU, memory, PCIe, cache)**, **disk errors (bad blocks, controller resets, NTFS corruption events)**, **GPU driver resets (TDR)**, **failed Windows updates**, and **boot/shutdown performance** (last boot duration, slow startup apps, from Diagnostics-Performance events). Event-log access can need administrator rights for some logs (Tier 2); the UI shows which ones were readable.

### 4.18 Health check (see section 8 for rules)
The full list of findings (Problems, Warnings, Notes, and Passed checks), each with **what it means, why it matters, and what to do**, with links to the relevant category. **Rescan** and **Copy findings**.

### 4.19 Tools (all opt-in, started by the user)
- **Display test:** full-screen solid colors, gradients, grayscale steps, checkerboard, sharpness text, and a moving-box refresh test, for dead pixels and uniformity. Esc exits.
- **Keyboard test** (key map lights up as pressed, with scan codes; stuck keys), **mouse test** (clicks, double-click, wheel, drag), **gamepad tester** (buttons, sticks, triggers, rumble), **audio test** (left/right tone, speaker test, live microphone level meter), **touch test** (multi-touch points).
- **Disk speed test:** sequential and random read/write on a chosen drive using one temporary file (size selectable 256 MB–4 GB), with a warning about SSD wear being minimal, a cancel button, and deletion of the file afterwards.
- **CPU and memory quick benchmark:** single-thread and multi-thread integer/floating-point loops and memory bandwidth; results show as a **relative score** saved in history ("not comparable with other tools"), plus a thermal-throttling indication (clock drop during the run).
- **Battery test** is not included (too long-running); the battery report covers it.
- A **stress test** is *not* in version 1 (needs strict temperature safety).

### 4.20 Compatibility
- **Windows 11 readiness check:** CPU 64-bit, ≥ 2 cores and ≥ 1 GHz, **supported CPU generation** (best-effort from a bundled list, labeled as such), RAM ≥ 4 GB, storage ≥ 64 GB, **UEFI with Secure Boot capable**, **TPM 2.0**, DirectX 12 with WDDM 2.x, display > 9″ and 720p, with a pass/fail per item and how to fix.
- **Requirement checker:** the user enters (or picks from a small saved list) a program's or game's minimum/recommended requirements: OS version, CPU cores and base clock, **RAM, VRAM, free disk space, DirectX/Vulkan version, instruction sets (AVX/AVX2), TPM/Secure Boot**, and PC Checker compares each numeric and feature requirement against this PC ("Pass / Below minimum / Meets recommended"). It does **not** claim to know GPU/CPU model rankings; model comparisons are left to the user and are listed as "check manually".
- **Upgrade hints:** free RAM slots, maximum supported RAM, free M.2/SATA ports when SMBIOS reports them, current vs rated memory speed.

### 4.21 Reports & history
See section 9.

## 5. Data collection architecture

### 5.1 Provider model
All data comes from small, independent providers so each category can be built, tested and extended on its own:

```csharp
public interface IInfoProvider
{
    string Id { get; }                       // "cpu", "memory", "storage.smart" …
    string Category { get; }
    InfoTier Tier { get; }                   // Standard | Admin | Driver
    bool IsSupported(SystemContext ctx);     // OS version, edition, elevation, hardware present
    Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct);
}

public class InfoSection { public string Id; public string Title; public List<InfoGroup> Groups; public List<InfoTable> Tables; public List<Finding> Findings; public DateTime CollectedAt; public List<Unavailable> Missing; }
public class InfoItem { public string Key; public string Label; public string? Value; public string? RawValue; public string? Unit; public Sensitivity Sensitivity; /* None|Identifier|Secret */ public Status Status; /* None|Ok|Info|Warning|Problem */ public string? Explain; public string? Source; public DetailLevel Level; }
```

A common renderer draws any `InfoSection`, so adding a field never requires new UI code. Unavailable fields carry a reason (`NeedsAdmin`, `NeedsDriver`, `NotReported`, `Unsupported`, `Error`).

### 5.2 Data sources (use documented APIs; no kernel driver in Tier 1 or 2)
- **CIM/WMI** (`Microsoft.Management.Infrastructure`): `Win32_OperatingSystem`, `Win32_ComputerSystem`, `Win32_BIOS`, `Win32_BaseBoard`, `Win32_SystemEnclosure`, `Win32_Processor`, `Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray`, `Win32_DiskDrive`, `Win32_LogicalDisk`, `MSFT_PhysicalDisk`/`MSFT_Disk`/`MSFT_Partition`/`MSFT_Volume`, `MSFT_StorageReliabilityCounter`, `Win32_VideoController`, `WmiMonitorID`, `Win32_Battery`/`root\WMI` battery classes, `Win32_PnPEntity`/`Win32_PnPSignedDriver`, `Win32_QuickFixEngineering`, `Win32_Service`, `Win32_StartupCommand`, `Win32_Product` is **not** used (it triggers MSI repairs), `Win32_Tpm`, `root\Microsoft\Windows\Defender` (`MSFT_MpComputerStatus`), `MSFT_NetAdapter`, `Win32_ReliabilityRecords`.
- **Native APIs:** `GetLogicalProcessorInformationEx` (cores, caches, NUMA, efficiency class), CPUID via `System.Runtime.Intrinsics.X86.X86Base.CpuId` (x86/x64) with ARM64 fallbacks from the registry, **SMBIOS** via `GetSystemFirmwareTable('RSMB')` parsed by a small, tested parser (memory devices, slots, board and chassis info), **DXGI/D3D12/D3D11-video** for GPU, VRAM, feature levels and codecs, `EnumDisplaySettingsEx` and `DisplayConfigGetDeviceInfo` + **EDID from the registry** (`Enum\DISPLAY\…\Device Parameters\EDID`) parsed by a tested EDID parser, MMDevice/Core Audio, `IOCTL_STORAGE_QUERY_PROPERTY` for disk properties and NVMe/ATA health logs (Tier 2), SetupAPI/CfgMgr32 for the device tree and problem codes, IP Helper for network, WLAN API for Wi-Fi, BCrypt/Windows Update Agent API for update history, `NtQuerySystemInformation` speculation-control information for mitigations, `powercfg` output **only as a fallback** (prefer APIs), the Windows event log API (`EventLogReader`) for stability, `vulkan-1.dll` loaded dynamically if present.
- **Registry:** installed apps (HKLM/HKCU Uninstall, 32- and 64-bit views), Run keys, StartupApproved, environment variables, CPU microcode revision, Windows version details, graphics preferences.
- No scraping of console tools' text output where an API exists. Never shell out with user-controlled strings.

### 5.3 Collection behavior
- **Lazy per category:** a quick Overview scan collects headline fields in parallel (target < 3 s); a category loads its full data when opened; a **Scan everything** action loads all categories (for reports and snapshots) with a progress bar and cancel.
- Each provider has its own timeout (WMI/CIM 8 s, native 3 s, event logs 15 s). A slow or failing provider never blocks the page: show what arrived, mark the rest "Still loading…" or "Not available" with the error reason, and offer **Retry**.
- Cache results in memory with a TTL (static data 10 min, dynamic data not cached); invalidate on device-change messages (`WM_DEVICECHANGE`), power events, and manual refresh.
- Concurrency limit (e.g. 4 WMI queries at a time); all work on background threads; the UI thread only applies results.
- Handle machines that lack a class or hardware (virtual machines, ARM64, Windows Home/N editions, servers) without exceptions: `IsSupported` and graceful "Not reported by this PC".
- **Units:** show capacity as GB/GiB per user setting (default: how Windows shows it); store raw bytes in `RawValue`.

## 6. Privacy and security

- **Everything stays on the PC.** The core feature makes **no network requests**. Optional, user-initiated actions (public IP, ping, trace route, DNS lookup) say what they contact before they run. No telemetry; no uploads of reports.
- Identifiers (serial numbers, UUIDs, MAC/BSSID, IP addresses, computer and user names, disk and monitor serials, partial product key) are tagged `Identifier` and handled by the **Hide identifiers** toggle and by **export redaction** (default on in exports; see 9.1).
- Secrets (full product keys, passwords, tokens, secret-looking environment values) are **never collected**, or are masked when unavoidable.
- Snapshots store identifiers only as **salted SHA-256 hashes** (per-install random salt), so hardware changes can be detected without keeping raw serial numbers on disk. Raw identifiers shown in the UI aren't written to disk or logs.
- Read-only: no driver install, no setting changes, no deletions. Tests write only a temporary file for the disk test and delete it.
- The Sensors driver step needs administrator rights, is clearly explained, is off by default, and **AppLock never silently installs or loads a driver**. Before shipping, verify the licence, signing and current security status of any sensor library or driver used, and note the decision in `NOTES.md`; if no acceptable driver exists, ship Sensors with the driver-free subset only.
- Opening the PC Checker page needs the AppLock session to be authenticated, like other pages. **Restart as administrator** uses the standard UAC prompt and the shared elevation helper (never ask for the Windows password).
- Escape all device and software strings in exports and anywhere HTML is rendered (names can contain `<`, `&`, quotes); cap lengths and strip control characters.
- Logs go to `%APPDATA%\AppLock\logs` and never contain identifiers or the contents of reports.

## 7. Settings (on the page's More menu)

- **Details level** default (Basic/Detailed/Everything), **Hide identifiers** default, **Units** (GB or GiB), **Temperature** (°C/°F), **Date format** (system default).
- **Live refresh interval** (0.5 / 1 / 2 s), **Charts history** (60 s / 5 min).
- **Automatic snapshots** (off / weekly / monthly) and **retention** (default last 12), **Notify when hardware or software changes are detected** (default on once snapshots are enabled).
- **Health check thresholds** (disk free %, battery wear %, temperatures, driver age) with **Reset to defaults**.
- **Sensors** (consent state, enable/disable, remove driver).
- **Export defaults:** redact identifiers (on), include software list (on), include event details (off).

## 8. Health check rules (start set; rules are data-driven and easy to extend)

Each rule produces a finding with **severity, short title, explanation, suggested action, and a link to the related category**. Tone is calm and practical; no scare wording, and no recommendation to buy or install any product.

| Area | Rule (examples) | Severity |
|---|---|---|
| Storage | Drive health warning/critical from SMART or NVMe log; wear > 80%; pending/reallocated sectors > 0 | Problem / Warning |
| Storage | Free space < 10% (Warning) or < 5% (Problem) on the system drive | Warning / Problem |
| Storage | System drive is a hard disk (HDD) | Note |
| Memory | Single-channel configuration with two or more modules possible; memory running below rated speed (profile not enabled) | Note |
| Memory | Memory pressure: committed > 90% of limit, or Windows Memory Diagnostic reported errors | Warning / Problem |
| Devices | Devices with problem codes, unknown devices, disabled devices | Warning |
| Drivers | Unsigned drivers; GPU driver older than 12 months (informational, without claiming a newer one exists) | Note |
| Battery | Wear > 40% (Note), > 60% (Warning); battery reported as "replace" | Note / Warning |
| Firmware | BIOS older than 3 years (Note, "check the manufacturer's site"); TPM missing or off; Secure Boot off; virtualization disabled when WSL/Hyper-V features are enabled | Note / Warning |
| Security | Defender off or signatures older than 7 days; firewall off for the current profile; UAC disabled; SMBv1 enabled; RDP enabled on a Public profile; no BitLocker on a laptop's system drive; guest account enabled | Warning / Problem |
| Updates | Pending reboot; no successful update for 60+ days; failed updates in the last 30 days | Note / Warning |
| Stability | Blue screens in the last 30 days; WHEA hardware errors; repeated disk errors; frequent unexpected shutdowns | Problem / Warning |
| Thermals (if sensors) | Temperature above threshold under load; throttling detected | Warning |
| Software | Missing/duplicate/long PATH entries; many startup items (> 20); services running from unusual paths | Note |
| Time | Clock not synchronized | Note |
| Windows | Windows version out of support | Warning |

Findings can be **dismissed per item** ("Don't show again", stored locally and shown in a "Dismissed" list). The Overview status is the worst active severity.

## 9. Reports, snapshots and comparison

### 9.1 Export
- **Formats:** HTML (single self-contained file, light/dark aware), **PDF** (print of the HTML report), **TXT** (plain, paste-friendly), **JSON** (structured), **CSV** (per table), and **Copy summary** (short text).
- **Scope:** whole report or selected categories; **level** (Basic/Detailed/Everything).
- **Privacy options:** *Redact identifiers* (default on: serials, UUIDs, MACs, IPs, names replaced by `••••`), *Include installed software*, *Include event details*. A preview shows exactly what will be exported. Never include secrets.
- Every report has a header: PC Checker version, generation time, Windows build, scope and redaction state.

### 9.2 Snapshots and comparison
- **Take snapshot** (manual) stores the full data set (identifiers hashed) in `%APPDATA%\AppLock\PcChecker\snapshots` as compressed JSON with a schema version.
- **Compare**: pick two snapshots (or "now") and see a diff grouped by category: **hardware changes** (disk added or replaced, RAM changed, GPU changed, monitor changed), **driver and BIOS updates**, **OS build changes**, **apps installed/removed/updated**, **services and startup changes**, and **health findings that appeared or disappeared**.
- **What changed** timeline on the Overview (last 30 days) built from automatic snapshots.

### 9.3 Automatic snapshots (optional)
When enabled, AppLock takes a quick snapshot (weekly/monthly) in the background at low priority while it's running in the tray, compares it with the previous one and, if something notable changed (new storage device, RAM change, new startup item, driver or BIOS version change), shows the shared AppLock toast ("PC Checker: 2 changes since last week") that opens the comparison. Never runs on battery below 30% and never during full-screen apps.

## 10. UX and quality rules

- **Layout:** property cards in a two-column responsive grid (single column on narrow widths), tables with virtualization for long lists (installed apps, devices, events can be thousands of rows), sticky section headers, keyboard navigation, `Ctrl+F` focuses search, `Ctrl+C` copies the focused value.
- **Charts:** use one lightweight charting approach (Win2D or LiveCharts2); update only while visible; no layout shifts as numbers change (tabular numerals).
- Status is never by color alone (icon + text). Full theme support (Light/Dark/System, live switching), high contrast, screen-reader names and **live-region announcements are throttled** for the live charts.
- Localize all strings through the existing resource file (hardware strings come from the system as-is); format dates, numbers and sizes by locale.
- Performance: the Overview opens in a few seconds even on slow disks; nothing runs in the background unless the page is open (or automatic snapshots/sensors are enabled); memory use stays modest when lists are large (virtualized UI, no caching of full event logs).
- Robustness: every provider is wrapped in try/catch and reports errors as "Not available (reason)"; log errors without personal data.

## 11. Data model and storage

```csharp
public class PcCheckerSettings
{
    public string DetailLevel { get; set; } = "Detailed";     // Basic|Detailed|Everything
    public bool HideIdentifiers { get; set; }
    public string SizeUnits { get; set; } = "Windows";        // Windows|GB|GiB
    public string TempUnit { get; set; } = "C";
    public double LiveIntervalSeconds { get; set; } = 1.0;
    public int ChartHistorySeconds { get; set; } = 60;
    public string AutoSnapshot { get; set; } = "Off";        // Off|Weekly|Monthly
    public int SnapshotRetention { get; set; } = 12;
    public bool NotifyOnChanges { get; set; } = true;
    public bool SensorsEnabled { get; set; }
    public int CpuTempWarn { get; set; } = 90;
    public int GpuTempWarn { get; set; } = 90;
    public int DiskTempWarn { get; set; } = 70;
    public bool ExportRedact { get; set; } = true;
    public bool ExportIncludeSoftware { get; set; } = true;
    public bool ExportIncludeEvents { get; set; }
    public List<string> DismissedFindings { get; set; } = new();
    public List<RequirementProfile> SavedRequirements { get; set; } = new();
}

public class PcSnapshot { public int SchemaVersion; public DateTime Taken; public string AppVersion; public string WindowsBuild; public Dictionary<string, InfoSection> Sections; /* identifiers hashed */ }
```

Settings live in the AppLock settings JSON (same integrity protection as the other sections); snapshots are separate compressed files. Validate ranges on load and migrate missing fields.

## 12. Project structure additions

```
src/AppLock/
  Views/PcCheckerPage.xaml(.cs), Views/PcChecker/ (CategoryRail, PropertyCard, InfoTableView, OverviewDashboard, HealthPanel, LiveCharts, ReportPreviewDialog, ComparisonView, RequirementEditor, SensorsConsentDialog)
  Views/PcChecker/Tools/ DisplayTestWindow, KeyboardTestPage, MouseTestPage, GamepadTestPage, AudioTestPage, TouchTestPage, DiskSpeedTestDialog, BenchmarkDialog
  ViewModels/PcChecker/ PcCheckerViewModel, CategoryViewModel, HealthViewModel, LivePerformanceViewModel, ComparisonViewModel
  Services/PcChecker/
    InfoCollector.cs               // orchestration, timeouts, caching, concurrency, progress
    Providers/ OsProvider, FirmwareProvider, CpuProvider, MemoryProvider, StorageProvider, StorageHealthProvider, GpuProvider, DisplayProvider, AudioProvider, NetworkProvider, BatteryProvider, DevicesProvider, SoftwareProvider, SecurityProvider, StabilityProvider, SensorsProvider
    Parsers/ SmbiosParser, EdidParser, BugCheckTable, PortableCpuId
    HealthRules/ RuleEngine, rules.json
    LivePerformanceService.cs
    SnapshotService.cs, SnapshotDiffer.cs, ReportExporter.cs (HTML/TXT/JSON/CSV, redaction, escaping)
    CompatibilityService.cs (Windows 11 readiness, requirement checker)
    Tools/ DiskSpeedTest, CpuBenchmark, MemoryBenchmark
    ElevationHelper (shared)
  Assets/PcChecker/ cpu-codenames.json, bugcheck-codes.json, port/driver/help text, win11-supported-cpus.json
  Models/PcChecker/ InfoSection, InfoItem, Finding, PcSnapshot, PcCheckerSettings
tests/AppLock.Tests/PcChecker/ (see 13)
```

Define `ICimSession`, `IRegistryReader`, `ISystemApi`, `IEventLogSource`, `IClock` and `IFileSystem` so providers and rules are testable on any machine with canned data (including virtual machines, laptops and ARM64 samples).

## 13. Tests

Unit tests with fakes and **sample data files**: SMBIOS parser (memory device, slot and board records from real dumps), EDID parser (multiple real EDIDs), BugCheck table, size/units formatting (GB vs GiB), identifier hashing and redaction, HTML/CSV export escaping (names containing `<script>`, quotes, commas), snapshot diff (disk replaced, RAM added, app updated), health rules (each rule at and around thresholds), dismissed-findings behavior, requirement checker comparisons, Windows 11 readiness logic, provider timeout and partial-failure behavior, settings migration, and "unsupported hardware" paths (VM without battery/TPM, desktop without battery, ARM64 CPU).

## 14. Milestones (build, run, verify each before moving on)

1. **Framework and shell:** PC Checker card (order 80), page with category rail, global search, details-level selector, hide-identifiers toggle; `IInfoProvider` framework, `InfoCollector`, common renderer; Overview tiles with **OS, CPU, memory, GPU and disk basics**; theme-correct (Light/Dark/System).
2. **Operating system** and **Motherboard & firmware** (SMBIOS parser, TPM, Secure Boot, chassis).
3. **Processor** (topology, caches, instruction sets, hybrid cores, microcode, mitigations) and **Memory** (per-module details, channel/profile hints).
4. **Storage** (disks, partitions, volumes) and **storage health** (SMART/NVMe; Tier 2 with the elevation banner).
5. **Graphics** and **Displays** (DXGI/D3D12, EDID parser, display map).
6. **Audio, Network** (adapters, Wi-Fi, Bluetooth) and **Battery & power**.
7. **Devices & peripherals** (device tree, problem devices, USB, HID, cameras, printers).
8. **Software** (installed apps, startup, services, drivers, runtimes/dev tools, PATH checker, updates, processes view).
9. **Security** category.
10. **Performance (live)** and **Stability & events**.
11. **Health check** rule engine, findings, dismissal, Overview summary.
12. **Reports & history:** export (HTML/PDF/TXT/JSON/CSV), redaction, snapshots, compare, what-changed timeline, automatic snapshots.
13. **Tools** (display/keyboard/mouse/gamepad/audio/touch tests, disk speed, CPU/memory benchmark) and **Compatibility** (Windows 11 readiness, requirement checker, upgrade hints).
14. **Sensors** (driver-free subset first, then the optional driver-based mode after the licence/signing decision in section 6).
15. Tests, accessibility and theme pass, performance check, `NOTES.md` (which fields need admin or a driver, what was unavailable on the test PCs), manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 15. Manual test checklist

- [ ] PC Checker card appears in Others after Speed Meter; page opens; breadcrumb/back work; Light, Dark, System themes look right and switch live
- [ ] Overview loads in a few seconds; tiles match Task Manager and Settings → System → About (CPU, RAM, GPU, disk, OS build)
- [ ] CPU details match CPU-Z/Task Manager (cores, threads, caches, base/boost, instruction sets); a hybrid Intel CPU shows P and E cores
- [ ] Memory modules match the physical sticks (capacity, speed rated vs configured, part number); dual-channel hint is correct
- [ ] Disks, partitions and free space match Disk Management; SMART/NVMe health, temperature, wear and power-on hours match CrystalDiskInfo when elevated, and show "needs administrator rights" when not
- [ ] GPU name, VRAM, driver version/date, DirectX feature level and Vulkan version match dxdiag/GPU-Z; monitors show native resolution, refresh rate, size, HDR state and connection type correctly (including a second monitor)
- [ ] Network adapters, IP/DNS/gateway and Wi-Fi details match `ipconfig /all` and `netsh wlan show interfaces`; no saved Wi-Fi passwords anywhere
- [ ] Laptop: battery design vs full-charge capacity, wear %, cycle count match the Windows battery report; desktop shows "No battery"
- [ ] Device list shows a deliberately disabled device as a problem and in the health check; USB speeds show correctly for USB 2 and 3 devices
- [ ] Installed apps match Settings → Apps; startup items match Task Manager; services match services.msc; PATH checker flags a non-existent folder you add
- [ ] Security category matches Windows Security (Defender, firewall, memory integrity, TPM, Secure Boot, BitLocker); recommendations are calm and accurate
- [ ] Live charts update smoothly, stop when leaving the page (CPU drops), per-core and GPU engine charts work; top-5 panel matches Task Manager
- [ ] Stability shows past blue screens, unexpected shutdowns and WHEA errors on a PC that has them; BugCheck codes have readable names
- [ ] Health check findings appear for a nearly full drive, a disabled device and Secure Boot off (test with a VM or temporary changes); dismissing a finding persists
- [ ] Hide identifiers masks serials/UUIDs/MACs/IPs/names with click-to-reveal; exports are redacted by default; HTML/TXT/JSON/CSV/PDF all open correctly; a device name containing `<script>` is escaped
- [ ] Snapshots and compare show a changed RAM/disk/app/driver correctly; automatic snapshot notification appears after a notable change; raw serial numbers are not found in the snapshot files
- [ ] Display test, keyboard/mouse/gamepad/audio/touch tests work; disk speed test deletes its temp file and can be cancelled; benchmark shows a relative score and no crash on slow PCs
- [ ] Windows 11 readiness and requirement checker give correct pass/fail and don't claim GPU/CPU model rankings
- [ ] Sensors stay off until consent; the driver-free subset works; no driver is installed without the explicit step; temperatures match HWiNFO within a few degrees when the driver mode is enabled
- [ ] Virtual machine, desktop, laptop and (if available) ARM64 PCs all open every category without exceptions, showing "Not reported by this PC" where hardware is absent
- [ ] Nothing runs when the page is closed (check CPU), and no network requests happen unless the user presses the opt-in buttons
