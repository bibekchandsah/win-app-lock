using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLock.Models;

public enum LockRuleType
{
    ProcessName,
    WindowTitle,
    PickedWindow
}

public class LockRule : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LockRuleType Type { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public string WindowTitlePattern { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string DisplayName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon { get; set; }
}
