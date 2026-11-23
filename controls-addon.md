# Island › Controls Tab Add-on (Wi-Fi, Bluetooth, Quick Cards)

This is an add-on to `island.md` section 9.7 (Controls tab) for the **native WinUI 3 Island** that is already built. Read `island.md` and `instructions.md` first. It replaces the current Controls tab layout and its Wi-Fi/Bluetooth lists. Do not touch the other tabs. Follow the same rules as the rest of the Island: animations stay on the compositor (no XAML Width/Height/Margin animation), nothing slow runs on the UI thread, Light/Dark/System/Island theme are respected, all strings go through the localization resource file.

## 1. What is wrong today (from the screenshots) and what to build

Today the Controls tab has: tiles (Wi-Fi, Bluetooth, Dark Theme, Battery, VPN), a brightness slider, and a Lock / Sleep / Restart / Shut down row. The Wi-Fi panel lists **only saved networks** with a Connect button, and the Bluetooth panel lists only paired devices. The tab also has empty space below the rows.

Build:

1. A **real Wi-Fi network list** like Windows' own (all networks in range, signal, security, connect to **new** networks with a password, hidden networks, disconnect, forget).
2. A **real Bluetooth device list** (paired devices **and** nearby devices to pair, connect/disconnect, remove, with battery levels).
3. A new **layout**: tiles on top, a scrollable middle with a **Bluetooth battery card** and **quick-action cards** (built-in + user-defined), and the **brightness slider and the power row pinned to the bottom**.
4. **Quick-action cards:** Lock screen, Show/Hide desktop, Task view, Calculator, and **custom cards** (name + command), with Default apps (`ms-settings:defaultapps`) and Startup apps (`ms-settings:startupapps`) included as defaults.

## 2. New layout of the Controls tab

Fixed design size for the tab (replaces the Controls row in the island.md size table): **600 × 400 DIPs at 100%** (tune in the existing size table). Nothing resizes while it is animating; the middle section scrolls instead.

```
┌ Tab strip (unchanged) ───────────────────────────────────────────────┐
│ Row 1  Tiles:  Wi-Fi | Bluetooth | Dark theme | Battery | VPN         │  (fixed)
├──────────────────────────────────────────────────────────────────────┤
│ Row 2  Scrollable area                                               │
│   ┌ Bluetooth battery card (wide) ─────────────────────────────┐     │
│   └─────────────────────────────────────────────────────────────┘     │
│   Quick cards grid (4 columns):                                       │
│   [Lock screen][Show desktop][Task view][Calculator]                  │
│   [Default apps][Startup apps][ + Add card ]                          │
├──────────────────────────────────────────────────────────────────────┤
│ Row 3  Brightness slider  ☀ ───────────●  100%        (pinned)       │
│ Row 4  [Lock] [Sleep] [Restart] [Shut down]            (pinned)       │
└──────────────────────────────────────────────────────────────────────┘
```

- Rows 1, 3, 4 are always visible; Row 2 takes the remaining height and scrolls vertically with a thin overlay scrollbar (no scrollbar when it fits). No empty gap appears when there are few cards.
- Row 1 tile behavior: **tap the tile** toggles (Wi-Fi radio, Bluetooth radio, theme), **tap the list icon** (top-right of the Wi-Fi and Bluetooth tiles, as today) opens the panel (section 3/4). The Battery tile shows the same text as now. Tiles can be hidden individually in settings.
- **Brightness slider (Row 3):** unchanged behavior (reuses the Brightness service); moved to the bottom. Hide the row if no controllable display (setting to hide it too).
- **Power row (Row 4):** Lock, Sleep, Restart, Shut down, same two-tap confirmation for Restart/Shut down (button turns into `Tap again` for 3 s). Option to hide it.
- **Edit mode:** a small `Edit` (pencil) text button at the right end of the "Quick cards" section header toggles edit mode: cards show a drag handle and a ✕ remove button, the `+ Add card` tile is highlighted, and the button changes to `Done`. Outside edit mode, right-click on a card opens `Edit… / Remove / Move to start`.
- **Panels** (Wi-Fi, Bluetooth, Add card, Edit card) open as an **in-tab overlay panel** over rows 1–2 exactly like the current Wi-Fi/Bluetooth panels (title, `Settings` link, ✕). Rows 3–4 stay visible below. Opening a panel is a short fade/slide (160 ms, transform + opacity only).
- Escape closes a panel first, then the tab.

## 3. Wi-Fi panel (full network list and connect)

### 3.1 Panel contents
- **Header:** title `Wi-Fi networks`, a **Wi-Fi on/off switch** (radio), a **Refresh** icon (spins while scanning), `Settings` (opens `ms-settings:network-wifi`), ✕.
- **List**, sorted: **connected network first**, then **saved networks** by signal, then **other networks** by signal. Networks with the same SSID are **merged** into one row (strongest signal wins; keep the best BSSID).
- **Row content:** signal icon (0–4 bars, pick the five matching Segoe Fluent Wi-Fi strength glyphs), SSID (`Hidden network` for empty), a status line:
  - `Connected` (accent) or `Connected, secured` / `Connected, open`
  - `Saved` / `Saved, auto-connect`
  - `Secured` / `Open — not secure` (warning color for open networks)
  - a small band chip `2.4 GHz` / `5 GHz` / `6 GHz` (from `ChannelCenterFrequencyInKilohertz`: below 3,000,000 = 2.4; 4,900,000–5,900,000 = 5; 5,925,000+ = 6)
  - Right side button: **Connect** (accent), or **Disconnect** (neutral) on the connected row. A `⋯` menu on saved rows: **Forget**, **Connect automatically on/off**.
- A footer row: **`Hidden network…`** (opens the hidden-network form) and the count `12 networks`.
- **Empty/error states:** `Wi-Fi is off` (with a Turn on button), `No Wi-Fi adapter on this PC`, `Searching…` skeleton rows, `No networks found`, and `Location is off — Windows needs it to list Wi-Fi networks` with an **Open location settings** button (`ms-settings:privacy-location`). Do not show blank space; show the right message.

### 3.2 Connect flows (inline, inside the row, not a new window)
Tapping **Connect** expands the row (height animation is not allowed, so the next rows slide with a `transform` and the row content cross-fades; keep it short):
- **Open network:** a one-line warning `This network is not secure` with **Connect anyway** / Cancel.
- **Saved network:** connects immediately using the saved profile.
- **New secured network (WPA2/WPA3 personal):** a **password box** with show/hide eye, a **`Connect automatically`** checkbox (default on), **Connect** and **Cancel**. While connecting: spinner + `Connecting…`; success collapses the row and shows `Connected`; failure shows a friendly message under the box (`Wrong password`, `Network not available`, `Timed out`, `This network uses a security type AppLock can't join`) and keeps the password box open.
- **Hidden network:** form with **Network name**, **Security** (Open / WPA2-Personal / WPA3-Personal), **Password**, **Connect automatically**.
- **Enterprise (802.1X/EAP) networks and any security type the API cannot join:** do not fail silently. Show the row with a **`Connect in Windows`** button that opens the system flyout (`ms-availablenetworks:`), falling back to `ms-settings:network-wifi`.
- **Disconnect** disconnects the adapter; **Forget** removes the saved profile (confirm with a small inline `Forget this network?` Yes/No).

### 3.3 Implementation notes
- Use WinRT `Windows.Devices.WiFi`: `WiFiAdapter.RequestAccessAsync()` / `FindAllAdaptersAsync()`, `adapter.NetworkReport.AvailableNetworks` (`Ssid`, `Bssid`, `SignalBars`, `NetworkRssiInDecibelMilliwatts`, `SecuritySettings.NetworkAuthenticationType/NetworkEncryptionType`, `ChannelCenterFrequencyInKilohertz`, `IsWiFiDirect`), `adapter.ScanAsync()`, `adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic | Manual, new PasswordCredential { Password = … })` (use the overload with an explicit SSID for hidden networks), and `adapter.Disconnect()`. Map `WiFiConnectionStatus` to the friendly messages above.
- **Instant display:** show the **cached `NetworkReport`** immediately when the panel opens (Windows keeps the last scan), then call `ScanAsync` and update. Never wait for a scan before drawing the list.
- **Scan policy:** one scan in flight at a time, scan on open and every **15 s while the panel is open**, stop when closed or the island hides. Debounce Refresh clicks.
- **Merge and sort** in a pure, unit-tested `WifiNetworkMerger` (dedupe by SSID, handle hidden SSIDs by BSSID, mark saved, mark connected).
- **Saved profiles / connected network:** `NetworkInformation.GetConnectionProfiles()` for the connected SSID; saved profile names via the WLAN API (`WlanGetProfileList` P/Invoke) or `netsh wlan show profiles` parsed defensively (arguments are never built from user text without validation). Forget uses `WlanDeleteProfile` (or `netsh wlan delete profile name="…"` with the SSID validated and quoted).
- Radio on/off: `Windows.Devices.Radios` (`Radio.RequestAccessAsync`, `SetStateAsync`) for the Wi-Fi radio.
- **Windows needs Location services on to return Wi-Fi scan results.** Detect the denied/disabled case and show the message in 3.1; never request it silently.
- **Passwords:** read from a `PasswordBox`, put in a `PasswordCredential` for the single call, then clear the control and drop references. **Never log, store, or send a password** and never write it to AppLock's settings (Windows keeps the profile itself).
- All WinRT calls run off the UI thread; results are marshalled in one batch.

## 4. Bluetooth panel (devices, pairing, battery)

### 4.1 Panel contents
- **Header:** title `Bluetooth devices`, an **on/off switch** (radio), **Add device** button (starts discovery), `Settings` (`ms-settings:bluetooth`), ✕.
- **Sections:** `Connected`, `Paired` (not connected), and during discovery `Nearby devices`. Each row: device-type icon (from the Bluetooth class of device / BLE appearance: headphones/audio, phone, keyboard/mouse (peripheral), computer, generic), name, status (`Connected`, `Paired`, `Not connected`, `Connecting…`, `Pairing…`), **battery** chip when known (`🔋 82%`, low = warning color at or below the threshold), and the action button: **Connect** / **Disconnect** (paired devices) or **Pair** (nearby). A `⋯` menu on paired devices: **Remove device**.
- States: `Bluetooth is off` (Turn on), `No Bluetooth adapter`, `Searching for devices…` (stops automatically after 30 s with a **Search again** button), `No devices found`.

### 4.2 Flows
- **Add device:** start a `DeviceWatcher` for **unpaired** devices (classic `BluetoothDevice.GetDeviceSelectorFromPairingState(false)` and Low Energy `BluetoothLEDevice.GetDeviceSelectorFromPairingState(false)`), list them under `Nearby devices` live, stop after 30 s or when the panel closes. Deduplicate classic + LE entries of the same physical device by address when possible; skip nameless devices unless the user toggles `Show devices without names`.
- **Pair:** `DeviceInformation.Pairing.Custom.PairAsync(...)` with `PairingRequested` handled **by an in-panel confirmation row** (never auto-accept): `ConfirmOnly` → `Pair with <name>?` Pair/Cancel; `ConfirmPinMatch` → `Does this code match on <name>? 123456` Yes/No; `DisplayPin` → show the PIN; `ProvidePin` → PIN entry box. Map `DevicePairingResultStatus` to friendly messages (`Pairing failed`, `Rejected by device`, `Device not available`, `Already paired`). After pairing, move the device to `Paired` and try to connect it.
- **Connect / Disconnect a paired device:** **Windows has no official, reliable WinRT API to connect or disconnect classic Bluetooth audio devices.** Implement and test this with a spike (milestone 5): enable/disable the device's relevant services with the Win32 `BluetoothSetServiceState` (audio sink A2DP, headset/hands-free, HID), which is how most third-party tools connect headphones, with the radio handle from `BluetoothFindFirstRadio`. For LE devices use a `BluetoothLEDevice` connection (keep a GATT session while connecting). Behavior varies by driver, so: show `Connecting…` for up to 10 s, verify the result by reading `System.Devices.Aep.IsConnected`, and on failure show `Couldn't connect. Try in Windows settings` with an **Open Bluetooth settings** button. Never claim connected without verifying.
- **Remove device:** `DeviceInformation.Pairing.UnpairAsync()` after an inline confirmation.
- Device list updates live through the `DeviceWatcher` (Added/Updated/Removed) and is batched to one UI update per 100 ms.

### 4.3 Battery
- Read battery for paired/connected devices from the device property **`{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`** (`DEVPKEY_Bluetooth_Battery`, percent) through `DeviceInformation` additional properties, and for LE devices fall back to the GATT **Battery Service (0x180F / Battery Level 0x2A19)** when the device is connected and the service is exposed. Many devices do not report battery; then show no battery value (never `0%`, never a guess).
- Refresh on property-updated events, plus a 60 s poll **only while the island is open or the battery card is visible**.
- **Low battery alert:** when a connected device drops to or below the threshold (default 20%, configurable), pop one Island live card `Airdopes 161 battery 15%` per device per charge cycle (reset when the level rises above threshold + 10). Uses the existing live card queue (priority: Battery).

## 5. Bluetooth battery card (Row 2, top)

- A wide card titled **Bluetooth devices** showing up to **4 connected devices** side by side (icon, name truncated, battery % with a thin bar; the bar turns warning color at or below the threshold; `—` when the device doesn't report battery). Charging state if reported.
- **No connected devices:** show a compact card `No Bluetooth devices connected` with an `Add device` button (opens the panel), or hide the card (setting: `Hide when nothing is connected`, default off).
- **Bluetooth off:** card shows `Bluetooth is off` with a Turn on button.
- Tapping a device row opens the Bluetooth panel focused on that device. Data comes from the same `BluetoothService` as the panel (one device watcher in the app, not two).

## 6. Quick-action cards

### 6.1 Card model and behavior
Cards are small buttons in a grid (4 columns at the default width, icon on top, name below, one line, ellipsis). Press feedback: scale 0.96 + accent flash (transform/opacity only). A card runs its action and, by default, **closes the island first** (setting `Close the island after running a card`, default on; needed for Win+Tab, Show desktop, and to avoid the island overlapping the launched app).

Default cards, in this order: **Lock screen, Show/Hide desktop, Task view, Calculator, Default apps, Startup apps.**

### 6.2 Built-in cards (catalog for `+ Add card`)
| Card | Action |
|---|---|
| **Lock screen** | `LockWorkStation()` (Windows lock screen, not AppLock's overlay) |
| **Show / Hide desktop** | Shell `ToggleDesktop` (`Shell.Application`/`IShellDispatch4.ToggleDesktop`), fall back to `Win + D` via `SendInput` |
| **Task view** | `SendInput` `Win + Tab` (island closes first; small delay so the island is gone) |
| **Calculator** | `calculator:` URI, fall back to `calc.exe` |
| **Settings** | `ms-settings:` |
| **Task Manager** | `Ctrl+Shift+Esc` via `taskmgr.exe` |
| **File Explorer** | `explorer.exe` |
| **Terminal** | `wt.exe`, fall back to `cmd.exe` |
| **Screenshot** | `Win + Shift + S` |
| **Sound / Display / Night light / Windows Update / Bluetooth devices** | the matching `ms-settings:` URIs (`sound`, `display`, `nightlight`, `windowsupdate`, `bluetooth`) |
| **Device Manager** | `devmgmt.msc` |

The catalog is **data-driven** (`controls-cards.json` shipped with the app: id, name, icon glyph, action type, command), so more built-ins can be added without code changes. Already-added cards are hidden or marked `Added` in the picker.

### 6.3 Custom cards
`+ Add card` opens a panel with two tabs: **Built-in** (catalog above, tap to add) and **Custom**:

- **Card name** (required, max 24 chars)
- **Command** (required): a URI (`ms-settings:defaultapps`, `https://…`, `steam://run/730`), an app/exe name or path, a folder, or a file. Optional **Arguments** box for executables.
- **Icon:** a picker with about 60 curated Segoe Fluent icons (search by name), or **Use the app's icon** (for an exe path) / **Use a picture…**.
- **Run as administrator** checkbox (only offered for executables; uses the UAC prompt, never silently elevates).
- **Test** button (runs it once), **Save**, **Cancel**.
- Validation: name and command not empty; the command and arguments are kept **separate** and launched with `ProcessStartInfo` (`UseShellExecute = true`), **never concatenated into a `cmd /c` string**; a URI must have a valid scheme; a path is checked for existence (warn, still allow, since PATH commands like `notepad` are valid). Failures show `Couldn't run this card: <reason>` as a live card or inline text, never a crash.
- Examples that must work out of the box (also the defaults):
  - Name `Default apps`, command `ms-settings:defaultapps`
  - Name `Startup apps`, command `ms-settings:startupapps`
- Launched programs go through normal Windows, so **AppLock's own app locks still apply** to anything the card opens.

Max 24 cards. Reorder by drag (edit mode) or `Move to start` from the context menu; drag uses transform animation only. `Remove` does not need confirmation (undo toast for 5 s via the island live card is nice to have). A **Reset to defaults** button lives in settings.

## 7. Settings (Island settings page › Modules › Controls)

- **Tiles shown:** Wi-Fi, Bluetooth, Dark theme, Battery, VPN (each toggle).
- **Sections shown:** Bluetooth battery card, Quick cards, Brightness slider, Power row (each toggle).
- **Quick cards:** the card manager (list with reorder, edit, remove, `+ Add`, `Reset to defaults`).
- **Close the island after running a card** (default on).
- **Wi-Fi:** `Show networks that are not saved` (default on), `Scan while the panel is open every` 15 s (10–60 s), warn about open networks (default on).
- **Bluetooth:** `Show devices without names` (default off), `Low battery alert` on/off and threshold (default 20%, 5–50%), `Hide battery card when nothing is connected`.
- **Network in the control centre** (Auto / Wi-Fi / Ethernet, from island.md) stays.

## 8. Data model

```csharp
public class ControlsSettings
{
    public ControlsTileVisibility Tiles { get; set; } = new();          // wifi, bluetooth, darkTheme, battery, vpn (all true)
    public bool ShowBluetoothBatteryCard { get; set; } = true;
    public bool HideBatteryCardWhenNone { get; set; } = false;
    public bool ShowQuickCards { get; set; } = true;
    public bool ShowBrightness { get; set; } = true;
    public bool ShowPowerRow { get; set; } = true;
    public bool CloseIslandAfterCard { get; set; } = true;
    public List<QuickCard> Cards { get; set; } = QuickCard.Defaults();   // lock, desktop, taskview, calculator, defaultapps, startupapps

    public bool WifiShowUnsaved { get; set; } = true;
    public int  WifiScanSeconds { get; set; } = 15;
    public bool WifiWarnOpen { get; set; } = true;

    public bool BtShowUnnamed { get; set; } = false;
    public bool BtLowBatteryAlert { get; set; } = true;
    public int  BtLowBatteryPercent { get; set; } = 20;
}

public class QuickCard
{
    public string Id { get; set; }                  // catalog id, or a new Guid for custom cards
    public bool IsCustom { get; set; }
    public string Name { get; set; }
    public string IconGlyph { get; set; }           // or IconPath for exe/picture icons
    public string IconPath { get; set; }
    public QuickCardKind Kind { get; set; }         // Builtin | Uri | Executable | Path
    public string Command { get; set; }
    public string Arguments { get; set; }
    public bool RunAsAdmin { get; set; }
}
```

Stored inside the existing Island settings in the protected settings JSON (migrate old files without errors; the previous Controls settings keep working, new defaults applied). Never store Wi-Fi passwords, scan results, or Bluetooth addresses on disk (device names/battery live in memory only).

## 9. Services and project structure (additions)

```
AppLock/Island/Controls/
  WifiService.cs            // adapter, radio, scan (cached + live), connect, disconnect, forget, location check
  WifiNetworkMerger.cs      // pure merge/sort/band/security-label logic (unit-tested)
  BluetoothService.cs       // radio, DeviceWatchers (paired + unpaired, classic + LE), pairing, battery, remove
  BluetoothConnector.cs     // BluetoothSetServiceState wrapper + verification (spike first)
  BluetoothBatteryReader.cs // DEVPKEY battery + GATT fallback
  QuickCardRunner.cs        // built-in actions, validation, safe launching
  QuickCardCatalog.cs       // reads controls-cards.json
Views/Island/Controls/
  ControlsTab.xaml, WifiPanel.xaml, BluetoothPanel.xaml, BluetoothBatteryCard.xaml, QuickCardsGrid.xaml, AddCardPanel.xaml, PowerRow.xaml
Assets/island/controls-cards.json
```

## 10. Performance and smoothness rules for this add-on

The built-in frame monitor from the earlier screenshots shows occasional frames above 100 ms (p95 about 60–95 ms, max 120–260 ms), so this add-on must not add any UI-thread work:

- **Do not touch Wi-Fi, Bluetooth, battery, or card icon data during the open/close/tab-switch animation.** Subscribe and load **after** the animation completes (or from a cached snapshot already in memory), and unsubscribe when the tab/panel is hidden.
- **Cache snapshots** (last Wi-Fi report, last Bluetooth device list with batteries) in memory so the tab and panels paint instantly, then refresh in the background and update in one batch.
- **Virtualize** the Wi-Fi and Bluetooth lists (`ItemsRepeater`/`ListView` with virtualization); row templates are lightweight (no nested heavy controls); the connect form is created only for the expanded row.
- Icon extraction/decoding for custom cards is done once, off-thread, and cached.
- All WinRT/Win32 calls (scan, pairing, watchers, GATT reads) are asynchronous and off the UI thread; results are coalesced to at most one UI update per 100 ms.
- Run the island benchmark before and after; **p95/max for open/close/tab-switch must not get worse** than before this add-on. Report both numbers.

## 11. Edge cases

- No Wi-Fi adapter / desktop PC with Ethernet only; Wi-Fi radio off; Airplane mode on (both radios disabled; show `Airplane mode is on`).
- Location services off or app denied; Wi-Fi scan returns zero results even when networks exist.
- Duplicate SSIDs across bands; hidden SSIDs; very long SSIDs; non-Latin SSIDs; captive portals (after connecting show `Sign-in may be required` if Windows reports limited connectivity, with an `Open sign-in page` button that opens `http://www.msftconnecttest.com/redirect`).
- Bluetooth: radio off, no adapter, device out of range, device pairing rejected, device reporting no battery, devices that appear twice (classic + LE), connecting while another audio device is connected, Windows pairing dialog appearing instead of ours (Windows may show its own prompt; handle `PairingRequested` not firing and show `Finish pairing in the Windows prompt`).
- Brightness not supported (desktop monitor): row hidden.
- Custom card command missing or blocked by policy; very many cards (scroll); icon file removed.
- Sleep/resume: refresh radios and device lists after resume.
- Elevation: nothing here needs administrator; custom cards with `Run as administrator` use a UAC prompt only on press.

## 12. Tests and manual checklist

Unit tests: `WifiNetworkMerger` (dedupe, hidden SSIDs, sort order, saved/connected flags, band from frequency, security labels), Wi-Fi status → message mapping, `BluetoothBatteryReader` parsing (byte/percent, missing property), device list merge (classic + LE), low-battery alert hysteresis, `QuickCardRunner` validation (URI scheme, empty name, long name, arguments kept separate), card JSON serialization and migration, catalog loading.

Manual:
- [ ] Controls tab: tiles on top, scrollable middle, brightness slider and power row pinned at the bottom, no empty gap
- [ ] Wi-Fi panel shows all networks in range (not only saved), connected first, then saved, then others, sorted by signal; signal bars and lock icons correct
- [ ] Connect to a saved network, to a new WPA2 network with a password (wrong password shows a message; correct works), to an open network (warning first), to a hidden network
- [ ] Enterprise network shows `Connect in Windows` and opens the Windows flyout
- [ ] Disconnect and Forget work; Wi-Fi toggle turns the radio off/on; Location-off message appears with a working button
- [ ] Bluetooth panel lists connected, paired, and (after Add device) nearby devices; pairing a new device shows the confirmation (PIN match) in the panel and completes
- [ ] Connect/Disconnect a paired headset works and is verified; a failure shows the fallback message with the Windows settings button
- [ ] Remove device works after confirmation
- [ ] Bluetooth battery card shows connected devices with battery (or `—`), low-battery card appears once at the threshold
- [ ] Quick cards: Lock screen locks Windows, Show/Hide desktop toggles, Task view opens, Calculator opens, Default apps and Startup apps open the right Settings pages
- [ ] Add a built-in card from the catalog; add a custom card (name + command + icon); edit, reorder (drag), remove; reset to defaults
- [ ] Custom card with an invalid command shows a clear error, no crash
- [ ] Island closes after running a card (setting on), stays open (setting off)
- [ ] Light/Dark/Island themes and High Contrast look right; keyboard navigation works (Tab, Enter, Esc, arrows in lists)
- [ ] Benchmark p95/max unchanged or better than before

## 13. Delivery

Implement in this order, one milestone at a time:

1. **Layout + model:** new Controls layout (tiles, scrollable middle, pinned brightness and power row), `ControlsSettings` + migration, quick-cards grid with the six default cards, edit mode, settings section skeleton.
2. **Quick-action cards:** `QuickCardRunner` with all built-ins, catalog JSON, `+ Add card` panel (Built-in and Custom tabs), validation, icons, reorder, close-island-after-run.
3. **Wi-Fi service + list:** adapter/radio/location handling, cached + live scans, `WifiNetworkMerger` with tests, panel list with all networks (read-only actions: Settings, Refresh).
4. **Wi-Fi connect:** open, password, hidden, enterprise fallback, disconnect, forget, auto-connect option, friendly errors.
5. **Bluetooth service + list + spike:** watchers for paired/unpaired (classic and LE), radio toggle, panel UI, battery reading, and the **connect/disconnect spike** (`BluetoothSetServiceState`) with a written result (works / works for audio only / unreliable) before building the UI around it.
6. **Bluetooth pairing and actions:** Add device, pairing confirmations, Connect/Disconnect with verification and fallback, Remove device, **Bluetooth battery card** and the low-battery alert.
7. **Polish:** settings page options, performance pass (benchmark before/after), edge cases, tests, run the manual checklist and report failing items.

After each milestone: `dotnet build -c Release` (fix all errors and warnings), **launch the app and open the Island's Controls tab**, report in 5 lines or fewer (include the benchmark p95/max from milestone 1 on), commit, then **stop and wait for me to reply "continue"**. Do not start the next milestone on your own.
