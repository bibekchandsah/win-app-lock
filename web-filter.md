# Web Filter — Add-on Spec for AppLock

This is an add-on to `instructions.md`. Read that file first. Implement on top of the existing C# / .NET 8 / WinUI 3 AppLock project, using the same MVVM structure (CommunityToolkit.Mvvm), shared services (elevation helper "Restart as administrator", shared custom toast service, admin password / Windows Hello verification, protected settings JSON with DPAPI/HMAC, schedule evaluator and `IClock` from Self-Discipline, Windows Firewall helper from Connection Firewall), and the same theme rules (Light / Dark / System via theme resources only). Build, run, and verify before reporting.

## 1. What this feature is, and the name

The sidebar currently has a greyed placeholder called **Websites**. This feature replaces it with an enabled item named **Web Filter** (globe icon ``, keep the same style as the other items). Sidebar order after the Vault spec: **Protection, Vault, Web Filter, Internet Access, BitLocker**. Remove the "phase 2" tooltip from the old placeholder.

"Web Filter" is a better name than "Websites" or "Website Lock" because this feature **blocks** sites by category; it cannot put a password prompt in front of a website (that would need a browser extension, see section 17, phase 2).

**What it does:** blocks whole categories of websites (Adult, Gambling, Social media, and more) plus individual sites the user chooses, for **every browser and app on the PC**, using **DNS filtering**. Two ways to do it, user's choice:

| Mode | How | Pros | Cons |
|---|---|---|---|
| **Local filter** (recommended) | AppLock runs a small DNS filter on this PC (127.0.0.1:53) with its own category block lists, and points Windows DNS at it. Allowed lookups are forwarded to a real DNS server. | Real categories (adult, gambling, social, more), per-site control, schedules, SafeSearch, activity log, works offline lists | Needs administrator; AppLock must be running to filter |
| **Filtered DNS provider** (simple) | AppLock just sets Windows DNS to a family-filter DNS provider (Cloudflare Family, CleanBrowsing, OpenDNS FamilyShield, AdGuard Family). | Nothing runs on the PC; very simple | Mostly Adult + malware only; no gambling/social choice; no custom sites, schedules, or log |

Both modes use the same page, same status area, and the same anti-bypass protection.

## 2. Honest scope and limits (show a short version in the UI, repeat the full version in the final report)

DNS filtering blocks a **domain name**. Therefore:

- It **cannot** block a single page or path of a site (e.g. `reddit.com/r/something`), only the whole domain. It cannot see HTTPS content.
- It can be **bypassed** by: a VPN or proxy, Tor, a browser's built-in Secure DNS (mitigated, see section 9), apps with hard-coded DNS (mitigated), a mobile hotspot, another device, another Windows user, an IP address typed directly, and by anyone with administrator rights who changes DNS or stops AppLock. This is a self-control and family-safety layer, not a security boundary.
- Blocking "Facebook" or "TikTok" also blocks their embedded widgets and login buttons on other sites.
- Category lists are community-maintained and will have misses and false positives. The page must make it easy to **allow a wrongly blocked site** (with authentication) and to **block a missed site** (without).
- Windows resolves names through the DNS Client service, so the DNS filter **cannot tell which app** made a request. Do not promise per-app attribution in the activity log.

## 3. Page layout

Header card (always visible): a **Web Filter** ToggleSwitch with status text, e.g. `● Web Filter is On — Local filter · 3 categories · 1.2M domains`, with a **Pause** split button (15 min / 1 hour / until tomorrow; requires authentication, auto-resumes) on the right. Under the header, a **status info bar** area (only when relevant): not elevated, DNS changed by something else, VPN detected, port 53 busy, lists out of date, etc. (section 10).

Quick tool above the tabs: **"Check a website"** TextBox + button (paste a URL or a domain). Result: `Blocked — Gambling (list: <name>)`, `Blocked — Always block list`, `Blocked — Social media › Instagram`, or `Allowed`. Also shows "Allow for 15 min / 1 hour / today" and "Add to always allow" buttons for blocked results (these require authentication).

Four tabs (same tab-strip style as the Protection page):

1. **Categories**
2. **Sites** (custom lists and services)
3. **Activity**
4. **Settings**

## 4. Categories tab

A vertical list of cards. Each card: icon, name, one-line description, domain count (e.g. `412,300 domains`), a **ToggleSwitch**, and a **schedule button** that reads `Always` or `Mon–Fri 09:00–17:00` (opens the same time-block editor UI used by Self-Discipline: start/end hour+minute combos, 7 day circles, up to 10 blocks per category; blocks mean "filter is **active** during these periods"; no blocks = always). Cards expand to show sub-options.

| Category | Default | Notes |
|---|---|---|
| **Adult content** | Off in first-run wizard unless chosen; recommended | Sub-option: **Enforce SafeSearch** (Google, Bing, DuckDuckGo, YouTube Restricted Mode Moderate/Strict), see section 11 |
| **Gambling** | Off | Casinos, betting, lotteries, poker |
| **Social media** | Off | Expands to per-service toggles (section 6.2) |
| **Malware & phishing** | **On** (recommended, not forced) | Low false-positive lists only |
| **Bypass tools (VPN, proxy, DoH, anonymizers)** | **On when any other category is on** | Domains of well-known VPN/proxy/anonymizer services and public DoH hostnames; makes bypass harder |
| **Video streaming** | Off | YouTube, Netflix, Twitch etc. (also available per service) |
| **Dating** | Off | |
| **Games** | Off | Gaming stores/launchers/browser-game sites |
| **Piracy & torrents** | Off | |
| **Drugs & alcohol** | Off | |
| **Ads & trackers** | Off | Convenience, not safety; can break sites; warn in the card |
| **News/Misinformation** | not included | Do not ship; too subjective |

Rules:

- A card toggle is the category's main switch; the schedule only limits **when** it is active.
- If a category's list failed to download and no bundled copy exists, the card shows `List not available` and the toggle is disabled.
- A card's description must say plainly what it does not catch (a short "Limits" tooltip is enough).
- Show a one-time inline hint on first enabling any category: *"Blocked sites will show 'This site can't be reached' in your browser."*

## 5. Precedence (the matching engine's decision order)

Evaluated for every DNS query name (all record types, including A, AAAA, HTTPS/SVCB `65`), first match wins:

1. **Never-block list** (bundled, not editable): Windows connectivity checks (`www.msftconnecttest.com`, `dns.msftncsi.com`, `ipv6.msftncsi.com`), Windows Update / Microsoft Store / Defender essentials, certificate revocation hosts (OCSP/CRL domains), the hosts AppLock uses to download its own lists. Purpose: a filtering mistake must never break Windows or lock the user out of the internet.
2. **Temporary allow** entries (not expired).
3. **Always allow** list (user).
4. **Always block** list (user).
5. **Service blocks** (section 6.2) and **category lists**, only for categories/services that are enabled **and currently active by schedule**.
6. Otherwise **allow**.

Matching is **by domain suffix**: blocking `example.com` blocks `example.com` and `*.example.com`. The **most specific** entry wins: an allow entry for `www.example.com` overrides a block of `example.com`, and a block of `ads.example.com` still applies if `example.com` is allowed. Names are lower-cased, trailing dot removed, IDN converted to punycode.

## 6. Sites tab

### 6.1 Always block / Always allow lists

Two sub-sections (a Pivot or two cards):

- **Always block:** list with search, **Add** (TextBox; accepts `example.com`, `*.example.com`, or a pasted URL, from which the host is extracted; reject IP addresses, single labels, and invalid names with an inline message), per-row delete, optional **note** per row. **Import** / **Export** as plain text (one domain per line, `#` comments allowed). Max 5,000 entries. Adding needs no authentication; removing does.
- **Always allow:** same UI. Adding and removing both need authentication (an allowed domain weakens protection). Domains matched by the Never-block list are shown as informational and not added.

### 6.2 Services (per-site toggles)

A curated catalog `Assets/web-filter/services.json` shipped with the app (name, icon, list of domains, category) so a user can block one service without blocking a whole category. Initial catalog: YouTube, Netflix, Twitch, Facebook, Instagram, WhatsApp Web, Messenger, TikTok, X (Twitter), Snapchat, Reddit, Discord, Telegram, Pinterest, LinkedIn, Tumblr, Steam, Roblox, Epic Games. Each service has a ToggleSwitch and an optional schedule (same editor). Services under "Social media" also appear as sub-toggles on that category card; there is one underlying state, not two. Include each service's known CDN/API domains (e.g. Facebook/Instagram share Meta domains) and keep the catalog data-driven so it can be updated without code changes.

## 7. Activity tab

- **Summary row:** Blocked today, Blocked this week, Top blocked category.
- **Blocked requests list** (most recent first, virtualized): time, domain, matched reason (`Gambling`, `Always block`, `Service › TikTok`), and a ⋯ menu: **Allow for 15 min / 1 h / today** (auth), **Add to always allow** (auth), **Copy domain**.
- Group repeats: show one row per domain per minute with a count badge (`×24`), because browsers retry.
- **Top blocked domains** (7 days) simple list.
- **Logging controls:** "Log blocked requests" (default on), "Log allowed requests" (default **off**, with a privacy warning: it records browsing history on this PC), retention 7 / 30 / 90 days (default 30), **Clear log** (authentication). Stored in `%LOCALAPPDATA%\AppLock\webfilter.db` (SQLite via `Microsoft.Data.Sqlite`; if the Screen Time spec is already implemented, reuse the same SQLite helper but keep a separate file). Never log query types other than the name and time; never send logs anywhere.
- Optional toast via the shared toast service: `Blocked: example.com (Gambling)`. Setting "Notify when a site is blocked", default **off**, throttled to once per domain per 5 minutes.
- Live updates while the tab is open (batch UI updates every 500 ms, never one dispatcher call per query).

## 8. Settings tab

- **Mode:** radio **Local filter** / **Filtered DNS provider**. Changing mode needs authentication when the filter is on.
- **Local filter options:**
  - **Upstream DNS** (where allowed lookups go): Cloudflare (DoH, default), Google (DoH), Quad9 (DoH), **My network's original DNS** (plain DNS to the servers Windows used before AppLock; best for company/VPN/split-DNS setups), or Custom (DoH URL or IP). Include a **Test upstream** button.
  - **Cache** on/off (default on) and **Clear cache**.
  - **Block response:** `Not found (NXDOMAIN)` (default) or `0.0.0.0 / ::`.
- **Filtered DNS provider options:** provider list with addresses and a plain statement of what each covers (see section 12).
- **Block lists:** per-category list sources (read-only default sources + "Add custom list URL"), **Update now**, last updated time, **Update automatically** (weekly, default on), size and domain counts, a **Credits & licenses** link listing every list used with its license and URL.
- **Bypass protection** group (section 9), each with its own toggle.
- **Startup:** "Start the filter when AppLock starts" (on, forced on while Web Filter is on). If "Start with Windows" is off in the main app settings, show an inline link to enable it, because the filter only runs while AppLock runs.
- **Emergency:** **Restore my network settings** button (always works, no authentication required, but it turns Web Filter off and logs the event; that is the user's escape hatch if something breaks) and the command-line switch `AppLock.exe --restore-dns` doing the same.
- **Export / import settings** is not needed in v1.

## 9. Anti-bypass protection (Settings › Bypass protection)

All options default **on** in Local filter mode, require administrator, are applied with a visible summary before the first apply, and are fully reverted when Web Filter is turned off or AppLock is uninstalled. All firewall rules use the group name **`AppLock Web Filter`** and the existing Windows Firewall helper; never touch rules outside that group.

1. **Keep Windows DNS pointed at the filter** (always on, not a toggle): a verifier checks every 30 s and on `NetworkChange` events; if any enabled adapter's DNS is not the filter, it re-applies and logs `DNS settings were changed outside AppLock`.
2. **Block other DNS servers:** outbound firewall rules that block UDP/TCP port **53** and TCP port **853** (DNS over TLS) to **all remote addresses except** loopback and the original/upstream DNS servers (compute the complement address ranges). This stops apps that hard-code `8.8.8.8` etc.
3. **Block known public DoH servers:** outbound block rules (TCP/UDP 443) to a bundled list of well-known public DNS-over-HTTPS resolver IP addresses (Cloudflare, Google, Quad9, AdGuard, OpenDNS, NextDNS, CleanBrowsing, etc., IPv4 and IPv6), **minus the addresses of the upstream resolver currently chosen** (AppLock itself needs it, and Windows Firewall block rules cannot exclude one program). If the user picks Cloudflare as upstream, Cloudflare's resolver IPs are not blocked, and the UI tells them so. Prefer "My network's original DNS" as upstream when maximum bypass resistance is wanted. Bundled list lives in `Assets/web-filter/doh-servers.json`, data-driven.
4. **Turn off browser Secure DNS (policy):** write machine policies that disable DNS-over-HTTPS in Chrome (`HKLM\SOFTWARE\Policies\Google\Chrome` `DnsOverHttpsMode` = `off`), Edge (`...\Microsoft\Edge`), Brave (`...\BraveSoftware\Brave`), and Firefox (`distribution\policies.json` `DNSOverHTTPS` Enabled=false, Locked=true, only if Firefox is installed). Tell the user clearly that browsers will show "managed by your organization". Record exactly what was written so it can be removed cleanly. Skip with a message on domain-joined PCs.
5. **VPN / proxy awareness:** on `NetworkChange`, detect VPN adapters (tunnel/TAP/WireGuard/known VPN adapter descriptions) and an enabled system proxy (WinINet/WinHTTP). Show an info bar: *"A VPN or proxy is active. Web Filter may not apply to traffic that goes through it."* and log it. Do not try to break VPNs.
6. **Bypass tools category** (section 4) on by default.

Windows Firewall rules and DNS changes need administrator. If AppLock is not elevated, show the existing **Restart as administrator** flow; never half-apply silently.

## 10. Status, warnings, and failure handling

Info bars (use `InfoBar`, severity as noted), shown at the top of the page:

- **Not running as administrator** (warning): filter cannot be applied. Button: Restart as administrator.
- **Port 53 busy** (warning): see 12.1. Offer "Use another loopback address automatically" (default behavior) or show who is using it.
- **DNS changed outside AppLock** (informational, auto-repaired).
- **VPN or proxy active** (informational).
- **Lists out of date** (> 14 days, informational) with **Update now**.
- **Domain-joined / managed network detected** (warning): DNS may be controlled by policy; ask before continuing.
- **Filter stopped unexpectedly** (error): auto-restart, and if it cannot be restarted, restore the original DNS and say so.

**Fail-safe rule (very important):** pointing Windows at 127.0.0.1 while nothing listens there kills the internet. Therefore:
- On a normal exit/shutdown of AppLock, **restore the original DNS settings** (the filter is then off; show a tray toast `Web Filter paused because AppLock closed`).
- On startup, if DNS points to the filter but the filter is not running (after a crash or kill), **repair immediately**: start the filter, or if that fails, restore the original DNS.
- A watchdog inside the process checks that the listener answers a self-test query every 10 s; if not, restart it, and after 3 failures restore the original DNS and show an error.
- `AppLock.exe --restore-dns` (and the Settings button) restores from the saved original configuration without starting the UI.
- Phase 2 (section 17): run the filter as a Windows service so it keeps working when the AppLock window/process is closed, with fail-closed as an option. Design the engine as a class library (`AppLock.WebFilter.Engine`) with no UI dependencies so it can move into a service later.

## 11. DNS engine details (Local filter mode)

### 11.1 Listener
- Listen on UDP and TCP, `127.0.0.1:53` and `[::1]:53`. Windows cannot be told a port, so port 53 is required. If 53 is busy (Internet Connection Sharing, Hyper-V/WSL host networking, another DNS tool), try other loopback addresses (`127.0.0.53`, then `127.0.53.53`); the system DNS is set to whichever address was bound. Any `127.x.x.x` works as a loopback address on Windows.
- Handle EDNS0 and UDP payload size correctly; set the truncation bit when needed; support TCP for large answers. Limit concurrent in-flight queries (e.g. 256), per-query timeout (3 s), and discard malformed packets silently.

### 11.2 Parsing and responses
- Do **not** rewrite the whole DNS stack. Parse only the header and the **first question** (name, type, class) with strict bounds checks (reject compression pointer loops, label length > 63, name > 255, QDCOUNT ≠ 1 for blocking decisions). Forward the original packet bytes untouched when allowed.
- Blocked response: copy the question, set QR, RA, RCODE = NXDOMAIN (or NOERROR with `0.0.0.0` / `::` for A/AAAA when that block mode is selected, and NOERROR with empty answer for other types). Optionally add an Extended DNS Error "Filtered" (code 17) in EDNS0.
- Unit-test the parser with malformed, truncated, looped, and oversized input and add a **fuzz test** (random byte mutation, thousands of iterations) asserting no exceptions escape and no unbounded allocation.

### 11.3 Forwarding
- **DoH upstream:** RFC 8484 `POST` with `application/dns-message` over HTTP/2 using one long-lived `HttpClient` (`SocketsHttpHandler`, connection reuse). Use the resolver's IP address in the URL or a pre-resolved IP so the filter never needs the filter itself to find its upstream (avoid a bootstrap loop). Validate the TLS certificate normally; never disable validation.
- **Original DNS upstream:** plain UDP/TCP to the saved original servers.
- **Fallback:** if the chosen upstream fails 3 times in a row, try the second one (original DNS), and show the "upstream unreachable" state in the status area instead of silently failing.
- **Local names go to the original DNS, never DoH:** single-label names, `.local`, `.lan`, `.home`, `.home.arpa`, `.internal`, `.corp`, the machine's DNS suffix, private reverse zones (`10.in-addr.arpa`, `168.192.in-addr.arpa`, `16–31.172.in-addr.arpa`, etc.), so routers, NAS devices, and company networks keep working.
- **Cache:** small TTL-respecting LRU (clamp TTL between 30 s and 1 h, max 10,000 entries). Block decisions are not cached because they are cheap. When any rule or list changes, clear the cache **and** flush the Windows resolver cache (`DnsFlushResolverCache` or `ipconfig /flushdns`) so already-cached names are re-evaluated.
- **CNAME cloaking (milestone 11):** inspect CNAME targets in upstream answers; if any target name matches a block entry, replace the answer with the blocked response.

### 11.4 Applying system DNS
- Use `SetInterfaceDnsSettings` from `iphlpapi.dll` (`DNS_INTERFACE_SETTINGS`, `DNS_SETTING_NAMESERVER`, per adapter GUID, Windows 10 version 2004 or newer); fall back to `netsh interface ip set dns` / `netsh interface ipv6 set dns` on older builds, with strictly validated arguments (no string-built shell commands from user input).
- Apply to **every connected, non-loopback adapter that has an IPv4 or IPv6 gateway** (Wi-Fi, Ethernet; VPN adapters are only touched if the user opted in). Set both IPv4 and IPv6 DNS: IPv4 → bound loopback address; IPv6 → `::1` (otherwise IPv6 resolvers from router advertisements are a bypass).
- **Before the first change, save the original per-adapter configuration** (adapter GUID, whether DNS was DHCP-assigned or static, the server list, for IPv4 and IPv6) in the protected settings. Restore exactly that on disable/exit/uninstall, including "back to DHCP". Never overwrite a saved original with a filter address (guard against saving `127.x` as the original).
- Handle adapters appearing/disappearing and network changes via `NetworkChange.NetworkAddressChanged` plus the 30 s verifier.
- Do not leave a secondary DNS server set (Windows may query it in parallel and leak around the filter).

## 12. Filtered DNS provider mode (simple)

- Providers (verify addresses against the provider's current documentation at implementation time, keep them in `Assets/web-filter/providers.json`): **Cloudflare Family** (`1.1.1.3`, `1.0.0.3`, IPv6 `2606:4700:4700::1113`, `2606:4700:4700::1003`: malware + adult), **CleanBrowsing Family / Adult** filters, **OpenDNS FamilyShield** (`208.67.222.123`, `208.67.220.123`: adult), **AdGuard Family Protection** (adult + safe search + malware).
- Each provider row states in one line what it blocks (usually adult + malware, **not** gambling or social media) and a link to its site. If the user wants gambling/social, the UI suggests switching to Local filter.
- Applies the same way (11.4), same save/restore of original DNS, same verifier, same "Block other DNS servers" and "Block known public DoH servers" options (the chosen provider's own IPs are excluded from the DoH block list), same browser policy options.
- Custom lists, services, schedules, SafeSearch rewriting, and the activity log are **not available** in this mode; show those tabs as disabled with the message "Available in Local filter mode".
- **Check a website** in this mode resolves the name through the provider and treats NXDOMAIN, `0.0.0.0`, or the provider's documented block-page IP as blocked, and says the verdict is the provider's.

## 13. SafeSearch and YouTube Restricted Mode (Local filter only)

- When **Enforce SafeSearch** is on, rewrite lookups for the search engines' hostnames to their enforced-safe endpoints by **answering with the target's addresses under the original name** (resolve the target upstream, return it with the original question name): Google domains → `forcesafesearch.google.com` (use Google's published list of Google domains, bundled and updatable), `www.bing.com` → `strict.bing.com`, DuckDuckGo → `safe.duckduckgo.com`, YouTube → `restrict.youtube.com` (Strict) or `restrictmoderate.youtube.com` (Moderate).
- Provide a small **SafeSearch status** line in the card (`Google ✓ Bing ✓ DuckDuckGo ✓ YouTube: Strict`).
- Known limit to state in the UI: it does not apply to apps/search engines not on the list, and it stops working if the user bypasses DNS.

## 14. Block-list pipeline

### 14.1 Sources
Data-driven in `Assets/web-filter/sources.json`: for each category, one or more list URLs, format, license, attribution, and a bundled fallback file name. Use well-known open, community-maintained lists with permissive or share-alike licenses (for example the Blocklist Project lists and the StevenBlack unified-hosts extension lists for porn/gambling/social, UT1 blacklists, and a malware/phishing list such as URLhaus or Phishing Army). **Verify each list's current URL and license when implementing, show the license and credit in "Credits & licenses", and respect share-alike terms (CC BY-SA requires attribution and keeping the same license for the list data itself).** Do not scrape lists from sites that forbid redistribution.

### 14.2 Download and update
- HTTPS only, conditional requests (`ETag` / `If-Modified-Since`), size cap (e.g. 50 MB per list), timeout, user-agent `AppLock-WebFilter`. Update at startup if stale and then weekly; never block the UI; show progress on **Update now**.
- A bad download must never replace a good list: parse into a temp structure, require a sane domain count (e.g. > 50% of the previous count and > 100 entries) before swapping, keep the previous compiled copy, and record the failure.
- Ship a **bundled snapshot** of each category (compressed) so filtering works immediately after install and offline.

### 14.3 Parsing and storage
- Parse **hosts format** (`0.0.0.0 domain`), **plain domain lists**, and the simple adblock form `||domain^`; ignore comments, regex, cosmetic or path rules, IPs, and entries with ports or paths. Lower-case, punycode, dedupe.
- Compile each category into a compact structure: a **sorted array of 64-bit hashes** (e.g. XxHash64 with a per-install seed) of normalized domain names, stored in a binary file under `%LOCALAPPDATA%\AppLock\webfilter\lists\`. Lookup walks the name's suffixes (`a.b.example.com`, `b.example.com`, `example.com`) and binary-searches each. 1M domains ≈ 8 MB, load via memory-mapped file. Accept the tiny false-positive probability of 64-bit hash collisions (document it).
- Lookups must be fast (< 50 µs typical), lock-free for readers (immutable snapshot swapped atomically).

## 15. Settings model and authentication

```csharp
public class WebFilterSettings
{
    public bool Enabled { get; set; }
    public WebFilterMode Mode { get; set; } = WebFilterMode.LocalFilter;   // LocalFilter | Provider
    public string ProviderId { get; set; } = "cloudflare-family";
    public UpstreamKind Upstream { get; set; } = UpstreamKind.CloudflareDoh; // CloudflareDoh | GoogleDoh | Quad9Doh | OriginalDns | Custom
    public string CustomUpstream { get; set; } = "";
    public BlockResponse BlockResponse { get; set; } = BlockResponse.NxDomain;

    public Dictionary<string, CategoryRule> Categories { get; set; } = new(); // by category id
    public Dictionary<string, ServiceRule>  Services   { get; set; } = new(); // by service id
    public List<DomainEntry> AlwaysBlock { get; set; } = new();
    public List<DomainEntry> AlwaysAllow { get; set; } = new();
    public List<TempAllow>   TempAllows  { get; set; } = new();  // Domain, ExpiresUtc

    public bool EnforceSafeSearch { get; set; }
    public YouTubeRestriction YouTubeMode { get; set; } = YouTubeRestriction.Off; // Off | Moderate | Strict

    public BypassProtection Bypass { get; set; } = new();  // BlockOtherDns, BlockKnownDoh, BrowserPolicies (all true)
    public bool LogBlocked { get; set; } = true;
    public bool LogAllowed { get; set; } = false;
    public int  LogRetentionDays { get; set; } = 30;
    public bool NotifyOnBlock { get; set; } = false;
    public bool AutoUpdateLists { get; set; } = true;
    public DateTime? PausedUntilUtc { get; set; }

    public SavedDnsConfig OriginalDns { get; set; }          // per adapter, see 11.4
    public AppliedChanges Applied { get; set; } = new();     // firewall rules + registry/policy values written, for exact revert
}

public class CategoryRule { public bool Enabled; public List<TimeBlock> ActiveBlocks = new(); } // empty = always
public class ServiceRule  { public bool Enabled; public List<TimeBlock> ActiveBlocks = new(); }
public class DomainEntry  { public string Domain; public string Note; public DateTime AddedUtc; }
```

Persist in the existing protected settings JSON (HMAC/DPAPI applies; tampering falls back to the same behavior as the rest of AppLock). Migrate older settings without errors.

**Authentication ("weakening" rules, same pattern as Self-Discipline §6):** verifying the admin password / Windows Hello is required to: turn a category or service **off**, turn Web Filter off, change mode, **remove** a domain from Always block, **add or remove** an Always allow domain, create a **temporary allow**, shorten a schedule's coverage, turn **off** a bypass-protection option, **Pause**, **Clear log**, and edit the exclusion/never-block-related items. Enabling things, adding to Always block, and viewing never need it. While a category/service is **currently inside its active schedule period**, it cannot be turned off or have its schedule weakened (but a user can still use the authenticated temporary allow for a single site). **Restore my network settings** is the one intentional exception (section 8) and is logged.

## 16. Project structure (additions)

```
AppLock/
  WebFilter/Engine/                    // no UI references; movable to a service later
    DnsPacket.cs                       // minimal parse/build, strict bounds checks
    DnsFilterServer.cs                 // UDP+TCP listeners, concurrency limits, watchdog hook
    UpstreamForwarder.cs               // DoH + plain UDP/TCP + fallback
    DnsCache.cs
    DomainMatcher.cs                   // precedence (section 5), suffix walk, schedules via IClock
    ListCompiler.cs, ListParser.cs, ListUpdater.cs, ListSnapshot.cs
    SafeSearchRewriter.cs
  WebFilter/System/
    DnsConfigurator.cs                 // SetInterfaceDnsSettings, save/restore original, netsh fallback
    DnsVerifier.cs                     // 30 s + NetworkChange re-apply
    BypassProtection.cs                // firewall rules (group "AppLock Web Filter"), browser policies
    NetworkEnvironmentProbe.cs         // VPN/proxy/domain-joined detection
  WebFilter/WebFilterService.cs        // orchestrates everything, exposes status
  WebFilter/WebFilterStore.cs          // SQLite activity log + stats
  Assets/web-filter/
    sources.json, services.json, providers.json, doh-servers.json, never-block.json
    lists/ (bundled compressed snapshots)
  Models/WebFilterSettings.cs
  ViewModels/WebFilterViewModel.cs, CategoryCardViewModel.cs, ServiceToggleViewModel.cs, BlockedRequestViewModel.cs
  Views/WebFilterPage.xaml(.cs)
  Views/WebFilter/CategoriesTab.xaml, SitesTab.xaml, ActivityTab.xaml, SettingsTab.xaml
  Views/WebFilter/Dialogs/SetupWizardDialog.xaml, ScheduleDialog.xaml, TempAllowDialog.xaml
```

Add the sidebar item (replace the Websites placeholder), navigation, and the localization strings (all strings go through the existing localization resource file).

## 17. Out of scope / phase 2 (keep hooks only)

- Windows service host for the filter (survives closing AppLock, optional fail-closed).
- Browser extension for real **website password lock**, URL/path-level rules, and a friendly **block page** (needs a trusted local certificate or an extension).
- Per-user / per-profile category sets, kids profiles.
- Router-level enforcement (see the Router adapter idea in `network-scanner.md`).
- DoH/DoT configured through Windows' built-in encrypted DNS settings for the filtered-provider mode.
- Group/total daily web time limits (would connect with Screen Time).

## 18. First-run setup wizard

Shown the first time the user turns Web Filter on (ContentDialog with 3 steps):

1. **How should AppLock filter?** Two big choices: *Local filter (recommended)* / *Filtered DNS provider (simple)*, each with a 2-line summary from section 1.
2. **What should be blocked?** Category checkboxes (Adult, Gambling, Social media, Malware & phishing preselected on, Bypass tools preselected on) with a note that more options are on the Categories tab.
3. **Review changes:** plain-language summary of what will change on this PC: *"Windows DNS will be set to the AppLock filter on: Wi-Fi, Ethernet. Firewall rules named 'AppLock Web Filter' will be added. Browser Secure DNS will be turned off by policy. You can undo all of this by turning Web Filter off or using Restore my network settings."* Buttons **Apply** (accent, triggers elevation if needed) / **Cancel**. Nothing is changed before the user confirms.

## 19. Tests and manual checklist

Unit tests:
- DNS parser: valid, truncated, pointer loops, oversize, mixed case, IDN, multiple questions; **fuzz test**.
- Matcher: suffix matching, most-specific-wins, precedence order, never-block list, schedule windows (including midnight-spanning blocks and DST) via fake `IClock`, temporary allow expiry.
- List parsing: hosts format, plain list, adblock `||domain^`, comments, junk lines, huge lines; bad update does not replace a good list.
- Original DNS save/restore round-trip (DHCP vs static, IPv4 and IPv6) with a fake configurator; never saves a loopback address as the original.
- Blocked responses are valid DNS (parse with a reference parser), NXDOMAIN and `0.0.0.0` modes.
- Settings migration; authentication required for every weakening action.

Manual checklist:
- [ ] Sidebar shows Web Filter (no greyed Websites) and the page opens with four tabs
- [ ] Setup wizard applies changes only after the confirm step; elevation works
- [ ] With Local filter + a category on, a site in that category fails to load in Chrome, Edge, and Firefox; normal sites work; `nslookup` against the system DNS shows the filter answering
- [ ] Turning the category off (after authentication) unblocks it immediately (cache flushed)
- [ ] A custom blocked domain is blocked including its subdomains; allowing a subdomain overrides it
- [ ] Browser Secure DNS is off and shows "managed"; switching a browser to a custom DoH provider does not bypass the filter
- [ ] Hard-coded DNS (`nslookup example.com 8.8.8.8`) fails while "Block other DNS servers" is on
- [ ] Schedule: a category set to 09:00–17:00 only blocks in that window; cannot be turned off during the window
- [ ] Kill AppLock from Task Manager: on next start DNS is repaired; internet is never left dead (also test `--restore-dns`)
- [ ] Close AppLock normally: DNS is restored and a toast says the filter paused
- [ ] Change DNS manually in Windows settings: it is re-applied within about 30 s and an event is logged
- [ ] Captive portal / hotel Wi-Fi sign-in page still works; router/NAS local names still resolve
- [ ] Port 53 conflict (start Internet Connection Sharing or a second DNS tool) → fallback loopback address is used and the page explains it
- [ ] Connect a VPN: info bar appears
- [ ] Activity tab lists blocked domains with counts; Allow for 15 min works after authentication and expires
- [ ] Update now downloads lists; a simulated broken download keeps the old list
- [ ] SafeSearch: Google/Bing results are filtered; YouTube Restricted Mode is active
- [ ] Provider mode: DNS set to the chosen provider, restore works, disabled tabs show the message
- [ ] Light, Dark, System theme look correct; all text localized
- [ ] Turning Web Filter off removes DNS changes, firewall rules (group "AppLock Web Filter"), and browser policies completely

## 20. Delivery

Implement in this order, one milestone at a time:

1. **Foundation:** sidebar rename/enable, empty page with four tabs, `WebFilterSettings` + migration, localization, status area skeleton.
2. **DNS engine core:** `DnsPacket`, `DnsFilterServer`, forwarder (DoH + plain), cache, with unit and fuzz tests and a small console/test harness proving it resolves and blocks on a non-system port first. **Do not touch system DNS yet.**
3. **Block-list pipeline:** parsers, compiler, snapshot matcher with precedence and schedules, bundled snapshots, updater with safe swap, "Check a website" tool working in the UI.
4. **System DNS apply/restore:** `DnsConfigurator`, original-config save/restore, fail-safe/startup repair, verifier, `--restore-dns`, elevation, setup wizard. Verify the internet never dies.
5. **Categories tab:** toggles, counts, status, master toggle, Pause.
6. **Sites tab:** Always block/allow lists, import/export, Services catalog.
7. **Schedules + authentication rules + temporary allow.**
8. **Activity tab:** log, stats, notifications.
9. **Bypass protection:** firewall rules, browser policies, VPN/proxy detection, Bypass tools category.
10. **Filtered DNS provider mode.**
11. **SafeSearch / YouTube Restricted Mode and CNAME-cloaking check.**
12. **Polish and tests:** theme pass, localization, credits & licenses page, all unit tests, run the full manual checklist and report any failing item.

After each milestone: `dotnet build -c Release` (fix all errors and warnings), **launch the app**, report in 5 lines or fewer, commit, then **stop and wait for me to reply "continue"**. Do not start the next milestone on your own. Never leave the PC's DNS pointing at a filter that is not running; test restore paths before moving on from milestone 4.
