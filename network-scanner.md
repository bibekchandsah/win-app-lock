# Internet Access → Devices (Network Device Scanner) — Feature Spec for AppLock

This is an add-on to `instructions.md` and `connection-firewall.md`. Read both first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, auth rules and Fluent look as the other pages.

## 1. Idea

A third tab named **Devices** on the **Internet Access** page (next to **Apps** and **Active connections**). It finds the devices on the user's own Wi-Fi/LAN, shows them in a list with names, manufacturers and online status, remembers them, and shows an AppLock alert when an **unknown device joins**. The user can trust devices, give them nicknames, watch chosen ones for online/offline changes, and get help blocking a device at the router.

Light / Dark / System theme like every other page (theme resources only).

## 2. Scope: what this feature is, and is not

**In scope (version 1):**
- Discover devices on the PC's current local subnet; identify them by name, manufacturer and likely type.
- Keep a per-network device database with trust state, history and nicknames.
- Alert on new unknown devices, and on online/offline changes for devices marked "Watch".
- Router help: copy a device's MAC address, open the router's admin page.
- Extras: Wake-on-LAN, export, device details, merge duplicate devices (random MAC addresses).

**Phase 2 (listed here, built after version 1 works):**
- User-initiated **port check** and **risk hints** (section 9).
- **Router integration** through an adapter interface: read the router's DHCP client list for better names, then optional per-device speed limits and blocking on routers that support it (section 10).

**Explicitly out of scope, do not build, even if asked by a later prompt:**
- ARP spoofing/poisoning, man-in-the-middle, packet sniffing of other devices' traffic, de-authentication attacks, "kick device" buttons that work by attacking the network.
- Scanning anything other than a **private address range** (RFC 1918: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, plus 169.254.0.0/16 link-local) of an adapter the PC is actually connected to. Never scan public IP addresses or other people's networks.
- Per-device traffic statistics (a PC can't see other devices' traffic without being the gateway).
- Bypassing Wi-Fi client isolation, or seeing guest networks/VLANs the PC isn't on.

## 3. Placement and page layout

- The Internet Access page gets a third tab **Devices** (icon `` / `` or a device-list glyph). The existing elevation and firewall InfoBars stay on the page; scanning itself does **not** require administrator rights.
- **First-run notice** (ContentDialog the first time the tab opens): "Network scanning sends small requests to devices on your current network to find out who is connected. Use it only on networks you own or are allowed to manage. AppLock does not read the content of anyone's traffic." Buttons: **Enable scanning**, **Not now**. Scanning stays off until the user accepts.

### 3.1 Command bar
**Scan now** (F5) · **Auto-scan** toggle · **Trust all current devices** · **Export** (CSV/JSON) · **More** (Networks, Event history, Settings, Reset data).

### 3.2 Summary strip
Network name (SSID or adapter name), gateway IP, subnet (e.g. `192.168.1.0/24`), and counts: **Online · Total · Unknown · Watching**. A **Last scan** timestamp and a small progress ring during scans. A link **Open router page** (`http://<gateway>`, opens in the default browser) and a **Learn about this network** expander explaining the limits in section 11.

### 3.3 Device list
A searchable `ListView` (or DataGrid), one row per device, sortable, with filter chips **All · Online · Unknown · Trusted · Watching · Ignored · New (last 24 h)**. Columns:
- **Icon** (phone, laptop, TV, printer, camera, router, game console, speaker, IoT, unknown) and **Name** (nickname if set, else discovered hostname, else vendor + short MAC)
- **IP**, **MAC** (with a **Private address** badge when randomized, see 5.4), **Manufacturer**
- **Status** chip: Online / Offline (text + icon, not color only)
- **Trust** chip: Trusted / Unknown / Watching / Ignored
- **First seen**, **Last seen**, **Online for / Offline for**

Empty state: "No devices found yet. Press Scan now." Error states: not connected to a network, public network (see 5.2), subnet too large.

### 3.4 Details pane (right side or expander) for the selected device
- Editable **Nickname**, **Icon**, **Notes**; read-only IP, MAC (both colon and hyphen formats), vendor, hostname(s), discovered services (e.g. "AirPlay", "Chromecast", "Printer (IPP)"), response time, first/last seen.
- **History timeline** (joined, left, IP changed, name changed).
- **Actions:** Trust / Untrust · Watch / Stop watching · Ignore · Rename · **Merge with another device…** · **Ping** · **Wake-on-LAN** (only when the device is a trusted, offline device) · **Copy IP** · **Copy MAC** · **Open web page** (only offered after a probe found port 80/443 open; never guess) · **Router block help** (see 8) · **Forget device**.
- Phase 2 buttons appear here too, disabled with a "coming later" tooltip until built: **Check open ports**, **Limit speed (router)**, **Block (router)**.

## 4. Settings (SettingsExpander at the bottom of the tab or in the page's More menu)

- **Enable network scanning** (default off until the first-run notice is accepted).
- **Auto-scan interval**: 1, 5 (default), 15 minutes, or off. **Scan when the network changes** (default on). **Pause auto-scan on battery** (default on) and **while the screen is locked** (default on).
- **Public networks**: *Never auto-scan* (default), *Ask each time*, or *Treat as trusted*. Windows' network category (`INetworkListManager` / `NetworkInformation`) decides what "public" means; manual **Scan now** on a public network asks for confirmation.
- **Offline after** N missed scans (default 3) so a sleeping phone doesn't flap.
- **Alerts**: *New unknown device* (default on), *Watched device comes online / goes offline* (default on), *Network summary when 4+ new devices join within a minute* (default on, batch instead of a toast flood), **Quiet hours** (from–to), **Sound** (none / system / custom .wav), **Toast style** (follows the shared AppLock toast settings; see 6).
- **Require AppLock password to trust devices or turn alerts off** (default on; see 7).
- **History retention**: 30 / 90 (default) / 365 days / forever.
- **IPv6 neighbors**: off by default (phase 2).
- **Detailed logging** (default off; see 12).

## 5. Discovery engine

All discovery runs on background threads with cancellation and a hard cap on request rate (default 200 packets/second). Use only documented Windows APIs; no kernel drivers; no raw sockets in version 1.

### 5.1 Finding the scan range
1. Pick the **active adapter** (the one holding the default route, as in `speed-meter.md`; if Speed Meter's `NetworkStatsProvider` exists, reuse its adapter selection).
2. Read its IPv4 address and prefix length; derive the subnet. Verify the range is **private** (section 2). Refuse any adapter with a public address.
3. Cap the scan at **/22 (1,024 addresses)**. For larger subnets, offer "Scan only the first /24 around my IP" or "Cancel".
4. Identify the **network** for the database: gateway IP + gateway MAC (+ SSID when Wi-Fi, via the WLAN API or `ConnectionProfile`). This makes "home Wi-Fi" and "office Wi-Fi" separate device lists, and lets AppLock say "new network" when the gateway MAC is unfamiliar.

### 5.2 Layered discovery (combine results by MAC)
1. **ARP/neighbor sweep (main method):** for every address in the range, trigger ARP resolution with `SendARP` (iphlpapi) in parallel (concurrency about 64, per-request cancellation, because failed requests can take seconds), then read results from `GetIpNetTable2` (the neighbor table), keeping entries whose state is Reachable/Stale/Delay/Probe and ignoring Incomplete/Permanent/broadcast/multicast. ARP finds devices that ignore ping.
2. **ICMP echo** (optional, parallel, 500 ms timeout) for response time and as a second signal.
3. **Name discovery**, each independent and time-boxed (about 1–2 s per device; whole pass should not block the list from showing IPs first):
   - **Reverse DNS** (PTR) against the system DNS (often the router, which knows DHCP hostnames).
   - **mDNS** (UDP 5353, multicast `224.0.0.251`): a unicast PTR query for `x.x.x.x.in-addr.arpa` to each device, plus a service browse for common types (`_airplay._tcp`, `_googlecast._tcp`, `_ipp._tcp`, `_printer._tcp`, `_hap._tcp`, `_spotify-connect._tcp`, `_smb._tcp`, `_workstation._tcp`, `_companion-link._tcp`). Use a managed mDNS implementation (for example Makaretu.Dns or Zeroconf) rather than writing the protocol by hand.
   - **SSDP/UPnP** (UDP 1900, multicast `239.255.255.250`): `M-SEARCH`, then fetch each `LOCATION` XML (only from the same private IP) for `friendlyName`, `manufacturer`, `modelName`, `deviceType`.
   - **NetBIOS** name query (UDP 137, NBSTAT) for Windows PCs and many NAS devices.
4. **Manufacturer** from a bundled **offline IEEE OUI database** (MA-L/MA-M/MA-S) stored as an app asset; no network calls. Show "Unknown vendor" when missing. Updated with app releases only.
5. **Device type** heuristic from: mDNS service types, SSDP device type, vendor (Apple, Samsung, Google, Espressif → IoT, Hikvision → camera…), hostname patterns (`iPhone`, `Galaxy`, `Android-`, `DESKTOP-`, `PS5`, `Chromecast`…). Rule-based, with a confidence flag; the user can override the icon.
6. **Our own PC and the gateway** are always listed and labeled "This PC" and "Router/Gateway".

### 5.3 Identity model
- Primary key: **MAC address within a network**. A device record stores: id, networkId, MAC, current IP, hostnames, vendor, type, nickname, notes, trust state, watch flag, first/last seen, online state, `IsRandomMac`, optional `MergedIntoId`.
- IP address is **never** the identity (DHCP changes it); an IP change is an event, not a new device.

### 5.4 Random (private) MAC addresses: avoiding false alarms
- Modern iOS, Android and Windows use a **random MAC per Wi-Fi network** (often stable per network, but it can rotate). Detect them by the **locally administered bit** (second-least-significant bit of the first octet set) and mark them with a **Private address** badge; show vendor as "Private address (vendor hidden)".
- When a "new" random-MAC device appears, look for a likely previous match on the same network (same hostname or mDNS name, same device type) and show in the toast and details: **"Looks like 'Bibek's iPhone', which previously used a different private address"** with buttons **Same device (merge)** / **New device**.
- Merging moves history and trust to the older record and remembers the extra MAC as an alias, so future appearances of either MAC match.
- Let the user turn off the "Private address" grouping ("Treat every MAC as a separate device").

### 5.5 Online/offline state
- A device is **Online** when seen in a scan (ARP or ping). It becomes **Offline** after N consecutive missed scans (setting, default 3), so phones that sleep don't flap. Offline devices stay in the list with a "Last seen" time.
- Devices asleep may not answer ARP right away; the UI explains that "Last seen" is only as precise as the scan interval.
- Passive help between scans: reuse the neighbor-table change notifications (`NotifyIpInterfaceChange` / re-reading `GetIpNetTable2` on a short timer while the tab is open) to catch devices that join between scans.

### 5.6 Scheduling
A `NetworkScanService` owns scans: manual, on interval, on network change, on resume from sleep (with a short delay to let Wi-Fi reconnect). Only one scan at a time; a new request while scanning is coalesced. Auto-scans run while AppLock runs (tray); they stop when the setting is off, the PC is on battery (if set), the screen is locked (if set), or the network is public and not allowed.

## 6. Alerts and toasts

- **Baseline (first scan of a network):** don't alert for every device. Show a one-time banner: "Found 14 devices on 'HomeWiFi'. Mark them as trusted?" with **Trust all** (requires password if the setting is on) and **Review one by one**. Until the user decides, devices are "Unknown" but alerts for that network are suppressed for 10 minutes.
- **New unknown device alert:** a custom AppLock toast (reuse `ConnectionToastService`/the shared toast window from `speed-meter.md` when it exists; otherwise implement a small `IToastService` with a themed, rounded, topmost, non-activating window, and a native Windows toast fallback). Content: icon, "New device on HomeWiFi", name or vendor, IP, MAC, "Looks like …" hint for random MACs, time. Buttons: **Trust**, **Keep watching**, **Details**, and **Ignore**. The toast stays until dismissed for unknown devices (setting: auto-dismiss after N seconds), stacks up to 3, and batches into a summary beyond that.
- **Watched device online/offline:** short toast ("Kids' tablet is online"). Skip if the same event occurred in the last 2 minutes (debounce).
- **Network alerts:** "New network detected (gateway not seen before)". Optional.
- Quiet hours queue alerts and show one summary afterwards. Respect "Do not show while a full-screen app is running" (queue, then summarize).
- All alert actions from the toast follow the password rule in section 7.
- Screen-reader support: raise a UI Automation notification event with the alert text.

## 7. Security and AppLock rules

- Opening the Devices tab requires the AppLock session to be authenticated, like other pages.
- **Weakening actions need the AppLock admin password or Windows Hello** (cache 2 minutes), when "Require AppLock password…" is on (default): *Trust all current devices*, *Trust* from a toast or list, *turning alerts off*, *turning scanning off*, *Forget device*, *Reset data*. Viewing and marking a device as Unknown/Watching never needs it.
- The device database and settings use the same integrity protection as the rest of AppLock (HMAC over the trust list; tamper → show warning and treat the list as unverified).
- Router credentials (phase 2) are DPAPI-protected, shown never, and require the password to edit.
- No telemetry. The core feature makes only local network requests to private addresses. No cloud lookups (the vendor database is bundled). Log MACs only as **redacted** values (first 3 octets) unless "Detailed logging" is on.
- Hard guard in code: every outbound probe goes through a `PrivateAddressGuard` that rejects non-private destinations; unit-tested.
- Respect Windows Firewall: AppLock must not silently add firewall rules. If mDNS/SSDP replies are blocked by the user's firewall, show a note ("Some device names may be missing because the firewall blocks discovery replies") and a button that opens the Internet Access Apps tab.

## 8. Router help (version 1: always available)

Because only the router can disconnect or slow a device, version 1 gives the user the information needed to do it there:
- **Router block help** dialog for a selected device: device name, **MAC in three formats** (`AA:BB:CC:DD:EE:FF`, `AA-BB-CC-DD-EE-FF`, `aabbccddeeff`) each with a **Copy** button, the gateway IP with an **Open router page** button, and short generic steps ("Open your router's page → Wi-Fi/Access Control or Parental Controls → add this MAC to the block list or speed limit").
- A note for random MACs: "Block the *private* address shown here for this Wi-Fi network; on the device, setting 'Use device MAC' would change it."
- No block/limit buttons that pretend to work without a router adapter. They appear disabled until phase 2.

## 9. Phase 2 (a): user-initiated port check and risk hints

- **Check open ports** (button in details, never automatic): TCP connect scan of a short list (22, 23, 53, 80, 443, 445, 554, 3389, 5555, 8000, 8080, 8443, 9100) with 1 s timeout, concurrency 16, only against a **private** address the user selected, with a confirmation the first time. Show results and the service name.
- **Risk hints** from results (informational, never alarming): Telnet (23) open, RDP (3389) open, **Android Debug Bridge (5555) open**, RTSP (554) on a camera, SMB (445) on a non-PC, router admin page over HTTP only. Each hint explains what to do.
- Never scan more than the selected device; rate-limited; cancelable.

## 10. Phase 2 (b): router integration through an adapter interface

Design version 1 so this can be added without rewriting: define now (implementation later)

```csharp
public interface IRouterAdapter
{
    string Name { get; }
    Task<bool> DetectAsync(IPAddress gateway, CancellationToken ct);
    Task LoginAsync(RouterCredentials creds, CancellationToken ct);
    Task<IReadOnlyList<RouterClient>> GetClientsAsync(CancellationToken ct);   // read-only DHCP/Wi-Fi client list: names, MACs, signal
    Task SetRateLimitAsync(string mac, int? downKbps, int? upKbps, CancellationToken ct);
    Task RemoveRateLimitAsync(string mac, CancellationToken ct);
    Task SetBlockedAsync(string mac, bool blocked, CancellationToken ct);
    RouterCapabilities Capabilities { get; }                                    // which of the above are supported
}
```

Rules for the phase-2 work:
- Start with **read-only** (`GetClientsAsync`) to enrich names, catch devices that don't answer ARP and detect random-MAC devices correctly; add limits/blocks only after that works on a real router.
- **Pick the first adapter based on the router the user actually has** (candidates: OpenWrt via ubus/LuCI RPC, MikroTik RouterOS 7 REST API, ASUS routers with Merlin firmware, DD-WRT). Most ISP-supplied routers expose no usable interface; for those the tab stays at version-1 behavior (section 8).
- Research and test each adapter against the real router model before shipping it; document capabilities and tested firmware versions in `NOTES.md`.
- Credentials are stored per network (DPAPI, CurrentUser), edited only after password verification, and sent **only** to the gateway's private IP. For self-signed router HTTPS certificates, use trust-on-first-use pinning with a clear prompt.
- Speed limits and blocks show a confirmation dialog stating the device, MAC, and limit, and are **listed in an "AppLock-managed router rules" view** so the user can review and remove them.
- Bandwidth limiting for **apps on this PC** (Windows QoS policy is **upload-only**; download limiting needs a driver) is a separate feature with its own spec, not part of this scanner.

## 11. Honest limits shown in the UI ("Learn about this network" expander)

- Many phones use a different private MAC on each Wi-Fi network, so a known phone may look new until it's matched or merged.
- Only devices on **this** network segment appear. Guest Wi-Fi, other VLANs, and devices behind a second router aren't visible, and some routers use client isolation.
- Sleeping devices may not respond, so Online/Offline is approximate.
- AppLock cannot disconnect or slow other devices from the PC. Use the router (see Router block help).
- Device types and manufacturers are best-effort guesses.

## 12. Data model and storage

Store settings in the AppLock settings JSON; store networks, devices and events in a local **SQLite** database (`Microsoft.Data.Sqlite`) at `%APPDATA%\AppLock\Network\devices.db` with a schema version and migrations.

```csharp
public class NetworkScannerSettings
{
    public bool Enabled { get; set; }
    public bool AcceptedNotice { get; set; }
    public int AutoScanMinutes { get; set; } = 5;        // 0 = off
    public bool ScanOnNetworkChange { get; set; } = true;
    public bool PauseOnBattery { get; set; } = true;
    public bool PauseWhenLocked { get; set; } = true;
    public string PublicNetworkMode { get; set; } = "Never"; // Never|Ask|Trust
    public int OfflineAfterMissedScans { get; set; } = 3;
    public bool AlertNewDevice { get; set; } = true;
    public bool AlertWatched { get; set; } = true;
    public bool BatchAlerts { get; set; } = true;
    public string? QuietFrom { get; set; }               // "HH:mm"
    public string? QuietTo { get; set; }
    public string? SoundPath { get; set; }
    public bool RequirePasswordToTrust { get; set; } = true;
    public bool GroupPrivateMacs { get; set; } = true;
    public int RetentionDays { get; set; } = 90;
    public int MaxPacketsPerSecond { get; set; } = 200;
    public bool DetailedLogging { get; set; }
}

public class KnownNetwork { public Guid Id; public string Name; public string GatewayMac; public string GatewayIp; public string? Ssid; public bool TrustedForAutoScan; public DateTime FirstSeen, LastSeen; }

public class DeviceRecord
{
    public Guid Id; public Guid NetworkId; public string Mac; public List<string> AliasMacs = new();
    public string? Ip; public string? Hostname; public List<string> Services = new();
    public string? Vendor; public string DeviceType = "Unknown";
    public string? Nickname; public string? Notes; public string? IconOverride;
    public string Trust = "Unknown";          // Unknown|Trusted|Ignored
    public bool Watch; public bool IsRandomMac; public bool Online;
    public DateTime FirstSeen, LastSeen; public int MissedScans; public Guid? MergedIntoId;
}

public class DeviceEvent { public long Id; public Guid DeviceId; public Guid NetworkId; public DateTime Time; public string Kind; /* Joined|Left|IpChanged|NameChanged|Trusted|Merged… */ public string? Detail; }
```

Validate ranges on load; prune events older than the retention period; use WAL mode and atomic writes; "Reset data" deletes the file after confirmation and password check.

## 13. Project structure additions

```
src/AppLock/
  Views/DevicesTab.xaml(.cs)           // tab content inside InternetAccessPage
  Views/Dialogs/FirstRunNoticeDialog, RouterBlockHelpDialog, MergeDeviceDialog, EventHistoryDialog, NetworksDialog
  ViewModels/DevicesViewModel.cs, DeviceItemViewModel.cs
  Services/Network/
    NetworkScanService.cs            // scheduling, coalescing, pause rules
    SubnetResolver.cs                // adapter, subnet, gateway, network id, private-range guard
    ArpScanner.cs                    // SendARP sweep + GetIpNetTable2 reading
    NameResolvers/ ReverseDnsResolver.cs, MdnsResolver.cs, SsdpResolver.cs, NetBiosResolver.cs
    OuiDatabase.cs                   // offline vendor lookup
    DeviceClassifier.cs              // type heuristics
    DeviceStore.cs                   // SQLite access
    DeviceTracker.cs                 // identity, merging, online/offline state machine, events
    NetworkAlertService.cs           // baseline, new-device logic, batching, quiet hours, toasts
    WakeOnLanService.cs
    PrivateAddressGuard.cs
    Routers/IRouterAdapter.cs        // interface only in version 1
  Models/DeviceRecord.cs, KnownNetwork.cs, DeviceEvent.cs, NetworkScannerSettings.cs
  Assets/oui.csv                      // IEEE OUI data (check its license/attribution, include notice in About)
tests/AppLock.Tests/Network/          // see 14
```

Define `IArpSource`, `INameResolver`, `INetworkInfoProvider`, `IClock`, `IDeviceStore` and `IToastService` so the logic can be tested without a real network.

## 14. Tests

Unit tests with fakes: private-range guard (rejects public, accepts RFC 1918/link-local), subnet math (/24, /22 cap, odd prefixes), OUI lookup and locally-administered-bit detection, identity/merge logic, offline-after-N-missed-scans, baseline behavior (no alert flood), batching, quiet hours, watched-device debounce, alert password rule, SQLite migration, MAC formatting, and Wake-on-LAN packet bytes (6×0xFF + 16×MAC).

## 15. Quality rules

- Localize all strings; keyboard accessible; screen-reader names for chips and actions; status never by color alone; high-contrast friendly.
- UI stays responsive: scans on background threads; show IP-only results first, then fill in names and vendors as they arrive.
- Performance: a /24 scan finishes in about 10 s on a normal network and stays under about 5% CPU; no scanning at all when the tab and alerts are off.
- Log to `%APPDATA%\AppLock\logs` with MACs redacted by default; no telemetry.

## 16. Milestones (build, run, verify each before moving on)

1. **Devices tab shell** in the Internet Access page: first-run notice, empty state, command bar, summary strip skeleton; theme-correct (Light/Dark/System). `SubnetResolver` + `PrivateAddressGuard` showing network, gateway and subnet.
2. **ARP sweep and device list:** `ArpScanner`, list with IP/MAC/status, Scan now, progress; this PC and gateway labeled. Verify against the router's own client list.
3. **Names, vendors, types:** reverse DNS, mDNS, SSDP, NetBIOS, offline OUI database, `DeviceClassifier`; icons.
4. **Database and tracking:** SQLite store, identity by MAC, first/last seen, online/offline state machine, event history, networks list.
5. **Trust and alerts:** baseline flow, Trust/Unknown/Watching/Ignored, new-device toast with actions, watched online/offline toasts, batching, quiet hours, password rule.
6. **Random MAC handling:** private-address badge, "looks like" suggestions, merge dialog and aliases.
7. **Auto-scan and scheduling:** interval, network-change and resume triggers, battery/lock pauses, public-network rules.
8. **Details pane and extras:** nickname/icon/notes, ping, Wake-on-LAN, copy actions, Router block help dialog, export CSV/JSON, filters and search.
9. **Settings, tests, accessibility and theme pass, `NOTES.md`, manual checklist.**
10. *(Phase 2)* Port check and risk hints; then the router adapter work in section 10, starting with read-only on the user's real router.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 17. Manual test checklist

- [ ] Devices tab appears in Internet Access; first-run notice shows; scanning does nothing until accepted; Light, Dark, System themes look right
- [ ] Scan finds this PC, the router and the phones/TVs known to be connected; the count roughly matches the router's client list; scanning works without administrator rights
- [ ] Names and manufacturers appear for common devices (phone, TV, printer); unknown ones show "Unknown vendor"
- [ ] Baseline prompt appears on the first scan of a network; **Trust all** asks for the AppLock password; no toast flood
- [ ] Connect a new phone to the Wi-Fi: a "New device" toast appears with Trust/Watch/Details; Trust updates the list
- [ ] A phone using a private MAC is badged correctly; after toggling its private address setting, the "looks like …" merge hint appears and merging keeps history
- [ ] Watched device online/offline toasts work; sleeping phones don't cause flapping alerts (offline after 3 missed scans)
- [ ] Switching to another Wi-Fi creates a separate device list; public network never auto-scans (and asks on manual scan)
- [ ] Quiet hours queue alerts; full-screen suppression works; 4+ new devices produce one summary
- [ ] Wake-on-LAN wakes a trusted PC that has it enabled; Ping, Copy IP/MAC, Router block help (three MAC formats, open router page) work
- [ ] Export CSV/JSON is correct; history retention prunes old events; Reset data needs the password
- [ ] Nothing is scanned outside private ranges (try a VPN or hotspot with a public address); no requests leave the local network; logs contain redacted MACs only
- [ ] Turning scanning off stops timers and network traffic (check with Resource Monitor); disabling alerts or trusting devices asks for the password when the setting is on
