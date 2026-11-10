# Volume Mixer M5: App Icons & Drag Mode

## Milestone Scope

M5 focuses on visual polish and user customization:
1. **Real app icon extraction** - Replace generic glyph with actual app icons
2. **Drag mode** - Let users drag overlay to custom position
3. **Enhanced positioning** - Better multi-monitor support

---

## Feature 1: Real App Icon Extraction

### Current State
- All apps show generic glyph `\uE71D`
- `AppIconResolver.ResolveApp()` returns `null` for icon
- Names resolve correctly via `FileVersionInfo`

### Implementation Plan

#### A. Icon Extraction Methods (Priority Order)

**1. Session IconPath** (Easiest)
- Many apps provide `session.IconPath`
- Format: `@%SystemRoot%\System32\AudioSrv.dll,-202` or file path
- Extract using resource APIs

**2. Process Exe Icon** (Most Common)
- Get process path via `Process.GetProcessById(pid).MainModule.FileName`
- Extract icon using Win32 `SHGetFileInfo` or `ExtractIconEx`
- Works for most desktop apps

**3. UWP/MSIX Package Logo** (Store Apps)
- Detect packaged apps via exception on MainModule access
- Use `Package.Current` / `PackageManager.FindPackageForUser`
- Get logo from manifest
- Convert to BitmapImage

**4. Fallback** (Generic)
- Keep current `\uE71D` glyph for failures

#### B. Win32 Interop Code

```csharp
// Services/VolumeMixer/IconExtractor.cs
[DllImport("shell32.dll", CharSet = CharSet.Auto)]
private static extern IntPtr SHGetFileInfo(
    string pszPath, uint dwFileAttributes,
    ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
private struct SHFILEINFO
{
    public IntPtr hIcon;
    public int iIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
    public uint dwAttributes;
}

private const uint SHGFI_ICON = 0x100;
private const uint SHGFI_LARGEICON = 0x0;
private const uint SHGFI_SMALLICON = 0x1;
```

#### C. Icon Cache

**Strategy**:
- Cache by exe path (not process ID)
- `Dictionary<string, BitmapImage>` in `AppIconResolver`
- Load icons on background thread
- Marshal `BitmapImage` creation to UI thread

**Lifetime**:
- Cache persists for app lifetime
- Clear on memory pressure (optional)

#### D. UI Integration

**Update `AppIconResolver.ResolveApp()`**:
```csharp
public (string Name, BitmapImage? Icon) ResolveApp(int processId, string displayName, string iconPath)
{
    var name = ResolveName(...); // existing logic
    
    // Try to get exe path
    string? exePath = GetProcessPath(processId);
    if (exePath != null && _iconCache.TryGetValue(exePath, out var cachedIcon))
    {
        return (name, cachedIcon);
    }
    
    // Extract icon
    var icon = ExtractIcon(exePath, iconPath);
    if (icon != null && exePath != null)
    {
        _iconCache[exePath] = icon;
    }
    
    return (name, icon);
}
```

**Update `CreateAppRow()` in VolumeOverlayWindow**:
```csharp
// Replace FontIcon with Image when icon available
if (viewModel.Icon != null)
{
    var image = new Image
    {
        Source = viewModel.Icon,
        Width = 24,
        Height = 24
    };
    appIcon = image;
}
else
{
    var fontIcon = new FontIcon
    {
        Glyph = "\uE71D",
        FontSize = 24
    };
    appIcon = fontIcon;
}
```

---

## Feature 2: Drag Mode

### Current State
- `DragMode` setting exists in VolumeMixerSettings
- `DragOutline` Border exists in XAML (always collapsed)
- `CustomX/CustomY` fields exist but unused
- No drag logic implemented

### Implementation Plan

#### A. Visual Indicator

**When DragMode enabled**:
- Show `DragOutline` Border with accent color
- Set cursor to `SizeAll` on RootGrid
- Add tooltip: "Drag to reposition"

**XAML already has**:
```xaml
<Border x:Name="DragOutline"
        BorderBrush="{ThemeResource AccentFillColorDefaultBrush}"
        BorderThickness="2"
        CornerRadius="20"
        Visibility="Collapsed"/>
```

#### B. Drag Logic

**State tracking** (already exists):
```csharp
private bool _isDragging;
private int _dragStartX, _dragStartY, _dragStartWidth, _dragStartHeight;
```

**Event handlers** (some exist, need completion):

1. **PointerPressed**:
   - Check if DragMode enabled
   - Save start position (`AppWindow.Position`)
   - Set `_isDragging = true`
   - Capture pointer

2. **PointerMoved**:
   - If `_isDragging`
   - Calculate delta from start point
   - Call `AppWindow.Move(newX, newY)`
   - Clamp to work area bounds

3. **PointerReleased**:
   - Set `_isDragging = false`
   - Save final position to `CustomX/CustomY`
   - Persist settings
   - Release pointer capture

#### C. Position Clamping

```csharp
private void ClampToWorkArea(ref int x, ref int y, int width, int height)
{
    var workArea = DisplayArea.Primary.WorkArea;
    
    x = Math.Max(workArea.X, Math.Min(x, workArea.X + workArea.Width - width));
    y = Math.Max(workArea.Y, Math.Min(y, workArea.Y + workArea.Height - height));
}
```

#### D. Position Reset Logic

**When IconPosition changes**:
- Clear `CustomX/CustomY`
- Recalculate position from `IconPosition` setting
- Save settings

**In Settings UI** (already exists):
- IconPosition ComboBox SelectionChanged handler
- Clear custom position when changed

---

## Feature 3: Enhanced Multi-Monitor Support

### Current State
- Uses `DisplayArea.Primary` only
- No handling of monitor changes

### Enhancements

**A. Monitor Change Detection**:
```csharp
// In VolumeOverlayWindow constructor
DisplayArea.Changed += OnDisplayAreaChanged;
```

**B. Reposition on Changes**:
```csharp
private void OnDisplayAreaChanged(object? sender, EventArgs e)
{
    DispatcherQueue.TryEnqueue(() =>
    {
        PositionWindow(); // Recalculate position
    });
}
```

**C. DPI Awareness**:
- Already using work area coordinates (DPI-independent)
- Window size already in pixels
- Should work across DPI changes

---

## Files to Modify

### New Files
- `src/AppLock/Services/VolumeMixer/IconExtractor.cs` - Win32 icon extraction

### Modified Files
- `src/AppLock/Services/VolumeMixer/AppIconResolver.cs`:
  - Add icon extraction methods
  - Add icon cache
  - Update `ResolveApp()` to return BitmapImage

- `src/AppLock/ViewModels/MixerSessionViewModel.cs`:
  - Change `Icon` property from `ImageSource?` to `BitmapImage?`

- `src/AppLock/Views/VolumeOverlayWindow.xaml.cs`:
  - Complete drag mode event handlers
  - Add `ClampToWorkArea()` method
  - Update `CreateAppRow()` to use real Image control
  - Wire DisplayArea.Changed event
  - Update ApplySettings() to show/hide DragOutline

- `src/AppLock/Views/VolumeMixerSettingsPage.xaml.cs`:
  - Add IconPosition_SelectionChanged handler to clear custom position

---

## Testing Checklist

### Icon Extraction
- [ ] Chrome shows Chrome icon (not generic)
- [ ] Spotify shows Spotify icon
- [ ] WhatsApp (Store app) shows correct logo
- [ ] System sounds show system icon
- [ ] Apps without icons fall back to generic glyph
- [ ] Icons cached (second open is instant)
- [ ] Access denied processes still work with generic icon
- [ ] High DPI icons look sharp (not blurry)

### Drag Mode
- [ ] Enable drag mode in settings
- [ ] Drag outline appears on overlay
- [ ] Cursor changes to move cursor
- [ ] Can drag overlay to any screen position
- [ ] Position clamped to visible work area (can't drag off-screen)
- [ ] Position persists after app restart
- [ ] Disable drag mode → outline disappears, can't drag
- [ ] Change IconPosition → custom position cleared, returns to preset
- [ ] Drag works in all states (Idle, Expanded, MixerOpen)

### Multi-Monitor
- [ ] Overlay appears on primary monitor
- [ ] Dragging to secondary monitor works
- [ ] Unplugging monitor with overlay → repositions to primary
- [ ] Changing DPI → overlay stays in same relative position
- [ ] Monitor resolution change → overlay repositions correctly

---

## Implementation Order

1. **Icon Extraction** (2-3 hours)
   - Create IconExtractor.cs with Win32 interop
   - Update AppIconResolver with cache
   - Test with common apps
   - Handle UWP/packaged apps
   - Update CreateAppRow() UI logic

2. **Drag Mode** (1-2 hours)
   - Complete PointerPressed/Moved/Released handlers
   - Add clamping logic
   - Wire DragOutline visibility to settings
   - Test position persistence

3. **Multi-Monitor** (30 min)
   - Wire DisplayArea.Changed event
   - Test monitor changes

4. **Testing & Polish** (1 hour)
   - Test all scenarios
   - Fix edge cases
   - Performance check (icon extraction shouldn't block UI)

**Total Estimate**: 4-6 hours

---

## Known Limitations

### Icon Extraction
- Protected system processes may still show generic icon (by design)
- Some UWP apps use placeholder icons in exe (need package manifest)
- Icon extraction adds 10-50ms per app on first load (cached after)

### Drag Mode
- No snap-to-edge feature (can add in M6)
- No preview while dragging (immediate move)

---

## Next: M6

After M5 complete:
- Start with Windows implementation
- Global hotkey to show/hide mixer
- Performance optimizations (reduce polling when possible)
- Accessibility (AutomationProperties.Name, keyboard navigation)
- System tray icon (optional)
- Edge case hardening

---

Ready to implement M5? Reply **"continue"** to start with icon extraction.
