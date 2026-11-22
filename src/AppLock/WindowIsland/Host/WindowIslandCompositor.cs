using System;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using AppLock.Models;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Builds and animates the Window Island shape using the Windows.UI.Composition API only.
/// No XAML Width/Height/Margin animations — all motion is via spring KeyFrameAnimations on
/// Offset, Scale, Opacity and InsetClip properties of ContainerVisuals.
/// </summary>
public sealed class WindowIslandCompositor : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _rootVisual;
    private readonly SpriteVisual _notchBody;
    private readonly CompositionRoundedRectangleGeometry _bodyGeometry;
    private readonly ShapeVisual _shapeVisual;
    private bool _disposed;

    public WindowIslandCompositor(UIElement hostElement, WindowIslandSettings settings)
    {
        _compositor = ElementCompositionPreview.GetElementVisual(hostElement).Compositor;

        // Root container — no background; everything is on top of the desktop
        _rootVisual = _compositor.CreateContainerVisual();
        _rootVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;

        // Notch body — rounded rectangle shape drawn by Composition
        _bodyGeometry = _compositor.CreateRoundedRectangleGeometry();
        var bodyShape = _compositor.CreateSpriteShape(_bodyGeometry);
        bodyShape.FillBrush = GetBodyBrush(settings);

        _shapeVisual = _compositor.CreateShapeVisual();
        _shapeVisual.Shapes.Add(bodyShape);
        _rootVisual.Children.InsertAtTop(_shapeVisual);

        // Placeholder for the notch sprite (actual drawing area)
        _notchBody = _compositor.CreateSpriteVisual();
        _notchBody.Brush = _compositor.CreateColorBrush(Colors.Transparent);
        _rootVisual.Children.InsertAtTop(_notchBody);

        ElementCompositionPreview.SetElementChildVisual(hostElement, _rootVisual);

        ApplySettings(settings);
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        if (_disposed) return;

        // Animate opacity via Composition (never XAML Opacity on the panel)
        float opacity = settings.OpacityPercent / 100f;
        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(1f, opacity);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(200);
        _shapeVisual.StartAnimation("Opacity", opacityAnim);

        // Update fill color for theme
        var bodyShape = (CompositionSpriteShape)_shapeVisual.Shapes[0];
        bodyShape.FillBrush = GetBodyBrush(settings);
    }

    private CompositionBrush GetBodyBrush(WindowIslandSettings settings)
    {
        // Respect theme setting — Composition colors only, no XAML resources
        var color = settings.Theme switch
        {
            "Light" => Color.FromArgb(230, 245, 245, 245),
            "Dark"  => Color.FromArgb(230, 24, 24, 24),
            _       => Color.FromArgb(230, 24, 24, 24) // System defaults to dark for the notch
        };
        return _compositor.CreateColorBrush(color);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notchBody.Dispose();
        _shapeVisual.Dispose();
        _bodyGeometry.Dispose();
        _rootVisual.Dispose();
    }
}
