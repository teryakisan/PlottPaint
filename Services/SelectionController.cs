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

    private const double HIT_TOLERANCE = 8.0; // mm - distance to click on a stroke
    private const double HANDLE_SIZE = 12.0;  // Visual size of handles
    private const double HANDLE_HIT_RADIUS = 20.0; // Hit test radius for handles (generous for easier clicking)
    private const double ROTATE_HANDLE_OFFSET = 35.0; // Distance from top of selection to rotation handle
    private const double RULER_THICKNESS = 18.0;  // Must match the ruler offset used in rendering
    private const double SELECTION_PADDING = 20.0; // Padding between selection handles and content
    private const double PAINT_WELL_HIT_MARGIN = 10.0; // Extra margin around paint well bounds for easier clicking

    private readonly Canvas _canvas;
    private readonly Func<PlotDocument> _getDocument;
    private readonly Action _requestRender;

    // Selection state - strokes
    private readonly HashSet<int> _selectedIndices = new();
    
    // Selection state - paint wells
    private readonly HashSet<Guid> _selectedPaintWellIds = new();
    
    private Rect _selectionBounds;
    private SelectionMode _mode = SelectionMode.Idle;
    private SelectionHandle _activeHandle = SelectionHandle.None;

    // Drag state
    private PointMm _dragStart;
    private Rect _originalBounds;
    private List<LineStroke>? _originalStrokes;
    private List<(Guid Id, Rect Bounds)>? _originalPaintWellBounds;
    private double _originalAngle;
    
    // Cumulative rotation tracking for selection box display
    private double _selectionRotationAngle;
    private Point _selectionRotationCenter;

    // Preview visuals
    private Rectangle? _marqueeRect;
    private Path? _boundsPath;  // Changed from Rectangle to Path for proper rotation
    private readonly List<Rectangle> _handles = new();
    private Ellipse? _rotateHandle;
    private Line? _rotateConnector;

    public bool HasSelection => _selectedIndices.Count > 0 || _selectedPaintWellIds.Count > 0;
    public bool IsActive => _mode != SelectionMode.Idle;
    public IReadOnlySet<int> SelectedIndices => _selectedIndices;
    public IReadOnlySet<Guid> SelectedPaintWellIds => _selectedPaintWellIds;
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

        // Check if clicking on a handle (handles are in canvas space for paint wells)
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

        // For paint well hit testing, convert canvas coordinates to document coordinates
        var docPoint = CanvasToDocument(point);

        // Check if clicking on a paint well first (they're visually larger/easier to click)
        var hitWell = HitTestPaintWell(docPoint, doc.PaintWells);
        if (hitWell != null)
        {
            if (shiftHeld)
            {
                // Toggle selection
                if (_selectedPaintWellIds.Contains(hitWell.Id))
                    _selectedPaintWellIds.Remove(hitWell.Id);
                else
                    _selectedPaintWellIds.Add(hitWell.Id);
            }
            else
            {
                // Single select - clear all selections first
                _selectedIndices.Clear();
                _selectedPaintWellIds.Clear();
                _selectedPaintWellIds.Add(hitWell.Id);
                // Reset rotation for new selection
                _selectionRotationAngle = 0;
            }
            UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
            _requestRender();
            return true;
        }

        // Check if clicking on a stroke (single select) - strokes use canvas coordinates directly
        // When clicking on a stroke, select all strokes in the same group (object)
        var hitIndex = HitTestStroke(point, doc.Strokes);
        if (hitIndex >= 0)
        {
            // Find all strokes in the same group as the clicked stroke
            var groupedIndices = FindGroupedStrokes(hitIndex, doc.Strokes);
            
            // Check if clicking on an already-selected grouped object
            // If so, do nothing (prevents indicator jumping on repeated clicks)
            var allGroupedAlreadySelected = groupedIndices.All(idx => _selectedIndices.Contains(idx));
            if (allGroupedAlreadySelected && !shiftHeld)
            {
                // Already selected - do nothing, just return
                return true;
            }
            
            if (shiftHeld)
            {
                // Toggle selection of entire grouped object
                // Check if any of the grouped strokes are already selected
                var anySelected = groupedIndices.Any(idx => _selectedIndices.Contains(idx));
                
                if (anySelected)
                {
                    // Remove all grouped strokes from selection
                    foreach (var idx in groupedIndices)
                    {
                        _selectedIndices.Remove(idx);
                    }
                }
                else
                {
                    // Add all grouped strokes to selection
                    foreach (var idx in groupedIndices)
                    {
                        _selectedIndices.Add(idx);
                    }
                }
            }
            else
            {
                // Single select entire grouped object - clear all selections first
                _selectedIndices.Clear();
                _selectedPaintWellIds.Clear();
                
                // Reset rotation for new selection
                _selectionRotationAngle = 0;
                
                // Find and select all strokes in the same group
                foreach (var idx in groupedIndices)
                {
                    _selectedIndices.Add(idx);
                }
            }
            UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
            _requestRender();
            return true;
        }

        // Start marquee selection
        if (!shiftHeld)
        {
            _selectedIndices.Clear();
            _selectedPaintWellIds.Clear();
            // Reset rotation for new selection
            _selectionRotationAngle = 0;
        }
        BeginMarquee(point);
        return true;
    }

    /// <summary>
    /// Converts canvas coordinates to document coordinates by removing ruler offset.
    /// </summary>
    private static PointMm CanvasToDocument(PointMm canvasPoint)
    {
        return new PointMm(canvasPoint.X - RULER_THICKNESS, canvasPoint.Y - RULER_THICKNESS);
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
            var doc = _getDocument();

            // Restore original strokes
            if (_originalStrokes != null)
            {
                foreach (var idx in _selectedIndices.OrderBy(i => i))
                {
                    var originalIdx = _selectedIndices.ToList().IndexOf(idx);
                    if (originalIdx >= 0 && originalIdx < _originalStrokes.Count)
                    {
                        doc.Strokes[idx] = _originalStrokes[originalIdx];
                    }
                }
            }

            // Restore original paint well bounds
            if (_originalPaintWellBounds != null)
            {
                foreach (var (id, originalBounds) in _originalPaintWellBounds)
                {
                    var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                    if (well != null)
                    {
                        well.Bounds = originalBounds;
                    }
                }
            }
        }

        _mode = SelectionMode.Idle;
        _activeHandle = SelectionHandle.None;
        _originalStrokes = null;
        _originalPaintWellBounds = null;
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
        _selectedPaintWellIds.Clear();
        _selectionBounds = Rect.Empty;
        _selectionRotationAngle = 0;
        RemoveSelectionVisuals();
        _requestRender();
    }

    /// <summary>
    /// Deletes the selected strokes and paint wells.
    /// Returns information for undo purposes.
    /// </summary>
    public (List<(int Index, LineStroke Stroke)>? Strokes, List<PaintWell>? PaintWells) DeleteSelection()
    {
        if (!HasSelection) return (null, null);

        var doc = _getDocument();
        List<(int Index, LineStroke Stroke)>? deletedStrokes = null;
        List<PaintWell>? deletedPaintWells = null;

        // Delete strokes in reverse order to preserve indices
        if (_selectedIndices.Count > 0)
        {
            deletedStrokes = new List<(int Index, LineStroke Stroke)>();
            foreach (var idx in _selectedIndices.OrderByDescending(i => i))
            {
                if (idx >= 0 && idx < doc.Strokes.Count)
                {
                    deletedStrokes.Add((idx, doc.Strokes[idx]));
                    doc.Strokes.RemoveAt(idx);
                }
            }
        }

        // Delete paint wells
        if (_selectedPaintWellIds.Count > 0)
        {
            deletedPaintWells = new List<PaintWell>();
            foreach (var wellId in _selectedPaintWellIds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == wellId);
                if (well != null)
                {
                    deletedPaintWells.Add(well);
                    doc.PaintWells.Remove(well);

                    // Clear stroke associations to this paint well
                    foreach (var stroke in doc.Strokes.Where(s => s.PaintWellId == wellId))
                    {
                        stroke.PaintWellId = null;
                    }
                }
            }
        }

        _selectedIndices.Clear();
        _selectedPaintWellIds.Clear();
        _selectionBounds = Rect.Empty;
        _requestRender();

        return (deletedStrokes?.Count > 0 ? deletedStrokes : null, 
                deletedPaintWells?.Count > 0 ? deletedPaintWells : null);
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
        
        // During rotation, use the original bounds to keep the box size constant.
        // Otherwise, recalculate the bounds from current stroke positions.
        Rect boundsToUse;
        if (_mode == SelectionMode.Rotating && !_originalBounds.IsEmpty)
        {
            boundsToUse = _originalBounds;
        }
        else
        {
            UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
            boundsToUse = _selectionBounds;
        }

        if (boundsToUse.IsEmpty) return;

        // Paint wells are rendered with ruler offset, strokes are not.
        // For paint well selection, we need to add the ruler offset.
        // For stroke selection, we don't (strokes render at their raw coordinates).
        var offset = _selectedPaintWellIds.Count > 0 ? RULER_THICKNESS : 0;

        // Calculate visual bounds with padding
        var visualLeft = offset + boundsToUse.Left - SELECTION_PADDING;
        var visualTop = offset + boundsToUse.Top - SELECTION_PADDING;
        var visualWidth = boundsToUse.Width + (SELECTION_PADDING * 2);
        var visualHeight = boundsToUse.Height + (SELECTION_PADDING * 2);
        var visualRight = visualLeft + visualWidth;
        var visualBottom = visualTop + visualHeight;
        
        // Calculate center for rotation
        var centerX = visualLeft + visualWidth / 2;
        var centerY = visualTop + visualHeight / 2;
        
        // Create rotation transform if we have a rotation angle
        RotateTransform? rotateTransform = null;
        if (Math.Abs(_selectionRotationAngle) > 0.001)
        {
            rotateTransform = new RotateTransform(_selectionRotationAngle * 180 / Math.PI, centerX, centerY);
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
        
        // Calculate the outward direction from the rotated top edge (perpendicular to top edge, pointing up/out)
        var topEdgeDx = topRight.X - topLeft.X;
        var topEdgeDy = topRight.Y - topLeft.Y;
        var topEdgeLength = Math.Sqrt(topEdgeDx * topEdgeDx + topEdgeDy * topEdgeDy);
        
        // Perpendicular direction (rotate 90 degrees counter-clockwise for "up" relative to the box)
        double perpX = -topEdgeDy / topEdgeLength;
        double perpY = topEdgeDx / topEdgeLength;
        
        // Rotation handle position
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
            Width = HANDLE_SIZE,
            Height = HANDLE_SIZE,
            Fill = Brushes.White,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(_rotateHandle, rotateHandleX - HANDLE_SIZE / 2);
        Canvas.SetTop(_rotateHandle, rotateHandleY - HANDLE_SIZE / 2);
        Panel.SetZIndex(_rotateHandle, 19);
        _canvas.Children.Add(_rotateHandle);
        
        // Store the rotation center for hit testing
        _selectionRotationCenter = new Point(centerX, centerY);
    }
    
    /// <summary>
    /// Transforms a point using the given rotation transform, or returns it unchanged if no transform.
    /// </summary>
    private static Point TransformPoint(double x, double y, RotateTransform? transform)
    {
        var point = new Point(x, y);
        return transform?.Transform(point) ?? point;
    }

    /// <summary>
    /// Checks if a stroke index is selected.
    /// </summary>
    public bool IsSelected(int index) => _selectedIndices.Contains(index);

    /// <summary>
    /// Checks if a paint well is selected by its ID.
    /// </summary>
    public bool IsPaintWellSelected(Guid id) => _selectedPaintWellIds.Contains(id);

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

    /// <summary>
    /// Finds all strokes that belong to the same group as the stroke at the given index.
    /// If the stroke has no GroupId, only that stroke is returned (individual line).
    /// </summary>
    private HashSet<int> FindGroupedStrokes(int startIndex, List<LineStroke> strokes)
    {
        var result = new HashSet<int> { startIndex };
        
        var hitStroke = strokes[startIndex];
        
        // If this stroke has no group, it's an individual object - select only this stroke
        if (!hitStroke.GroupId.HasValue)
        {
            return result;
        }
        
        // Find all strokes with the same GroupId
        var groupId = hitStroke.GroupId.Value;
        for (int i = 0; i < strokes.Count; i++)
        {
            if (strokes[i].GroupId == groupId)
            {
                result.Add(i);
            }
        }
        
        return result;
    }

    private PaintWell? HitTestPaintWell(PointMm point, List<PaintWell> paintWells)
    {
        // Check paint wells in reverse order (top-most first)
        for (int i = paintWells.Count - 1; i >= 0; i--)
        {
            var well = paintWells[i];
            // Check if point is inside the well bounds with extra margin for easier clicking
            var expandedBounds = new Rect(
                well.Bounds.Left - PAINT_WELL_HIT_MARGIN,
                well.Bounds.Top - PAINT_WELL_HIT_MARGIN,
                well.Bounds.Width + (PAINT_WELL_HIT_MARGIN * 2),
                well.Bounds.Height + (PAINT_WELL_HIT_MARGIN * 2));
            
            if (expandedBounds.Contains(new Point(point.X, point.Y)))
            {
                return well;
            }
        }
        return null;
    }

    private SelectionHandle HitTestHandle(PointMm point)
    {
        // During rotation, use original bounds to match the visual display
        var boundsToUse = (_mode == SelectionMode.Rotating && !_originalBounds.IsEmpty) 
            ? _originalBounds 
            : _selectionBounds;
            
        if (boundsToUse.IsEmpty) return SelectionHandle.None;

        // Paint wells are rendered with ruler offset, strokes are not.
        var offset = _selectedPaintWellIds.Count > 0 ? RULER_THICKNESS : 0;

        // Calculate padded bounds matching the visual display
        var paddedLeft = offset + boundsToUse.Left - SELECTION_PADDING;
        var paddedTop = offset + boundsToUse.Top - SELECTION_PADDING;
        var paddedRight = offset + boundsToUse.Right + SELECTION_PADDING;
        var paddedBottom = offset + boundsToUse.Bottom + SELECTION_PADDING;
        var paddedCenterX = (paddedLeft + paddedRight) / 2;
        var paddedCenterY = (paddedTop + paddedBottom) / 2;

        // Create rotation transform if we have a rotation angle
        RotateTransform? rotateTransform = null;
        if (Math.Abs(_selectionRotationAngle) > 0.001)
        {
            rotateTransform = new RotateTransform(_selectionRotationAngle * 180 / Math.PI, paddedCenterX, paddedCenterY);
        }

        // Calculate rotated corner positions (same as in RenderSelectionVisuals)
        var topLeft = TransformPoint(paddedLeft, paddedTop, rotateTransform);
        var topRight = TransformPoint(paddedRight, paddedTop, rotateTransform);
        var bottomRight = TransformPoint(paddedRight, paddedBottom, rotateTransform);
        var bottomLeft = TransformPoint(paddedLeft, paddedBottom, rotateTransform);
        
        // Calculate rotation handle position
        var topCenter = new Point((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2);
        var topEdgeDx = topRight.X - topLeft.X;
        var topEdgeDy = topRight.Y - topLeft.Y;
        var topEdgeLength = Math.Sqrt(topEdgeDx * topEdgeDx + topEdgeDy * topEdgeDy);
        double perpX = topEdgeLength > 0 ? -topEdgeDy / topEdgeLength : 0;
        double perpY = topEdgeLength > 0 ? topEdgeDx / topEdgeLength : -1;
        var rotateHandlePos = new Point(topCenter.X + perpX * ROTATE_HANDLE_OFFSET, topCenter.Y + perpY * ROTATE_HANDLE_OFFSET);

        // Check rotate handle first - use larger hit radius for easier clicking
        if (Distance(point, new PointMm(rotateHandlePos.X, rotateHandlePos.Y)) <= HANDLE_HIT_RADIUS * 1.5)
            return SelectionHandle.Rotate;

        // Check corner handles
        if (Distance(point, new PointMm(topLeft.X, topLeft.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.TopLeft;
        if (Distance(point, new PointMm(topRight.X, topRight.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.TopRight;
        if (Distance(point, new PointMm(bottomLeft.X, bottomLeft.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.BottomLeft;
        if (Distance(point, new PointMm(bottomRight.X, bottomRight.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.BottomRight;

        // Check edge handles (midpoints of edges)
        var topCenterHandle = new Point((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2);
        var bottomCenterHandle = new Point((bottomLeft.X + bottomRight.X) / 2, (bottomLeft.Y + bottomRight.Y) / 2);
        var middleLeftHandle = new Point((topLeft.X + bottomLeft.X) / 2, (topLeft.Y + bottomLeft.Y) / 2);
        var middleRightHandle = new Point((topRight.X + bottomRight.X) / 2, (topRight.Y + bottomRight.Y) / 2);

        if (Distance(point, new PointMm(topCenterHandle.X, topCenterHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.TopCenter;
        if (Distance(point, new PointMm(bottomCenterHandle.X, bottomCenterHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.BottomCenter;
        if (Distance(point, new PointMm(middleLeftHandle.X, middleLeftHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.MiddleLeft;
        if (Distance(point, new PointMm(middleRightHandle.X, middleRightHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.MiddleRight;

        return SelectionHandle.None;
    }

    private bool IsPointInBounds(PointMm point, Rect bounds)
    {
        // During rotation, use original bounds to match the visual display
        var boundsToUse = (_mode == SelectionMode.Rotating && !_originalBounds.IsEmpty) 
            ? _originalBounds 
            : bounds;
            
        // Paint wells are rendered with ruler offset, strokes are not.
        var offset = _selectedPaintWellIds.Count > 0 ? RULER_THICKNESS : 0;

        // Include the padding area as part of the movable selection region
        var paddedLeft = offset + boundsToUse.Left - SELECTION_PADDING;
        var paddedTop = offset + boundsToUse.Top - SELECTION_PADDING;
        var paddedWidth = boundsToUse.Width + (SELECTION_PADDING * 2);
        var paddedHeight = boundsToUse.Height + (SELECTION_PADDING * 2);
        
        // If we have a rotation, we need to transform the point back to the unrotated space
        // to check if it's inside the original (unrotated) rectangle
        double testX = point.X;
        double testY = point.Y;
        
        if (Math.Abs(_selectionRotationAngle) > 0.001)
        {
            // Calculate center of the padded bounds
            var centerX = paddedLeft + paddedWidth / 2;
            var centerY = paddedTop + paddedHeight / 2;
            
            // Rotate the test point in the opposite direction around the same center
            var cos = Math.Cos(-_selectionRotationAngle);
            var sin = Math.Sin(-_selectionRotationAngle);
            var dx = point.X - centerX;
            var dy = point.Y - centerY;
            testX = centerX + dx * cos - dy * sin;
            testY = centerY + dx * sin + dy * cos;
        }
        
        return testX >= paddedLeft && testX <= paddedLeft + paddedWidth &&
               testY >= paddedTop && testY <= paddedTop + paddedHeight;
    }

    private void UpdateSelectionBounds(List<LineStroke> strokes, List<PaintWell> paintWells)
    {
        if (_selectedIndices.Count == 0 && _selectedPaintWellIds.Count == 0)
        {
            _selectionBounds = Rect.Empty;
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // Include stroke bounds
        foreach (var idx in _selectedIndices)
        {
            if (idx < 0 || idx >= strokes.Count) continue;

            var stroke = strokes[idx];
            minX = Math.Min(minX, Math.Min(stroke.A.X, stroke.B.X));
            minY = Math.Min(minY, Math.Min(stroke.A.Y, stroke.B.Y));
            maxX = Math.Max(maxX, Math.Max(stroke.A.X, stroke.B.X));
            maxY = Math.Max(maxY, Math.Max(stroke.A.Y, stroke.B.Y));
        }

        // Include paint well bounds
        foreach (var wellId in _selectedPaintWellIds)
        {
            var well = paintWells.FirstOrDefault(w => w.Id == wellId);
            if (well == null) continue;

            minX = Math.Min(minX, well.Bounds.Left);
            minY = Math.Min(minY, well.Bounds.Top);
            maxX = Math.Max(maxX, well.Bounds.Right);
            maxY = Math.Max(maxY, well.Bounds.Bottom);
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

    // Legacy overload for backward compatibility
    private void UpdateSelectionBounds(List<LineStroke> strokes)
    {
        UpdateSelectionBounds(strokes, _getDocument().PaintWells);
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

        // Select paint wells that intersect the marquee
        foreach (var well in doc.PaintWells)
        {
            if (PaintWellIntersectsRect(well, marqueeRect))
            {
                _selectedPaintWellIds.Add(well.Id);
            }
        }

        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        _requestRender();
    }

    private static bool PaintWellIntersectsRect(PaintWell well, Rect marquee)
    {
        // Check if the paint well bounds intersect with the marquee rectangle
        return well.Bounds.IntersectsWith(marquee);
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
        
        // Move strokes
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
                PaintWellId = original.PaintWellId,
                GroupId = original.GroupId,
                IsGroupStart = original.IsGroupStart,
                IsGroupEnd = original.IsGroupEnd
            };
        }

        // Move paint wells
        if (_originalPaintWellBounds != null)
        {
            foreach (var (id, originalBounds) in _originalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    well.Bounds = new Rect(
                        originalBounds.Left + dx,
                        originalBounds.Top + dy,
                        originalBounds.Width,
                        originalBounds.Height);
                }
            }
        }

        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
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
            
            // The rotation handle visual is in canvas coordinates.
            // For paint wells, handles are rendered at (offset + bounds), so mouse coords match.
            // Calculate the visual center where handles are displayed.
            var offset = _selectedPaintWellIds.Count > 0 ? RULER_THICKNESS : 0;
            var visualCenterX = offset + _originalBounds.Left + _originalBounds.Width / 2;
            var visualCenterY = offset + _originalBounds.Top + _originalBounds.Height / 2;
            
            // Calculate initial angle from center to mouse position (both in canvas coords)
            _originalAngle = Math.Atan2(start.Y - visualCenterY, start.X - visualCenterX);
        }
        else
        {
            _mode = SelectionMode.Resizing;
        }
    }

    private void UpdateResize(PointMm current)
    {
        if (_originalBounds.IsEmpty) return;

        var doc = _getDocument();

        // Get raw edge positions without normalization
        double left = _originalBounds.Left;
        double top = _originalBounds.Top;
        double right = _originalBounds.Right;
        double bottom = _originalBounds.Bottom;

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

        // Detect flip BEFORE normalizing
        bool flipH = left > right;
        bool flipV = top > bottom;

        // Now normalize for the visual bounds
        if (flipH) (left, right) = (right, left);
        if (flipV) (top, bottom) = (bottom, top);

        var newBounds = new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        if (newBounds.Width < 1 || newBounds.Height < 1) return;

        // Scale and flip strokes
        if (_originalStrokes != null)
        {
            var indices = _selectedIndices.ToList();
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _originalStrokes.Count) continue;

                var original = _originalStrokes[i];
                doc.Strokes[idx] = ScaleAndFlipStroke(original, _originalBounds, newBounds, flipH, flipV);
            }
        }

        // Scale and flip paint wells
        if (_originalPaintWellBounds != null)
        {
            foreach (var (id, originalWellBounds) in _originalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    well.Bounds = ScaleAndFlipRect(originalWellBounds, _originalBounds, newBounds, flipH, flipV);
                }
            }
        }

        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        _requestRender();
    }

    private static LineStroke ScaleAndFlipStroke(LineStroke original, Rect oldBounds, Rect newBounds, bool flipH, bool flipV)
    {
        // Calculate relative position (0 to 1) within original bounds
        double relAx = oldBounds.Width > 0 ? (original.A.X - oldBounds.Left) / oldBounds.Width : 0.5;
        double relAy = oldBounds.Height > 0 ? (original.A.Y - oldBounds.Top) / oldBounds.Height : 0.5;
        double relBx = oldBounds.Width > 0 ? (original.B.X - oldBounds.Left) / oldBounds.Width : 0.5;
        double relBy = oldBounds.Height > 0 ? (original.B.Y - oldBounds.Top) / oldBounds.Height : 0.5;

        // Apply flip by mirroring the relative position
        if (flipH)
        {
            relAx = 1.0 - relAx;
            relBx = 1.0 - relBx;
        }
        if (flipV)
        {
            relAy = 1.0 - relAy;
            relBy = 1.0 - relBy;
        }

        // Map back to new bounds
        var newA = new PointMm(
            newBounds.Left + relAx * newBounds.Width,
            newBounds.Top + relAy * newBounds.Height
        );
        var newB = new PointMm(
            newBounds.Left + relBx * newBounds.Width,
            newBounds.Top + relBy * newBounds.Height
        );

        return new LineStroke(newA, newB)
        {
            PaintWellId = original.PaintWellId,
            GroupId = original.GroupId,
            IsGroupStart = original.IsGroupStart,
            IsGroupEnd = original.IsGroupEnd
        };
    }

    private static Rect ScaleAndFlipRect(Rect original, Rect oldBounds, Rect newBounds, bool flipH, bool flipV)
    {
        // Calculate relative positions (0 to 1) within original bounds
        double relLeft = oldBounds.Width > 0 ? (original.Left - oldBounds.Left) / oldBounds.Width : 0;
        double relTop = oldBounds.Height > 0 ? (original.Top - oldBounds.Top) / oldBounds.Height : 0;
        double relRight = oldBounds.Width > 0 ? (original.Right - oldBounds.Left) / oldBounds.Width : 1;
        double relBottom = oldBounds.Height > 0 ? (original.Bottom - oldBounds.Top) / oldBounds.Height : 1;

        // Apply flip by mirroring relative positions
        if (flipH)
        {
            (relLeft, relRight) = (1.0 - relRight, 1.0 - relLeft);
        }
        if (flipV)
        {
            (relTop, relBottom) = (1.0 - relBottom, 1.0 - relTop);
        }

        // Map back to new bounds
        double newLeft = newBounds.Left + relLeft * newBounds.Width;
        double newTop = newBounds.Top + relTop * newBounds.Height;
        double newRight = newBounds.Left + relRight * newBounds.Width;
        double newBottom = newBounds.Top + relBottom * newBounds.Height;

        return new Rect(newLeft, newTop, Math.Max(10, newRight - newLeft), Math.Max(10, newBottom - newTop));
    }

    private static Rect ScaleRect(Rect original, Rect oldBounds, Rect newBounds)
    {
        return ScaleAndFlipRect(original, oldBounds, newBounds, false, false);
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

        // Normalize
        if (left > right) (left, right) = (right, left);
        if (top > bottom) (top, bottom) = (bottom, top);

        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static LineStroke ScaleStroke(LineStroke original, Rect oldBounds, Rect newBounds)
    {
        return ScaleAndFlipStroke(original, oldBounds, newBounds, false, false);
    }

    // ===== ROTATE =====

    private void UpdateRotate(PointMm current)
    {
        if (_originalBounds.IsEmpty) return;

        var doc = _getDocument();

        // Calculate visual center in canvas coordinates (same as BeginHandleOperation)
        var offset = _selectedPaintWellIds.Count > 0 ? RULER_THICKNESS : 0;
        var visualCenterX = offset + _originalBounds.Left + _originalBounds.Width / 2;
        var visualCenterY = offset + _originalBounds.Top + _originalBounds.Height / 2;

        // Calculate current angle from visual center to mouse position
        var currentAngle = Math.Atan2(current.Y - visualCenterY, current.X - visualCenterX);
        var deltaAngle = currentAngle - _originalAngle;

        // Update the selection rotation angle for visual display
        _selectionRotationAngle = deltaAngle;

        // The actual rotation center for transforming objects is in document coordinates
        var transformCenter = new PointMm(
            _originalBounds.Left + _originalBounds.Width / 2,
            _originalBounds.Top + _originalBounds.Height / 2);

        // Rotate strokes
        if (_originalStrokes != null)
        {
            var indices = _selectedIndices.ToList();
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _originalStrokes.Count) continue;

                var original = _originalStrokes[i];
                doc.Strokes[idx] = RotateStroke(original, transformCenter, deltaAngle);
            }
        }

        // Rotate paint wells (rotate their bounds around the selection center)
        if (_originalPaintWellBounds != null)
        {
            foreach (var (id, originalWellBounds) in _originalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    well.Bounds = RotateRect(originalWellBounds, transformCenter, deltaAngle);
                }
            }
        }

        // Don't update selection bounds during rotation - we use _originalBounds to keep the box size constant
        _requestRender();
    }

    private static Rect RotateRect(Rect original, PointMm center, double angle)
    {
        // Rotate the center of the rectangle around the selection center
        var rectCenterX = original.Left + original.Width / 2;
        var rectCenterY = original.Top + original.Height / 2;
        var rotatedCenter = RotatePoint(new PointMm(rectCenterX, rectCenterY), center, angle);

        // Keep the same width/height, just move the position
        return new Rect(
            rotatedCenter.X - original.Width / 2,
            rotatedCenter.Y - original.Height / 2,
            original.Width,
            original.Height);
    }

    private static LineStroke RotateStroke(LineStroke original, PointMm center, double angle)
    {
        var newA = RotatePoint(original.A, center, angle);
        var newB = RotatePoint(original.B, center, angle);
        return new LineStroke(newA, newB) 
        { 
            PaintWellId = original.PaintWellId, 
            GroupId = original.GroupId,
            IsGroupStart = original.IsGroupStart,
            IsGroupEnd = original.IsGroupEnd
        };
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

        // Also save paint well bounds
        _originalPaintWellBounds = _selectedPaintWellIds
            .Select(id => doc.PaintWells.FirstOrDefault(w => w.Id == id))
            .Where(w => w != null)
            .Select(w => (w!.Id, w.Bounds))
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
