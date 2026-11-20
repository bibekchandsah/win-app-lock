using Microsoft.UI.Xaml.Media;

namespace AppLock.ViewModels;

/// <summary>One ring segment of the "App share" donut: an app's slice of the range's screen time.</summary>
public readonly record struct DonutSlice(string Label, long Seconds, Brush Brush);

/// <summary>One line of the donut legend, rebuilt on each refresh.</summary>
public sealed class DonutLegendItem
{
    public DonutLegendItem(string label, string shareText, Brush brush)
    {
        Label = label;
        ShareText = shareText;
        Brush = brush;
    }

    public string Label { get; }
    public string ShareText { get; }
    public Brush Brush { get; }
}
