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

    // Per-app custom password (optional — falls back to admin password when not set)
    public byte[] CustomPasswordHash { get; set; } = Array.Empty<byte>();
    public byte[] CustomPasswordSalt { get; set; } = Array.Empty<byte>();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomPassword =>
        CustomPasswordHash != null && CustomPasswordHash.Length > 0 &&
        CustomPasswordSalt != null && CustomPasswordSalt.Length > 0;

    /// <summary>
    /// Call this after modifying CustomPasswordHash/CustomPasswordSalt to update the UI.
    /// </summary>
    public void NotifyCustomPasswordChanged()
    {
        OnPropertyChanged(nameof(HasCustomPassword));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon { get; set; }
}
