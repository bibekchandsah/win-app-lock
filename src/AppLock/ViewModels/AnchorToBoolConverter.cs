using Microsoft.UI.Xaml.Data;
using System;

namespace AppLock.ViewModels;

public class AnchorToBoolConverter : IValueConverter
{
    private string? _currentValue;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string anchor && parameter is string targetAnchor)
        {
            _currentValue = anchor; // Store current value
            return anchor == targetAnchor;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // Only update when checking (not unchecking)
        if (value is bool isChecked && isChecked && parameter is string targetAnchor)
        {
            return targetAnchor;
        }
        // Return current value to prevent change when unchecking
        return _currentValue ?? "BottomRight";
    }
}
