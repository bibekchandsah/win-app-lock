# Milestone 7 Verification Checklist

## Quick Start

1. **Build the project:**
   ```powershell
   .\build-test.ps1
   ```

2. **Run AppLock:**
   ```powershell
   Start-Process ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -Verb RunAs
   ```

3. **Navigate:** Others → Brightness → Scroll to "Blur Overlay"

---

## Visual Verification (5 minutes)

### ✅ Checkpoint 1: Preview Appears
**Location:** Blur Overlay expander, below "Blur Level" slider

**Expected:**
- [ ] Rounded card with "Live Preview" header
- [ ] 200px height preview panel
- [ ] Loading spinner appears briefly on start
- [ ] Preview shows your desktop/windows (blurred)

**If you see "Preview unavailable":**
- Windows version too old (need 1903+)
- Run as administrator
- Graphics.Capture API not available

---

### ✅ Checkpoint 2: Real-Time Blur Updates
**Action:** Move blur level slider 0% → 50% → 100%

**Expected:**
- [ ] Preview blur changes immediately as you drag
- [ ] 0% = Clear/sharp desktop view
- [ ] 50% = Moderate blur
- [ ] 100% = Heavy blur (very blurry)
- [ ] Slider shows percentage value (e.g., "50%")

**If blur doesn't change:**
- Check console for errors
- Verify ScreenCaptureService is capturing frames

---

### ✅ Checkpoint 3: No Feedback Loop
**What to look for:** Preview should NOT show itself

**Expected:**
- [ ] Preview shows OTHER windows/desktop
- [ ] Preview does NOT show the preview panel itself
- [ ] No infinite mirroring/recursion effect
- [ ] SetWindowDisplayAffinity working correctly

**If you see recursion:**
- Windows < 2004 (build 19041)
- SetWindowDisplayAffinity failed
- Check if running as administrator

---

### ✅ Checkpoint 4: Performance
**Action:** Open Task Manager → Performance

**Expected:**
- [ ] CPU: 5-10% while on Brightness page
- [ ] GPU: Moderate usage (varies by hardware)
- [ ] Navigate away → CPU/GPU drops
- [ ] Return to page → Preview restarts automatically

**If performance too high:**
- Reduce fps in ScreenBlurPreview.xaml.cs (line ~73)
- Change from 33ms (30fps) to 66ms (15fps)

---

### ✅ Checkpoint 5: Stop/Start Behavior
**Actions:**
1. Observe preview running
2. Click "Others" (go back)
3. Return to Brightness page

**Expected:**
- [ ] Preview stops when leaving page
- [ ] Loading spinner appears when returning
- [ ] Preview restarts automatically
- [ ] Blur level preserved from before

---

### ✅ Checkpoint 6: Theme Support
**Actions:**
1. Settings → Appearance → Theme
2. Switch Light / Dark / System

**Expected:**
- [ ] Preview card border color changes
- [ ] Text colors adapt to theme
- [ ] Preview background follows theme
- [ ] All controls remain visible/readable

---

### ✅ Checkpoint 7: Privacy Notice
**Location:** Bottom of preview card

**Expected:**
- [ ] Small gray text visible
- [ ] Says "AppLock captures screen content only for local blur preview..."
- [ ] Explains nothing is saved or transmitted

---

## Functional Integration Tests

### Test 1: Blur Slider + Preview Sync
```
1. Set blur to 0% → Preview clear
2. Set blur to 25% → Preview slightly blurred  
3. Set blur to 50% → Preview moderately blurred
4. Set blur to 75% → Preview heavily blurred
5. Set blur to 100% → Preview maximum blur
```
**Pass if:** Preview matches slider value in real-time

---

### Test 2: Preview Lifecycle
```
1. Open Brightness page → Preview starts
2. Wait 10 seconds → Preview still running
3. Go to Others page → Preview stops
4. Go to Settings page → Preview stopped
5. Return to Brightness → Preview restarts
```
**Pass if:** Preview only runs when Brightness page visible

---

### Test 3: Memory Stability
```
1. Open Brightness page
2. Move blur slider repeatedly for 1 minute
3. Check Task Manager → Memory usage
4. Navigate away and return 5 times
5. Check Memory again
```
**Pass if:** Memory stable (no continuous growth)

---

### Test 4: Error Handling
```
1. Disconnect external monitor (if any)
2. Preview should continue on primary
3. Change display settings (resolution)
4. Preview should adapt or show fallback
```
**Pass if:** No crashes, graceful handling

---

## Technical Verification

### Code Review Checklist

**ScreenCaptureService.cs:**
- [ ] GraphicsCaptureItem created from monitor handle
- [ ] Direct3D11CaptureFramePool initialized
- [ ] CanvasDevice created successfully
- [ ] TryGetNextFrame() called in loop
- [ ] Proper cleanup in Dispose()

**ScreenBlurPreview.xaml.cs:**
- [ ] DispatcherTimer set to 33ms (30fps)
- [ ] GaussianBlurEffect with BlurAmount 0-48px
- [ ] SetWindowDisplayAffinity called on start
- [ ] Affinity restored on stop
- [ ] Matrix3x2 scaling for aspect ratio

**BrightnessPage.xaml:**
- [ ] controls:ScreenBlurPreview included
- [ ] Positioned after BlurLevelSlider
- [ ] BlurPreview named element

**BrightnessPage.xaml.cs:**
- [ ] BlurLevel_Changed calls BlurPreview?.SetBlurLevel()
- [ ] Updates both preview and overlay

---

## Bug Report Template

If something doesn't work:

```markdown
**Issue:** [Brief description]

**Steps to Reproduce:**
1. 
2. 
3. 

**Expected:** [What should happen]

**Actual:** [What actually happened]

**Environment:**
- Windows Version: [e.g., Windows 11 23H2]
- Build: [e.g., 22631.4602]
- GPU: [e.g., NVIDIA RTX 3060]

**Console Errors:** [Copy any error messages]

**Screenshot:** [If visual issue]
```

---

## Success Criteria

All checkpoints must pass:

- ✅ Preview appears and shows live content
- ✅ Blur updates in real-time (0-100%)
- ✅ No feedback loop (preview doesn't show itself)
- ✅ Performance acceptable (<10% CPU)
- ✅ Preview stops when not visible
- ✅ Theme switching works
- ✅ No memory leaks

**If all pass:** Milestone 7 COMPLETE! Ready to commit.

**If any fail:** Document in MILESTONE_7_ISSUES.md and fix.

---

## Screenshots to Take

For documentation:

1. **Preview at 0% blur** (clear)
2. **Preview at 50% blur** (moderate)
3. **Preview at 100% blur** (heavy)
4. **Task Manager showing CPU/GPU usage**
5. **Light theme** preview
6. **Dark theme** preview

Save in `docs/milestone-7/` folder.

---

## Final Report Template

After testing:

```markdown
# Milestone 7 Test Report

**Date:** [Today's date]
**Tester:** [Your name]
**Build:** Release x64

## Results Summary
- Preview: ✅ Working / ❌ Failed
- Real-time blur: ✅ Working / ❌ Failed  
- Feedback prevention: ✅ Working / ❌ Failed
- Performance: ✅ Acceptable / ❌ Too high
- Theme support: ✅ Working / ❌ Failed

## Detailed Findings
[List any issues, bugs, or observations]

## Screenshots
[Attach screenshots]

## Recommendation
✅ Ready to commit
⚠️ Needs fixes: [list]
❌ Blocked by: [issue]
```

---

## Quick Commands Reference

```powershell
# Build
.\build-test.ps1

# Run
Start-Process ".\src\AppLock\bin\x64\Release\net8.0-windows10.0.19041.0\AppLock.exe" -Verb RunAs

# Check running processes
Get-Process AppLock

# Kill if needed
Stop-Process -Name AppLock -Force

# View logs (if any)
Get-Content "$env:APPDATA\AppLock\logs\*.log" -Tail 50

# Commit after success
git add -A
git commit -m "Milestone 7 complete and tested"
```

---

## Milestone 7 Status: Ready for Testing

**Code:** ✅ 100% complete  
**Build:** ⏳ Pending manual build  
**Test:** ⏳ Awaiting verification  
**Commit:** ⏳ After successful test

**Next:** Complete this checklist, then proceed to Milestone 8.
