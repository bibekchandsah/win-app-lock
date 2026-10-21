using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppLock.Models;

public partial class RunningWindow : ObservableObject
{
    public nint Handle { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public BitmapImage? Icon { get; set; }

    /// <summary>
    /// How many windows share this same process name.
    /// Set by the ViewModel after grouping. 1 means unique.
    /// </summary>
    [ObservableProperty]
    private int _windowCount = 1;

    [ObservableProperty]
    private bool _isLocked;

    /// <summary>
    /// Display name for the process, appending "(xN)" when multiple windows share the same process.
    /// Example: "chrome (x3)" or just "chrome".
    /// </summary>
    public string ProcessDisplayName =>
        WindowCount > 1 ? $"{ProcessName} (x{WindowCount})" : ProcessName;

    partial void OnWindowCountChanged(int value)
    {
        OnPropertyChanged(nameof(ProcessDisplayName));
    }
}
