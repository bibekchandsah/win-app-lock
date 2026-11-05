# Eye Care Card Expander Fix - DataTemplateSelector Approach

## Final Root Cause
Even with x:Bind and computed properties, the Eye Care card was showing the same content as the Brightness card. This indicates the expander content wasn't being re-evaluated per card instance - likely a caching or template reuse issue.

## Ultimate Solution: DataTemplateSelector

Using **DataTemplateSelector** to programmatically select completely separate templates based on card type. This ensures each card type gets its own unique content structure with NO shared elements.

## Changes Made

### 1. Created CardContentTemplateSelector Class

```csharp
public class CardContentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BrightnessTemplate { get; set; }
    public DataTemplate? EyeCareTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is UtilityCard card)
        {
            return card.Id switch
            {
                "brightness" => BrightnessTemplate,
                "eyecare" => EyeCareTemplate,
                _ => null
            };
        }
        return null;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
```

### 2. Replaced Expander.Content with Expander.ContentTemplateSelector

**Before:** Single content with conditional visibility
```xml
<Expander.Content>
    <StackPanel>
        <Grid Visibility="{x:Bind BrightnessContentVisibility}">...</Grid>
        <Grid Visibility="{x:Bind BrightnessContentVisibility}">...</Grid>
        <Grid Visibility="{x:Bind EyeCareContentVisibility}">...</Grid>
    </StackPanel>
</Expander.Content>
```

**After:** Completely separate templates selected at runtime
```xml
<Expander.ContentTemplateSelector>
    <local:CardContentTemplateSelector>
        <local:CardContentTemplateSelector.BrightnessTemplate>
            <DataTemplate x:DataType="local:UtilityCard">
                <!-- Only Brightness toggles here -->
            </DataTemplate>
        </local:CardContentTemplateSelector.BrightnessTemplate>
        
        <local:CardContentTemplateSelector.EyeCareTemplate>
            <DataTemplate x:DataType="local:UtilityCard">
                <!-- Only Eye Care toggle here -->
            </DataTemplate>
        </local:CardContentTemplateSelector.EyeCareTemplate>
    </local:CardContentTemplateSelector>
</Expander.ContentTemplateSelector>
```

### 3. Brightness Template Content

Contains ONLY brightness-related controls:
- Brightness Hotkeys toggle
- Blur Overlay Hotkey toggle

```xml
<local:CardContentTemplateSelector.BrightnessTemplate>
    <DataTemplate x:DataType="local:UtilityCard">
        <StackPanel Spacing="12" Margin="0,8,0,0">
            <Grid><!-- Brightness Hotkeys --></Grid>
            <Grid><!-- Blur Hotkey --></Grid>
        </StackPanel>
    </DataTemplate>
</local:CardContentTemplateSelector.BrightnessTemplate>
```

### 4. Eye Care Template Content

Contains ONLY eye care control:
- Eye Care Reminders toggle

```xml
<local:CardContentTemplateSelector.EyeCareTemplate>
    <DataTemplate x:DataType="local:UtilityCard">
        <StackPanel Spacing="12" Margin="0,8,0,0">
            <Grid><!-- Eye Care Toggle --></Grid>
        </StackPanel>
    </DataTemplate>
</local:CardContentTemplateSelector.EyeCareTemplate>
```

## How DataTemplateSelector Works

1. **Expander receives DataContext** (the UtilityCard instance)
2. **Selector's SelectTemplateCore is called** with the UtilityCard as parameter
3. **Selector examines card.Id** and returns appropriate template:
   - Id == "brightness" → Returns BrightnessTemplate
   - Id == "eyecare" → Returns EyeCareTemplate
4. **WinUI applies the selected template** to create the content
5. **Each card gets completely different content** - no sharing, no visibility games

## Why This Approach is Bulletproof

| Issue | Previous Approaches | DataTemplateSelector |
|-------|---------------------|---------------------|
| DataContext flow | Failed with Binding | ✅ Not needed - selector gets object directly |
| Template reuse | Visibility didn't work | ✅ Completely separate templates |
| Property binding | x:Bind didn't trigger | ✅ Template selected before binding |
| Caching | Content might be cached | ✅ Selector called per card |
| Debugging | Hard to trace | ✅ Clear selection logic |

## Benefits

1. **Explicit separation** - No shared elements, no visibility logic
2. **Type-safe** - Each template uses x:DataType
3. **Debuggable** - Set breakpoint in SelectTemplateCore to see selection
4. **Maintainable** - Each card type has its own clear template
5. **Extensible** - Easy to add new card types later

## Expected Behavior

### Brightness Card:
- Expands → Shows **BrightnessTemplate**
  - ✅ Brightness Hotkeys toggle
  - ✅ Blur Overlay Hotkey toggle

### Eye Care Card:
- Expands → Shows **EyeCareTemplate**
  - ✅ Eye Care Reminders toggle ONLY

**No visibility conditions, no shared content, no interdependencies!**

## Files Modified
- `src/AppLock/Views/OthersPage.xaml`
  - Replaced Expander.Content with Expander.ContentTemplateSelector
  - Removed Expanding event handler binding
  - Created two separate DataTemplates inside selector
- `src/AppLock/Views/OthersPage.xaml.cs`
  - Added CardContentTemplateSelector class
  - Removed CardExpander_Expanding method

## Build & Test

1. Clean solution (to clear any XAML cache)
2. Rebuild project
3. Run application
4. Navigate to Others page
5. Expand Brightness card → Should see 2 toggles
6. Expand Eye Care card → Should see 1 toggle (different content!)

## Debugging Tip

If it still doesn't work, add debug output in SelectTemplateCore:
```csharp
protected override DataTemplate? SelectTemplateCore(object item)
{
    if (item is UtilityCard card)
    {
        System.Diagnostics.Debug.WriteLine($"Selecting template for card: {card.Id}");
        return card.Id switch
        {
            "brightness" => BrightnessTemplate,
            "eyecare" => EyeCareTemplate,
            _ => null
        };
    }
    return null;
}
```

Check Visual Studio Output window to see which template is being selected for each card.

## This MUST Work! 🎯

DataTemplateSelector is the nuclear option - it completely bypasses all visibility, binding, and DataContext issues by programmatically selecting different templates. If this doesn't work, there's something fundamentally wrong with the expander or card setup itself.
