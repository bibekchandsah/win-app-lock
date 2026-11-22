using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using AppLock.Models;

namespace AppLock.WindowIsland.Host;

/// <summary>
/// Builds and animates the Window Island notch shape using the Windows.UI.Composition API only.
/// No XAML Width/Height/Margin animations — all motion is driven by Composition spring
/// animations on Geometry Size, CornerRadius, and ExpressionAnimations on Offset.
/// </summary>
public sealed class WindowIslandCompositor : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _rootVisual;
    private readonly ShapeVisual _shapeVisual;
    private readonly CompositionRoundedRectangleGeometry _bodyGeometry;
    private readonly CompositionSpriteShape _bodyShape;

    // Design dimensions in DIPs
    public const float FoldedWidthDip = 176f;
    public const float FoldedHeightDip = 32f;
    public const float FoldedRadiusDip = 16f;

    public const float OpenWidthDip = 420f;
    public const float OpenHeightDip = 180f;
    public const float OpenRadiusDip = 24f;

    private float _sizeFactor = 1.0f;
    private float _windowWidthDip = 440f;
    private bool _isOpen;
    private bool _disposed;

    public bool IsOpen => _isOpen;

    public WindowIslandCompositor(UIElement hostElement, WindowIslandSettings settings, float windowWidthDip)
    {
        _compositor = ElementCompositionPreview.GetElementVisual(hostElement).Compositor;
        _windowWidthDip = windowWidthDip;
        _sizeFactor = (float)(settings.SizePercent / 100.0);

        _rootVisual = _compositor.CreateContainerVisual();
        _rootVisual.RelativeSizeAdjustment = Vector2.One;

        // Notch body geometry
        _bodyGeometry = _compositor.CreateRoundedRectangleGeometry();
        float initialWidth = FoldedWidthDip * _sizeFactor;
        float initialHeight = FoldedHeightDip * _sizeFactor;
        float initialRadius = FoldedRadiusDip * _sizeFactor;
        _bodyGeometry.Size = new Vector2(initialWidth, initialHeight);
        _bodyGeometry.CornerRadius = new Vector2(initialRadius, initialRadius);

        // Body shape
        _bodyShape = _compositor.CreateSpriteShape(_bodyGeometry);
        _bodyShape.FillBrush = GetBodyBrush(settings);

        // Expression animation keeping the notch centered horizontally: (windowWidth - geom.Size.X) / 2
        var centerAnim = _compositor.CreateExpressionAnimation("Vector2((windowWidth - geom.Size.X) / 2.0, yOffset)");
        centerAnim.SetReferenceParameter("geom", _bodyGeometry);
        centerAnim.SetScalarParameter("windowWidth", _windowWidthDip);
        centerAnim.SetScalarParameter("yOffset", settings.Shape == "Capsule" ? 6f : 0f);
        _bodyShape.StartAnimation("Offset", centerAnim);

        _shapeVisual = _compositor.CreateShapeVisual();
        _shapeVisual.RelativeSizeAdjustment = Vector2.One;
        _shapeVisual.Shapes.Add(_bodyShape);

        _rootVisual.Children.InsertAtTop(_shapeVisual);
        ElementCompositionPreview.SetElementChildVisual(hostElement, _rootVisual);

        ApplySettings(settings);
    }

    public void AnimateToOpen(Action? onCompleted = null)
    {
        if (_disposed || _isOpen) return;
        _isOpen = true;

        float targetWidth = OpenWidthDip * _sizeFactor;
        float targetHeight = OpenHeightDip * _sizeFactor;
        float targetRadius = OpenRadiusDip * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: 0.72f,
            periodMs: 320,
            onCompleted);
    }

    public void AnimateToFolded(Action? onCompleted = null)
    {
        if (_disposed || !_isOpen) return;
        _isOpen = false;

        float targetWidth = FoldedWidthDip * _sizeFactor;
        float targetHeight = FoldedHeightDip * _sizeFactor;
        float targetRadius = FoldedRadiusDip * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: 0.90f,
            periodMs: 260,
            onCompleted);
    }

    private void AnimateShape(Vector2 targetSize, Vector2 targetRadius, float dampingRatio, int periodMs, Action? onCompleted)
    {
        // Composition Spring Vector2 animations for smooth, physical, interruptible motion
        var sizeAnim = _compositor.CreateSpringVector2Animation();
        sizeAnim.DampingRatio = dampingRatio;
        sizeAnim.Period = TimeSpan.FromMilliseconds(periodMs);
        sizeAnim.FinalValue = targetSize;

        var radiusAnim = _compositor.CreateSpringVector2Animation();
        radiusAnim.DampingRatio = dampingRatio;
        radiusAnim.Period = TimeSpan.FromMilliseconds(periodMs);
        radiusAnim.FinalValue = targetRadius;

        if (onCompleted != null)
        {
            var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) => onCompleted();
            _bodyGeometry.StartAnimation("Size", sizeAnim);
            _bodyGeometry.StartAnimation("CornerRadius", radiusAnim);
            batch.End();
        }
        else
        {
            _bodyGeometry.StartAnimation("Size", sizeAnim);
            _bodyGeometry.StartAnimation("CornerRadius", radiusAnim);
        }
    }

    public void UpdateWindowWidth(float windowWidthDip, WindowIslandSettings settings)
    {
        if (_disposed) return;
        _windowWidthDip = windowWidthDip;
        _sizeFactor = (float)(settings.SizePercent / 100.0);

        var centerAnim = _compositor.CreateExpressionAnimation("Vector2((windowWidth - geom.Size.X) / 2.0, yOffset)");
        centerAnim.SetReferenceParameter("geom", _bodyGeometry);
        centerAnim.SetScalarParameter("windowWidth", _windowWidthDip);
        centerAnim.SetScalarParameter("yOffset", settings.Shape == "Capsule" ? 6f : 0f);
        _bodyShape.StartAnimation("Offset", centerAnim);
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        if (_disposed) return;
        _sizeFactor = (float)(settings.SizePercent / 100.0);

        // Animate opacity via Composition
        float opacity = settings.OpacityPercent / 100f;
        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(1f, opacity);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(200);
        _shapeVisual.StartAnimation("Opacity", opacityAnim);

        _bodyShape.FillBrush = GetBodyBrush(settings);

        // Update target dimensions according to open/folded state
        float width = (_isOpen ? OpenWidthDip : FoldedWidthDip) * _sizeFactor;
        float height = (_isOpen ? OpenHeightDip : FoldedHeightDip) * _sizeFactor;
        float radius = (_isOpen ? OpenRadiusDip : FoldedRadiusDip) * _sizeFactor;
        _bodyGeometry.Size = new Vector2(width, height);
        _bodyGeometry.CornerRadius = new Vector2(radius, radius);
    }

    private CompositionBrush GetBodyBrush(WindowIslandSettings settings)
    {
        var color = settings.Theme switch
        {
            "Light" => Color.FromArgb(235, 245, 245, 245),
            "Dark"  => Color.FromArgb(235, 24, 24, 24),
            _       => Color.FromArgb(235, 24, 24, 24) // System defaults to dark
        };
        return _compositor.CreateColorBrush(color);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bodyGeometry.Dispose();
        _bodyShape.Dispose();
        _shapeVisual.Dispose();
        _rootVisual.Dispose();
    }
}
