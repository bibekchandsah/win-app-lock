namespace AppLock.Models;

/// <summary>
/// One daily window during which system audio is muted automatically. Times are stored as "HH:mm" local
/// time; a window whose end is earlier than its start crosses midnight (for example 23:00 – 05:00).
/// </summary>
public sealed class MuteWindow
{
    public string Start { get; set; } = "23:00";
    public string End { get; set; } = "07:00";

    public MuteWindow Clone() => new() { Start = Start, End = End };
}
