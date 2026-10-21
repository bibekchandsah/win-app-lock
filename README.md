# AppLock for Windows

A Windows 10/11 desktop application that locks selected applications/windows behind a password or Windows Hello authentication.

## Tech Stack

- **Language:** C# 12
- **.NET:** 8 (LTS)
- **UI Framework:** WinUI 3 (Windows App SDK 1.5+)
- **Pattern:** MVVM with CommunityToolkit.Mvvm
- **Win32 Interop:** CsWin32 source generator

## Build Instructions

### Prerequisites

1. Windows 10 version 1809 (build 17763) or later
2. .NET 8 SDK or later
3. Visual Studio 2022 with:
   - .NET Desktop Development workload
   - WinUI application development components
   - Windows App SDK

### Building

Open PowerShell as Administrator (to avoid permission issues) and run:

```powershell
cd "d:\programming exercise\winUI\win app lock"
dotnet restore
dotnet build -c Release
```

### Running

```powershell
dotnet run --project src\AppLock\AppLock.csproj
```

Or run the built executable:
```powershell
.\src\AppLock\bin\Release\net8.0-windows10.0.19041.0\win-x64\AppLock.exe
```

## Current Status

**Milestone 1 Complete:** Solution scaffold with WinUI 3 window, Mica backdrop, navigation rail, and tabbed interface.

### Features Implemented
- ✅ Dark theme with Mica backdrop
- ✅ Left navigation rail with icons (Protection, Rules, Websites, Feedback, Settings)
- ✅ Protection page with status card and master toggle
- ✅ Two tabs: "Running programs" and "Locked programs"
- ✅ Settings page placeholder
- ✅ Search box and control buttons UI

### Next Milestones
- Milestone 2: Window enumeration + Running programs list
- Milestone 3: Settings store, first-run password setup, authentication
- Milestone 4: Lock rules and master toggle functionality
- Milestone 5: WinEventHook service + overlay window
- And more...

## Known Issues

- Currently experiencing .NET CLI permission issues in sandboxed environment
- Requires running PowerShell as Administrator for build operations

## Project Structure

```
AppLock/
  AppLock.sln
  src/AppLock/
    App.xaml(.cs)            - Application entry point
    MainWindow.xaml(.cs)     - Main window with navigation
    Views/
      ProtectionPage.xaml    - Main protection interface
      SettingsPage.xaml      - Settings (placeholder)
    NativeMethods.txt        - CsWin32 API declarations
```
