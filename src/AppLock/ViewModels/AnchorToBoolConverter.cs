using Microsoft.UI.Xaml.Data;
using System;
using AppLock.Services;

namespace AppLock.ViewModels;

/// <summary>
/// Converts a string anchor value to bool for RadioButton IsChecked binding.
/// Stateless — no shared mutable field, so it is safe even when the same
/// instance is reused for multiple bindings.
/// </summary>
public class AnchorToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string anchor && parameter is string targetAnchor)
            return string.Equals(anchor, targetAnchor, StringComparison.Ordinal);
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // Only update when a RadioButton is being CHECKED (not unchecked).
        // When a RadioButton is unchecked (another one selected), WPF/WinUI
        // calls ConvertBack with value=false — we return DependencyProperty.UnsetValue
        // equivalent by returning the parameter itself only on true.
        if (value is bool isChecked && isChecked && parameter is string targetAnchor)
            return targetAnchor;

        // Return binding.DoNothing equivalent: use a sentinel that the ViewModel
        // will silently ignore.  Returning null here would set Anchor to null.
        // Returning parameter when false would immediately overwrite with
        // the wrong anchor.  The safest option: do nothing by returning
        // the CURRENT anchor from settings so the property doesn't change.
        return ServiceContainer.Settings.NotificationCustom.Anchor;
    }
}
