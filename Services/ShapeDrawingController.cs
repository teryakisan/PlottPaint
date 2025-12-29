using System;
using System.Collections.Generic;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NVSPlotter.Models;
using NVSPlotter.Properties;
using NVSPlotter.Util;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;

namespace NVSPlotter.Services;

public sealed class ShapeDrawingController
{
    private readonly Utility _util = new();

    private const double MIN_DIST = 0.25;
    private const int BEZIER_SEGMENTS = 48;
    private const double HANDLE_SIZE = 10.0;
    private static int CIRCLE_SEGMENTS => Settings.Default.circleSegments;

    private readonly Canvas _canvas;
    private readonly Action<LineStroke> _commitStroke;
    private readonly Action _requestRender;
    private readonly Func<Guid?> _getActivePaintWellId;

    // Current group ID for multi-stroke objects (rectangle, circle, polyline, bezier, etc.)
    // Null means the next stroke is an individual object (single line).
    private Guid? _currentGroupId;

    private bool _isDrawing;
    private bool _isPolylineActive;
    private ToolMode _activeTool = ToolMode.Line;
    private PointMm _start;
    private PointMm _polylineLast;
    private PointMm _polylineStart; // Track the starting point for auto-close detection
    private Line? _previewLine;
    private Shape? _previewShape;

    // Bezier state: three-click interaction
    // Click 1: P0 (start), Click 2: P3 (end), Click 3: control point (symmetric)
    private bool _isBezierActive;
    private int _bezierClickCount;
    private PointMm _bezierP0;  // Start point
    private PointMm _bezierP3;  // End point
    private PointMm _bezierControl; // User-controlled point that defines both P1 and P2
    private Path? _bezierPreviewPath;
    private Ellipse? _bezierHandleP0;
    private Ellipse? _bezierHandleP3;
    private Ellipse? _bezierHandleControl;
    private Line? _bezierControlLine1;
    private Line? _bezierControlLine2;

    // PolyBezier state: connected Bezier curve segments
    // Workflow:
    // Click 1: Set start point
    // Move ? Click 2: Set end point of segment, curve control becomes active
    // Drag ? Click 3: Confirm curve, commit segment, auto-start next from end point
    // Repeat until right-click/Enter to finish
    private bool _isPolyBezierActive;
    private enum PolyBezierPhase { PlacingStart, PlacingEnd, AdjustingCurve }
    private PolyBezierPhase _polyBezierPhase;
    private PointMm _polyBezierStart;     // Start point of entire poly-bezier
    private PointMm _polyBezierSegStart;  // Start of current segment (= end of previous)
    private PointMm _polyBezierSegEnd;    // End point of current segment
    private PointMm _polyBezierControl;   // Control point for current segment
    private Path? _polyBezierPreviewPath;
    private Path? _polyBezierCommittedPath; // Shows already-committed segments
    private Ellipse? _polyBezierHandleStart;
    private Ellipse? _polyBezierHandleEnd;
    private Ellipse? _polyBezierHandleControl;
    private Line? _polyBezierControlLine1;
    private Line? _polyBezierControlLine2;
    private Ellipse? _polyBezierCloseIndicator; // Visual indicator when mouse is near start point
    private readonly List<(PointMm P0, PointMm P1, PointMm P2, PointMm P3)> _polyBezierSegments = new();
    private const double POLYBEZIER_CLOSE_TOLERANCE = 10.0; // mm - distance threshold to show close indicator

    // FreeDraw state: continuous drawing while mouse is held
    // Collects points and fits smooth Bezier curves through them
    private bool _isFreeDrawing;
    private readonly List<PointMm> _freeDrawPoints = new(); // All captured points
    private Path? _freeDrawPreviewPath; // Smooth curve preview
    private const double FREEDRAW_PIXEL_THRESHOLD = 20.0; // Minimum pixels between captured points (zoom-independent)
    private const double FREEDRAW_SMOOTHING = 0.1; // Catmull-Rom tension (0 = sharp, 0.5 = smooth)
    private const double FREEDRAW_FLATNESS_TOLERANCE = 50.0; // Maximum deviation from curve in mm before adding a point (A0 scale)

    public bool IsDrawing => _isDrawing;
    public bool IsPolylineActive => _isPolylineActive;
    public bool IsBezierActive => _isBezierActive;
    public bool IsPolyBezierActive => _isPolyBezierActive;
    public bool IsFreeDrawing => _isFreeDrawing;

    /// <summary>
    /// Event raised when a multi-stroke shape (rectangle, circle, polyline, bezier, etc.) is completed.
    /// The parameter indicates how many strokes were added.
    /// </summary>
    public event Action<int>? ShapeCompleted;

    public ShapeDrawingController(Canvas canvas, Action<LineStroke> commitStroke, Action requestRender, Func<Guid?>? getActivePaintWellId = null)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _commitStroke = commitStroke ?? throw new ArgumentNullException(nameof(commitStroke));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        _getActivePaintWellId = getActivePaintWellId ?? (() => null);
    }

    public void BeginDraw(ToolMode tool, PointMm start)
    {
        if (tool is not (ToolMode.Line or ToolMode.Rectangle or ToolMode.Circle)) return;

        _activeTool = tool;
        _isDrawing = true;
        _start = start;

        switch (tool)
        {
            case ToolMode.Rectangle:
                BeginRectanglePreview(start);
                break;
            case ToolMode.Circle:
                BeginCirclePreview(start);
                break;
            default:
                BeginLinePreview(start);
                break;
        }

        _canvas.CaptureMouse();
    }

    public bool Update(PointMm current)
    {
        if (_isBezierActive)
        {
            UpdateBezierPreview(current);
            return true;
        }

        if (_isPolyBezierActive)
        {
            UpdatePolyBezierPreview(current);
            return true;
        }

        if (_isDrawing)
        {
            switch (_activeTool)
            {
                case ToolMode.Rectangle:
                    UpdateRectanglePreview(current);
                    break;
                case ToolMode.Circle:
                    UpdateCirclePreview(current);
                    break;
                default:
                    if (_previewLine != null)
                    {
                        _previewLine.X2 = current.X;
                        _previewLine.Y2 = current.Y;
                    }
                    break;
            }
            return true;
        }

        if (_isPolylineActive)
        {
            UpdatePolylinePreview(current);
            return true;
        }

        return false;
    }

    public void CompleteDraw(PointMm end)
    {
        if (!_isDrawing) return;

        int strokesAdded = 0;
        switch (_activeTool)
        {
            case ToolMode.Rectangle:
                _currentGroupId = Guid.NewGuid(); // All rectangle strokes share same group
                _strokeCountInGroup = 0;
                FinalizeRectangle(_start, end);
                strokesAdded = _strokeCountInGroup;
                _currentGroupId = null;
                break;
            case ToolMode.Circle:
                _currentGroupId = Guid.NewGuid(); // All circle strokes share same group
                _strokeCountInGroup = 0;
                FinalizeEllipse(_start, end);
                strokesAdded = _strokeCountInGroup;
                _currentGroupId = null;
                break;
            default:
                // Single line - no group (null GroupId)
                _currentGroupId = null;
                AddStroke(_start, end);
                strokesAdded = 1;
                break;
        }

        CancelPreview();
        _requestRender();

        // Notify that a shape was completed (for rectangle/circle auto-select)
        if (strokesAdded > 0 && _activeTool is ToolMode.Rectangle or ToolMode.Circle)
        {
            ShapeCompleted?.Invoke(strokesAdded);
        }
    }

    public void HandlePolylineClick(PointMm point, bool isDoubleClick)
    {
        if (!_isPolylineActive)
        {
            // First click: start the polyline
            _isPolylineActive = true;
            _activeTool = ToolMode.Polyline;
            _currentGroupId = Guid.NewGuid(); // All polyline strokes share same group
            _strokeCountInGroup = 0;
            _polylineLast = point;
            _polylineStart = point; // Remember the starting point for auto-close
            BeginLinePreview(point);
            _canvas.CaptureMouse();
            return;
        }

        // Check if clicking on the starting point to close the polyline
        // Use a slightly larger tolerance for easier closing
        const double CLOSE_TOLERANCE = 5.0; // mm
        if (Utility.Distance(_polylineStart, point) <= CLOSE_TOLERANCE && Utility.Distance(_polylineLast, _polylineStart) >= MIN_DIST)
        {
            // Close the polyline by connecting back to start - this is the last stroke
            CancelPreview();
            // Mark first stroke as start (handled when first added), mark this as end
            AddStroke(_polylineLast, _polylineStart, isGroupStart: _strokeCountInGroup == 0, isGroupEnd: true);
            _requestRender();
            FinishPolyline();
            return;
        }

        if (Utility.Distance(_polylineLast, point) >= MIN_DIST)
        {
            CancelPreview();
            // Mark first stroke as group start
            AddStroke(_polylineLast, point, isGroupStart: _strokeCountInGroup == 0);
            _polylineLast = point;
            _requestRender();
        }
        else
        {
            _polylineLast = point;
        }

        if (isDoubleClick)
        {
            // Mark the previous stroke as group end if we have strokes
            MarkLastStrokeAsGroupEnd();
            FinishPolyline();
        }
        else
        {
            BeginLinePreview(_polylineLast);
            _canvas.CaptureMouse();
        }
    }

    /// <summary>
    /// Marks the last stroke in the current group as the group end.
    /// Called when finishing a polyline/bezier/etc. without closing.
    /// </summary>
    private void MarkLastStrokeAsGroupEnd()
    {
        // This would need access to the document, but we don't have it here.
        // The marking should be done at AddStroke time by tracking when we're finishing.
        // For now, this is a no-op placeholder - the caller should ensure the last stroke
        // is added with isGroupEnd: true.
    }

    public bool TryFinishPolyline()
    {
        if (!_isPolylineActive) return false;
        FinishPolyline();
        return true;
    }

    public void FinishPolyline()
    {
        if (!_isPolylineActive) return;
        var strokesAdded = _strokeCountInGroup;
        _isPolylineActive = false;
        _currentGroupId = null; // Clear group ID
        _activeTool = ToolMode.Line;
        CancelPreview();

        // Notify that a polyline was completed
        if (strokesAdded > 0)
        {
            ShapeCompleted?.Invoke(strokesAdded);
        }
    }

    public void CancelAll()
    {
        _isDrawing = false;
        _isPolylineActive = false;
        _isBezierActive = false;
        _bezierClickCount = 0;
        _isPolyBezierActive = false;
        _polyBezierPhase = PolyBezierPhase.PlacingStart;
        _polyBezierSegments.Clear();
        _currentGroupId = null; // Clear group ID
        CancelPreview();
        CancelBezierPreview();
        CancelPolyBezierPreview();
        CancelFreeDraw();
    }

    // Counter to track strokes within current group for marker assignment
    private int _strokeCountInGroup;

    private void AddStroke(PointMm a, PointMm b, bool isGroupStart = false, bool isGroupEnd = false)
    {
        if (Utility.Distance(a, b) < MIN_DIST) return;
        var stroke = new LineStroke(a, b)
        {
            PaintWellId = _getActivePaintWellId(),
            GroupId = _currentGroupId,
            IsGroupStart = isGroupStart || _currentGroupId == null, // Individual strokes are their own start
            IsGroupEnd = isGroupEnd || _currentGroupId == null // Individual strokes are their own end
        };
        _commitStroke(stroke);
        _strokeCountInGroup++;
    }

  

    private void BeginLinePreview(PointMm start)
    {
        _previewLine = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = start.X,
            Y2 = start.Y,
            Stroke = Brushes.OrangeRed,
            StrokeThickness = 1.5,
            StrokeDashArray = [3, 2],
            SnapsToDevicePixels = true
        };
        _canvas.Children.Add(_previewLine);
        Panel.SetZIndex(_previewLine, 15);
    }

    private void BeginRectanglePreview(PointMm start)
    {
        var rect = new Rectangle
        {
            Stroke = Brushes.OrangeRed,
            StrokeThickness = 1.5,
            StrokeDashArray = [3, 2],
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true
        };

        _previewShape = rect;
        _canvas.Children.Add(rect);
        Panel.SetZIndex(rect, 15);
        Canvas.SetLeft(rect, start.X);
        Canvas.SetTop(rect, start.Y);
        rect.Width = 0;
        rect.Height = 0;
    }

    private void BeginCirclePreview(PointMm start)
    {
        var ellipse = new Ellipse
        {
            Stroke = Brushes.OrangeRed,
            StrokeThickness = 1.5,
            StrokeDashArray = [3, 2],
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true
        };

        _previewShape = ellipse;
        _canvas.Children.Add(ellipse);
        Panel.SetZIndex(ellipse, 15);
        Canvas.SetLeft(ellipse, start.X);
        Canvas.SetTop(ellipse, start.Y);
        ellipse.Width = 0;
        ellipse.Height = 0;
    }

    private void UpdateRectanglePreview(PointMm current)
    {
        if (_previewShape is not Rectangle rect) return;

        var left = Math.Min(_start.X, current.X);
        var top = Math.Min(_start.Y, current.Y);
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);

        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        rect.Width = width;
        rect.Height = height;
    }

    private void UpdateCirclePreview(PointMm current)
    {
        if (_previewShape is not Ellipse ellipse) return;

        var left = Math.Min(_start.X, current.X);
        var top = Math.Min(_start.Y, current.Y);
        var width = Math.Abs(current.X - _start.X);
        var height = Math.Abs(current.Y - _start.Y);

        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        ellipse.Width = width;
        ellipse.Height = height;
    }

    private void UpdatePolylinePreview(PointMm current)
    {
        if (_previewLine == null)
        {
            BeginLinePreview(_polylineLast);
        }

        _previewLine!.X1 = _polylineLast.X;
        _previewLine.Y1 = _polylineLast.Y;
        _previewLine.X2 = current.X;
        _previewLine.Y2 = current.Y;
    }

    private void FinalizeRectangle(PointMm start, PointMm end)
    {
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);

        if ((right - left) < MIN_DIST || (bottom - top) < MIN_DIST) return;

        var topLeft = new PointMm(left, top);
        var topRight = new PointMm(right, top);
        var bottomRight = new PointMm(right, bottom);
        var bottomLeft = new PointMm(left, bottom);

        _strokeCountInGroup = 0;
        AddStroke(topLeft, topRight, isGroupStart: true);
        AddStroke(topRight, bottomRight);
        AddStroke(bottomRight, bottomLeft);
        AddStroke(bottomLeft, topLeft, isGroupEnd: true);
    }

    private void FinalizeEllipse(PointMm start, PointMm end)
    {
        var left = Math.Min(start.X, end.X);
        var right = Math.Max(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var bottom = Math.Max(start.Y, end.Y);

        if ((right - left) < MIN_DIST || (bottom - top) < MIN_DIST) return;

        var centerX = (left + right) / 2.0;
        var centerY = (top + bottom) / 2.0;
        var radiusX = Math.Max((right - left) / 2.0, 0.1);
        var radiusY = Math.Max((bottom - top) / 2.0, 0.1);

        PointMm? firstPoint = null;
        PointMm? prevPoint = null;

        _strokeCountInGroup = 0;
        for (int i = 0; i < CIRCLE_SEGMENTS; i++)
        {
            var angle = 2.0 * Math.PI * i / CIRCLE_SEGMENTS;
            var x = centerX + radiusX * Math.Cos(angle);
            var y = centerY + radiusY * Math.Sin(angle);
            var point = new PointMm(x, y);

            if (prevPoint is PointMm prev)
            {
                // Mark first segment as group start
                AddStroke(prev, point, isGroupStart: _strokeCountInGroup == 0);
            }
            else
            {
                firstPoint = point;
            }

            prevPoint = point;
        }

        if (prevPoint is PointMm last && firstPoint is PointMm first)
        {
            // Mark last segment as group end
            AddStroke(last, first, isGroupEnd: true);
        }
    }

    private void CancelPreview()
    {
        _isDrawing = false;

        if (_previewLine != null)
        {
            _canvas.Children.Remove(_previewLine);
            _previewLine = null;
        }

        if (_previewShape != null)
        {
            _canvas.Children.Remove(_previewShape);
            _previewShape = null;
        }

        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

    // ===== BEZIER TOOL =====

    /// <summary>
    /// Handles click interactions for the Bezier tool.
    /// Click 1: Set start point (P0)
    /// Click 2: Set end point (P3)
    /// Click 3: Confirm control point and finalize curve
    /// </summary>
    public void HandleBezierClick(PointMm point)
    {
        if (!_isBezierActive)
        {
            // First click: set start point
            _isBezierActive = true;
            _activeTool = ToolMode.Bezier;
            _currentGroupId = Guid.NewGuid(); // All bezier strokes share same group
            _bezierClickCount = 1;
            _bezierP0 = point;
            _bezierP3 = point;
            _bezierControl = point;
            BeginBezierPreview();
            _canvas.CaptureMouse();
            return;
        }

        _bezierClickCount++;

        if (_bezierClickCount == 2)
        {
            // Second click: set end point
            _bezierP3 = point;
            // Control point defaults to midpoint, will be adjusted by mouse move
            _bezierControl = new PointMm(
                (_bezierP0.X + _bezierP3.X) / 2.0,
                (_bezierP0.Y + _bezierP3.Y) / 2.0
            );
            UpdateBezierPreview(_bezierControl);
        }
        else if (_bezierClickCount >= 3)
        {
            // Third click: finalize
            FinalizeBezier();
        }
    }

    public bool TryFinishBezier()
    {
        if (!_isBezierActive) return false;
        if (_bezierClickCount >= 2)
        {
            FinalizeBezier();
        }
        else
        {
            CancelBezier();
        }
        return true;
    }

    public void CancelBezier()
    {
        _isBezierActive = false;
        _bezierClickCount = 0;
        _currentGroupId = null; // Clear group ID
        _activeTool = ToolMode.Line;
        CancelBezierPreview();
    }

    private void BeginBezierPreview()
    {
        // Create the curve path
        _bezierPreviewPath = new Path
        {
            Stroke = Brushes.OrangeRed,
            StrokeThickness = 1.5,
            StrokeDashArray = [3, 2],
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        _canvas.Children.Add(_bezierPreviewPath);
        Panel.SetZIndex(_bezierPreviewPath, 15);

        // Create handle for P0 (start)
        _bezierHandleP0 = CreateBezierHandle(Brushes.LimeGreen);
        _canvas.Children.Add(_bezierHandleP0);
        Panel.SetZIndex(_bezierHandleP0, 16);
        PositionHandle(_bezierHandleP0, _bezierP0);
    }

    private void UpdateBezierPreview(PointMm current)
    {
        if (_bezierPreviewPath == null) return;

        if (_bezierClickCount == 1)
        {
            // Before second click: show line from P0 to cursor (P3 candidate)
            _bezierP3 = current;
            _bezierControl = new PointMm(
                (_bezierP0.X + current.X) / 2.0,
                (_bezierP0.Y + current.Y) / 2.0
            );
        }
        else if (_bezierClickCount >= 2)
        {
            // After second click: mouse controls the control point
            _bezierControl = current;
        }

        // Compute P1 and P2 from single control point (quadratic-like behavior via symmetric cubic)
        var (p1, p2) = ComputeControlPoints(_bezierP0, _bezierP3, _bezierControl);

        // Update path geometry
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(_bezierP0.X, _bezierP0.Y),
            IsClosed = false
        };
        figure.Segments.Add(new BezierSegment(
            new Point(p1.X, p1.Y),
            new Point(p2.X, p2.Y),
            new Point(_bezierP3.X, _bezierP3.Y),
            true));
        geometry.Figures.Add(figure);
        _bezierPreviewPath.Data = geometry;

        // Update/create handles
        if (_bezierClickCount >= 1)
        {
            PositionHandle(_bezierHandleP0, _bezierP0);

            // Show P3 handle after first click
            if (_bezierHandleP3 == null)
            {
                _bezierHandleP3 = CreateBezierHandle(Brushes.OrangeRed);
                _canvas.Children.Add(_bezierHandleP3);
                Panel.SetZIndex(_bezierHandleP3, 16);
            }
            PositionHandle(_bezierHandleP3, _bezierP3);
        }

        if (_bezierClickCount >= 2)
        {
            // Show control point handle and guide lines
            if (_bezierHandleControl == null)
            {
                _bezierHandleControl = CreateBezierHandle(Brushes.DodgerBlue);
                _canvas.Children.Add(_bezierHandleControl);
                Panel.SetZIndex(_bezierHandleControl, 16);
            }
            PositionHandle(_bezierHandleControl, _bezierControl);

            // Control lines from endpoints to control point
            if (_bezierControlLine1 == null)
            {
                _bezierControlLine1 = CreateControlLine();
                _canvas.Children.Add(_bezierControlLine1);
                Panel.SetZIndex(_bezierControlLine1, 14);
            }
            _bezierControlLine1.X1 = _bezierP0.X;
            _bezierControlLine1.Y1 = _bezierP0.Y;
            _bezierControlLine1.X2 = _bezierControl.X;
            _bezierControlLine1.Y2 = _bezierControl.Y;

            if (_bezierControlLine2 == null)
            {
                _bezierControlLine2 = CreateControlLine();
                _canvas.Children.Add(_bezierControlLine2);
                Panel.SetZIndex(_bezierControlLine2, 14);
            }
            _bezierControlLine2.X1 = _bezierP3.X;
            _bezierControlLine2.Y1 = _bezierP3.Y;
            _bezierControlLine2.X2 = _bezierControl.X;
            _bezierControlLine2.Y2 = _bezierControl.Y;
        }
    }

    private static (PointMm P1, PointMm P2) ComputeControlPoints(PointMm p0, PointMm p3, PointMm control)
    {
        // For a symmetric curve, we compute P1 and P2 such that the curve
        // passes through (or near) the control point at t=0.5
        // Using quadratic-to-cubic conversion: P1 = P0 + 2/3*(C - P0), P2 = P3 + 2/3*(C - P3)
        // where C is the quadratic control point
        var p1 = new PointMm(
            p0.X + (2.0 / 3.0) * (control.X - p0.X),
            p0.Y + (2.0 / 3.0) * (control.Y - p0.Y)
        );
        var p2 = new PointMm(
            p3.X + (2.0 / 3.0) * (control.X - p3.X),
            p3.Y + (2.0 / 3.0) * (control.Y - p3.Y)
        );
        return (p1, p2);
    }

    private static Ellipse CreateBezierHandle(Brush fill)
    {
        return new Ellipse
        {
            Width = HANDLE_SIZE,
            Height = HANDLE_SIZE,
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
    }

    private static Line CreateControlLine()
    {
        return new Line
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [2, 2],
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
    }

    private static void PositionHandle(Ellipse? handle, PointMm point)
    {
        if (handle == null) return;
        Canvas.SetLeft(handle, point.X - HANDLE_SIZE / 2.0);
        Canvas.SetTop(handle, point.Y - HANDLE_SIZE / 2.0);
    }

    private void FinalizeBezier()
    {
        if (!_isBezierActive) return;

        var (p1, p2) = ComputeControlPoints(_bezierP0, _bezierP3, _bezierControl);

        // Tessellate the cubic Bezier into line segments
        var samples = SampleCubicBezier(_bezierP0, p1, p2, _bezierP3, BEZIER_SEGMENTS).ToList();

        _strokeCountInGroup = 0;
        for (int i = 0; i < samples.Count - 1; i++)
        {
            var isFirst = i == 0;
            var isLast = i == samples.Count - 2;
            AddStroke(samples[i], samples[i + 1], isGroupStart: isFirst, isGroupEnd: isLast);
        }

        var strokesAdded = _strokeCountInGroup;

        CancelBezierPreview();
        _isBezierActive = false;
        _bezierClickCount = 0;
        _currentGroupId = null; // Clear group ID
        _activeTool = ToolMode.Line;
        _requestRender();

        // Notify that a bezier was completed
        if (strokesAdded > 0)
        {
            ShapeCompleted?.Invoke(strokesAdded);
        }
    }

    private static IEnumerable<PointMm> SampleCubicBezier(PointMm p0, PointMm p1, PointMm p2, PointMm p3, int segments)
    {
        for (int i = 0; i <= segments; i++)
        {
            double t = (double)i / segments;
            double u = 1.0 - t;

            // Cubic Bezier formula: B(t) = (1-t)³P0 + 3(1-t)²tP1 + 3(1-t)t²P2 + t³P3
            double x = u * u * u * p0.X
                     + 3 * u * u * t * p1.X
                     + 3 * u * t * t * p2.X
                     + t * t * t * p3.X;

            double y = u * u * u * p0.Y
                     + 3 * u * u * t * p1.Y
                     + 3 * u * t * t * p2.Y
                     + t * t * t * p3.Y;

            yield return new PointMm(x, y);
        }
    }

    private void CancelBezierPreview()
        {
            if (_bezierPreviewPath != null)
            {
                _canvas.Children.Remove(_bezierPreviewPath);
                _bezierPreviewPath = null;
            }

            if (_bezierHandleP0 != null)
            {
                _canvas.Children.Remove(_bezierHandleP0);
                _bezierHandleP0 = null;
            }

            if (_bezierHandleP3 != null)
            {
                _canvas.Children.Remove(_bezierHandleP3);
                _bezierHandleP3 = null;
            }

            if (_bezierHandleControl != null)
            {
                _canvas.Children.Remove(_bezierHandleControl);
                _bezierHandleControl = null;
            }

            if (_bezierControlLine1 != null)
            {
                _canvas.Children.Remove(_bezierControlLine1);
                _bezierControlLine1 = null;
            }

            if (_bezierControlLine2 != null)
            {
                _canvas.Children.Remove(_bezierControlLine2);
                _bezierControlLine2 = null;
            }

            if (_canvas.IsMouseCaptured)
            {
                _canvas.ReleaseMouseCapture();
            }
        }

    // ===== POLY-BEZIER TOOL =====

    /// <summary>
    /// Handles click interactions for the PolyBezier tool.
    /// Workflow:
    /// Click 1: Set start point
    /// Move ? Click 2: Set end point of segment, curve control becomes active
    /// Drag ? Click 3: Confirm curve, commit segment, auto-start next from end point
    /// Repeat until right-click/Enter to finish
    /// </summary>
    public void HandlePolyBezierClick(PointMm point)
    {
        if (!_isPolyBezierActive)
        {
            // First click: set start point
            _isPolyBezierActive = true;
            _activeTool = ToolMode.PolyBezier;
            _currentGroupId = Guid.NewGuid(); // All poly-bezier strokes share same group
            _polyBezierPhase = PolyBezierPhase.PlacingEnd; // Next click will place end
            _polyBezierStart = point;
            _polyBezierSegStart = point;
            _polyBezierSegEnd = point;
            _polyBezierControl = point;
            _polyBezierSegments.Clear();
            BeginPolyBezierPreview();
            _canvas.CaptureMouse();
            return;
        }

        switch (_polyBezierPhase)
        {
            case PolyBezierPhase.PlacingEnd:
                // Click to set end point of segment - now switch to curve adjustment
                _polyBezierSegEnd = point;
                // Control point defaults to midpoint
                _polyBezierControl = new PointMm(
                    (_polyBezierSegStart.X + _polyBezierSegEnd.X) / 2.0,
                    (_polyBezierSegStart.Y + _polyBezierSegEnd.Y) / 2.0
                );
                _polyBezierPhase = PolyBezierPhase.AdjustingCurve;
                UpdatePolyBezierPreview(_polyBezierControl);
                break;

            case PolyBezierPhase.AdjustingCurve:
                // Click to confirm curve and commit segment
                CommitCurrentPolyBezierSegment();
                
                // Auto-start next segment from end of previous
                _polyBezierSegStart = _polyBezierSegEnd;
                _polyBezierSegEnd = point; // Mouse position becomes new end candidate
                _polyBezierControl = new PointMm(
                    (_polyBezierSegStart.X + _polyBezierSegEnd.X) / 2.0,
                    (_polyBezierSegStart.Y + _polyBezierSegEnd.Y) / 2.0
                );
                
                // Go back to placing end point phase
                _polyBezierPhase = PolyBezierPhase.PlacingEnd;
                
                // Hide control handles until next end point is placed
                HidePolyBezierControlHandles();
                UpdatePolyBezierPreview(point);
                break;
        }
    }

    /// <summary>
    /// Tries to finish the poly-bezier. Returns true if there was an active poly-bezier.
    /// </summary>
    public bool TryFinishPolyBezier()
    {
        if (!_isPolyBezierActive) return false;
        
        // If we're in curve adjustment phase, commit the current segment first
        if (_polyBezierPhase == PolyBezierPhase.AdjustingCurve)
        {
            CommitCurrentPolyBezierSegment();
        }
        
        FinalizePolyBezier();
        return true;
    }

    /// <summary>
    /// Cancels the poly-bezier without committing.
    /// </summary>
    public void CancelPolyBezier()
    {
        _isPolyBezierActive = false;
        _polyBezierPhase = PolyBezierPhase.PlacingStart;
        _polyBezierSegments.Clear();
        _currentGroupId = null; // Clear group ID
        _activeTool = ToolMode.Line;
        CancelPolyBezierPreview();
    }

    private void CommitCurrentPolyBezierSegment()
    {
        var (p1, p2) = ComputeControlPoints(_polyBezierSegStart, _polyBezierSegEnd, _polyBezierControl);
        _polyBezierSegments.Add((_polyBezierSegStart, p1, p2, _polyBezierSegEnd));
        UpdateCommittedPolyBezierPath();
    }

    private void BeginPolyBezierPreview()
    {
        // Create the current segment preview path
        _polyBezierPreviewPath = new Path
        {
            Stroke = Brushes.OrangeRed,
            StrokeThickness = 1.5,
            StrokeDashArray = [3, 2],
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        _canvas.Children.Add(_polyBezierPreviewPath);
        Panel.SetZIndex(_polyBezierPreviewPath, 15);

        // Create the committed segments path (solid, not dashed)
        _polyBezierCommittedPath = new Path
        {
            Stroke = Brushes.DarkOrange,
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        _canvas.Children.Add(_polyBezierCommittedPath);
        Panel.SetZIndex(_polyBezierCommittedPath, 14);

        // Create handle for start point
        _polyBezierHandleStart = CreateBezierHandle(Brushes.LimeGreen);
        _canvas.Children.Add(_polyBezierHandleStart);
        Panel.SetZIndex(_polyBezierHandleStart, 16);
        PositionHandle(_polyBezierHandleStart, _polyBezierSegStart);
    }

    private void UpdatePolyBezierPreview(PointMm current)
    {
        if (_polyBezierPreviewPath == null) return;

        switch (_polyBezierPhase)
        {
            case PolyBezierPhase.PlacingEnd:
                // Mouse is positioning the end point - show straight line preview
                _polyBezierSegEnd = current;
                _polyBezierControl = new PointMm(
                    (_polyBezierSegStart.X + current.X) / 2.0,
                    (_polyBezierSegStart.Y + current.Y) / 2.0
                );
                break;

            case PolyBezierPhase.AdjustingCurve:
                // Mouse is controlling the curve - update control point
                _polyBezierControl = current;
                break;
        }

        // Compute control points for current segment
        var (p1, p2) = ComputeControlPoints(_polyBezierSegStart, _polyBezierSegEnd, _polyBezierControl);

        // Update current segment preview
        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(_polyBezierSegStart.X, _polyBezierSegStart.Y),
            IsClosed = false
        };
        figure.Segments.Add(new BezierSegment(
            new Point(p1.X, p1.Y),
            new Point(p2.X, p2.Y),
            new Point(_polyBezierSegEnd.X, _polyBezierSegEnd.Y),
            true));
        geometry.Figures.Add(figure);
        _polyBezierPreviewPath.Data = geometry;

        // Update segment start handle position
        PositionHandle(_polyBezierHandleStart, _polyBezierSegStart);

        // Update/create end handle
        if (_polyBezierHandleEnd == null)
        {
            _polyBezierHandleEnd = CreateBezierHandle(Brushes.OrangeRed);
            _canvas.Children.Add(_polyBezierHandleEnd);
            Panel.SetZIndex(_polyBezierHandleEnd, 16);
        }
        PositionHandle(_polyBezierHandleEnd, _polyBezierSegEnd);

        // Show control point handle and guide lines only when adjusting curve
        if (_polyBezierPhase == PolyBezierPhase.AdjustingCurve)
        {
            if (_polyBezierHandleControl == null)
            {
                _polyBezierHandleControl = CreateBezierHandle(Brushes.DodgerBlue);
                _canvas.Children.Add(_polyBezierHandleControl);
                Panel.SetZIndex(_polyBezierHandleControl, 16);
            }
            PositionHandle(_polyBezierHandleControl, _polyBezierControl);

            // Control lines
            if (_polyBezierControlLine1 == null)
            {
                _polyBezierControlLine1 = CreateControlLine();
                _canvas.Children.Add(_polyBezierControlLine1);
                Panel.SetZIndex(_polyBezierControlLine1, 14);
            }
            _polyBezierControlLine1.X1 = _polyBezierSegStart.X;
            _polyBezierControlLine1.Y1 = _polyBezierSegStart.Y;
            _polyBezierControlLine1.X2 = _polyBezierControl.X;
            _polyBezierControlLine1.Y2 = _polyBezierControl.Y;

            if (_polyBezierControlLine2 == null)
            {
                _polyBezierControlLine2 = CreateControlLine();
                _canvas.Children.Add(_polyBezierControlLine2);
                Panel.SetZIndex(_polyBezierControlLine2, 14);
            }
            _polyBezierControlLine2.X1 = _polyBezierSegEnd.X;
            _polyBezierControlLine2.Y1 = _polyBezierSegEnd.Y;
            _polyBezierControlLine2.X2 = _polyBezierControl.X;
            _polyBezierControlLine2.Y2 = _polyBezierControl.Y;
        }

        // Show close indicator when mouse is near the starting point (only after at least one segment)
        UpdatePolyBezierCloseIndicator(current);
    }

    private void HidePolyBezierControlHandles()
    {
        if (_polyBezierHandleControl != null)
        {
            _canvas.Children.Remove(_polyBezierHandleControl);
            _polyBezierHandleControl = null;
        }
        if (_polyBezierControlLine1 != null)
        {
            _canvas.Children.Remove(_polyBezierControlLine1);
            _polyBezierControlLine1 = null;
        }
        if (_polyBezierControlLine2 != null)
        {
            _canvas.Children.Remove(_polyBezierControlLine2);
            _polyBezierControlLine2 = null;
        }
    }

    /// <summary>
    /// Shows or hides the close indicator based on mouse proximity to the starting point.
    /// Only shows after at least one segment has been committed.
    /// </summary>
    private void UpdatePolyBezierCloseIndicator(PointMm current)
    {
        // Only show close indicator if we have at least one committed segment
        // (need something to close back to)
        if (_polyBezierSegments.Count == 0)
        {
            HidePolyBezierCloseIndicator();
            return;
        }

        var distanceToStart = Utility.Distance(current, _polyBezierStart);
        var isNearStart = distanceToStart <= POLYBEZIER_CLOSE_TOLERANCE;

        if (isNearStart)
        {
            // Show close indicator
            if (_polyBezierCloseIndicator == null)
            {
                _polyBezierCloseIndicator = new Ellipse
                {
                    Width = POLYBEZIER_CLOSE_TOLERANCE * 2,
                    Height = POLYBEZIER_CLOSE_TOLERANCE * 2,
                    Stroke = Brushes.LimeGreen,
                    StrokeThickness = 2,
                    StrokeDashArray = [3, 2],
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 255, 0)), // Semi-transparent green
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                _canvas.Children.Add(_polyBezierCloseIndicator);
                Panel.SetZIndex(_polyBezierCloseIndicator, 17); // Above other handles
            }

            // Position the indicator centered on the start point
            Canvas.SetLeft(_polyBezierCloseIndicator, _polyBezierStart.X - POLYBEZIER_CLOSE_TOLERANCE);
            Canvas.SetTop(_polyBezierCloseIndicator, _polyBezierStart.Y - POLYBEZIER_CLOSE_TOLERANCE);
        }
        else
        {
            HidePolyBezierCloseIndicator();
        }
    }

    private void HidePolyBezierCloseIndicator()
    {
        if (_polyBezierCloseIndicator != null)
        {
            _canvas.Children.Remove(_polyBezierCloseIndicator);
            _polyBezierCloseIndicator = null;
        }
    }

    private void UpdateCommittedPolyBezierPath()
    {
        if (_polyBezierCommittedPath == null || _polyBezierSegments.Count == 0) return;

        var geometry = new PathGeometry();
        
        // Start from the first segment
        var firstSeg = _polyBezierSegments[0];
        var figure = new PathFigure
        {
            StartPoint = new Point(firstSeg.P0.X, firstSeg.P0.Y),
            IsClosed = false
        };

        foreach (var seg in _polyBezierSegments)
        {
            figure.Segments.Add(new BezierSegment(
                new Point(seg.P1.X, seg.P1.Y),
                new Point(seg.P2.X, seg.P2.Y),
                new Point(seg.P3.X, seg.P3.Y),
                true));
        }

        geometry.Figures.Add(figure);
        _polyBezierCommittedPath.Data = geometry;
    }

    private void FinalizePolyBezier()
    {
        if (!_isPolyBezierActive) return;

        // Collect all strokes from all segments first to count total
        var allStrokes = new List<(PointMm A, PointMm B)>();
        
        // Tessellate all committed segments into line strokes
        foreach (var seg in _polyBezierSegments)
        {
            var samples = SampleCubicBezier(seg.P0, seg.P1, seg.P2, seg.P3, BEZIER_SEGMENTS).ToList();
            for (int i = 0; i < samples.Count - 1; i++)
            {
                allStrokes.Add((samples[i], samples[i + 1]));
            }
        }

        // Add all strokes with proper markers
        _strokeCountInGroup = 0;
        for (int i = 0; i < allStrokes.Count; i++)
        {
            var isFirst = i == 0;
            var isLast = i == allStrokes.Count - 1;
            AddStroke(allStrokes[i].A, allStrokes[i].B, isGroupStart: isFirst, isGroupEnd: isLast);
        }

        var strokesAdded = _strokeCountInGroup;

        CancelPolyBezierPreview();
        _isPolyBezierActive = false;
        _polyBezierPhase = PolyBezierPhase.PlacingStart;
        _polyBezierSegments.Clear();
        _currentGroupId = null; // Clear group ID
        _activeTool = ToolMode.Line;
        _requestRender();

        // Notify that a poly-bezier was completed
        if (strokesAdded > 0)
        {
            ShapeCompleted?.Invoke(strokesAdded);
        }
    }

    private void CancelPolyBezierPreview()
    {
        if (_polyBezierPreviewPath != null)
        {
            _canvas.Children.Remove(_polyBezierPreviewPath);
            _polyBezierPreviewPath = null;
        }

        if (_polyBezierCommittedPath != null)
        {
            _canvas.Children.Remove(_polyBezierCommittedPath);
            _polyBezierCommittedPath = null;
        }

        if (_polyBezierHandleStart != null)
        {
            _canvas.Children.Remove(_polyBezierHandleStart);
            _polyBezierHandleStart = null;
        }

        if (_polyBezierHandleEnd != null)
        {
            _canvas.Children.Remove(_polyBezierHandleEnd);
            _polyBezierHandleEnd = null;
        }

        HidePolyBezierControlHandles();
        HidePolyBezierCloseIndicator();

        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

        // ===== FREEDRAW TOOL =====

        /// <summary>
        /// Begins freehand drawing mode. Called on mouse down.
        /// </summary>
        public void BeginFreeDraw(PointMm start)
        {
            _isFreeDrawing = true;
            _activeTool = ToolMode.FreeDraw;
            _currentGroupId = Guid.NewGuid(); // All freedraw strokes share same group
            _freeDrawPoints.Clear();
            _freeDrawPoints.Add(start);

            // Create preview path
            _freeDrawPreviewPath = new Path
            {
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_freeDrawPreviewPath);
            Panel.SetZIndex(_freeDrawPreviewPath, 15);

            _canvas.CaptureMouse();
        }

        /// <summary>
        /// Updates freehand drawing as the mouse moves.
        /// Captures points and updates the smooth curve preview.
        /// The zoom parameter allows pixel-based distance calculation.
        /// </summary>
        public bool UpdateFreeDraw(PointMm current, double zoom = 1.0)
        {
            if (!_isFreeDrawing) return false;
            if (_freeDrawPoints.Count == 0) return true;

            var last = _freeDrawPoints[^1];

            // Calculate distance in screen pixels (zoom-independent threshold)
            var distMm = Utility.Distance(last, current);
            var distPixels = distMm * zoom;

            if (distPixels >= FREEDRAW_PIXEL_THRESHOLD)
            {
                _freeDrawPoints.Add(current);
                UpdateFreeDrawPreview();
            }

            return true;
        }

        /// <summary>
        /// Updates the smooth curve preview from captured points.
        /// </summary>
        private void UpdateFreeDrawPreview()
        {
            if (_freeDrawPreviewPath == null || _freeDrawPoints.Count < 2) return;

            var geometry = new PathGeometry();
            var figure = new PathFigure
            {
                StartPoint = new Point(_freeDrawPoints[0].X, _freeDrawPoints[0].Y),
                IsClosed = false
            };

            if (_freeDrawPoints.Count == 2)
            {
                // Just two points - draw a line
                figure.Segments.Add(new LineSegment(
                    new Point(_freeDrawPoints[1].X, _freeDrawPoints[1].Y), true));
            }
            else
            {
                // Multiple points - use Catmull-Rom spline converted to Bezier
                var bezierSegments = CatmullRomToBezier(_freeDrawPoints, FREEDRAW_SMOOTHING);
                foreach (var seg in bezierSegments)
                {
                    figure.Segments.Add(new BezierSegment(
                        new Point(seg.P1.X, seg.P1.Y),
                        new Point(seg.P2.X, seg.P2.Y),
                        new Point(seg.P3.X, seg.P3.Y),
                        true));
                }
            }

            geometry.Figures.Add(figure);
            _freeDrawPreviewPath.Data = geometry;
        }

            /// <summary>
            /// Completes freehand drawing. Called on mouse up.
            /// Fits smooth Bezier curves through captured points and converts to minimal line segments
            /// using adaptive flattening (fewer segments where curve is nearly straight).
            /// </summary>
            public void EndFreeDraw()
            {
                if (!_isFreeDrawing) return;

                _isFreeDrawing = false;

                // Remove preview path
                if (_freeDrawPreviewPath != null)
                {
                    _canvas.Children.Remove(_freeDrawPreviewPath);
                    _freeDrawPreviewPath = null;
                }

                if (_canvas.IsMouseCaptured)
                {
                    _canvas.ReleaseMouseCapture();
                }

                // Need at least 2 points to create strokes
                if (_freeDrawPoints.Count < 2)
                {
                    _freeDrawPoints.Clear();
                    return;
                }

                _strokeCountInGroup = 0;

                // Convert points to smooth Bezier curves
                if (_freeDrawPoints.Count == 2)
                {
                    // Just two points - single line (both start and end)
                    AddStroke(_freeDrawPoints[0], _freeDrawPoints[1], isGroupStart: true, isGroupEnd: true);
                }
                else
                {
                    // Multiple points - fit smooth curves and flatten adaptively
                    var bezierSegments = CatmullRomToBezier(_freeDrawPoints, FREEDRAW_SMOOTHING);
            
                    // Flatten all Bezier curves into a minimal set of line segments
                    var flattenedPath = new List<PointMm>();
            
                    foreach (var seg in bezierSegments)
                    {
                        // Add start point only for first segment
                        if (flattenedPath.Count == 0)
                        {
                            flattenedPath.Add(seg.P0);
                        }
                
                        // Adaptively flatten this Bezier curve
                        FlattenBezierAdaptive(seg.P0, seg.P1, seg.P2, seg.P3, flattenedPath, FREEDRAW_FLATNESS_TOLERANCE);
                    }
            
                    // Create connected strokes from the flattened path with proper markers
                    for (int i = 0; i < flattenedPath.Count - 1; i++)
                    {
                        var isFirst = i == 0;
                        var isLast = i == flattenedPath.Count - 2;
                        AddStroke(flattenedPath[i], flattenedPath[i + 1], isGroupStart: isFirst, isGroupEnd: isLast);
                    }
                }

                var strokesAdded = _strokeCountInGroup;
                _freeDrawPoints.Clear();
                _currentGroupId = null; // Clear group ID
                _requestRender();

                // NOTE: FreeDraw does NOT trigger ShapeCompleted event
                // to allow continuous free-flowing drawing without interruption
            }

            /// <summary>
            /// Adaptively flattens a cubic Bezier curve into line segments.
            /// Only subdivides where the curve deviates from a straight line by more than the tolerance.
            /// This produces far fewer line segments than uniform sampling.
            /// </summary>
            private static void FlattenBezierAdaptive(PointMm p0, PointMm p1, PointMm p2, PointMm p3, List<PointMm> output, double tolerance)
            {
                // Calculate how far the control points deviate from the line P0->P3
                // If the deviation is small enough, just add the endpoint
                var deviation = MaxBezierDeviation(p0, p1, p2, p3);
        
                if (deviation <= tolerance)
                {
                    // Curve is flat enough - just add the endpoint
                    output.Add(p3);
                }
                else
                {
                    // Curve needs subdivision - use de Casteljau's algorithm to split at t=0.5
                    // First half control points
                    var p01 = Midpoint(p0, p1);
                    var p12 = Midpoint(p1, p2);
                    var p23 = Midpoint(p2, p3);
                    var p012 = Midpoint(p01, p12);
                    var p123 = Midpoint(p12, p23);
                    var p0123 = Midpoint(p012, p123); // This is the point on the curve at t=0.5
            
                    // Recursively flatten each half
                    FlattenBezierAdaptive(p0, p01, p012, p0123, output, tolerance);
                    FlattenBezierAdaptive(p0123, p123, p23, p3, output, tolerance);
                }
            }

            /// <summary>
            /// Calculates the maximum deviation of a Bezier curve's control points from the baseline.
            /// Uses the convex hull property: the curve lies within the convex hull of its control points,
            /// so the maximum deviation is bounded by how far the control points are from the baseline.
            /// </summary>
            private static double MaxBezierDeviation(PointMm p0, PointMm p1, PointMm p2, PointMm p3)
            {
                // Calculate distance from control points to the line P0->P3
                var d1 = PointToLineDistance(p1, p0, p3);
                var d2 = PointToLineDistance(p2, p0, p3);
                return Math.Max(d1, d2);
            }

            /// <summary>
            /// Calculates the perpendicular distance from a point to a line defined by two points.
            /// </summary>
            private static double PointToLineDistance(PointMm point, PointMm lineStart, PointMm lineEnd)
            {
                var dx = lineEnd.X - lineStart.X;
                var dy = lineEnd.Y - lineStart.Y;
                var lengthSq = dx * dx + dy * dy;
        
                if (lengthSq < 0.0001) // Line is essentially a point
                {
                    return Utility.Distance(point, lineStart);
                }
        
                // Calculate perpendicular distance using cross product formula
                var cross = Math.Abs((point.X - lineStart.X) * dy - (point.Y - lineStart.Y) * dx);
                return cross / Math.Sqrt(lengthSq);
            }

            /// <summary>
            /// Returns the midpoint between two points.
            /// </summary>
            private static PointMm Midpoint(PointMm a, PointMm b)
            {
                return new PointMm((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            }

            /// <summary>
                /// Cancels freehand drawing without completing.
                /// </summary>
                public void CancelFreeDraw()
                {
                    if (!_isFreeDrawing) return;

                    _isFreeDrawing = false;
                    _freeDrawPoints.Clear();
                    _currentGroupId = null; // Clear group ID

                    // Remove preview path
                    if (_freeDrawPreviewPath != null)
                    {
                        _canvas.Children.Remove(_freeDrawPreviewPath);
                        _freeDrawPreviewPath = null;
                    }

                    if (_canvas.IsMouseCaptured)
                    {
                        _canvas.ReleaseMouseCapture();
                    }
                }

                /// <summary>
                /// Represents a cubic Bezier segment with start point P0, control points P1 and P2, and end point P3.
                /// </summary>
                private readonly record struct BezierCurveSegment(PointMm P0, PointMm P1, PointMm P2, PointMm P3);

                /// <summary>
                /// Converts a list of points to smooth cubic Bezier curves using Catmull-Rom spline algorithm.
                /// The tension parameter controls smoothness (0 = sharp corners, 0.5 = very smooth).
                /// </summary>
                private static List<BezierCurveSegment> CatmullRomToBezier(List<PointMm> points, double tension)
                {
                    var result = new List<BezierCurveSegment>();
                    if (points.Count < 2) return result;

                    // For Catmull-Rom, we need 4 points per segment: P(i-1), P(i), P(i+1), P(i+2)
                    // We extend the endpoints by reflection to handle the boundary cases
                    var extended = new List<PointMm>(points.Count + 2);

                    // Reflect first point
                    var firstReflected = new PointMm(
                        2 * points[0].X - points[1].X,
                        2 * points[0].Y - points[1].Y);
                    extended.Add(firstReflected);

                    // Add all original points
                    extended.AddRange(points);

                    // Reflect last point
                    var lastReflected = new PointMm(
                        2 * points[^1].X - points[^2].X,
                        2 * points[^1].Y - points[^2].Y);
                    extended.Add(lastReflected);

                    // Convert each segment from Catmull-Rom to cubic Bezier
                    double alpha = tension;

                    for (int i = 1; i < extended.Count - 2; i++)
                    {
                        var p0 = extended[i - 1];
                        var p1 = extended[i];
                        var p2 = extended[i + 1];
                        var p3 = extended[i + 2];

                        // Calculate Bezier control points from Catmull-Rom points
                        var c1 = new PointMm(
                            p1.X + (p2.X - p0.X) * alpha / 3.0,
                            p1.Y + (p2.Y - p0.Y) * alpha / 3.0);

                        var c2 = new PointMm(
                            p2.X - (p3.X - p1.X) * alpha / 3.0,
                            p2.Y - (p3.Y - p1.Y) * alpha / 3.0);

                        result.Add(new BezierCurveSegment(p1, c1, c2, p2));
                    }

                    return result;
                }
            }