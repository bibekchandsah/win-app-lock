using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
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
/// 
/// Interruptible: calling AnimateToOpen() while closing (and vice-versa) retargets the
/// FinalValue from the current in-flight position — no jump, no restart.
/// </summary>
public sealed class WindowIslandCompositor : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _rootVisual;
    private readonly ShapeVisual _shapeVisual;
    private readonly CompositionVisualSurface _maskSurface;
    private readonly CompositionSurfaceBrush _maskSurfaceBrush;
    private readonly CompositionMaskBrush _outputMaskBrush;
    private readonly CompositionColorBrush _bodyColorBrush;
    private readonly SpriteVisual _outputVisual;
    private readonly CompositionRoundedRectangleGeometry _bodyGeometry;
    private readonly CompositionRoundedRectangleGeometry _clipGeometry;
    private readonly CompositionGeometricClip _bodyClip;
    private readonly CompositionSpriteShape _bodyShape;
    private readonly CompositionSpriteShape _notchLeftEarShape;
    private readonly CompositionSpriteShape _notchRightEarShape;
    private readonly CompositionEasingFunction _easeOut;
    private WindowIslandSettings _settings;
    private string? _lastShape;
    private int _lastOpacityPercent = -1;
    private int _lastSizePercent = -1;

    public const float NotchEarRadius = 12f;
    public const float FoldedNotchRadiusDip = 12f;

    // Design dimensions in DIPs (folded)
    public const float FoldedWidthDip  = 176f;
    public const float FoldedHeightDip = 32f;
    public const float FoldedRadiusDip = 16f;

    // Card dimensions in DIPs
    public const float CardDefaultWidthDip  = 270f;
    public const float CardDefaultHeightDip = 38f;
    public const float CardDefaultRadiusDip = 19f;

    // Open defaults — overridden per-tab
    public const float OpenWidthDip  = 520f;
    public const float OpenHeightDip = 240f;
    public const float OpenRadiusDip = 24f;

    private float _sizeFactor    = 1.0f;
    private float _windowWidthDip = 560f;
    private bool  _isOpen;
    private bool  _isCard;
    private bool  _isCommand;
    private bool  _disposed;

    // Current target tab size (may change per tab switch)
    private float _openTargetW = OpenWidthDip;
    private float _openTargetH = OpenHeightDip;
    private float _cardTargetW = CardDefaultWidthDip;
    private float _cardTargetH = CardDefaultHeightDip;

    public bool IsOpen => _isOpen;
    public bool IsCard => _isCard;
    public bool IsCommand => _isCommand;
    public float CurrentFoldedWidthDip { get; private set; } = FoldedWidthDip;
    public float CurrentFoldedHeightDip { get; private set; } = FoldedHeightDip;

    private float GetEffectiveFoldedRadius() =>
        CurrentFoldedHeightDip > FoldedHeightDip + 8f
            ? OpenRadiusDip
            : (_settings.Shape == "Capsule" ? FoldedRadiusDip : FoldedNotchRadiusDip);

    public void SetFoldedWidth(float widthDip, bool animate = true) =>
        SetFoldedSize(widthDip, FoldedHeightDip, animate);

    public void SetFoldedSize(float widthDip, float heightDip, bool animate = true)
    {
        if (_disposed) return;
        float clampedW = Math.Clamp(widthDip, FoldedWidthDip, 560f);
        float clampedH = Math.Clamp(heightDip, FoldedHeightDip, 200f);
        if (Math.Abs(clampedW - CurrentFoldedWidthDip) < 1f && Math.Abs(clampedH - CurrentFoldedHeightDip) < 1f) return;
        CurrentFoldedWidthDip = clampedW;
        CurrentFoldedHeightDip = clampedH;

        if (!_isOpen && !_isCard && !_isCommand)
        {
            float targetWidth = CurrentFoldedWidthDip * _sizeFactor;
            float targetHeight = CurrentFoldedHeightDip * _sizeFactor;
            float targetRadius = GetEffectiveFoldedRadius() * _sizeFactor;
            if (animate)
            {
                AnimateShape(
                    new Vector2(targetWidth, targetHeight),
                    new Vector2(targetRadius, targetRadius),
                    dampingRatio: IslandMotion.CloseDampingRatio,
                    periodMs:     IslandMotion.ClosePeriodMs,
                    onCompleted:  null);
            }
            else
            {
                _bodyGeometry.Size = new Vector2(targetWidth, targetHeight);
                _bodyGeometry.CornerRadius = new Vector2(targetRadius, targetRadius);
            }
        }
    }

    public WindowIslandCompositor(UIElement hostElement, WindowIslandSettings settings, float windowWidthDip)
    {
        _settings      = settings;
        _compositor    = ElementCompositionPreview.GetElementVisual(hostElement).Compositor;
        _easeOut       = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));
        _windowWidthDip = windowWidthDip;
        _sizeFactor    = (float)(settings.SizePercent / 100.0);

        _rootVisual = _compositor.CreateContainerVisual();
        _rootVisual.RelativeSizeAdjustment = Vector2.One;

        // Notch body geometry
        _bodyGeometry = _compositor.CreateRoundedRectangleGeometry();
        float initialWidth  = FoldedWidthDip  * _sizeFactor;
        float initialHeight = FoldedHeightDip * _sizeFactor;
        float initialRadius = (settings.Shape == "Capsule" ? FoldedRadiusDip : FoldedNotchRadiusDip) * _sizeFactor;
        _bodyGeometry.Size         = new Vector2(initialWidth, initialHeight);
        _bodyGeometry.CornerRadius = new Vector2(initialRadius, initialRadius);

        // All mask shapes are solid opaque white so overlapping edges never double-composite alpha
        var whiteMaskBrush = _compositor.CreateColorBrush(Colors.White);

        // Body shape
        _bodyShape = _compositor.CreateSpriteShape(_bodyGeometry);
        _bodyShape.FillBrush = whiteMaskBrush;

        // GPU clip geometry mirroring _bodyGeometry + _bodyShape.Offset so foreground layers
        // stay cleanly clipped inside the expanding notch without scaling distortion.
        _clipGeometry = _compositor.CreateRoundedRectangleGeometry();
        var clipSizeAnim = _compositor.CreateExpressionAnimation("geom.Size");
        clipSizeAnim.SetReferenceParameter("geom", _bodyGeometry);
        _clipGeometry.StartAnimation("Size", clipSizeAnim);

        var clipRadiusAnim = _compositor.CreateExpressionAnimation("geom.CornerRadius");
        clipRadiusAnim.SetReferenceParameter("geom", _bodyGeometry);
        _clipGeometry.StartAnimation("CornerRadius", clipRadiusAnim);

        var clipOffsetAnim = _compositor.CreateExpressionAnimation("shape.Offset");
        clipOffsetAnim.SetReferenceParameter("shape", _bodyShape);
        _clipGeometry.StartAnimation("Offset", clipOffsetAnim);

        _bodyClip = _compositor.CreateGeometricClip(_clipGeometry);

        // Apple-style concave top ears + corner fillers
        _notchLeftEarShape = _compositor.CreateSpriteShape(CreateNotchEarGeometry(isRight: false));
        _notchLeftEarShape.FillBrush = whiteMaskBrush;

        _notchRightEarShape = _compositor.CreateSpriteShape(CreateNotchEarGeometry(isRight: true));
        _notchRightEarShape.FillBrush = whiteMaskBrush;

        // Expression animation: keep the notch centered horizontally
        RefreshCenterAnimation(settings.Shape);

        _shapeVisual = _compositor.CreateShapeVisual();
        float maskH = MathF.Max(OpenHeightDip + 40f, (IslandMotion.ChatHeightDip + 40f) * _sizeFactor);
        _shapeVisual.Size = new Vector2(_windowWidthDip, maskH);
        _shapeVisual.Shapes.Add(_bodyShape);
        _shapeVisual.Shapes.Add(_notchLeftEarShape);
        _shapeVisual.Shapes.Add(_notchRightEarShape);

        _maskSurface = _compositor.CreateVisualSurface();
        _maskSurface.SourceVisual = _shapeVisual;
        _maskSurface.SourceSize = new Vector2(_windowWidthDip, maskH);

        _maskSurfaceBrush = _compositor.CreateSurfaceBrush(_maskSurface);
        _maskSurfaceBrush.Stretch = CompositionStretch.None;
        _maskSurfaceBrush.HorizontalAlignmentRatio = 0f;
        _maskSurfaceBrush.VerticalAlignmentRatio = 0f;

        Color initialColor = WindowIslandColorHelper.ResolveBaseColor(settings);
        _bodyColorBrush = _compositor.CreateColorBrush(Color.FromArgb(255, initialColor.R, initialColor.G, initialColor.B));

        _outputMaskBrush = _compositor.CreateMaskBrush();
        _outputMaskBrush.Source = _bodyColorBrush;
        _outputMaskBrush.Mask = _maskSurfaceBrush;

        _outputVisual = _compositor.CreateSpriteVisual();
        _outputVisual.RelativeSizeAdjustment = Vector2.One;
        _outputVisual.Brush = _outputMaskBrush;
        int initOpacityPct = Math.Clamp(settings.OpacityPercent, 10, 100);
        _outputVisual.Opacity = initOpacityPct / 100f;
        _lastOpacityPercent = initOpacityPct;
        _lastShape = settings.Shape;
        _lastSizePercent = settings.SizePercent;

        _rootVisual.Children.InsertAtTop(_outputVisual);
        ElementCompositionPreview.SetElementChildVisual(hostElement, _rootVisual);

        ApplySettings(settings);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void AttachContentClip(UIElement contentHost)
    {
        if (_disposed) return;
        ElementCompositionPreview.GetElementVisual(contentHost).Clip = _bodyClip;
    }

    /// <summary>Animate to open state for the given tab index (or default if -1).</summary>
    public void AnimateToOpen(int tabIndex = 0, Action? onCompleted = null)
    {
        if (_disposed) return;

        var (tw, th) = IslandMotion.TabSize(tabIndex);
        _openTargetW = tw;
        _openTargetH = th;
        _isOpen = true;
        _isCard = false;
        _isCommand = false;

        float targetWidth  = _openTargetW * _sizeFactor;
        float targetHeight = _openTargetH * _sizeFactor;
        float targetRadius = OpenRadiusDip * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: IslandMotion.OpenDampingRatio,
            periodMs:     IslandMotion.OpenPeriodMs,
            onCompleted);
    }

    /// <summary>Animate notch into a live card expansion.</summary>
    public void AnimateToCard(float widthDip = CardDefaultWidthDip, float heightDip = CardDefaultHeightDip, Action? onCompleted = null)
    {
        if (_disposed) return;
        _isCard = true;
        _isOpen = false;
        _isCommand = false;
        _cardTargetW = widthDip;
        _cardTargetH = heightDip;

        float targetWidth  = widthDip * _sizeFactor;
        float targetHeight = heightDip * _sizeFactor;
        float targetRadius = MathF.Min(CardDefaultRadiusDip, heightDip / 2f) * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: IslandMotion.CardInDampingRatio,
            periodMs:     IslandMotion.CardInPeriodMs,
            onCompleted);
    }

    /// <summary>Animate notch into Command Bar expansion.</summary>
    public void AnimateToCommand(float widthDip = 660f, float heightDip = 320f, Action? onCompleted = null)
    {
        if (_disposed) return;
        _isCommand = true;
        _isOpen = false;
        _isCard = false;

        float targetWidth  = widthDip * _sizeFactor;
        float targetHeight = heightDip * _sizeFactor;
        float targetRadius = OpenRadiusDip * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: IslandMotion.OpenDampingRatio,
            periodMs:     IslandMotion.OpenPeriodMs,
            onCompleted);
    }

    public void AnimateToFolded(Action? onCompleted = null)
    {
        if (_disposed) return;
        _isOpen = false;
        _isCard = false;
        _isCommand = false;

        float targetWidth  = CurrentFoldedWidthDip * _sizeFactor;
        float targetHeight = CurrentFoldedHeightDip * _sizeFactor;
        float targetRadius = GetEffectiveFoldedRadius() * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: IslandMotion.CloseDampingRatio,
            periodMs:     IslandMotion.ClosePeriodMs,
            onCompleted,
            isClosing: true);
    }

    /// <summary>
    /// Switch to a different tab size while already open.
    /// Uses a quick spring retarget — no jump.
    /// </summary>
    public void AnimateToTab(int tabIndex, Action? onCompleted = null)
    {
        if (_disposed || !_isOpen) return;

        var (tw, th) = IslandMotion.TabSize(tabIndex);
        _openTargetW = tw;
        _openTargetH = th;

        float targetWidth  = _openTargetW * _sizeFactor;
        float targetHeight = _openTargetH * _sizeFactor;
        float targetRadius = OpenRadiusDip * _sizeFactor;

        // Retarget spring from current in-flight position
        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: IslandMotion.OpenDampingRatio,
            periodMs:     IslandMotion.OpenPeriodMs,
            onCompleted);
    }

    public void UpdateWindowWidth(float windowWidthDip, WindowIslandSettings settings)
    {
        if (_disposed) return;
        float nextSizeFactor = (float)(settings.SizePercent / 100.0);
        float maskH = MathF.Max(OpenHeightDip + 40f, (IslandMotion.ChatHeightDip + 40f) * nextSizeFactor);
        bool widthChanged = Math.Abs(_windowWidthDip - windowWidthDip) > 0.5f;
        bool scaleChanged = Math.Abs(_sizeFactor - nextSizeFactor) > 0.001f;
        bool shapeChanged = !string.Equals(_lastShape, settings.Shape, StringComparison.Ordinal);

        _windowWidthDip = windowWidthDip;
        _sizeFactor = nextSizeFactor;

        if (widthChanged || scaleChanged || Math.Abs(_shapeVisual.Size.Y - maskH) > 0.5f)
        {
            _shapeVisual.Size = new Vector2(_windowWidthDip, maskH);
            _maskSurface.SourceSize = new Vector2(_windowWidthDip, maskH);
        }
        if (widthChanged || shapeChanged)
        {
            _lastShape = settings.Shape;
            RefreshCenterAnimation(settings.Shape);
        }
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        if (_disposed) return;
        _settings = settings;
        _sizeFactor = (float)(settings.SizePercent / 100.0);

        // Apply opacity linearly via _outputVisual.Opacity (10..100%) immediately when changed
        int clampedOpacityPct = Math.Clamp(settings.OpacityPercent, 10, 100);
        if (clampedOpacityPct != _lastOpacityPercent)
        {
            _lastOpacityPercent = clampedOpacityPct;
            float opacity = clampedOpacityPct / 100f;
            _outputVisual.StopAnimation("Opacity");
            _outputVisual.Opacity = opacity;
        }

        Color baseColor = WindowIslandColorHelper.ResolveBaseColor(settings);
        _bodyColorBrush.Color = Color.FromArgb(255, baseColor.R, baseColor.G, baseColor.B);

        bool shapeChanged = !string.Equals(_lastShape, settings.Shape, StringComparison.Ordinal);
        bool sizeChanged  = _lastSizePercent != settings.SizePercent;
        if (shapeChanged)
        {
            _lastShape = settings.Shape;
            RefreshCenterAnimation(settings.Shape);
        }

        if (shapeChanged || sizeChanged)
        {
            _lastSizePercent = settings.SizePercent;
            // Snap to current logical state (no animation for settings change)
            float foldedRadius = GetEffectiveFoldedRadius();
            float width  = (_isOpen ? _openTargetW : (_isCard ? _cardTargetW : CurrentFoldedWidthDip))  * _sizeFactor;
            float height = (_isOpen ? _openTargetH : (_isCard ? _cardTargetH : CurrentFoldedHeightDip)) * _sizeFactor;
            float radius = (_isOpen ? OpenRadiusDip : (_isCard ? MathF.Min(CardDefaultRadiusDip, _cardTargetH / 2f) : foldedRadius)) * _sizeFactor;
            _bodyGeometry.Size         = new Vector2(width, height);
            _bodyGeometry.CornerRadius = new Vector2(radius, radius);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _outputVisual.Dispose();
        _outputMaskBrush.Dispose();
        _bodyColorBrush.Dispose();
        _maskSurfaceBrush.Dispose();
        _maskSurface.Dispose();
        _notchLeftEarShape.Dispose();
        _notchRightEarShape.Dispose();
        _bodyClip.Dispose();
        _clipGeometry.Dispose();
        _bodyGeometry.Dispose();
        _bodyShape.Dispose();
        _shapeVisual.Dispose();
        _easeOut.Dispose();
        _rootVisual.Dispose();
    }

    // ── Internal helpers ───────────────────────────────────────────────────────

    private void AnimateShape(Vector2 targetSize, Vector2 targetRadius, float dampingRatio, int periodMs, Action? onCompleted, bool isClosing = false)
    {
        bool reducedMotion = _settings.ReducedMotion;
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            if (!uiSettings.AnimationsEnabled) reducedMotion = true;
        }
        catch { }

        int rawConfiguredMs = isClosing ? _settings.CloseAnimationDurationMs : _settings.AnimationDurationMs;
        int fallbackMs = isClosing ? IslandMotion.DefaultCloseAnimationDurationMs : IslandMotion.DefaultAnimationDurationMs;
        int configuredMs = rawConfiguredMs > 0
            ? Math.Clamp(rawConfiguredMs, IslandMotion.MinAnimationDurationMs, IslandMotion.MaxAnimationDurationMs)
            : fallbackMs;

        if (_settings.LiteMode)
        {
            // Fast linear keyframe animation (no springs or long tail)
            int liteMs = Math.Min(configuredMs, 120);
            var sizeAnim = _compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Duration = TimeSpan.FromMilliseconds(liteMs);
            sizeAnim.InsertKeyFrame(1.0f, targetSize);

            var radiusAnim = _compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.Duration = TimeSpan.FromMilliseconds(liteMs);
            radiusAnim.InsertKeyFrame(1.0f, targetRadius);

            RunBatchOrDirect(sizeAnim, radiusAnim, onCompleted);
        }
        else if (reducedMotion)
        {
            // 80 ms fast transition for reduced motion
            var sizeAnim = _compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Duration = TimeSpan.FromMilliseconds(IslandMotion.ReducedMotionFadeMs);
            sizeAnim.InsertKeyFrame(1.0f, targetSize, _easeOut);

            var radiusAnim = _compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.Duration = TimeSpan.FromMilliseconds(IslandMotion.ReducedMotionFadeMs);
            radiusAnim.InsertKeyFrame(1.0f, targetRadius, _easeOut);

            RunBatchOrDirect(sizeAnim, radiusAnim, onCompleted);
        }
        else
        {
            // Exact-duration ease-out keyframe animation: starts seamlessly from current live
            // size/radius on mid-flight retargets and settles cleanly at the configured duration
            // (eliminating the ~1.5-2s spring settle tail).
            int durationMs = periodMs < IslandMotion.OpenPeriodMs
                ? Math.Max(IslandMotion.MinAnimationDurationMs, (int)MathF.Round(configuredMs * 0.9f))
                : configuredMs;

            var sizeAnim = _compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            sizeAnim.InsertKeyFrame(1.0f, targetSize, _easeOut);

            var radiusAnim = _compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            radiusAnim.InsertKeyFrame(1.0f, targetRadius, _easeOut);

            RunBatchOrDirect(sizeAnim, radiusAnim, onCompleted);
        }
    }

    private void RunBatchOrDirect(CompositionAnimation sizeAnim, CompositionAnimation radiusAnim, Action? onCompleted)
    {
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

    private void RefreshCenterAnimation(string shape)
    {
        bool isCapsule = shape == "Capsule";
        float yOffset = isCapsule ? 6f : 0f;

        var centerAnim = _compositor.CreateExpressionAnimation("Vector2((windowWidth - geom.Size.X) / 2.0, yOffset)");
        centerAnim.SetReferenceParameter("geom", _bodyGeometry);
        centerAnim.SetScalarParameter("windowWidth", _windowWidthDip);
        centerAnim.SetScalarParameter("yOffset", yOffset);
        _bodyShape.StartAnimation("Offset", centerAnim);

        if (isCapsule)
        {
            _notchLeftEarShape.StopAnimation("Scale");
            _notchRightEarShape.StopAnimation("Scale");
            _notchLeftEarShape.Scale = Vector2.Zero;
            _notchRightEarShape.Scale = Vector2.Zero;
            return;
        }

        var earScaleAnim = _compositor.CreateExpressionAnimation("Vector2(geom.CornerRadius.X / earR, geom.CornerRadius.Y / earR)");
        earScaleAnim.SetReferenceParameter("geom", _bodyGeometry);
        earScaleAnim.SetScalarParameter("earR", NotchEarRadius);
        _notchLeftEarShape.StartAnimation("Scale", earScaleAnim);
        _notchRightEarShape.StartAnimation("Scale", earScaleAnim);

        var leftEarOffsetAnim = _compositor.CreateExpressionAnimation("Vector2((windowWidth - geom.Size.X) / 2.0 - geom.CornerRadius.X, 0.0)");
        leftEarOffsetAnim.SetReferenceParameter("geom", _bodyGeometry);
        leftEarOffsetAnim.SetScalarParameter("windowWidth", _windowWidthDip);
        _notchLeftEarShape.StartAnimation("Offset", leftEarOffsetAnim);

        var rightEarOffsetAnim = _compositor.CreateExpressionAnimation("Vector2((windowWidth + geom.Size.X) / 2.0 - geom.CornerRadius.X, 0.0)");
        rightEarOffsetAnim.SetReferenceParameter("geom", _bodyGeometry);
        rightEarOffsetAnim.SetScalarParameter("windowWidth", _windowWidthDip);
        _notchRightEarShape.StartAnimation("Offset", rightEarOffsetAnim);
    }

    private CompositionPathGeometry CreateNotchEarGeometry(bool isRight)
    {
        float r = NotchEarRadius;
        using var builder = new CanvasPathBuilder(null);
        // 1px inner overlap along the shared body-corner boundary eliminates the anti-aliasing hairline
        if (!isRight)
        {
            builder.BeginFigure(0f, 0f);
            builder.AddLine(2f * r + 1f, 0f);
            builder.AddLine(2f * r + 1f, 0.5f);
            builder.AddArc(new Vector2(r + 1f, r + 0.5f), r, r, 0f, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            builder.AddLine(r, r);
            builder.AddArc(new Vector2(0f, 0f), r, r, 0f, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            builder.EndFigure(CanvasFigureLoop.Closed);
        }
        else
        {
            builder.BeginFigure(-1f, 0f);
            builder.AddLine(2f * r, 0f);
            builder.AddArc(new Vector2(r, r), r, r, 0f, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            builder.AddLine(r - 1f, r + 0.5f);
            builder.AddArc(new Vector2(-1f, 0.5f), r, r, 0f, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            builder.EndFigure(CanvasFigureLoop.Closed);
        }
        using var canvasGeom = CanvasGeometry.CreatePath(builder);
        return _compositor.CreatePathGeometry(new CompositionPath(canvasGeom));
    }

    public static Color ResolveBaseColor(WindowIslandSettings settings)
        => WindowIslandColorHelper.ResolveBaseColor(settings);

    public static Color HexToColor(string hex)
        => WindowIslandColorHelper.HexToColor(hex);
}
