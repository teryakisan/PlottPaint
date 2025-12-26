using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NVSPlotter.Models;

namespace NVSPlotter.Services;

public sealed class ShapeDrawingController
{
    private const double MIN_DIST = 0.25;
    private const int CIRCLE_SEGMENTS = 64;

    private readonly Canvas _canvas;
    private readonly Action<LineStroke> _commitStroke;
    private readonly Action _requestRender;

    private bool _isDrawing;
    private bool _isPolylineActive;
    private ToolMode _activeTool = ToolMode.Line;
    private PointMm _start;
    private PointMm _polylineLast;
    private Line? _previewLine;
    private Shape? _previewShape;

    public bool IsDrawing => _isDrawing;
    public bool IsPolylineActive => _isPolylineActive;

    public ShapeDrawingController(Canvas canvas, Action<LineStroke> commitStroke, Action requestRender)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _commitStroke = commitStroke ?? throw new ArgumentNullException(nameof(commitStroke));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
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

        if (Distance(_polylineLast, point) >= MIN_DIST)
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
        CancelPreview();
    }

    private void AddStroke(PointMm a, PointMm b)
    {
        if (Distance(a, b) < MIN_DIST) return;
        _commitStroke(new LineStroke(a, b));
    }

    private static double Distance(PointMm a, PointMm b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
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
            StrokeDashArray = new DoubleCollection { 3, 2 },
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
            StrokeDashArray = new DoubleCollection { 3, 2 },
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
            StrokeDashArray = new DoubleCollection { 3, 2 },
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
}