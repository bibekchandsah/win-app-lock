# Internet Access → Listening Ports (Open Ports Monitor) — Feature Spec for AppLock

This is an add-on to `instructions.md` and `connection-firewall.md`. Read both first. Implement it in the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure, services, firewall service, auth rules and Fluent look as the Internet Access page.

## 1. Idea

The Internet Access page already has **Apps**, **Active connections** and **Network devices** tabs. Add a fourth tab, **Listening ports**, that shows which apps on this PC are **waiting for incoming connections** (open ports), who can reach each port, whether the Windows Firewall is likely to let that traffic in, and a one-click way to block it.

This answers "what on my PC can other devices connect to?", which is different from the Active connections tab ("who is my PC talking to right now?").

This spec also includes small improvements to the Active connections tab (section 10): hide local loopback connections by default and group rows by app.

Light / Dark / System theme like every other page (theme resources only).

## 2. Scope

**In scope:**
- Read the PC's own listening TCP ports and bound UDP ports (IPv4 and IPv6), with the owning process.
- Classify each port: who can reach it (localhost / local network / all interfaces), what the port is usually used for, and a short risk hint.
- Estimate firewall exposure from the Windows Firewall rules (clearly labeled as an estimate).
- Block inbound traffic for a port or for an app, using AppLock's own firewall rule group.
- Optional "Port watch": alert when a **new** port starts listening on an address other devices can reach.

**Out of scope, do not build:**
- Scanning other devices or ports from this tab. This tab only reads this PC's own sockets and never sends probe traffic. (Port checks on other devices belong to the Network devices feature, phase 2.)
- Killing processes or stopping services from this tab (see 6.4 for the safe alternatives).
- Sniffing traffic, packet capture, or reading connection contents.

## 3. Placement and page layout

- New tab **Listening ports** (icon `` / `` or a plug glyph) on the Internet Access page, placed after **Network devices**. The existing elevation/firewall InfoBars stay on the page. **Listing ports does not need administrator rights**; creating firewall rules does.
- Tabs (in order): Apps · Active connections · Network devices · **Listening ports**.

### 3.1 Summary strip
Counts: **Listening** (TCP, UDP), **Reachable from your network** (bound to a non-loopback address), **Localhost only**, **Blocked by AppLock**. A **Last updated** time and an auto-refresh indicator ("Auto-refreshing every 5 seconds").

### 3.2 Filters and options
- Search box: filter by app, path, port, protocol or address.
- Filter chips: **All · Reachable from network · Localhost only · Blocked · TCP · UDP · IPv4 · IPv6**.
- Toggles: **Group by app** (default on), **Show Windows system services** (default on), **Hide UDP** (default off).
- Sort: by app, port, scope or PID (stable sort; the list must not jump around on refresh).

### 3.3 Port list
Grouped by app (collapsible header: icon, app name, path, PID(s), and "3 ports"); in flat mode each row shows the app too. Each port row:
- **Port** and **protocol** (TCP/UDP), **bind address** (e.g. `0.0.0.0`, `::`, `127.0.0.1`, `192.168.1.71`)
- **Reach chip**, text plus icon (not color only):
  - **Localhost only** — bound to `127.0.0.0/8` or `::1`; other devices cannot connect.
  - **Local network** — bound to a specific private LAN address.
  - **All interfaces** — bound to `0.0.0.0` or `::`; reachable on every network the PC is on (home, public Wi-Fi, VPN) unless the firewall blocks it. Shown with the warning color.
- **Service name** (from a bundled port table, e.g. "SMB file sharing", "Remote Desktop", "Android Debug Bridge") and a short **hint** (see 5.3).
- **Firewall chip**: *Blocked by AppLock* · *Allowed by Windows rule "…"* · *Blocked by default (no allow rule)* · *Firewall off* · *Unknown* (see 5.2).
- Actions: **Block** (split button: *Block this port* / *Block this app's incoming traffic*), **More** (Open file location, Copy details, Show details).
- Windows services running in a shared host (`svchost.exe`) show the service names (e.g. "Windows Update Delivery Optimization (DoSvc)") instead of only "svchost".
- New ports (appeared since the last refresh) are highlighted briefly and carry a **New** badge for 60 seconds.

Empty/error states: "No listening ports found" (rare), and an InfoBar if some process details can't be read without administrator rights ("Some apps show as 'System' because Windows doesn't allow reading them. Restart as administrator to see more").

### 3.4 Details flyout (Show details)
App name, publisher (file `CompanyName`), full path, PID, user, started time, bind address and port, protocol, scope, service name, firewall estimate with the matching rule name and profile, and the list of AppLock rules that affect this port. **Do not show command lines** (they can contain secrets).

## 4. Settings

- **Refresh interval**: 3 / 5 (default) / 10 s while the tab is visible; no polling when the tab is not visible and Port watch is off.
- **Group by app**, **Show Windows system services**, **Hide UDP** (as above).
- **Port watch** (see 7), default off.
- **Require AppLock password to unblock/remove rules**: shared with the Internet Access page (default on).
- **Known/allowed ports** list (managed from the tab; see 7.2).

## 5. Engine

### 5.1 Reading listening sockets
- IP Helper API, no admin needed:
  - `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_LISTENER` for `AF_INET` and `AF_INET6` (IPv4 and IPv6 listeners with owning PID).
  - `GetExtendedUdpTable` with `UDP_TABLE_OWNER_PID` for `AF_INET` and `AF_INET6`. **UDP has no listening state**: every bound UDP socket is shown as an open UDP port, and the UI explains this ("UDP ports are shown when an app has opened them to receive data").
- Retry the call with a larger buffer on `ERROR_INSUFFICIENT_BUFFER`; handle the table changing while it is read.
- Resolve the process for each PID with `QueryFullProcessImageName` (using `PROCESS_QUERY_LIMITED_INFORMATION`), icon from the exe, and publisher from `FileVersionInfo.CompanyName`. For PIDs that can't be opened, show "System / protected process" and the name from the process list if available.
- **Service names:** map PID → service(s) with `EnumServicesStatusEx` (`SERVICE_STATUS_PROCESS`, process ID), so `svchost.exe` rows show which services own the ports.
- **Store/UWP apps:** resolve the package family name where possible and show the friendly name.
- Diff each refresh against the previous snapshot (key: protocol, local address, local port, PID) so the UI updates in place without flicker; mark new and removed entries.

### 5.2 Firewall exposure estimate
A port is only reachable from the network if it is bound to a non-loopback address **and** the firewall lets the traffic in. Windows Firewall normally blocks unsolicited inbound traffic unless an allow rule matches. Implement `FirewallExposureEvaluator` using the existing `IFirewallService` (read access, via `INetFwPolicy2`):
1. If the firewall is off for the current profile → *Firewall off* (treat as exposed).
2. Find enabled **inbound** rules that match the port: by protocol, local port (including ranges and `*`), application path, and the current profile(s); consider rules scoped to the **current** network profile (Domain/Private/Public), and the profile's **default inbound action**.
3. A matching **block** rule wins over any allow rule. Otherwise, a matching **allow** rule → *Allowed by Windows rule "<name>"*. No match and default inbound = Block → *Blocked by default*.
4. Windows normally does **not filter loopback traffic** (127.0.0.1 / ::1), so *Localhost only* ports aren't affected by firewall rules. Say this in the details flyout.
5. Mark the result **"estimate"** (tooltip: "AppLock estimates this from your firewall rules. Complex rules, third-party firewalls or group policy can change the real result."). If a third-party firewall is registered, show *Unknown*.

Cache the rule list and refresh it when firewall rules change (poll every 30 s while the tab is visible, or on `INetFwPolicy2` change events if available); evaluating must not block the UI.

### 5.3 Port names and hints (bundled table)
Ship a small offline table (JSON asset; written from scratch from public IANA/common-usage knowledge, no network lookups) with: protocol, port or range, service name, category, and an optional hint with severity (Info / Review / Important). Starter set: 21 FTP, 22 SSH, 23 Telnet, 53 DNS, 80/443 HTTP(S), 135 RPC endpoint mapper, 137–139 NetBIOS, 445 SMB, 1433 SQL Server, 1900 UPnP/SSDP (UDP), 3306 MySQL, 3389 Remote Desktop, 5037 ADB server, 5040 Windows device sharing, 5353 mDNS (UDP), 5432 PostgreSQL, 5555 Android Debug Bridge over network, 5900 VNC, 5985/5986 WinRM, 6379 Redis, 8000/8080/8081/3000/5173 development servers, 9200 Elasticsearch, 27017 MongoDB, and the dynamic RPC range 49152–65535 (shown as "Windows dynamic port"). Hints are factual and calm, e.g.:
- "Remote Desktop is open to your network. If you don't use it, block it."
- "Android Debug Bridge is reachable on the network. Anyone on this network could try to connect to ADB."
- "A development server is reachable from other devices on this network (bound to all interfaces). Bind it to localhost if you don't need that."
- "Listening only on localhost: other devices cannot connect."

Unknown ports show just the number; the table never claims certainty ("usually used for…").

## 6. Actions

### 6.1 Block this port
Creates an **inbound block rule** in AppLock's group `AppLock Connection Firewall` (same group, naming and description convention as `connection-firewall.md`): name `AppLock – Block port <port>/<TCP|UDP> (Inbound)`, description `AppLock-managed|id=<GUID>`, `Direction = In`, `Action = Block`, `Protocol = TCP/UDP`, `LocalPorts = <port>`, profiles = all (user can choose), no application path (blocks the port for any app). Record it as a `PortRuleEntry` (model in section 8) so the tab shows *Blocked by AppLock*, and so it can be removed.

### 6.2 Block this app's incoming traffic
Creates or updates the app's `NetworkRuleEntry` with `BlockInbound = true` (and leaves `BlockOutbound` as it is) through the existing `IFirewallService`; this is the same as choosing "Incoming" on the Apps tab. The row also appears on the Apps tab.

### 6.3 Safety rules
- Never create rules outside AppLock's rule group; never delete rules that aren't in it (same rule as the rest of the page).
- **Critical ports/apps warning** (confirmation dialog; allowed after confirming): ports 135, 139, 445 and the dynamic RPC range ("can break file and printer sharing and some Windows features"), and processes such as `svchost.exe`, `lsass.exe`, `services.exe`, `wininit.exe`, `System`, and AppLock itself. Show the same one-time explanation of what AppLock changes in the firewall as on the other tabs.
- **Block for apps in "Localhost only"**: blocking is allowed but explained as unnecessary ("Other devices can't reach this port anyway, and the firewall does not filter localhost traffic").
- Blocking never needs the AppLock password. **Unblocking, removing a rule, and "Remove all port rules" need the AppLock admin password or Windows Hello** (cache 2 minutes), as set by "Require AppLock admin password to unblock or remove rules".
- Not elevated: the Block buttons are disabled with the standard "Restart as administrator" InfoBar; viewing still works.
- After blocking, re-read the firewall rule to confirm, then update the chip. Note in the UI: a block affects **new** inbound connections; connections that are already established may continue until they end.

### 6.4 No "close port" button
AppLock does not end processes or stop services from this tab. Instead, **More** offers: **Open file location**, **Copy details** (as text), **Open Services** (for service-hosted ports, opens `services.msc`), and a hint ("To close a port permanently, stop the app or service that opens it, or change its settings to listen on localhost only").

## 7. Port watch (optional, default off)

### 7.1 Behavior
When enabled, AppLock keeps a snapshot of listening ports and alerts when a **new** port starts listening on an address reachable from the network (**All interfaces** or **Local network** scope) and the firewall estimate isn't *Blocked by AppLock/default*.
- Alert via the shared AppLock toast (reuse `ConnectionToastService`/`IToastService` from `speed-meter.md`/`network-scanner.md` when present; otherwise a small themed toast window with a native Windows toast fallback). Content: app name, port, protocol, scope, "New port open to your network". Buttons: **Block**, **Allow (don't alert again)**, **Details**.
- Ignore short-lived ports (opened and closed within 10 seconds) and ports in the 49152–65535 dynamic range from **Windows system services** by default, so alerts stay meaningful.
- **Baseline:** on first enabling, take a snapshot, show "Found 12 ports reachable from your network. Mark them as known?" with **Mark as known** (needs the AppLock password if "weakening" rules are on) or **Review**. Alerts are suppressed for 10 minutes after enabling.
- Batch multiple new ports within a minute into one summary toast; quiet hours (same setting as other alerts); suppress during full-screen apps and send a summary later.
- Only watches while AppLock is running (tray).

### 7.2 Known/allowed ports
A list (managed from the tab's More menu) of entries `(app path, protocol, port)`; ports in it never alert. Editing/removal needs the password when the setting is on. Export/import as JSON.

## 8. Data model

```csharp
public class ListeningPortsSettings
{
    public int RefreshSeconds { get; set; } = 5;
    public bool GroupByApp { get; set; } = true;
    public bool ShowSystemServices { get; set; } = true;
    public bool HideUdp { get; set; }
    public bool WatchNewPorts { get; set; }
    public bool WatchIgnoreSystemDynamic { get; set; } = true;
    public List<KnownPort> KnownPorts { get; set; } = new();
    public List<PortRuleEntry> PortRules { get; set; } = new();
}

public class KnownPort { public string AppPath; public string Protocol; public int Port; public DateTime Added; }

public class PortRuleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Protocol { get; set; } = "TCP";        // TCP|UDP
    public string LocalPorts { get; set; }                // "8080" or "8000-8100"
    public int Profiles { get; set; } = int.MaxValue;     // NET_FW_PROFILE2_ALL
    public string? Note { get; set; }
    public DateTime Created { get; set; }
}

public record ListeningPort(string Protocol, IPAddress LocalAddress, int Port, int Pid, string? AppPath, string? AppName,
    string? Publisher, IReadOnlyList<string> Services, PortScope Scope, FirewallExposure Exposure, string? ServiceLabel, PortHint? Hint);

public enum PortScope { LocalhostOnly, LocalNetwork, AllInterfaces }
public enum FirewallExposure { FirewallOff, BlockedByAppLock, AllowedByRule, BlockedByDefault, Unknown }
```

Store settings in the AppLock settings JSON with the same integrity protection as the other Internet Access data (HMAC/DPAPI), validate ports (1–65535) and migrate missing fields safely. The firewall remains the source of truth; use the existing drift detection ("Rule missing", **Repair**) for port rules too.

## 9. Services and structure

```
src/AppLock/
  Views/Tabs/ListeningPortsTab.xaml(.cs)
  Views/Dialogs/PortDetailsFlyout, KnownPortsDialog, BlockPortDialog
  ViewModels/ListeningPortsViewModel.cs, PortGroupViewModel.cs, PortItemViewModel.cs
  Services/Firewall/
    ListeningPortService.cs        // IP Helper TCP/UDP tables (v4/v6), snapshot + diff
    ProcessInfoResolver.cs         // path, icon, publisher, services (EnumServicesStatusEx), UWP package
    PortClassifier.cs              // scope, service name, hints (bundled table)
    FirewallExposureEvaluator.cs   // best-effort estimate from INetFwPolicy2 rules
    PortRuleService.cs             // create/remove port rules in the AppLock group via IFirewallService
    PortWatchService.cs            // baseline, new-port detection, alerts
  Models/ListeningPort.cs, PortRuleEntry.cs, KnownPort.cs
  Assets/ports.json                // bundled port/service/hint table
tests/AppLock.Tests/Firewall/Ports/ // see 12
```

Define `ISocketTableSource`, `IProcessInfoSource`, and reuse `IFirewallService` and `IClock` so the logic is testable with fakes.

## 10. Active connections improvements (same milestone group)

Based on the current Active connections tab (rows such as `127.0.0.1:52278 → 127.0.0.1:64385` that never leave the PC, and many repeated rows per app):
1. **Hide local (loopback) connections** — new toggle, **default on**: hide rows where the remote address is `127.0.0.0/8` or `::1`, or equals the local address. Show a count ("86 local connections hidden").
2. **Scope filter chips**: **Internet only** (public remote addresses) · **Local network** (private remote addresses) · **All**. Default: *Internet only + Local network*.
3. **Group by app** (default on): one collapsible row per process with total connection count and a single **Block** button, with the individual connections inside; keep a flat mode toggle.
4. Rename the "Connected only" toggle to **Established only** (tooltip: "Hide listening, closing and waiting connections").
5. Keep the summary line accurate ("33 apps with connections · 162 total · 86 local hidden") and keep stable sorting so rows don't jump on the 3-second refresh.
6. Do not change the existing Block behavior, safety rules, or system-process restrictions.

## 11. UX and quality rules

- Fully theme-aware (Light, Dark, System, live switching), accessible (keyboard, screen-reader names for chips/buttons, high contrast); status never by color alone (text + icon).
- Localize all strings; UI responsive: socket reads, process lookups and rule evaluation on background threads with cancellation; apply diffs on the UI thread.
- Performance: a refresh of a few hundred sockets should take well under 100 ms of CPU; nothing runs when the tab isn't visible and Port watch is off.
- Log actions (block, unblock, repair, watch alerts) to `%APPDATA%\AppLock\logs`, without command lines or secrets.
- No network calls or telemetry; this feature reads local state only.

## 12. Tests

Unit tests with fakes: scope classification for every address type (loopback v4/v6, `0.0.0.0`, `::`, private, link-local, public-bound), port table lookup and range matching, UDP vs TCP handling, snapshot diff (new/removed/changed PID), exposure evaluator (block beats allow, default inbound action, profile matching, port ranges and `*`, app-path rules, firewall off, third-party firewall → Unknown), port rule naming and group guard (refuses to delete rules outside the AppLock group), critical-port warning logic, Port watch baseline and debounce (ignore ports open < 10 s, dynamic range), loopback hiding and grouping logic for Active connections, settings migration.

## 13. Milestones (build, run, verify each before moving on)

1. **Tab shell:** the Listening ports tab with summary strip, filters, empty state; theme-correct (Light/Dark/System). `ListeningPortService` listing TCP listeners and UDP sockets (v4/v6) with PID. Compare the result with `netstat -ano` on this PC.
2. **Process details and classification:** app names, icons, paths, publisher, service names for `svchost`, scope chips, bundled port table, hints.
3. **Grouping, filters, search, sorting and diff-based refresh** (no flicker, stable order, "New" badges).
4. **Firewall exposure estimate** and the firewall chip, details flyout; verify against rules created in `wf.msc`.
5. **Block actions:** Block this port / Block this app's incoming traffic, safety warnings, password gate for unblock/remove, drift/repair for port rules.
6. **Active connections improvements** (section 10).
7. **Port watch:** baseline, new-port toasts with Block/Allow/Details, known ports dialog, quiet hours.
8. Tests, theme and accessibility pass, `NOTES.md` (note which firewall cases the estimate doesn't cover), manual checklist.

After each milestone: `dotnet build -c Release`, fix all errors and warnings, launch the app, report in 5 lines or fewer (what works, what to click, known issues), commit to git, then STOP and wait for "continue".

## 14. Manual test checklist

- [ ] The Listening ports tab appears after Network devices; the list matches `netstat -ano` (listening TCP + UDP) and `Get-NetTCPConnection -State Listen`; Light, Dark, System themes look right
- [ ] Start a test server on localhost only (e.g. `python -m http.server 8000 --bind 127.0.0.1`): chip shows *Localhost only*. Restart it bound to all interfaces (`--bind 0.0.0.0`): chip shows *All interfaces* with a warning hint
- [ ] A phone on the same Wi-Fi can open the all-interfaces server before blocking, and cannot after **Block this port** (new connection); the rule appears in `wf.msc` in the group "AppLock Connection Firewall" and nothing else was touched
- [ ] **Block this app's incoming traffic** creates the same rule as the Incoming option on the Apps tab and shows on that tab
- [ ] `svchost.exe` ports show their service names; protected processes show "System / protected" when not elevated; the elevated view shows more
- [ ] Blocking 445/135 or a system service shows the critical warning; blocking needs administrator rights (disabled with the InfoBar otherwise); unblock/remove asks for the AppLock password
- [ ] Deleting an AppLock port rule in `wf.msc` shows "Rule missing" and **Repair** recreates it
- [ ] Firewall chip: *Allowed by rule* for an app with an inbound allow rule, *Blocked by default* for one without; block rule wins; turning the firewall off shows *Firewall off*; a third-party firewall shows *Unknown*
- [ ] Port watch: starting a new all-interfaces server triggers a toast with Block/Allow/Details; **Allow** adds it to known ports; short-lived ports and dynamic RPC ports from Windows services don't alert; baseline prompt appears the first time
- [ ] Active connections: loopback rows are hidden by default with a count, the "Internet only / Local network / All" chips work, grouping by app works, "Established only" label is shown, and the list doesn't jump on refresh
- [ ] Nothing runs when the tab is closed and Port watch is off (check CPU); no network requests are made by this feature
