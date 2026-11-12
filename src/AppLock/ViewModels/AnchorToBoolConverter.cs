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
            return string.Equals(anchor, targetAnchor, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isChecked && isChecked && parameter is string targetAnchor)
            return targetAnchor;

        return string.Empty;
    }
}
