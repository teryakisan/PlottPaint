using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NVSPlotter.Models;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;
using Cursors = System.Windows.Input.Cursors;

namespace NVSPlotter.Services;

/// <summary>
/// Handles selection, move, resize, and rotation of strokes.
/// </summary>
public sealed class SelectionController
{
    public enum SelectionHandle
    {
        None,
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleRight,
        BottomLeft, BottomCenter, BottomRight,
        Rotate,
        Body // For moving the entire selection
    }

    public enum SelectionMode
    {
        Idle,
        MarqueeSelecting,
        Moving,
        Resizing,
        Rotating
    }

    private const double HIT_TOLERANCE = 3.0; // mm
    private const double HANDLE_SIZE = 8.0;
    private const double ROTATE_HANDLE_OFFSET = 25.0;

    private readonly Canvas _canvas;
    private readonly Func<PlotDocument> _getDocument;
    private readonly Action _requestRender;

    // Selection state
    private readonly HashSet<int> _selectedIndices = new();
    private Rect _selectionBounds;
    private SelectionMode _mode = SelectionMode.Idle;
    private SelectionHandle _activeHandle = SelectionHandle.None;

    // Drag state
    private PointMm _dragStart;
    private Rect _originalBounds;
    private List<LineStroke>? _originalStrokes;
    private double _originalAngle;

    // Preview visuals
    private Rectangle? _marqueeRect;
    private Rectangle? _boundsRect;
    private readonly List<Rectangle> _handles = new();
    private Ellipse? _rotateHandle;
    private Line? _rotateConnector;

    public bool HasSelection => _selectedIndices.Count > 0;
    public bool IsActive => _mode != SelectionMode.Idle;
    public IReadOnlySet<int> SelectedIndices => _selectedIndices;
    public Rect SelectionBounds => _selectionBounds;

    public SelectionController(Canvas canvas, Func<PlotDocument> getDocument, Action requestRender)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
    }

    /// <summary>
    /// Handles mouse down for selection tool.
    /// Returns true if the event was handled.
    /// </summary>
    public bool HandleMouseDown(PointMm point, bool shiftHeld)
    {
        var doc = _getDocument();

        // Check if clicking on a handle
        if (HasSelection)
        {
            var handle = HitTestHandle(point);
            if (handle != SelectionHandle.None)
            {
                BeginHandleOperation(handle, point);
                return true;
            }

            // Check if clicking inside selection bounds (move)
            if (IsPointInBounds(point, _selectionBounds))
            {
                BeginMove(point);
                return true;
            }
        }

        // Check if clicking on a stroke (single select)
        var hitIndex = HitTestStroke(point, doc.Strokes);
        if (hitIndex >= 0)
        {
            if (shiftHeld)
            {
                // Toggle selection
                if (_selectedIndices.Contains(hitIndex))
                    _selectedIndices.Remove(hitIndex);
                else
                    _selectedIndices.Add(hitIndex);
            }
            else
            {
                // Single select
                _selectedIndices.Clear();
                _selectedIndices.Add(hitIndex);
            }
            UpdateSelectionBounds(doc.Strokes);
            _requestRender();
            return true;
        }

        // Start marquee selection
        if (!shiftHeld)
        {
            _selectedIndices.Clear();
        }
        BeginMarquee(point);
        return true;
    }

    /// <summary>
    /// Handles mouse move for selection tool.
    /// Returns true if the event was handled.
    /// </summary>
    public bool HandleMouseMove(PointMm point)
    {
        switch (_mode)
        {
            case SelectionMode.MarqueeSelecting:
                UpdateMarquee(point);
                return true;

            case SelectionMode.Moving:
                UpdateMove(point);
                return true;

            case SelectionMode.Resizing:
                UpdateResize(point);
                return true;

            case SelectionMode.Rotating:
                UpdateRotate(point);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles mouse up for selection tool.
    /// Returns the list of modified strokes if any changes were committed.
    /// </summary>
    public List<LineStroke>? HandleMouseUp(PointMm point)
    {
        List<LineStroke>? result = null;

        switch (_mode)
        {
            case SelectionMode.MarqueeSelecting:
                CompleteMarquee(point);
                break;

            case SelectionMode.Moving:
            case SelectionMode.Resizing:
            case SelectionMode.Rotating:
                result = CommitTransform();
                break;
        }

        _mode = SelectionMode.Idle;
        _activeHandle = SelectionHandle.None;
        RemoveMarqueeVisual();

        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        return result;
    }

    /// <summary>
    /// Cancels the current selection operation.
    /// </summary>
    public void Cancel()
    {
        if (_mode == SelectionMode.Moving || _mode == SelectionMode.Resizing || _mode == SelectionMode.Rotating)
        {
            // Restore original strokes
            if (_originalStrokes != null)
            {
                var doc = _getDocument();
                foreach (var idx in _selectedIndices.OrderBy(i => i))
                {
                    var originalIdx = _selectedIndices.ToList().IndexOf(idx);
                    if (originalIdx >= 0 && originalIdx < _originalStrokes.Count)
                    {
                        doc.Strokes[idx] = _originalStrokes[originalIdx];
                    }
                }
            }
        }

        _mode = SelectionMode.Idle;
        _activeHandle = SelectionHandle.None;
        _originalStrokes = null;
        RemoveMarqueeVisual();

        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        _requestRender();
    }

    /// <summary>
    /// Clears the current selection.
    /// </summary>
    public void ClearSelection()
    {
        _selectedIndices.Clear();
        _selectionBounds = Rect.Empty;
        RemoveSelectionVisuals();
        _requestRender();
    }

    /// <summary>
    /// Deletes the selected strokes and returns them for undo.
    /// </summary>
    public List<(int Index, LineStroke Stroke)>? DeleteSelection()
    {
        if (!HasSelection) return null;

        var doc = _getDocument();
        var deleted = new List<(int Index, LineStroke Stroke)>();

        // Delete in reverse order to preserve indices
        foreach (var idx in _selectedIndices.OrderByDescending(i => i))
        {
            if (idx >= 0 && idx < doc.Strokes.Count)
            {
                deleted.Add((idx, doc.Strokes[idx]));
                doc.Strokes.RemoveAt(idx);
            }
        }

        _selectedIndices.Clear();
        _selectionBounds = Rect.Empty;
        _requestRender();

        return deleted.Count > 0 ? deleted : null;
    }

    /// <summary>
    /// Renders selection visuals (bounding box, handles) on the canvas.
    /// Call this from RenderAll after drawing strokes.
    /// </summary>
    public void RenderSelectionVisuals()
    {
        RemoveSelectionVisuals();

        if (!HasSelection) return;

        var doc = _getDocument();
        UpdateSelectionBounds(doc.Strokes);

        if (_selectionBounds.IsEmpty) return;

        // Bounding box
        _boundsRect = new Rectangle
        {
            Width = _selectionBounds.Width,
            Height = _selectionBounds.Height,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = [4, 2],
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_boundsRect, _selectionBounds.Left);
        Canvas.SetTop(_boundsRect, _selectionBounds.Top);
        Panel.SetZIndex(_boundsRect, 18);
        _canvas.Children.Add(_boundsRect);

        // Resize handles
        AddHandle(_selectionBounds.Left, _selectionBounds.Top, SelectionHandle.TopLeft);
        AddHandle(_selectionBounds.Left + _selectionBounds.Width / 2, _selectionBounds.Top, SelectionHandle.TopCenter);
        AddHandle(_selectionBounds.Right, _selectionBounds.Top, SelectionHandle.TopRight);
        AddHandle(_selectionBounds.Left, _selectionBounds.Top + _selectionBounds.Height / 2, SelectionHandle.MiddleLeft);
        AddHandle(_selectionBounds.Right, _selectionBounds.Top + _selectionBounds.Height / 2, SelectionHandle.MiddleRight);
        AddHandle(_selectionBounds.Left, _selectionBounds.Bottom, SelectionHandle.BottomLeft);
        AddHandle(_selectionBounds.Left + _selectionBounds.Width / 2, _selectionBounds.Bottom, SelectionHandle.BottomCenter);
        AddHandle(_selectionBounds.Right, _selectionBounds.Bottom, SelectionHandle.BottomRight);

        // Rotation handle
        var centerX = _selectionBounds.Left + _selectionBounds.Width / 2;
        var rotateY = _selectionBounds.Top - ROTATE_HANDLE_OFFSET;

        _rotateConnector = new Line
        {
            X1 = centerX,
            Y1 = _selectionBounds.Top,
            X2 = centerX,
            Y2 = rotateY,
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
            Width = HANDLE_SIZE,
            Height = HANDLE_SIZE,
            Fill = Brushes.White,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_rotateHandle, centerX - HANDLE_SIZE / 2);
        Canvas.SetTop(_rotateHandle, rotateY - HANDLE_SIZE / 2);
        Panel.SetZIndex(_rotateHandle, 19);
        _canvas.Children.Add(_rotateHandle);
    }

    /// <summary>
    /// Checks if a stroke index is selected.
    /// </summary>
    public bool IsSelected(int index) => _selectedIndices.Contains(index);

    // ===== PRIVATE METHODS =====

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
    }

    private void RemoveSelectionVisuals()
    {
        if (_boundsRect != null)
        {
            _canvas.Children.Remove(_boundsRect);
            _boundsRect = null;
        }

        foreach (var h in _handles)
        {
            _canvas.Children.Remove(h);
        }
        _handles.Clear();

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
    }

    private void RemoveMarqueeVisual()
    {
        if (_marqueeRect != null)
        {
            _canvas.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        }
    }

    private int HitTestStroke(PointMm point, List<LineStroke> strokes)
    {
        for (int i = strokes.Count - 1; i >= 0; i--) // Top-most first
        {
            var stroke = strokes[i];
            if (DistanceToSegment(point, stroke.A, stroke.B) <= HIT_TOLERANCE)
            {
                return i;
            }
        }
        return -1;
    }

    private SelectionHandle HitTestHandle(PointMm point)
    {
        if (_selectionBounds.IsEmpty) return SelectionHandle.None;

        var centerX = _selectionBounds.Left + _selectionBounds.Width / 2;
        var centerY = _selectionBounds.Top + _selectionBounds.Height / 2;
        var rotateY = _selectionBounds.Top - ROTATE_HANDLE_OFFSET;

        // Check rotate handle first
        if (Distance(point, new PointMm(centerX, rotateY)) <= HANDLE_SIZE)
            return SelectionHandle.Rotate;

        // Check corner handles
        if (Distance(point, new PointMm(_selectionBounds.Left, _selectionBounds.Top)) <= HANDLE_SIZE)
            return SelectionHandle.TopLeft;
        if (Distance(point, new PointMm(_selectionBounds.Right, _selectionBounds.Top)) <= HANDLE_SIZE)
            return SelectionHandle.TopRight;
        if (Distance(point, new PointMm(_selectionBounds.Left, _selectionBounds.Bottom)) <= HANDLE_SIZE)
            return SelectionHandle.BottomLeft;
        if (Distance(point, new PointMm(_selectionBounds.Right, _selectionBounds.Bottom)) <= HANDLE_SIZE)
            return SelectionHandle.BottomRight;

        // Check edge handles
        if (Distance(point, new PointMm(centerX, _selectionBounds.Top)) <= HANDLE_SIZE)
            return SelectionHandle.TopCenter;
        if (Distance(point, new PointMm(centerX, _selectionBounds.Bottom)) <= HANDLE_SIZE)
            return SelectionHandle.BottomCenter;
        if (Distance(point, new PointMm(_selectionBounds.Left, centerY)) <= HANDLE_SIZE)
            return SelectionHandle.MiddleLeft;
        if (Distance(point, new PointMm(_selectionBounds.Right, centerY)) <= HANDLE_SIZE)
            return SelectionHandle.MiddleRight;

        return SelectionHandle.None;
    }

    private static bool IsPointInBounds(PointMm point, Rect bounds)
    {
        return point.X >= bounds.Left && point.X <= bounds.Right &&
               point.Y >= bounds.Top && point.Y <= bounds.Bottom;
    }

    private void UpdateSelectionBounds(List<LineStroke> strokes)
    {
        if (_selectedIndices.Count == 0)
        {
            _selectionBounds = Rect.Empty;
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var idx in _selectedIndices)
        {
            if (idx < 0 || idx >= strokes.Count) continue;

            var stroke = strokes[idx];
            minX = Math.Min(minX, Math.Min(stroke.A.X, stroke.B.X));
            minY = Math.Min(minY, Math.Min(stroke.A.Y, stroke.B.Y));
            maxX = Math.Max(maxX, Math.Max(stroke.A.X, stroke.B.X));
            maxY = Math.Max(maxY, Math.Max(stroke.A.Y, stroke.B.Y));
        }

        if (minX <= maxX && minY <= maxY)
        {
            _selectionBounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        }
        else
        {
            _selectionBounds = Rect.Empty;
        }
    }

    // ===== MARQUEE SELECTION =====

    private void BeginMarquee(PointMm start)
    {
        _mode = SelectionMode.MarqueeSelecting;
        _dragStart = start;

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
        _canvas.CaptureMouse();
    }

    private void UpdateMarquee(PointMm current)
    {
        if (_marqueeRect == null) return;

        var left = Math.Min(_dragStart.X, current.X);
        var top = Math.Min(_dragStart.Y, current.Y);
        var width = Math.Abs(current.X - _dragStart.X);
        var height = Math.Abs(current.Y - _dragStart.Y);

        Canvas.SetLeft(_marqueeRect, left);
        Canvas.SetTop(_marqueeRect, top);
        _marqueeRect.Width = width;
        _marqueeRect.Height = height;
    }

    private void CompleteMarquee(PointMm end)
    {
        var doc = _getDocument();
        var marqueeRect = new Rect(
            Math.Min(_dragStart.X, end.X),
            Math.Min(_dragStart.Y, end.Y),
            Math.Abs(end.X - _dragStart.X),
            Math.Abs(end.Y - _dragStart.Y)
        );

        // Select strokes that intersect the marquee
        for (int i = 0; i < doc.Strokes.Count; i++)
        {
            var stroke = doc.Strokes[i];
            if (StrokeIntersectsRect(stroke, marqueeRect))
            {
                _selectedIndices.Add(i);
            }
        }

        UpdateSelectionBounds(doc.Strokes);
        _requestRender();
    }

    private static bool StrokeIntersectsRect(LineStroke stroke, Rect rect)
    {
        // Check if either endpoint is inside
        if (rect.Contains(new Point(stroke.A.X, stroke.A.Y)) ||
            rect.Contains(new Point(stroke.B.X, stroke.B.Y)))
        {
            return true;
        }

        // Check if line intersects any edge of the rectangle
        var tl = new PointMm(rect.Left, rect.Top);
        var tr = new PointMm(rect.Right, rect.Top);
        var br = new PointMm(rect.Right, rect.Bottom);
        var bl = new PointMm(rect.Left, rect.Bottom);

        return SegmentsIntersect(stroke.A, stroke.B, tl, tr) ||
               SegmentsIntersect(stroke.A, stroke.B, tr, br) ||
               SegmentsIntersect(stroke.A, stroke.B, br, bl) ||
               SegmentsIntersect(stroke.A, stroke.B, bl, tl);
    }

    // ===== MOVE =====

    private void BeginMove(PointMm start)
    {
        _mode = SelectionMode.Moving;
        _activeHandle = SelectionHandle.Body;
        _dragStart = start;
        _originalBounds = _selectionBounds;
        SaveOriginalStrokes();
        _canvas.CaptureMouse();
    }

    private void UpdateMove(PointMm current)
    {
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;

        var doc = _getDocument();
        var indices = _selectedIndices.ToList();

        for (int i = 0; i < indices.Count; i++)
        {
            var idx = indices[i];
            if (_originalStrokes == null || i >= _originalStrokes.Count) continue;

            var original = _originalStrokes[i];
            doc.Strokes[idx] = new LineStroke(
                new PointMm(original.A.X + dx, original.A.Y + dy),
                new PointMm(original.B.X + dx, original.B.Y + dy)
            )
            {
                PaintWellId = original.PaintWellId
            };
        }

        UpdateSelectionBounds(doc.Strokes);
        _requestRender();
    }

    // ===== RESIZE =====

    private void BeginHandleOperation(SelectionHandle handle, PointMm start)
    {
        _activeHandle = handle;
        _dragStart = start;
        _originalBounds = _selectionBounds;
        SaveOriginalStrokes();
        _canvas.CaptureMouse();

        if (handle == SelectionHandle.Rotate)
        {
            _mode = SelectionMode.Rotating;
            var center = new PointMm(
                _originalBounds.Left + _originalBounds.Width / 2,
                _originalBounds.Top + _originalBounds.Height / 2);
            _originalAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        }
        else
        {
            _mode = SelectionMode.Resizing;
        }
    }

    private void UpdateResize(PointMm current)
    {
        if (_originalStrokes == null || _originalBounds.IsEmpty) return;

        var doc = _getDocument();
        var indices = _selectedIndices.ToList();

        // Calculate new bounds based on handle being dragged
        var newBounds = CalculateNewBounds(current);
        if (newBounds.Width < 1 || newBounds.Height < 1) return;

        // Scale strokes
        for (int i = 0; i < indices.Count; i++)
        {
            var idx = indices[i];
            if (i >= _originalStrokes.Count) continue;

            var original = _originalStrokes[i];
            doc.Strokes[idx] = ScaleStroke(original, _originalBounds, newBounds);
        }

        UpdateSelectionBounds(doc.Strokes);
        _requestRender();
    }

    private Rect CalculateNewBounds(PointMm current)
    {
        var left = _originalBounds.Left;
        var top = _originalBounds.Top;
        var right = _originalBounds.Right;
        var bottom = _originalBounds.Bottom;

        switch (_activeHandle)
        {
            case SelectionHandle.TopLeft:
                left = current.X;
                top = current.Y;
                break;
            case SelectionHandle.TopCenter:
                top = current.Y;
                break;
            case SelectionHandle.TopRight:
                right = current.X;
                top = current.Y;
                break;
            case SelectionHandle.MiddleLeft:
                left = current.X;
                break;
            case SelectionHandle.MiddleRight:
                right = current.X;
                break;
            case SelectionHandle.BottomLeft:
                left = current.X;
                bottom = current.Y;
                break;
            case SelectionHandle.BottomCenter:
                bottom = current.Y;
                break;
            case SelectionHandle.BottomRight:
                right = current.X;
                bottom = current.Y;
                break;
        }

        // Ensure min size and handle flipping
        if (left > right) (left, right) = (right, left);
        if (top > bottom) (top, bottom) = (bottom, top);

        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static LineStroke ScaleStroke(LineStroke original, Rect oldBounds, Rect newBounds)
    {
        var scaleX = oldBounds.Width > 0 ? newBounds.Width / oldBounds.Width : 1;
        var scaleY = oldBounds.Height > 0 ? newBounds.Height / oldBounds.Height : 1;

        var newA = new PointMm(
            newBounds.Left + (original.A.X - oldBounds.Left) * scaleX,
            newBounds.Top + (original.A.Y - oldBounds.Top) * scaleY
        );
        var newB = new PointMm(
            newBounds.Left + (original.B.X - oldBounds.Left) * scaleX,
            newBounds.Top + (original.B.Y - oldBounds.Top) * scaleY
        );

        return new LineStroke(newA, newB) { PaintWellId = original.PaintWellId };
    }

    // ===== ROTATE =====

    private void UpdateRotate(PointMm current)
    {
        if (_originalStrokes == null || _originalBounds.IsEmpty) return;

        var doc = _getDocument();
        var indices = _selectedIndices.ToList();

        var center = new PointMm(
            _originalBounds.Left + _originalBounds.Width / 2,
            _originalBounds.Top + _originalBounds.Height / 2);

        var currentAngle = Math.Atan2(current.Y - center.Y, current.X - center.X);
        var deltaAngle = currentAngle - _originalAngle;

        for (int i = 0; i < indices.Count; i++)
        {
            var idx = indices[i];
            if (i >= _originalStrokes.Count) continue;

            var original = _originalStrokes[i];
            doc.Strokes[idx] = RotateStroke(original, center, deltaAngle);
        }

        UpdateSelectionBounds(doc.Strokes);
        _requestRender();
    }

    private static LineStroke RotateStroke(LineStroke original, PointMm center, double angle)
    {
        var newA = RotatePoint(original.A, center, angle);
        var newB = RotatePoint(original.B, center, angle);
        return new LineStroke(newA, newB) { PaintWellId = original.PaintWellId };
    }

    private static PointMm RotatePoint(PointMm point, PointMm center, double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return new PointMm(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos
        );
    }

    // ===== HELPERS =====

    private void SaveOriginalStrokes()
    {
        var doc = _getDocument();
        _originalStrokes = _selectedIndices
            .OrderBy(i => i)
            .Where(i => i >= 0 && i < doc.Strokes.Count)
            .Select(i => doc.Strokes[i])
            .ToList();
    }

    private List<LineStroke>? CommitTransform()
    {
        // Return the new strokes for undo purposes
        if (_originalStrokes == null || _selectedIndices.Count == 0)
            return null;

        var doc = _getDocument();
        return _selectedIndices
            .OrderBy(i => i)
            .Where(i => i >= 0 && i < doc.Strokes.Count)
            .Select(i => doc.Strokes[i])
            .ToList();
    }

    private static double Distance(PointMm a, PointMm b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToSegment(PointMm point, PointMm a, PointMm b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSq = dx * dx + dy * dy;

        if (lengthSq < 0.0001)
            return Distance(point, a);

        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSq, 0, 1);
        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;

        return Distance(point, new PointMm(projX, projY));
    }

    private static bool SegmentsIntersect(PointMm a1, PointMm a2, PointMm b1, PointMm b2)
    {
        double d1 = CrossProduct(b2.X - b1.X, b2.Y - b1.Y, a1.X - b1.X, a1.Y - b1.Y);
        double d2 = CrossProduct(b2.X - b1.X, b2.Y - b1.Y, a2.X - b1.X, a2.Y - b1.Y);
        double d3 = CrossProduct(a2.X - a1.X, a2.Y - a1.Y, b1.X - a1.X, b1.Y - a1.Y);
        double d4 = CrossProduct(a2.X - a1.X, a2.Y - a1.Y, b2.X - a1.X, b2.Y - a1.Y);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        return false;
    }

    private static double CrossProduct(double ax, double ay, double bx, double by)
    {
        return ax * by - ay * bx;
    }
}
