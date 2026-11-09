using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AppLock.Services.RingLight;

/// <summary>
/// Creates, positions, and destroys one <see cref="RingOverlayWindow"/> per target display.
/// Supports multi-monitor, hot-plug (WM_DISPLAYCHANGE), and taskbar clearance.
/// Must be used on the UI (STA) thread.
/// </summary>
public sealed class RingOverlayManager : IDisposable
{
    // Key = "X,Y" of the display's OuterBounds origin (unique per physical monitor)
    private readonly Dictionary<string, RingOverlayWindow> _overlays = new();
    private bool _disposed;

    /// <summary>Show overlays on the target displays using current settings.</summary>
    public void Show(RingLightSettings settings)
    {
        EnsureOverlays(settings);
        foreach (var overlay in _overlays.Values)
        {
            overlay.ApplyCaptureExclusion(settings.ShowInScreenCapture);
            overlay.Show(settings);
        }
    }

    /// <summary>Hide and tear down all overlay windows.</summary>
    public async Task HideAllAsync(int fadeMs)
    {
        var tasks = new List<Task>();
        foreach (var overlay in _overlays.Values)
            tasks.Add(overlay.HideAsync(fadeMs));
        await Task.WhenAll(tasks);

        foreach (var overlay in _overlays.Values)
            overlay.Dispose();
        _overlays.Clear();
    }

    /// <summary>Re-apply settings live while overlays are visible.</summary>
    public void UpdateSettings(RingLightSettings settings)
    {
        var targets = GetTargetDisplays(settings);
        foreach (var display in targets)
        {
            var key = DisplayKey(display);
            if (_overlays.TryGetValue(key, out var overlay))
            {
                var bounds = GetDisplayBounds(display, settings);
                overlay.ApplySettings(settings, bounds);
            }
        }
    }

    /// <summary>
    /// Rebuilds overlays in response to WM_DISPLAYCHANGE or settings changes while On.
    /// Tears down windows for displays that disappeared; creates new ones for added displays.
    /// </summary>
    public void Rebuild(RingLightSettings settings)
    {
        if (_disposed) return;

        var targets = GetTargetDisplays(settings);
        var newKeys = new HashSet<string>();
        foreach (var d in targets)
            newKeys.Add(DisplayKey(d));

        // Remove overlays for displays that are no longer targeted
        var toRemove = new List<string>();
        foreach (var key in _overlays.Keys)
            if (!newKeys.Contains(key)) toRemove.Add(key);

        foreach (var key in toRemove)
        {
            _overlays[key].Dispose();
            _overlays.Remove(key);
        }

        // Add / refresh overlays for all targeted displays
        foreach (var display in targets)
        {
            var key    = DisplayKey(display);
            var bounds = GetDisplayBounds(display, settings);
            if (_overlays.TryGetValue(key, out var existing))
            {
                // Already exists – just reposition
                existing.ApplySettings(settings, bounds);
                existing.ApplyCaptureExclusion(settings.ShowInScreenCapture);
            }
            else
            {
                // New display appeared (or was targeted for the first time)
                var overlay = new RingOverlayWindow();
                overlay.ApplySettings(settings, bounds);
                overlay.ApplyCaptureExclusion(settings.ShowInScreenCapture);
                overlay.Show(settings);
                _overlays[key] = overlay;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var o in _overlays.Values)
            o.Dispose();
        _overlays.Clear();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EnsureOverlays(RingLightSettings settings)
    {
        var targets = GetTargetDisplays(settings);
        foreach (var display in targets)
        {
            var key = DisplayKey(display);
            if (_overlays.ContainsKey(key)) continue;
            var bounds  = GetDisplayBounds(display, settings);
            var overlay = new RingOverlayWindow();
            overlay.ApplySettings(settings, bounds);
            _overlays[key] = overlay;
        }
    }

    // ── Display enumeration ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of DisplayAreas to cover, based on settings.DisplayTarget:
    ///   "Primary"  → the primary display
    ///   "BuiltIn"  → first non-primary display if available, otherwise primary
    ///   "All"      → every connected display
    ///   anything else → try to match by DisplayKey; fall back to primary
    /// </summary>
    internal static List<DisplayArea> GetTargetDisplays(RingLightSettings settings)
    {
        IReadOnlyList<DisplayArea> all;
        try { all = DisplayArea.FindAll(); }
        catch { return new List<DisplayArea> { DisplayArea.Primary }; }

        var target = settings.DisplayTarget ?? "Primary";

        if (target == "All")
        {
            var list = new List<DisplayArea>();
            foreach (var d in all) list.Add(d);
            return list;
        }

        if (target == "BuiltIn")
        {
            // On multi-display setups, try non-primary first as external is usually primary or built-in is internal.
            // If only 1 display is connected, return the primary display so it still appears!
            foreach (var d in all)
                if (!d.IsPrimary)
                    return new List<DisplayArea> { d };
            return new List<DisplayArea> { DisplayArea.Primary };
        }

        if (target == "Primary")
            return new List<DisplayArea> { DisplayArea.Primary };

        // Specific display by key string (e.g. "0,0")
        foreach (var d in all)
            if (DisplayKey(d) == target)
                return new List<DisplayArea> { d };

        // Fallback to primary display
        return new List<DisplayArea> { DisplayArea.Primary };
    }

    private static RectInt32 GetDisplayBounds(DisplayArea display, RingLightSettings settings)
        => settings.KeepTaskbarClear ? display.WorkArea : display.OuterBounds;

    /// <summary>Unique string key for a display: "X,Y" of outer bounds origin.</summary>
    internal static string DisplayKey(DisplayArea d)
        => $"{d.OuterBounds.X},{d.OuterBounds.Y}";
}
