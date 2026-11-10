# M4 Build Error Fix

## Error
```
error CS0234: The type or namespace name 'AudioSessionState' does not exist in the namespace 'NAudio.CoreAudioApi'
at line 186 in VolumeOverlayWindow.xaml.cs
```

## Root Cause
Missing `using NAudio.CoreAudioApi;` directive and incorrect fully-qualified reference to enum type.

## Fix Applied

### File: `src/AppLock/Views/VolumeOverlayWindow.xaml.cs`

**Added using directive:**
```csharp
using NAudio.CoreAudioApi;
```

**Changed line 186 from:**
```csharp
sessions = sessions.Where(s => s.State == NAudio.CoreAudioApi.AudioSessionState.AudioSessionStateActive).ToList();
```

**To:**
```csharp
sessions = sessions.Where(s => s.State == AudioSessionState.AudioSessionStateActive).ToList();
```

## Build Now

Run in **Developer PowerShell for VS 2022**:
```powershell
cd "d:\programming exercise\winUI\win app lock"
msbuild AppLock.sln /p:Configuration=Release /p:Platform=x64 /v:minimal
```

**Or** in Visual Studio:
- Open AppLock.sln
- Build → Build Solution (Ctrl+Shift+B)

## Expected Result
✅ Build succeeds with 0 errors
⚠️ C++ linker warnings are OK (hook not needed for Volume Mixer)

## Next Step
After successful build, test M4 features per `VOLUMEMIXER_M4_COMPLETE.md` checklist, then reply **"continue"** for M5.
