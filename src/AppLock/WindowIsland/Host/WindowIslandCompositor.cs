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
    private readonly CompositionRoundedRectangleGeometry _bodyGeometry;
    private readonly CompositionSpriteShape _bodyShape;
    private readonly CompositionSpriteShape _notchLeftEarShape;
    private readonly CompositionSpriteShape _notchRightEarShape;
    private WindowIslandSettings _settings;

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

    public void SetFoldedWidth(float widthDip, bool animate = true)
    {
        if (_disposed) return;
        float clamped = Math.Clamp(widthDip, FoldedWidthDip, 560f);
        if (Math.Abs(clamped - CurrentFoldedWidthDip) < 1f) return;
        CurrentFoldedWidthDip = clamped;

        if (!_isOpen && !_isCard && !_isCommand)
        {
            float targetWidth = CurrentFoldedWidthDip * _sizeFactor;
            float targetHeight = FoldedHeightDip * _sizeFactor;
            float baseRadius = _settings.Shape == "Capsule" ? FoldedRadiusDip : FoldedNotchRadiusDip;
            float targetRadius = baseRadius * _sizeFactor;
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

        // Body shape
        _bodyShape = _compositor.CreateSpriteShape(_bodyGeometry);
        var bodyBrush = GetBodyBrush(settings);
        _bodyShape.FillBrush = bodyBrush;

        // Apple-style concave top ears + corner fillers (non-overlapping with _bodyGeometry)
        _notchLeftEarShape = _compositor.CreateSpriteShape(CreateNotchEarGeometry(isRight: false));
        _notchLeftEarShape.FillBrush = bodyBrush;

        _notchRightEarShape = _compositor.CreateSpriteShape(CreateNotchEarGeometry(isRight: true));
        _notchRightEarShape.FillBrush = bodyBrush;

        // Expression animation: keep the notch centered horizontally
        RefreshCenterAnimation(settings.Shape);

        _shapeVisual = _compositor.CreateShapeVisual();
        _shapeVisual.RelativeSizeAdjustment = Vector2.One;
        _shapeVisual.Shapes.Add(_bodyShape);
        _shapeVisual.Shapes.Add(_notchLeftEarShape);
        _shapeVisual.Shapes.Add(_notchRightEarShape);

        _rootVisual.Children.InsertAtTop(_shapeVisual);
        ElementCompositionPreview.SetElementChildVisual(hostElement, _rootVisual);

        ApplySettings(settings);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

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
        float targetHeight = FoldedHeightDip * _sizeFactor;
        float baseRadius   = _settings.Shape == "Capsule" ? FoldedRadiusDip : FoldedNotchRadiusDip;
        float targetRadius = baseRadius * _sizeFactor;

        AnimateShape(
            new Vector2(targetWidth, targetHeight),
            new Vector2(targetRadius, targetRadius),
            dampingRatio: IslandMotion.CloseDampingRatio,
            periodMs:     IslandMotion.ClosePeriodMs,
            onCompleted);
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
        _windowWidthDip = windowWidthDip;
        _sizeFactor = (float)(settings.SizePercent / 100.0);
        RefreshCenterAnimation(settings.Shape);
    }

    public void ApplySettings(WindowIslandSettings settings)
    {
        if (_disposed) return;
        _settings = settings;
        _sizeFactor = (float)(settings.SizePercent / 100.0);

        // Animate opacity via Composition
        float opacity = settings.OpacityPercent / 100f;
        var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(1f, opacity);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(200);
        _shapeVisual.StartAnimation("Opacity", opacityAnim);

        var bodyBrush = GetBodyBrush(settings);
        _bodyShape.FillBrush = bodyBrush;
        _notchLeftEarShape.FillBrush = bodyBrush;
        _notchRightEarShape.FillBrush = bodyBrush;

        RefreshCenterAnimation(settings.Shape);

        // Snap to current logical state (no animation for settings change)
        float foldedRadius = settings.Shape == "Capsule" ? FoldedRadiusDip : FoldedNotchRadiusDip;
        float width  = (_isOpen ? _openTargetW : (_isCard ? _cardTargetW : CurrentFoldedWidthDip))  * _sizeFactor;
        float height = (_isOpen ? _openTargetH : (_isCard ? _cardTargetH : FoldedHeightDip)) * _sizeFactor;
        float radius = (_isOpen ? OpenRadiusDip : (_isCard ? MathF.Min(CardDefaultRadiusDip, _cardTargetH / 2f) : foldedRadius)) * _sizeFactor;
        _bodyGeometry.Size         = new Vector2(width, height);
        _bodyGeometry.CornerRadius = new Vector2(radius, radius);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notchLeftEarShape.Dispose();
        _notchRightEarShape.Dispose();
        _bodyGeometry.Dispose();
        _bodyShape.Dispose();
        _shapeVisual.Dispose();
        _rootVisual.Dispose();
    }

    // ── Internal helpers ───────────────────────────────────────────────────────

    private void AnimateShape(Vector2 targetSize, Vector2 targetRadius, float dampingRatio, int periodMs, Action? onCompleted)
    {
        bool reducedMotion = _settings.ReducedMotion;
        try
        {
            var uiSettings = new Windows.UI.ViewManagement.UISettings();
            if (!uiSettings.AnimationsEnabled) reducedMotion = true;
        }
        catch { }

        if (_settings.LiteMode)
        {
            // Simple 120 ms linear fade / keyframe animation (no springs)
            var sizeAnim = _compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Duration = TimeSpan.FromMilliseconds(120);
            sizeAnim.InsertKeyFrame(1.0f, targetSize);

            var radiusAnim = _compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.Duration = TimeSpan.FromMilliseconds(120);
            radiusAnim.InsertKeyFrame(1.0f, targetRadius);

            RunBatchOrDirect(sizeAnim, radiusAnim, onCompleted);
        }
        else if (reducedMotion)
        {
            // 80 ms fade / fast transition for reduced motion
            var sizeAnim = _compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.Duration = TimeSpan.FromMilliseconds(80);
            sizeAnim.InsertKeyFrame(1.0f, targetSize);

            var radiusAnim = _compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.Duration = TimeSpan.FromMilliseconds(80);
            radiusAnim.InsertKeyFrame(1.0f, targetRadius);

            RunBatchOrDirect(sizeAnim, radiusAnim, onCompleted);
        }
        else
        {
            // Using SpringVector2NaturalMotionAnimation retargets from current in-flight position.
            // This is the key to interruptible animations — just update FinalValue.
            var sizeAnim = _compositor.CreateSpringVector2Animation();
            sizeAnim.DampingRatio = dampingRatio;
            sizeAnim.Period       = TimeSpan.FromMilliseconds(periodMs);
            sizeAnim.FinalValue   = targetSize;

            var radiusAnim = _compositor.CreateSpringVector2Animation();
            radiusAnim.DampingRatio = dampingRatio;
            radiusAnim.Period       = TimeSpan.FromMilliseconds(periodMs);
            radiusAnim.FinalValue   = targetRadius;

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

    private CompositionBrush GetBodyBrush(WindowIslandSettings settings)
    {
        Color baseColor = WindowIslandColorHelper.ResolveBaseColor(settings);
        byte alpha = settings.LiteMode
            ? (byte)255
            : (byte)Math.Clamp((int)(255 * (settings.OpacityPercent / 100.0)), 0, 255);

        return _compositor.CreateColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
    }

    public static Color ResolveBaseColor(WindowIslandSettings settings)
        => WindowIslandColorHelper.ResolveBaseColor(settings);

    public static Color HexToColor(string hex)
        => WindowIslandColorHelper.HexToColor(hex);
}
