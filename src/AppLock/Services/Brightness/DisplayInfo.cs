namespace AppLock.Services.Brightness;

public class DisplayInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string ControlMethod { get; set; } = "Unknown"; // Built-in, External (DDC/CI), Software dimming
    public bool IsBuiltIn { get; set; }
    public int CurrentBrightness { get; set; } = 100;
}
