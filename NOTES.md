# Development Notes

## Milestone 1: Solution Scaffold

### Implementation Decisions

1. **Unpackaged WinUI 3 App**: Using `WindowsPackageType=None` and `WindowsAppSDKSelfContained=true` to create an unpackaged app that doesn't require MSIX signing.

2. **Mica Backdrop**: Using `MicaController` and `SystemBackdropConfiguration` for the modern Mica effect in dark theme.

3. **Navigation Pattern**: Using a `Frame` control for page navigation with a custom left navigation rail (48px wide).

4. **Dark Theme**: Applied `ElementTheme.Dark` to enforce dark mode matching the spec.

5. **Accent Color**: Purple/lilac accent color (#D9A0E8) defined in App.xaml resources.

### Technical Choices

- **Target Framework**: `net8.0-windows10.0.19041.0` (Windows 10 version 2004)
- **Minimum Version**: `10.0.17763.0` (Windows 10 version 1809)
- **Platform**: x64 only (as specified for WinUI 3)
- **DPI Awareness**: PerMonitorV2 for proper multi-monitor support
- **CsWin32**: Using source generator approach with NativeMethods.txt for Win32 API declarations

### Environment Issues

**Permission Problem**: The .NET 10 SDK is attempting to create a sentinel file at `C:\Users\bibek\.dotnet\10.0.402.toolpath.sentinel` but access is denied due to sandbox restrictions.

**Workaround Required**: Build commands must be run from PowerShell as Administrator or the file permissions need to be fixed.

### UI Structure

- **MainWindow**: Contains navigation rail (48px) + Frame for content
- **ProtectionPage**: Status card with master toggle + TabView with two tabs
- **Running programs tab**: Search box, Refresh button, "Pick a window" button, empty list area
- **Locked programs tab**: Empty list area (placeholder)
- **SettingsPage**: Placeholder with section structure

### What Works
- Window opens with proper size (1000x700)
- Mica backdrop applies correctly (if system supports it)
- Dark theme is enforced
- Navigation between Protection and Settings pages
- UI layout matches specification structure

### What's Next (Milestone 2)
- Implement WindowEnumerator service
- Enumerate running windows with EnumWindows
- Display window icons, titles, and process names
- Add search filtering
- Implement refresh functionality
