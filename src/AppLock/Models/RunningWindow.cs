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
    
    [ObservableProperty]
    private bool _isLocked;
}
