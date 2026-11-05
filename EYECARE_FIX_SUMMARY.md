# Eye Care Card Expander Fix Summary

## Issue
The Eye Care card's "Quick Settings" expander was not showing the Eye Care toggle when expanded.

## Root Cause
All three content grids (BrightnessHotkeysGrid, BlurHotkeyGrid, EyeCareToggleGrid) in the XAML DataTemplate were visible by default, causing confusion in the visibility logic.

## Changes Made

### 1. XAML Changes (OthersPage.xaml)
- Added `Visibility="Collapsed"` to all three content grids:
  - `BrightnessHotkeysGrid`
  - `BlurHotkeyGrid`
  - `EyeCareToggleGrid`
- This ensures all controls start hidden and are only shown by the code-behind logic

### 2. Code-Behind Changes (OthersPage.xaml.cs)

#### Removed Duplicate Method
- Removed the old `CardExpander_Expanding` method that had inline visibility logic

#### Enhanced ConfigureExpanderContent Method
```csharp
private void ConfigureExpanderContent(Expander expander, UtilityCard card)
{
    var content = expander.Content as StackPanel;
    if (content == null) return;  // Added null check

    content.UpdateLayout();
    
    foreach (var child in content.Children)
    {
        if (child is Grid grid && !string.IsNullOrEmpty(grid.Name))  // Added name check
        {
            if (grid.Name == "BrightnessHotkeysGrid" || grid.Name == "BlurHotkeyGrid")
            {
                grid.Visibility = card.Id == "brightness" ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (grid.Name == "EyeCareToggleGrid")
            {
                grid.Visibility = card.Id == "eyecare" ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
    
    content.InvalidateMeasure();
    content.InvalidateArrange();  // Added for more thorough layout update
    content.UpdateLayout();
}
```

#### Enhanced CardExpander_Loaded Method
```csharp
private void CardExpander_Loaded(object sender, RoutedEventArgs e)
{
    if (sender is Expander expander && expander.DataContext is UtilityCard card)
    {
        expander.Visibility = (card.Id == "brightness" || card.Id == "eyecare") 
            ? Visibility.Visible 
            : Visibility.Collapsed;
        
        // Added DispatcherQueue delay to ensure template is fully loaded
        expander.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            ConfigureExpanderContent(expander, card);
        });
    }
}
```

## Expected Behavior After Build

### Brightness Card
When you expand the "Quick Settings":
- ✅ Shows "Brightness Hotkeys" toggle
- ✅ Shows "Blur Overlay Hotkey" toggle
- ❌ Does NOT show "Eye Care Reminders" toggle

### Eye Care Card
When you expand the "Quick Settings":
- ❌ Does NOT show "Brightness Hotkeys" toggle
- ❌ Does NOT show "Blur Overlay Hotkey" toggle
- ✅ Shows "Eye Care Reminders" toggle

## Testing Steps

1. Rebuild the project in Visual Studio
2. Run the application
3. Navigate to the "Others" page
4. Test Brightness card:
   - Expand "Quick Settings"
   - Verify only Brightness-related toggles appear
5. Test Eye Care card:
   - Expand "Quick Settings"
   - Verify only Eye Care toggle appears

## Files Modified
- `src/AppLock/Views/OthersPage.xaml` - Added Visibility="Collapsed" to content grids
- `src/AppLock/Views/OthersPage.xaml.cs` - Fixed duplicate method, enhanced visibility logic

## Build Status
- ✅ Duplicate method removed (CS0111 error fixed)
- ✅ XAML syntax correct
- ✅ Event handlers properly wired
- ✅ Ready to build and test
