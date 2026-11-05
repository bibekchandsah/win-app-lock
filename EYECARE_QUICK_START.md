# Eye Care Feature - Quick Start Guide

## Build & Run

1. **Build the project:**
   ```powershell
   # Using Visual Studio
   Open AppLock.sln in Visual Studio and build (Ctrl+Shift+B)
   
   # OR using MSBuild
   msbuild AppLock.sln /t:Restore,Build /p:Configuration=Debug /p:Platform=x64
   ```

2. **Run the app:**
   ```
   src\AppLock\bin\x64\Debug\net8.0-windows10.0.19041.0\AppLock.exe
   ```

## Usage

1. **Navigate to Eye Care:**
   - Launch AppLock
   - Click "Others" in the sidebar
   - Find the "Eye Care Reminder" card below Brightness

2. **Basic Setup:**
   - Toggle **Enabled** to start reminders
   - Choose an interval (default: 20 minutes)
   - Set a custom message (optional, defaults to built-in message)
   - Click **Preview** to test the overlay

3. **Advanced Options:**
   - **Custom Interval:** Click dropdown → "Custom..." → enter minutes (1-1440)
   - **Schedule:** Enable to limit reminders to specific hours (supports cross-midnight)
   - **Duration:** Adjust how long overlay stays visible (5-300 seconds)
   - **Custom Template:** Browse for your own HTML file or open templates folder

## Testing Checklist

After building, verify:

### Basic Functionality
- [ ] Card appears in Others page
- [ ] Enable toggle starts scheduler
- [ ] Disable toggle stops reminders
- [ ] Settings persist after restart

### Intervals
- [ ] Preset intervals (1, 20, 25, 30, 60) work
- [ ] Check mark appears on selected interval
- [ ] Custom interval dialog accepts 1-1440

### Overlay
- [ ] Preview button shows overlay immediately
- [ ] Overlay appears fullscreen
- [ ] Overlay shows on all monitors
- [ ] Overlay is always on top
- [ ] Custom message displays correctly
- [ ] Empty message shows default text
- [ ] Countdown is visible and accurate

### Closing
- [ ] Overlay auto-closes after duration
- [ ] Esc key closes overlay
- [ ] Right-click → Close works
- [ ] All monitors close together

### Schedule
- [ ] Schedule checkbox enables/disables time pickers
- [ ] Reminders respect time window
- [ ] Cross-midnight works (e.g., 22:00 to 06:00)
- [ ] Start time = End time means "always"

### Custom Templates
- [ ] "Browse..." opens file picker
- [ ] "Open folder" opens templates directory
- [ ] Custom HTML files work
- [ ] Placeholders are replaced:
  - `{{REMINDER_MESSAGE}}` → user message
  - `{{DURATION}}` → seconds
- [ ] Relative assets load (images/CSS/JS in same folder)

### System Events
- [ ] Reminders resume after computer sleeps/resumes
- [ ] No reminders while session is locked
- [ ] No reminders while overlay is already visible
- [ ] Next interval starts AFTER overlay closes (not during)

## Troubleshooting

### Overlay doesn't appear
- Check WebView2 Runtime is installed: https://developer.microsoft.com/en-us/microsoft-edge/webview2/
- Check debug output in Visual Studio Output window (search for "[EyeCare]")
- Verify Assets/EyeCare/index.html was copied to output directory

### Custom template doesn't work
- Ensure template is valid HTML
- Put images/CSS/JS in same folder as the HTML file
- Check placeholders: `{{REMINDER_MESSAGE}}` and `{{DURATION}}`
- Add close handler: `window.chrome.webview.postMessage('close')`

### Reminders don't fire
- Verify "Enabled" toggle is On
- Check schedule window if enabled
- Look for "[EyeCare]" messages in debug output
- Restart app to reload scheduler

### Settings don't persist
- Check write permissions to `%LOCALAPPDATA%\AppLock\`
- Verify settings.json is not read-only
- Check for exceptions in debug output

## File Locations

### User Data
- **Settings:** `%LOCALAPPDATA%\AppLock\settings.json`
- **Templates:** `%LOCALAPPDATA%\AppLock\EyeCare\templates\`
- **Rendered:** `%LOCALAPPDATA%\AppLock\EyeCare\.render\` (temporary)

### Shipped Assets
- **Built-in template:** `Assets\EyeCare\index.html` (in app folder)

## Custom Template Example

```html
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <title>Custom Eye Care</title>
    <style>
        body {
            margin: 0;
            height: 100vh;
            display: flex;
            justify-content: center;
            align-items: center;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            font-family: Arial, sans-serif;
            text-align: center;
        }
        .message {
            font-size: 32px;
            margin-bottom: 20px;
        }
        .countdown {
            font-size: 48px;
            font-weight: bold;
        }
    </style>
</head>
<body>
    <div>
        <div class="message">{{REMINDER_MESSAGE}}</div>
        <div class="countdown" id="countdown"></div>
    </div>

    <script>
        let countdown = {{DURATION}};
        const countdownEl = document.getElementById('countdown');
        
        function update() {
            if (countdown > 0) {
                countdownEl.textContent = countdown + 's';
                countdown--;
                setTimeout(update, 1000);
            } else {
                closeWindow();
            }
        }
        
        function closeWindow() {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage('close');
            }
        }
        
        update();
        
        // Close on Esc (native handler also works)
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') closeWindow();
        });
    </script>
</body>
</html>
```

Save as `custom.html` in templates folder or browse to it.

## Debug Output

Enable debug output to see scheduler activity:
- Visual Studio: View → Output → Show output from: Debug
- Look for lines starting with `[EyeCare]`

Example output:
```
[EyeCare] Scheduler started, next fire at 14:35:22
[EyeCare] Skipping reminder: outside schedule window
[EyeCare] Creating overlay on 2 display(s)
[EyeCare] WebView initialized, navigating to rendered.html
[EyeCare] Close message received from WebView
[EyeCare] Timer re-armed, next fire at 14:55:22
```

## Feature Integration

The Eye Care feature integrates with:
- **Settings persistence:** Same JSON file as other features
- **Service lifecycle:** Starts on app launch if enabled
- **UI patterns:** Matches Brightness card style
- **DI pattern:** Registered in ServiceContainer

It does NOT interfere with:
- Lock/Unlock functionality
- Brightness controls
- BitLocker features
- Self-Discipline quotas
- Network firewall

## Next Steps

After successful build and testing:

1. **User feedback:** Test with real users for interval preferences
2. **Template gallery:** Create additional built-in themes
3. **Statistics:** Track reminder counts, skip reasons
4. **Notifications:** Add system notification on reminder skip
5. **Fullscreen detection:** Add "Don't show over fullscreen apps" option

## Support

For issues or questions:
1. Check debug output for `[EyeCare]` messages
2. Verify WebView2 Runtime is installed
3. Review EYECARE_IMPLEMENTATION_COMPLETE.md for architecture details
4. Check eyecare.md for original specification
