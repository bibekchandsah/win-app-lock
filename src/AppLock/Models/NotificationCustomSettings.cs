using System.Collections.Generic;

namespace AppLock.Models;

public class NotificationCustomSettings
{
    public bool Enabled { get; set; }
    public string Anchor { get; set; } = "BottomRight";   // TopLeft|TopCenter|TopRight|MiddleLeft|Center|MiddleRight|BottomLeft|BottomCenter|BottomRight|Custom
    public int MarginPx { get; set; } = 12;
    public bool RespectTaskbar { get; set; } = true;
    public float CustomPercentX { get; set; }
    public float CustomPercentY { get; set; }
    public string MonitorMode { get; set; } = "Primary";   // Primary|ActiveWindow|Mouse|Specific
    public string? MonitorId { get; set; }
    public string EnterFrom { get; set; } = "Auto";        // Auto|Top|Bottom|Left|Right|Fade|ScaleFade|None
    public string ExitTo { get; set; } = "Reverse";        // Reverse|Top|Bottom|Left|Right|Fade|None
    public string DistanceMode { get; set; } = "Edge";     // Edge|Short
    public int ShortDistancePx { get; set; } = 60;
    public int EnterMs { get; set; } = 300;
    public int ExitMs { get; set; } = 250;
    public string Easing { get; set; } = "EaseOut";        // EaseOut|EaseInOut|Linear|Spring|Bounce
    public int OpacityPercent { get; set; } = 100;
    public bool ClickThrough { get; set; }
    public bool AltToInteract { get; set; } = true;
}
