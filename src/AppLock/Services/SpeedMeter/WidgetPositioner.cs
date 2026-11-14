using System;
using System.Linq;
using AppLock.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AppLock.Services.SpeedMeter;

/// <summary>
/// Position math for the widget: percent-of-work-area storage, edge snapping, and clamping back
/// onto a visible monitor after a resolution, DPI or monitor change.
/// </summary>
public static class WidgetPositioner
{
    public const int SnapMargin = 8;
    public const int SnapDistance = 16;

    /// <summary>Position for the stored percentages inside the given work area.</summary>
    public static PointInt32 FromPercent(SpeedMeterSettings settings, RectInt32 work, SizeInt32 size)
    {
        var percentX = Math.Clamp(settings.PositionPercentX, 0f, 100f) / 100f;
        var percentY = Math.Clamp(settings.PositionPercentY, 0f, 100f) / 100f;

        return new PointInt32(
            work.X + (int)Math.Round((work.Width - size.Width) * percentX),
            work.Y + (int)Math.Round((work.Height - size.Height) * percentY));
    }

    /// <summary>Stores the window position as a percentage of its monitor work area.</summary>
    public static void SavePercent(SpeedMeterSettings settings, PointInt32 position, SizeInt32 size, RectInt32 work)
    {
        if (work.Width <= size.Width || work.Height <= size.Height)
        {
            settings.PositionPercentX = 0;
            settings.PositionPercentY = 0;
            return;
        }

        settings.PositionPercentX = (float)Math.Round((position.X - work.X) * 100.0 / (work.Width - size.Width), 2);
        settings.PositionPercentY = (float)Math.Round((position.Y - work.Y) * 100.0 / (work.Height - size.Height), 2);
    }

    /// <summary>Pulls the window against the nearest edge when it is released close to one.</summary>
    public static PointInt32 Snap(PointInt32 position, SizeInt32 size, RectInt32 work)
    {
        var x = position.X;
        var y = position.Y;

        var leftEdge = work.X;
        var rightEdge = work.X + work.Width - size.Width;
        var topEdge = work.Y;
        var bottomEdge = work.Y + work.Height - size.Height;

        if (Math.Abs(x - leftEdge) <= SnapDistance) x = leftEdge + SnapMargin;
        else if (Math.Abs(rightEdge - x) <= SnapDistance) x = rightEdge - SnapMargin;

        if (Math.Abs(y - topEdge) <= SnapDistance) y = topEdge + SnapMargin;
        else if (Math.Abs(bottomEdge - y) <= SnapDistance) y = bottomEdge - SnapMargin;

        return new PointInt32(x, y);
    }

    /// <summary>Keeps the window inside a work area after it grew or the monitor shrank.</summary>
    public static PointInt32 Clamp(PointInt32 position, SizeInt32 size, RectInt32 work)
    {
        var maxX = work.X + Math.Max(0, work.Width - size.Width);
        var maxY = work.Y + Math.Max(0, work.Height - size.Height);

        return new PointInt32(
            Math.Clamp(position.X, work.X, maxX),
            Math.Clamp(position.Y, work.Y, maxY));
    }

    /// <summary>True when any part of the window lands on a monitor.</summary>
    public static bool IsOnAnyDisplay(PointInt32 position, SizeInt32 size)
    {
        try
        {
            return DisplayArea.FindAll().Any(area =>
                area.WorkArea.X < position.X + size.Width &&
                area.WorkArea.X + area.WorkArea.Width > position.X &&
                area.WorkArea.Y < position.Y + size.Height &&
                area.WorkArea.Y + area.WorkArea.Height > position.Y);
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>Detects full-screen apps so the widget can step out of the way.</summary>
public static class FullscreenDetector
{
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// Uses Windows' own notification state (full-screen D3D, presentation mode, busy) —
    /// the same source the Start menu uses to decide whether to stay quiet.
    /// </summary>
    public static bool IsFullscreenAppActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;

            return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpeedMeter] Fullscreen check failed: {ex.Message}");
            return false;
        }
    }
}
