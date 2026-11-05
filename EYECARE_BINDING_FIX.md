# Eye Care Card Expander Fix - Data Binding Approach

## Root Cause Analysis
The previous approach failed because `x:Name` in a DataTemplate creates separate namescopes for each template instance. The code couldn't find the named grids because they exist in the template's namescope, not the page's namescope.

## Solution: Data Binding with Value Converters

Instead of trying to manipulate visibility in code-behind, we now use **data binding with value converters** to automatically show/hide content based on the card's `Id` property.

## Changes Made

### 1. Added Value Converter Class (OthersPage.xaml.cs)

```csharp
// Converter to show/hide content based on card ID
public class CardIdVisibilityConverter : IValueConverter
{
    public string TargetId { get; set; } = string.Empty;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string cardId)
        {
            return cardId == TargetId ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
```

### 2. Added Converter Resources (OthersPage.xaml)

```xml
<Page.Resources>
    <local:CardIdVisibilityConverter x:Key="BrightnessVisibilityConverter" TargetId="brightness"/>
    <local:CardIdVisibilityConverter x:Key="EyeCareVisibilityConverter" TargetId="eyecare"/>
</Page.Resources>
```

### 3. Updated XAML Bindings

**Before:** Using `x:Name` and `Visibility="Collapsed"` (didn't work)
```xml
<Grid x:Name="BrightnessHotkeysGrid" Visibility="Collapsed">
```

**After:** Using data binding to `Id` property with converter
```xml
<Grid Visibility="{Binding Id, Converter={StaticResource BrightnessVisibilityConverter}}">
```

### 4. Simplified Code-Behind

Removed the entire `ConfigureExpanderContent` method and dispatcher logic since visibility is now handled automatically by data binding.

```csharp
private void CardExpander_Loaded(object sender, RoutedEventArgs e)
{
    // Show expander for both brightness and eyecare cards
    if (sender is Expander expander && expander.DataContext is UtilityCard card)
    {
        expander.Visibility = (card.Id == "brightness" || card.Id == "eyecare") 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }
}

private void CardExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
{
    // No manual configuration needed - visibility is handled by binding converters
}
```

## How It Works

1. Each `UtilityCard` has an `Id` property ("brightness" or "eyecare")
2. The DataContext of each card in the ItemsControl is the `UtilityCard` object
3. Each Grid in the expander content binds its `Visibility` to the `Id` property
4. The converter compares the card's `Id` against its `TargetId`:
   - **BrightnessVisibilityConverter** (TargetId="brightness"): Shows only for brightness card
   - **EyeCareVisibilityConverter** (TargetId="eyecare"): Shows only for eyecare card
5. Visibility updates automatically when the DataContext is set - no code-behind manipulation needed

## Expected Behavior

### Brightness Card Quick Settings:
- ✅ Brightness Hotkeys toggle (uses BrightnessVisibilityConverter)
- ✅ Blur Overlay Hotkey toggle (uses BrightnessVisibilityConverter)
- ❌ Eye Care toggle hidden (EyeCareVisibilityConverter returns Collapsed)

### Eye Care Card Quick Settings:
- ❌ Brightness Hotkeys toggle hidden (BrightnessVisibilityConverter returns Collapsed)
- ❌ Blur Overlay Hotkey toggle hidden (BrightnessVisibilityConverter returns Collapsed)
- ✅ Eye Care toggle visible (uses EyeCareVisibilityConverter)

## Benefits of This Approach

1. **Declarative** - Visibility logic is in XAML where it belongs
2. **No timing issues** - Binding happens automatically when DataContext is set
3. **No namescope problems** - Converters work within the template's namescope
4. **Cleaner code** - No need for complex visual tree traversal
5. **MVVM-friendly** - Separation of concerns between view and logic

## Files Modified
- `src/AppLock/Views/OthersPage.xaml` - Added Page.Resources with converters, updated Grid Visibility bindings, added xmlns:local
- `src/AppLock/Views/OthersPage.xaml.cs` - Added CardIdVisibilityConverter class, removed ConfigureExpanderContent method, simplified event handlers

## Build & Test
Rebuild the project and test:
1. Navigate to Others page
2. Expand Brightness card → should show 2 toggles (Brightness Hotkeys, Blur Hotkey)
3. Expand Eye Care card → should show 1 toggle (Eye Care Reminders)
