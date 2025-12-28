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
    private const double HANDLE_SIZE = 8.0;
    private static int CIRCLE_SEGMENTS => Settings.Default.circleSegments;

    private readonly Canvas _canvas;
    private readonly Action<LineStroke> _commitStroke;
    private readonly Action _requestRender;
    private readonly Func<Guid?> _getActivePaintWellId;

    private bool _isDrawing;
    private bool _isPolylineActive;
    private ToolMode _activeTool = ToolMode.Line;
    private PointMm _start;
    private PointMm _polylineLast;
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

    public bool IsDrawing => _isDrawing;
    public bool IsPolylineActive => _isPolylineActive;
    public bool IsBezierActive => _isBezierActive;

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

        switch (_activeTool)
        {
            case ToolMode.Rectangle:
                FinalizeRectangle(_start, end);
                break;
            case ToolMode.Circle:
                FinalizeEllipse(_start, end);
                break;
            default:
                AddStroke(_start, end);
                break;
        }

        CancelPreview();
        _requestRender();
    }

    public void HandlePolylineClick(PointMm point, bool isDoubleClick)
    {
        if (!_isPolylineActive)
        {
            _isPolylineActive = true;
            _activeTool = ToolMode.Polyline;
            _polylineLast = point;
            BeginLinePreview(point);
            _canvas.CaptureMouse();
            return;
        }

        if (Utility.Distance(_polylineLast, point) >= MIN_DIST)
        {
            CancelPreview();
            AddStroke(_polylineLast, point);
            _polylineLast = point;
            _requestRender();
        }
        else
        {
            _polylineLast = point;
        }

        if (isDoubleClick)
        {
            FinishPolyline();
        }
        else
        {
            BeginLinePreview(_polylineLast);
            _canvas.CaptureMouse();
        }
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
        _isPolylineActive = false;
        _activeTool = ToolMode.Line;
        CancelPreview();
    }

    public void CancelAll()
    {
        _isDrawing = false;
        _isPolylineActive = false;
        _isBezierActive = false;
        _bezierClickCount = 0;
        CancelPreview();
        CancelBezierPreview();
    }

    private void AddStroke(PointMm a, PointMm b)
    {
        if (Utility.Distance(a, b) < MIN_DIST) return;
        var stroke = new LineStroke(a, b)
        {
            PaintWellId = _getActivePaintWellId()
        };
        _commitStroke(stroke);
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

        AddStroke(topLeft, topRight);
        AddStroke(topRight, bottomRight);
        AddStroke(bottomRight, bottomLeft);
        AddStroke(bottomLeft, topLeft);
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

        for (int i = 0; i < CIRCLE_SEGMENTS; i++)
        {
            var angle = 2.0 * Math.PI * i / CIRCLE_SEGMENTS;
            var x = centerX + radiusX * Math.Cos(angle);
            var y = centerY + radiusY * Math.Sin(angle);
            var point = new PointMm(x, y);

            if (prevPoint is PointMm prev)
            {
                AddStroke(prev, point);
            }
            else
            {
                firstPoint = point;
            }

            prevPoint = point;
        }

        if (prevPoint is PointMm last && firstPoint is PointMm first)
        {
            AddStroke(last, first);
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
        var samples = SampleCubicBezier(_bezierP0, p1, p2, _bezierP3, BEZIER_SEGMENTS);

        PointMm? prev = null;
        foreach (var pt in samples)
        {
            if (prev is PointMm p)
            {
                AddStroke(p, pt);
            }
            prev = pt;
        }

        CancelBezierPreview();
        _isBezierActive = false;
        _bezierClickCount = 0;
        _activeTool = ToolMode.Line;
        _requestRender();
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
}