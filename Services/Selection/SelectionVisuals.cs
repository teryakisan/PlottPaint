using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FontAwesome.WPF;
using NVSPlotter.Models;

// Avoid ambiguity with System.Drawing types
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;
using Cursors = System.Windows.Input.Cursors;

namespace NVSPlotter.Services.Selection;

/// <summary>
/// Manages all WPF visual elements for selection (handles, bounds, marquee, hover effects).
/// Separates rendering concerns from selection logic.
/// </summary>
public sealed class SelectionVisuals
{
    // Visual sizing constants
    private const double HANDLE_SIZE = 12.0;
    private const double ROTATE_HANDLE_SIZE = 18.0;
    private const double ROTATE_HANDLE_OFFSET = 45.0;
    private const double SELECTION_PADDING = 20.0;
    private const double MAX_ICON_SIZE = 60.0;
    private const double MIN_ICON_SIZE = 20.0;
    private const double ICON_SIZE_RATIO = 0.5;
    private const double ROTATE_GLOW_SIZE = 70.0;
    private const double RESIZE_GLOW_SIZE = 55.0;

    private readonly Canvas _canvas;

    // Selection box visuals
    private Path? _boundsPath;
    private readonly List<Rectangle> _handles = new();
    private Ellipse? _rotateHandle;
    private Line? _rotateConnector;

    // Marquee visual
    private Rectangle? _marqueeRect;

    // Hover effect visuals
    private Ellipse? _rotateHandleHoverRing;
    private ImageAwesome? _rotateIcon;
    private Border? _resizeHandleHoverRing;
    private ImageAwesome? _resizeIcon;

    // Position tracking for hit testing
    private readonly List<Point> _resizeHandlePositions = new();
    private Point _rotateHandlePosition;
    private Point _selectionCenterPosition;

    /// <summary>
    /// Gets the current rotation handle position for hit testing.
    /// </summary>
    public Point RotateHandlePosition => _rotateHandlePosition;

    /// <summary>
    /// Gets the current selection center position for icon placement.
    /// </summary>
    public Point SelectionCenterPosition => _selectionCenterPosition;

    /// <summary>
    /// Gets the positions of all resize handles for hit testing.
    /// </summary>
    public IReadOnlyList<Point> ResizeHandlePositions => _resizeHandlePositions;

    /// <summary>
    /// Gets the selection padding value (needed by controller for bounds calculations).
    /// </summary>
    public static double Padding => SELECTION_PADDING;

    /// <summary>
    /// Gets the rotate handle offset value (needed by controller for hit testing).
    /// </summary>
    public static double RotateHandleOffsetValue => ROTATE_HANDLE_OFFSET;

    public SelectionVisuals(Canvas canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
    }

    /// <summary>
    /// Renders the selection box with handles around the given bounds.
    /// </summary>
    public void RenderSelectionBox(Rect bounds, double rotationAngle, SelectionMode mode)
    {
        if (bounds.IsEmpty) return;

        // Calculate visual bounds with padding
        var visualLeft = bounds.Left - SELECTION_PADDING;
        var visualTop = bounds.Top - SELECTION_PADDING;
        var visualWidth = bounds.Width + (SELECTION_PADDING * 2);
        var visualHeight = bounds.Height + (SELECTION_PADDING * 2);
        var visualRight = visualLeft + visualWidth;
        var visualBottom = visualTop + visualHeight;

        // Calculate center for rotation
        var centerX = visualLeft + visualWidth / 2;
        var centerY = visualTop + visualHeight / 2;

        // Create rotation transform if we have a rotation angle
        RotateTransform? rotateTransform = null;
        if (Math.Abs(rotationAngle) > 0.001)
        {
            rotateTransform = new RotateTransform(rotationAngle * 180 / Math.PI, centerX, centerY);
        }

        // Calculate rotated corner positions
        var topLeft = TransformPoint(visualLeft, visualTop, rotateTransform);
        var topRight = TransformPoint(visualRight, visualTop, rotateTransform);
        var bottomRight = TransformPoint(visualRight, visualBottom, rotateTransform);
        var bottomLeft = TransformPoint(visualLeft, visualBottom, rotateTransform);

        // Bounding box as a Path connecting the rotated corners
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = topLeft,
            IsClosed = true
        };
        figure.Segments.Add(new LineSegment(topRight, true));
        figure.Segments.Add(new LineSegment(bottomRight, true));
        figure.Segments.Add(new LineSegment(bottomLeft, true));
        geometry.Figures.Add(figure);

        _boundsPath = new Path
        {
            Data = geometry,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [4, 2],
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_boundsPath, 18);
        _canvas.Children.Add(_boundsPath);

        // Resize handles at padded corners (with rotation applied)
        AddHandle(topLeft.X, topLeft.Y, SelectionHandle.TopLeft);
        AddHandle((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2, SelectionHandle.TopCenter);
        AddHandle(topRight.X, topRight.Y, SelectionHandle.TopRight);
        AddHandle((topLeft.X + bottomLeft.X) / 2, (topLeft.Y + bottomLeft.Y) / 2, SelectionHandle.MiddleLeft);
        AddHandle((topRight.X + bottomRight.X) / 2, (topRight.Y + bottomRight.Y) / 2, SelectionHandle.MiddleRight);
        AddHandle(bottomLeft.X, bottomLeft.Y, SelectionHandle.BottomLeft);
        AddHandle((bottomLeft.X + bottomRight.X) / 2, (bottomLeft.Y + bottomRight.Y) / 2, SelectionHandle.BottomCenter);
        AddHandle(bottomRight.X, bottomRight.Y, SelectionHandle.BottomRight);

        // Rotation handle - position relative to the rotated top edge
        var topCenter = new Point((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2);

        // Calculate the outward direction from the rotated top edge
        var topEdgeDx = topRight.X - topLeft.X;
        var topEdgeDy = topRight.Y - topLeft.Y;
        var topEdgeLength = Math.Sqrt(topEdgeDx * topEdgeDx + topEdgeDy * topEdgeDy);

        // Perpendicular direction pointing OUTWARD from the box
        double perpX = topEdgeDy / topEdgeLength;
        double perpY = -topEdgeDx / topEdgeLength;

        // Rotation handle position - OUTSIDE the box
        var rotateHandleX = topCenter.X + perpX * ROTATE_HANDLE_OFFSET;
        var rotateHandleY = topCenter.Y + perpY * ROTATE_HANDLE_OFFSET;

        _rotateConnector = new Line
        {
            X1 = topCenter.X,
            Y1 = topCenter.Y,
            X2 = rotateHandleX,
            Y2 = rotateHandleY,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [2, 2],
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Panel.SetZIndex(_rotateConnector, 18);
        _canvas.Children.Add(_rotateConnector);

        _rotateHandle = new Ellipse
        {
            Width = ROTATE_HANDLE_SIZE,
            Height = ROTATE_HANDLE_SIZE,
            Fill = Brushes.White,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 2,
            Cursor = Cursors.Hand,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_rotateHandle, rotateHandleX - ROTATE_HANDLE_SIZE / 2);
        Canvas.SetTop(_rotateHandle, rotateHandleY - ROTATE_HANDLE_SIZE / 2);
        Panel.SetZIndex(_rotateHandle, 19);
        _canvas.Children.Add(_rotateHandle);

        // Store positions for hover detection and icon placement
        _rotateHandlePosition = new Point(rotateHandleX, rotateHandleY);
        _selectionCenterPosition = new Point(centerX, centerY);

        // Show mode-specific icons during active operations
        if (mode == SelectionMode.Rotating && _rotateIcon == null)
        {
            ShowRotateIcon(bounds, isActive: true);
        }

        if (mode == SelectionMode.Resizing && _resizeIcon == null)
        {
            ShowResizeIcon(bounds, isActive: true);
        }
    }

    /// <summary>
    /// Shows the marquee selection rectangle at the start position.
    /// </summary>
    public void ShowMarquee(PointMm start)
    {
        _marqueeRect = new Rectangle
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [4, 2],
            Fill = new SolidColorBrush(Color.FromArgb(32, 30, 144, 255)),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_marqueeRect, start.X);
        Canvas.SetTop(_marqueeRect, start.Y);
        _marqueeRect.Width = 0;
        _marqueeRect.Height = 0;
        Panel.SetZIndex(_marqueeRect, 20);
        _canvas.Children.Add(_marqueeRect);
    }

    /// <summary>
    /// Updates the marquee rectangle to span from start to current position.
    /// </summary>
    public void UpdateMarquee(PointMm start, PointMm current)
    {
        if (_marqueeRect == null) return;

        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(_marqueeRect, left);
        Canvas.SetTop(_marqueeRect, top);
        _marqueeRect.Width = width;
        _marqueeRect.Height = height;
    }

    /// <summary>
    /// Removes the marquee selection rectangle.
    /// </summary>
    public void RemoveMarquee()
    {
        if (_marqueeRect != null)
        {
            _canvas.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        }
    }

    /// <summary>
    /// Shows the rotation hover glow effect at the rotation handle.
    /// </summary>
    public void ShowRotateHoverGlow()
    {
        if (_rotateHandleHoverRing != null) return;

        var glowBrush = CreateLimeGreenGlowBrush();

        _rotateHandleHoverRing = new Ellipse
        {
            Width = ROTATE_GLOW_SIZE,
            Height = ROTATE_GLOW_SIZE,
            Fill = glowBrush,
            Stroke = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_rotateHandleHoverRing, _rotateHandlePosition.X - ROTATE_GLOW_SIZE / 2);
        Canvas.SetTop(_rotateHandleHoverRing, _rotateHandlePosition.Y - ROTATE_GLOW_SIZE / 2);
        Panel.SetZIndex(_rotateHandleHoverRing, 18);
        _canvas.Children.Add(_rotateHandleHoverRing);
    }

    /// <summary>
    /// Shows the rotation icon at the selection center.
    /// </summary>
    public void ShowRotateIcon(Rect selectionBounds, bool isActive)
    {
        if (_rotateIcon != null) return;

        var iconSize = CalculateIconSize(selectionBounds);
        var alpha = isActive ? (byte)80 : (byte)140;

        _rotateIcon = new ImageAwesome
        {
            Icon = FontAwesomeIcon.Refresh,
            Width = iconSize,
            Height = iconSize,
            Foreground = new SolidColorBrush(Color.FromArgb(alpha, 30, 144, 255)),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_rotateIcon, _selectionCenterPosition.X - iconSize / 2);
        Canvas.SetTop(_rotateIcon, _selectionCenterPosition.Y - iconSize / 2);
        Panel.SetZIndex(_rotateIcon, 3);
        _canvas.Children.Add(_rotateIcon);
    }

    /// <summary>
    /// Clears the rotation hover visuals (glow and icon).
    /// </summary>
    public void ClearRotateHoverVisuals()
    {
        if (_rotateHandleHoverRing != null)
        {
            _canvas.Children.Remove(_rotateHandleHoverRing);
            _rotateHandleHoverRing = null;
        }

        if (_rotateIcon != null)
        {
            _canvas.Children.Remove(_rotateIcon);
            _rotateIcon = null;
        }
    }

    /// <summary>
    /// Shows the resize hover glow effect at the specified handle position.
    /// </summary>
    public void ShowResizeHoverGlow(int handleIndex)
    {
        if (_resizeHandleHoverRing != null || handleIndex < 0 || handleIndex >= _resizeHandlePositions.Count) return;

        var handlePos = _resizeHandlePositions[handleIndex];
        var glowBrush = CreateLimeGreenGlowBrush();

        var glowEllipse = new Ellipse
        {
            Width = RESIZE_GLOW_SIZE,
            Height = RESIZE_GLOW_SIZE,
            Fill = glowBrush,
            Stroke = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };

        _resizeHandleHoverRing = new Border
        {
            Width = RESIZE_GLOW_SIZE,
            Height = RESIZE_GLOW_SIZE,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            IsHitTestVisible = false,
            Child = glowEllipse
        };
        Canvas.SetLeft(_resizeHandleHoverRing, handlePos.X - RESIZE_GLOW_SIZE / 2);
        Canvas.SetTop(_resizeHandleHoverRing, handlePos.Y - RESIZE_GLOW_SIZE / 2);
        Panel.SetZIndex(_resizeHandleHoverRing, 18);
        _canvas.Children.Add(_resizeHandleHoverRing);
    }

    /// <summary>
    /// Shows the resize icon at the selection center.
    /// </summary>
    public void ShowResizeIcon(Rect selectionBounds, bool isActive)
    {
        if (_resizeIcon != null) return;

        var iconSize = CalculateIconSize(selectionBounds);
        var alpha = isActive ? (byte)80 : (byte)140;

        _resizeIcon = new ImageAwesome
        {
            Icon = FontAwesomeIcon.ArrowsAlt,
            Width = iconSize,
            Height = iconSize,
            Foreground = new SolidColorBrush(Color.FromArgb(alpha, 30, 144, 255)),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_resizeIcon, _selectionCenterPosition.X - iconSize / 2);
        Canvas.SetTop(_resizeIcon, _selectionCenterPosition.Y - iconSize / 2);
        Panel.SetZIndex(_resizeIcon, 3);
        _canvas.Children.Add(_resizeIcon);
    }

    /// <summary>
    /// Clears the resize hover visuals (glow and icon).
    /// </summary>
    public void ClearResizeHoverVisuals()
    {
        if (_resizeHandleHoverRing != null)
        {
            _canvas.Children.Remove(_resizeHandleHoverRing);
            _resizeHandleHoverRing = null;
        }

        if (_resizeIcon != null)
        {
            _canvas.Children.Remove(_resizeIcon);
            _resizeIcon = null;
        }
    }

    /// <summary>
    /// Sets the rotate icon transparency for active rotation state.
    /// </summary>
    public void SetRotateIconActive(bool isActive)
    {
        if (_rotateIcon != null)
        {
            var alpha = isActive ? (byte)80 : (byte)140;
            _rotateIcon.Foreground = new SolidColorBrush(Color.FromArgb(alpha, 30, 144, 255));
        }
    }

    /// <summary>
    /// Sets the resize icon transparency for active resize state.
    /// </summary>
    public void SetResizeIconActive(bool isActive)
    {
        if (_resizeIcon != null)
        {
            var alpha = isActive ? (byte)80 : (byte)140;
            _resizeIcon.Foreground = new SolidColorBrush(Color.FromArgb(alpha, 30, 144, 255));
        }
    }

    /// <summary>
    /// Clears all selection visuals from the canvas.
    /// </summary>
    public void Clear()
    {
        if (_boundsPath != null)
        {
            _canvas.Children.Remove(_boundsPath);
            _boundsPath = null;
        }

        foreach (var h in _handles)
        {
            _canvas.Children.Remove(h);
        }
        _handles.Clear();
        _resizeHandlePositions.Clear();

        if (_rotateHandle != null)
        {
            _canvas.Children.Remove(_rotateHandle);
            _rotateHandle = null;
        }

        if (_rotateConnector != null)
        {
            _canvas.Children.Remove(_rotateConnector);
            _rotateConnector = null;
        }

        ClearRotateHoverVisuals();
        ClearResizeHoverVisuals();
    }

    /// <summary>
    /// Checks if the rotation handle exists (for hover detection).
    /// </summary>
    public bool HasRotateHandle => _rotateHandle != null;

    // ===== PRIVATE HELPERS =====

    private void AddHandle(double x, double y, SelectionHandle handleType)
    {
        var handle = new Rectangle
        {
            Width = HANDLE_SIZE,
            Height = HANDLE_SIZE,
            Fill = Brushes.White,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            Tag = handleType
        };
        Canvas.SetLeft(handle, x - HANDLE_SIZE / 2);
        Canvas.SetTop(handle, y - HANDLE_SIZE / 2);
        Panel.SetZIndex(handle, 19);
        _canvas.Children.Add(handle);
        _handles.Add(handle);

        // Store position for hover detection
        _resizeHandlePositions.Add(new Point(x, y));
    }

    private static Point TransformPoint(double x, double y, RotateTransform? transform)
    {
        var point = new Point(x, y);
        return transform?.Transform(point) ?? point;
    }

    private static RadialGradientBrush CreateLimeGreenGlowBrush()
    {
        var glowBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        // LimeGreen (50,205,50) - same as line start indicators
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 50, 205, 50), 0.0));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 50, 205, 50), 0.15));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(160, 50, 205, 50), 0.3));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, 50, 205, 50), 0.5));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(40, 50, 205, 50), 0.7));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(10, 50, 205, 50), 0.85));
        glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 50, 205, 50), 1.0));
        return glowBrush;
    }

    private double CalculateIconSize(Rect bounds)
    {
        if (bounds.IsEmpty) return MAX_ICON_SIZE;

        var smallerDimension = Math.Min(bounds.Width, bounds.Height);
        var visualSize = smallerDimension + (SELECTION_PADDING * 2);
        var iconSize = visualSize * ICON_SIZE_RATIO;

        return Math.Clamp(iconSize, MIN_ICON_SIZE, MAX_ICON_SIZE);
    }
}
