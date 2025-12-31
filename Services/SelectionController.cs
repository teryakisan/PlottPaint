using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FontAwesome.WPF;
using NVSPlotter.Models;
using NVSPlotter.Services.Selection;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;
using Cursors = System.Windows.Input.Cursors;
using SelectionMode = NVSPlotter.Services.Selection.SelectionMode;
using SelectionHandle = NVSPlotter.Services.Selection.SelectionHandle;

namespace NVSPlotter.Services;

/// <summary>
/// Handles selection, move, resize, and rotation of strokes.
/// </summary>
public sealed class SelectionController
{
    private const double RULER_THICKNESS = 18.0;  // Must match the ruler offset used in rendering

    private readonly Canvas _canvas;
    private readonly Func<PlotDocument> _getDocument;
    private readonly Action _requestRender;
    
    // Encapsulated selection state
    private readonly SelectionState _state = new();
    
    // Visual element management
    private readonly SelectionVisuals _visuals;
    private readonly SelectionHitTester _hitTester = new();

    public bool HasSelection => _state.HasSelection;
    public bool IsActive => _state.IsActive;
    public IReadOnlySet<int> SelectedIndices => _state.SelectedIndices;
    public IReadOnlySet<Guid> SelectedPaintWellIds => _state.SelectedPaintWellIds;
    public Rect SelectionBounds => _state.SelectionBounds;

    public SelectionController(Canvas canvas, Func<PlotDocument> getDocument, Action requestRender)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        _visuals = new SelectionVisuals(canvas);
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
            var handle = _hitTester.HitTestHandle(point, GetBoundsForHitTest(), _state.RotationAngle, _state.Mode);
            if (handle != SelectionHandle.None)
            {
                BeginHandleOperation(handle, point);
                return true;
            }

            // Check if clicking inside selection bounds (move)
            if (_hitTester.IsPointInBounds(point, GetBoundsForHitTest(), _state.RotationAngle))
            {
                BeginMove(point);
                return true;
            }
        }

        // For paint well hit testing, convert canvas coordinates to document coordinates
        var docPoint = CanvasToDocument(point);

        // Check if clicking on a paint well first (they're visually larger/easier to click)
        var hitWell = _hitTester.HitTestPaintWell(docPoint, doc.PaintWells);
        if (hitWell != null)
        {
            if (shiftHeld)
            {
                // Toggle selection
                if (_state.SelectedPaintWellIds.Contains(hitWell.Id))
                    _state.SelectedPaintWellIds.Remove(hitWell.Id);
                else
                    _state.SelectedPaintWellIds.Add(hitWell.Id);
                    
                // Reset rotation when selection changes - pivot point needs to be recalculated
                _state.RotationAngle = 0;
            }
            else
            {
                // Single select - clear all selections first
                _state.SelectedIndices.Clear();
                _state.SelectedPaintWellIds.Clear();
                _state.SelectedPaintWellIds.Add(hitWell.Id);
                // Reset rotation for new selection
                _state.RotationAngle = 0;
            }
            UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
            _state.LogicalBounds = _state.SelectionBounds; // Initialize logical bounds for new selection
            _requestRender();
            return true;
        }

        // Check if clicking on a stroke (single select) - strokes use canvas coordinates directly
        // When clicking on a stroke, select all strokes in the same group (object)
        var hitIndex = _hitTester.HitTestStroke(point, doc.Strokes);
        if (hitIndex >= 0)
        {
            // Find all strokes in the same group as the clicked stroke
            var groupedIndices = _hitTester.FindGroupedStrokes(hitIndex, doc.Strokes);
            
            // Check if clicking on an already-selected grouped object
            // If so, do nothing (prevents indicator jumping on repeated clicks)
            var allGroupedAlreadySelected = groupedIndices.All(idx => _state.SelectedIndices.Contains(idx));
            if (allGroupedAlreadySelected && !shiftHeld)
            {
                // Already selected - do nothing, just return
                return true;
            }
            
            if (shiftHeld)
            {
                // Toggle selection of entire grouped object
                // Check if any of the grouped strokes are already selected
                var anySelected = groupedIndices.Any(idx => _state.SelectedIndices.Contains(idx));
                
                if (anySelected)
                {
                    // Remove all grouped strokes from selection
                    foreach (var idx in groupedIndices)
                    {
                        _state.SelectedIndices.Remove(idx);
                    }
                }
                else
                {
                    // Add all grouped strokes to selection
                    foreach (var idx in groupedIndices)
                    {
                        _state.SelectedIndices.Add(idx);
                    }
                }
                
                // Reset rotation when selection changes - pivot point needs to be recalculated
                _state.RotationAngle = 0;
            }
            else
            {
                // Single select entire grouped object - clear all selections first
                _state.SelectedIndices.Clear();
                _state.SelectedPaintWellIds.Clear();
                
                // Reset rotation for new selection
                _state.RotationAngle = 0;
                
                // Find and select all strokes in the same group
                foreach (var idx in groupedIndices)
                {
                    _state.SelectedIndices.Add(idx);
                }
            }
            UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
            _state.LogicalBounds = _state.SelectionBounds; // Initialize logical bounds for new selection
            _requestRender();
            return true;
        }

        // Start marquee selection
        if (!shiftHeld)
        {
            _state.SelectedIndices.Clear();
            _state.SelectedPaintWellIds.Clear();
            // Reset rotation for new selection
            _state.RotationAngle = 0;
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
        switch (_state.Mode)
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

            case SelectionMode.Skewing:
                UpdateSkew(point);
                return true;

            default:
                // When idle, check for hover over rotation handle to show highlight
                if (HasSelection)
                {
                    UpdateRotateHandleHover(point);
                }
                return false;
        }
    }

    /// <summary>
    /// Updates the rotation handle hover highlight based on mouse position.
    /// </summary>
    private void UpdateRotateHandleHover(PointMm point)
    {
        if (!_visuals.HasRotateHandle) return;

        // Check rotation handle hover
        var isHoveringRotate = _hitTester.IsHoveringRotateHandle(point, _visuals.RotateHandlePosition);
        
        // Check resize handle hover (edge handles are used for skew, corners for resize)
        var (isHoveringHandle, hoveredHandleIndex) = _hitTester.CheckResizeHandleHover(point, _visuals.ResizeHandlePositions);

        // Handle rotation hover (highest priority)
        if (isHoveringRotate)
        {
            // Clear any resize/skew hover first
            _visuals.ClearResizeHoverVisuals();
            _visuals.ClearSkewHoverVisuals();
            
            // Show rotation hover visuals
            _visuals.ShowRotateHoverGlow();
            _visuals.ShowRotateIcon(_state.SelectionBounds, isActive: false);
        }
        else if (_state.Mode != SelectionMode.Rotating)
        {
            // Hide the rotation hover visuals when not hovering and not rotating
            _visuals.ClearRotateHoverVisuals();
        }
        
        // Handle handle hover (check if it's an edge handle for skew or corner for resize)
        if (!isHoveringRotate && isHoveringHandle)
        {
            // Determine handle type from index
            // Handle order: TopLeft(0), TopCenter(1), TopRight(2), MiddleLeft(3), MiddleRight(4), 
            //               BottomLeft(5), BottomCenter(6), BottomRight(7)
            var isEdgeHandle = hoveredHandleIndex == 1 || hoveredHandleIndex == 3 || 
                               hoveredHandleIndex == 4 || hoveredHandleIndex == 6;
            
            if (isEdgeHandle)
            {
                // Edge handle = skew
                _visuals.ClearResizeHoverVisuals();
                _visuals.ShowSkewIcon(_state.SelectionBounds, isActive: false);
            }
            else
            {
                // Corner handle = resize
                _visuals.ClearSkewHoverVisuals();
                _visuals.ShowResizeHoverGlow(hoveredHandleIndex);
                _visuals.ShowResizeIcon(_state.SelectionBounds, isActive: false);
            }
        }
        else if (!isHoveringHandle)
        {
            if (_state.Mode != SelectionMode.Resizing)
                _visuals.ClearResizeHoverVisuals();
            if (_state.Mode != SelectionMode.Skewing)
                _visuals.ClearSkewHoverVisuals();
        }
        
        // During rotation, make the icon more transparent to indicate active rotation
        if (_state.Mode == SelectionMode.Rotating)
        {
            _visuals.SetRotateIconActive(true);
        }
        
        // During resizing, make the icon more transparent to indicate active resizing
        if (_state.Mode == SelectionMode.Resizing)
        {
            _visuals.SetResizeIconActive(true);
        }

        // During skewing, make the icon more transparent to indicate active skewing
        if (_state.Mode == SelectionMode.Skewing)
        {
            _visuals.SetSkewIconActive(true);
        }
    }
    
    /// <summary>
    /// Handles mouse up for selection tool.
    /// Returns the list of modified strokes if any changes were committed.
    /// </summary>
    public List<LineStroke>? HandleMouseUp(PointMm point)
    {
        List<LineStroke>? result = null;

        switch (_state.Mode)
        {
            case SelectionMode.MarqueeSelecting:
                CompleteMarquee(point);
                break;

            case SelectionMode.Moving:
            case SelectionMode.Resizing:
            case SelectionMode.Rotating:
            case SelectionMode.Skewing:
                result = CommitTransform();
                break;
        }

        _state.Mode = SelectionMode.Idle;
        _state.ActiveHandle = SelectionHandle.None;
        _visuals.RemoveMarquee();

        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        return result;
    }

    /// <summary>
    /// Cancels the current selection operation.
    /// </summary>
    public void Cancel()
    {
        if (_state.Mode == SelectionMode.Moving || _state.Mode == SelectionMode.Resizing || 
            _state.Mode == SelectionMode.Rotating || _state.Mode == SelectionMode.Skewing)
        {
            var doc = _getDocument();

            // Restore original strokes
            if (_state.OriginalStrokes != null)
            {
                foreach (var idx in _state.SelectedIndices.OrderBy(i => i))
                {
                    var originalIdx = _state.SelectedIndices.ToList().IndexOf(idx);
                    if (originalIdx >= 0 && originalIdx < _state.OriginalStrokes.Count)
                    {
                        doc.Strokes[idx] = _state.OriginalStrokes[originalIdx];
                    }
                }
            }

            // Restore original paint well bounds (stored in canvas coordinates, convert back to document coords)
            if (_state.OriginalPaintWellBounds != null)
            {
                foreach (var (id, originalBounds, originalRotation) in _state.OriginalPaintWellBounds)
                {
                    var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                    if (well != null)
                    {
                        well.Bounds = new Rect(
                            originalBounds.Left - RULER_THICKNESS,
                            originalBounds.Top - RULER_THICKNESS,
                            originalBounds.Width,
                            originalBounds.Height);
                        well.Rotation = originalRotation;
                    }
                }
            }
        }

        _state.Mode = SelectionMode.Idle;
        _state.ActiveHandle = SelectionHandle.None;
        _state.OriginalStrokes = null;
        _state.OriginalPaintWellBounds = null;
        _visuals.RemoveMarquee();

        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        _requestRender();
    }

    /// <summary>
    /// Clears the current selection.
    /// </summary>
    public void ClearSelection()
    {
        _state.SelectedIndices.Clear();
        _state.SelectedPaintWellIds.Clear();
        _state.SelectionBounds = Rect.Empty;
        _state.LogicalBounds = Rect.Empty;
        _state.RotationAngle = 0;
        _visuals.Clear();
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
        if (_state.SelectedIndices.Count > 0)
        {
            deletedStrokes = new List<(int Index, LineStroke Stroke)>();
            foreach (var idx in _state.SelectedIndices.OrderByDescending(i => i))
            {
                if (idx >= 0 && idx < doc.Strokes.Count)
                {
                    deletedStrokes.Add((idx, doc.Strokes[idx]));
                    doc.Strokes.RemoveAt(idx);
                }
            }
        }

        // Delete paint wells
        if (_state.SelectedPaintWellIds.Count > 0)
        {
            deletedPaintWells = new List<PaintWell>();
            foreach (var wellId in _state.SelectedPaintWellIds)
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

        _state.SelectedIndices.Clear();
        _state.SelectedPaintWellIds.Clear();
        _state.SelectionBounds = Rect.Empty;
        _requestRender();

        return (deletedStrokes?.Count > 0 ? deletedStrokes : null, 
                deletedPaintWells?.Count > 0 ? deletedPaintWells : null);
    }

    /// <summary>
    /// Gets the currently selected strokes.
    /// </summary>
    public List<LineStroke> GetSelectedStrokes()
    {
        var doc = _getDocument();
        return _state.SelectedIndices
            .OrderBy(i => i)
            .Where(i => i >= 0 && i < doc.Strokes.Count)
            .Select(i => doc.Strokes[i])
            .ToList();
    }

    /// <summary>
    /// Gets the currently selected paint wells.
    /// </summary>
    public List<PaintWell> GetSelectedPaintWells()
    {
        var doc = _getDocument();
        return _state.SelectedPaintWellIds
            .Select(id => doc.PaintWells.FirstOrDefault(w => w.Id == id))
            .Where(w => w != null)
            .Select(w => w!)
            .ToList();
    }

    /// <summary>
    /// Selects the strokes at the specified indices.
    /// </summary>
    public void SelectStrokes(IEnumerable<int> indices)
    {
        var doc = _getDocument();
        foreach (var idx in indices)
        {
            if (idx >= 0 && idx < doc.Strokes.Count)
            {
                _state.SelectedIndices.Add(idx);
            }
        }
        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        _requestRender();
    }

    /// <summary>
    /// Selects the paint wells with the specified IDs.
    /// </summary>
    public void SelectPaintWells(IEnumerable<Guid> ids)
    {
        var doc = _getDocument();
        foreach (var id in ids)
        {
            if (doc.PaintWells.Any(w => w.Id == id))
            {
                _state.SelectedPaintWellIds.Add(id);
            }
        }
        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        _requestRender();
    }

    /// <summary>
    /// Renders selection visuals (bounding box, handles) on the canvas.
    /// Call this from RenderAll after drawing strokes.
    /// </summary>
    public void RenderSelectionVisuals()
    {
        _visuals.Clear();

        if (!HasSelection) return;

        var doc = _getDocument();
        
        // Determine which bounds to use for the visual display:
        // - During rotation, use the original bounds to keep the box size constant
        // - When we have a rotation angle, use the logical bounds (unrotated reference frame)
        // - Otherwise, recalculate from current stroke positions
        Rect boundsToUse;
        if (_state.Mode == SelectionMode.Rotating && !_state.OriginalBounds.IsEmpty)
        {
            boundsToUse = _state.OriginalBounds;
        }
        else if (Math.Abs(_state.RotationAngle) > 0.001 && !_state.LogicalBounds.IsEmpty)
        {
            // Use logical bounds when we have a rotation - this is the unrotated reference frame
            boundsToUse = _state.LogicalBounds;
        }
        else
        {
            UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
            boundsToUse = _state.SelectionBounds;
        }

        if (boundsToUse.IsEmpty) return;

        // Delegate rendering to SelectionVisuals
        _visuals.RenderSelectionBox(boundsToUse, _state.RotationAngle, _state.Mode);
        
        // Store the rotation center for hit testing
        _state.RotationCenter = _visuals.SelectionCenterPosition;
    }

    /// <summary>
    /// Separates the selected strokes from their parent groups by assigning them new unique GroupIds.
    /// After separation, the selected strokes form a new standalone group (or individual strokes if only one).
    /// Returns true if any strokes were separated.
    /// </summary>
    /// <summary>
    /// Separates the selected strokes from their parent groups by assigning them new unique GroupIds.
    /// When strokes are removed from the middle of a group, three separate groups are created:
    /// 1. Left segment (strokes before the selection)
    /// 2. Separated center (the selected strokes)
    /// 3. Right segment (strokes after the selection)
    /// Returns true if any strokes were separated.
    /// </summary>
    public bool SeparateFromGroup()
    {
        if (_state.SelectedIndices.Count == 0) return false;

        var doc = _getDocument();

        // Group selected indices by their original GroupId
        var selectedByGroup = new Dictionary<Guid, List<int>>();

        foreach (var idx in _state.SelectedIndices)
        {
            if (idx < 0 || idx >= doc.Strokes.Count) continue;
            var stroke = doc.Strokes[idx];

            if (stroke.GroupId.HasValue)
            {
                if (!selectedByGroup.TryGetValue(stroke.GroupId.Value, out var list))
                {
                    list = new List<int>();
                    selectedByGroup[stroke.GroupId.Value] = list;
                }
                list.Add(idx);
            }
        }

        if (selectedByGroup.Count == 0) return false; // No grouped strokes to separate

        // Process each original group
        foreach (var kvp in selectedByGroup)
        {
            var originalGroupId = kvp.Key;
            var selectedIndicesInGroup = kvp.Value;

            // Find ALL strokes in this original group (not just selected ones)
            var allGroupIndices = new List<int>();
            for (int i = 0; i < doc.Strokes.Count; i++)
            {
                if (doc.Strokes[i].GroupId == originalGroupId)
                {
                    allGroupIndices.Add(i);
                }
            }

            // Sort indices to maintain stroke order
            allGroupIndices.Sort();
            var selectedSet = new HashSet<int>(selectedIndicesInGroup);

            // Partition into: before (left), selected (center), after (right)
            var leftIndices = new List<int>();
            var centerIndices = new List<int>();
            var rightIndices = new List<int>();

            bool foundFirstSelected = false;

            foreach (var idx in allGroupIndices)
            {
                if (selectedSet.Contains(idx))
                {
                    foundFirstSelected = true;
                    centerIndices.Add(idx);
                }
                else if (!foundFirstSelected)
                {
                    leftIndices.Add(idx);
                }
                else
                {
                    rightIndices.Add(idx);
                }
            }

            // Left segment gets new GroupId (if it has strokes)
            if (leftIndices.Count > 0)
            {
                var leftGroupId = leftIndices.Count > 1 ? Guid.NewGuid() : (Guid?)null;
                for (int i = 0; i < leftIndices.Count; i++)
                {
                    var idx = leftIndices[i];
                    var stroke = doc.Strokes[idx];
                    doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                    {
                        PaintWellId = stroke.PaintWellId,
                        GroupId = leftGroupId,
                        IsGroupStart = i == 0,
                        IsGroupEnd = i == leftIndices.Count - 1
                    };
                }
            }

            // Center (selected) segment gets new GroupId
            if (centerIndices.Count > 0)
            {
                var centerGroupId = centerIndices.Count > 1 ? Guid.NewGuid() : (Guid?)null;
                var orderedCenter = centerIndices.OrderBy(i => i).ToList();

                for (int i = 0; i < orderedCenter.Count; i++)
                {
                    var idx = orderedCenter[i];
                    var stroke = doc.Strokes[idx];
                    doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                    {
                        PaintWellId = stroke.PaintWellId,
                        GroupId = centerGroupId,
                        IsGroupStart = i == 0,
                        IsGroupEnd = i == orderedCenter.Count - 1
                    };
                }
            }

            // Right segment gets new GroupId (if it has strokes)
            if (rightIndices.Count > 0)
            {
                var rightGroupId = rightIndices.Count > 1 ? Guid.NewGuid() : (Guid?)null;
                for (int i = 0; i < rightIndices.Count; i++)
                {
                    var idx = rightIndices[i];
                    var stroke = doc.Strokes[idx];
                    doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                    {
                        PaintWellId = stroke.PaintWellId,
                        GroupId = rightGroupId,
                        IsGroupStart = i == 0,
                        IsGroupEnd = i == rightIndices.Count - 1
                    };
                }
            }
        }

        // Update the bounds after modification
        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        _state.LogicalBounds = _state.SelectionBounds;
        _state.RotationAngle = 0; // Reset rotation for new selection

        _requestRender();
        return true;
    }

    /// <summary>
    /// Checks if a stroke index is selected.
    /// </summary>
    public bool IsSelected(int index) => _state.SelectedIndices.Contains(index);

    /// <summary>
    /// Checks if a paint well is selected by its ID.
    /// </summary>
    public bool IsPaintWellSelected(Guid id) => _state.SelectedPaintWellIds.Contains(id);

    /// <summary>
    /// Gets the appropriate bounds to use for hit testing, accounting for rotation state.
    /// </summary>
    private Rect GetBoundsForHitTest()
    {
        if (_state.Mode == SelectionMode.Rotating && !_state.OriginalBounds.IsEmpty)
            return _state.OriginalBounds;
        if (Math.Abs(_state.RotationAngle) > 0.001 && !_state.LogicalBounds.IsEmpty)
            return _state.LogicalBounds;
        return _state.SelectionBounds;
    }


    // ===== PRIVATE METHODS =====

    private void UpdateSelectionBounds(List<LineStroke> strokes, List<PaintWell> paintWells)
    {
        if (_state.SelectedIndices.Count == 0 && _state.SelectedPaintWellIds.Count == 0)
        {
            _state.SelectionBounds = Rect.Empty;
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // Include stroke bounds (strokes are stored in canvas coordinates)
        foreach (var idx in _state.SelectedIndices)
        {
            if (idx < 0 || idx >= strokes.Count) continue;

            var stroke = strokes[idx];
            minX = Math.Min(minX, Math.Min(stroke.A.X, stroke.B.X));
            minY = Math.Min(minY, Math.Min(stroke.A.Y, stroke.B.Y));
            maxX = Math.Max(maxX, Math.Max(stroke.A.X, stroke.B.X));
            maxY = Math.Max(maxY, Math.Max(stroke.A.Y, stroke.B.Y));
        }

        // Include paint well bounds (paint wells are stored in document coordinates,
        // so we need to add ruler offset to convert to canvas coordinates for consistency)
        foreach (var wellId in _state.SelectedPaintWellIds)
        {
            var well = paintWells.FirstOrDefault(w => w.Id == wellId);
            if (well == null) continue;

            // Convert paint well bounds from document coords to canvas coords
            minX = Math.Min(minX, RULER_THICKNESS + well.Bounds.Left);
            minY = Math.Min(minY, RULER_THICKNESS + well.Bounds.Top);
            maxX = Math.Max(maxX, RULER_THICKNESS + well.Bounds.Right);
            maxY = Math.Max(maxY, RULER_THICKNESS + well.Bounds.Bottom);
        }

        if (minX <= maxX && minY <= maxY)
        {
            _state.SelectionBounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        }
        else
        {
            _state.SelectionBounds = Rect.Empty;
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
        _state.Mode = SelectionMode.MarqueeSelecting;
        _state.DragStart = start;

        _visuals.ShowMarquee(start);
        _canvas.CaptureMouse();
    }

    private void UpdateMarquee(PointMm current)
    {
        _visuals.UpdateMarquee(_state.DragStart, current);
    }

    private void CompleteMarquee(PointMm end)
    {
        var doc = _getDocument();
        var marqueeRect = new Rect(
            Math.Min(_state.DragStart.X, end.X),
            Math.Min(_state.DragStart.Y, end.Y),
            Math.Abs(end.X - _state.DragStart.X),
            Math.Abs(end.Y - _state.DragStart.Y)
        );

        // Select strokes that intersect the marquee
        for (int i = 0; i < doc.Strokes.Count; i++)
        {
            var stroke = doc.Strokes[i];
            if (SelectionHitTester.StrokeIntersectsRect(stroke, marqueeRect))
            {
                _state.SelectedIndices.Add(i);
            }
        }

        // Select paint wells that intersect the marquee
        foreach (var well in doc.PaintWells)
        {
            if (SelectionHitTester.PaintWellIntersectsRect(well, marqueeRect))
            {
                _state.SelectedPaintWellIds.Add(well.Id);
            }
        }

        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        _state.LogicalBounds = _state.SelectionBounds; // Initialize logical bounds for new selection
        _state.RotationAngle = 0; // Reset rotation - pivot point is recalculated for new selection
        _requestRender();
    }

    // ===== MOVE =====

    private void BeginMove(PointMm start)
    {
        _state.Mode = SelectionMode.Moving;
        _state.ActiveHandle = SelectionHandle.Body;
        _state.DragStart = start;
        
        // When we have a rotation, use _state.LogicalBounds as the reference for move operations
        // This ensures the rotated selection box stays aligned with the object
        _state.OriginalBounds = Math.Abs(_state.RotationAngle) > 0.001 && !_state.LogicalBounds.IsEmpty 
            ? _state.LogicalBounds 
            : _state.SelectionBounds;
        
        SaveOriginalStrokes();
        _canvas.CaptureMouse();
    }

    private void UpdateMove(PointMm current)
    {
        var dx = current.X - _state.DragStart.X;
        var dy = current.Y - _state.DragStart.Y;

        var doc = _getDocument();
        
        // Move strokes
        var indices = _state.SelectedIndices.ToList();
        for (int i = 0; i < indices.Count; i++)
        {
            var idx = indices[i];
            if (_state.OriginalStrokes == null || i >= _state.OriginalStrokes.Count) continue;

            var original = _state.OriginalStrokes[i];
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

        // Move paint wells (rotation is preserved during move)
        if (_state.OriginalPaintWellBounds != null)
        {
            foreach (var (id, originalBounds, originalRotation) in _state.OriginalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    // Original bounds are in canvas coordinates, convert back to document coords
                    well.Bounds = new Rect(
                        originalBounds.Left + dx - RULER_THICKNESS,
                        originalBounds.Top + dy - RULER_THICKNESS,
                        originalBounds.Width,
                        originalBounds.Height);
                    // Rotation is preserved during move (no change)
                }
            }
        }

        UpdateSelectionBounds(doc.Strokes, doc.PaintWells);
        
        // Always update logical bounds to match selection bounds during move
        // This ensures consistency whether we have rotation or not
        if (!_state.OriginalBounds.IsEmpty)
        {
            _state.LogicalBounds = new Rect(
                _state.OriginalBounds.Left + dx,
                _state.OriginalBounds.Top + dy,
                _state.OriginalBounds.Width,
                _state.OriginalBounds.Height);
        }
        else
        {
            _state.LogicalBounds = _state.SelectionBounds;
        }
        
        _requestRender();
    }

    // ===== RESIZE =====

    private void BeginHandleOperation(SelectionHandle handle, PointMm start)
    {
        _state.ActiveHandle = handle;
        _state.DragStart = start;
        _canvas.CaptureMouse();

        if (handle == SelectionHandle.Rotate)
        {
            _state.Mode = SelectionMode.Rotating;
            
            // Use logical bounds as the reference for rotation
            // These are now in canvas coordinates (same as _state.SelectionBounds)
            _state.OriginalBounds = _state.LogicalBounds.IsEmpty ? _state.SelectionBounds : _state.LogicalBounds;
            SaveOriginalStrokes();
            
            // Save the current rotation angle as the base for this rotation operation
            // This allows cumulative rotation across multiple drag operations
            _state.BaseRotationAngle = _state.RotationAngle;
            
            // Selection bounds are now in canvas coordinates, so no offset needed
            var visualCenterX = _state.OriginalBounds.Left + _state.OriginalBounds.Width / 2;
            var visualCenterY = _state.OriginalBounds.Top + _state.OriginalBounds.Height / 2;
            
            // Calculate initial angle from center to mouse position (both in canvas coords)
            _state.OriginalAngle = Math.Atan2(start.Y - visualCenterY, start.X - visualCenterX);
        }
        else if (SelectionHitTester.IsEdgeHandle(handle))
        {
            // Edge handles (TopCenter, BottomCenter, MiddleLeft, MiddleRight) trigger skew
            BeginSkew(handle, start);
        }
        else
        {
            // Corner handles trigger resize
            _state.Mode = SelectionMode.Resizing;
            
            // For resize after rotation, we need to work in the logical (unrotated) coordinate space
            // Use the logical bounds as our reference frame (now in canvas coordinates)
            _state.OriginalBounds = _state.LogicalBounds.IsEmpty ? _state.SelectionBounds : _state.LogicalBounds;
            SaveOriginalStrokes();
        }
    }

    private void UpdateResize(PointMm current)
    {
        if (_state.OriginalBounds.IsEmpty) return;

        var doc = _getDocument();

        // Calculate the center of the logical bounds (now in canvas coordinates)
        var centerX = _state.OriginalBounds.Left + _state.OriginalBounds.Width / 2;
        var centerY = _state.OriginalBounds.Top + _state.OriginalBounds.Height / 2;

        // Transform the mouse position into the unrotated coordinate space
        // This allows resizing to work along the rotated selection box axes
        PointMm transformedCurrent = current;
        if (Math.Abs(_state.RotationAngle) > 0.001)
        {
            // Rotate the mouse position backwards (negative angle) around the center
            // to get coordinates in the unrotated space
            var cos = Math.Cos(-_state.RotationAngle);
            var sin = Math.Sin(-_state.RotationAngle);
            var dx = current.X - centerX;
            var dy = current.Y - centerY;
            transformedCurrent = new PointMm(
                centerX + dx * cos - dy * sin,
                centerY + dx * sin + dy * cos
            );
        }

        // Get raw edge positions without normalization (in logical/unrotated space)
        double left = _state.OriginalBounds.Left;
        double top = _state.OriginalBounds.Top;
        double right = _state.OriginalBounds.Right;
        double bottom = _state.OriginalBounds.Bottom;

        // Selection bounds are now in canvas coordinates, so use transformed mouse position directly
        var adjustedCurrent = transformedCurrent;

        // Determine the anchor point (opposite corner/edge from the handle being dragged)
        // This anchor point should remain stationary in world space
        double anchorX = centerX;
        double anchorY = centerY;

        switch (_state.ActiveHandle)
        {
            case SelectionHandle.TopLeft:
                left = adjustedCurrent.X;
                top = adjustedCurrent.Y;
                anchorX = right;
                anchorY = bottom;
                break;
            case SelectionHandle.TopCenter:
                top = adjustedCurrent.Y;
                anchorY = bottom;
                break;
            case SelectionHandle.TopRight:
                right = adjustedCurrent.X;
                top = adjustedCurrent.Y;
                anchorX = left;
                anchorY = bottom;
                break;
            case SelectionHandle.MiddleLeft:
                left = adjustedCurrent.X;
                anchorX = right;
                break;
            case SelectionHandle.MiddleRight:
                right = adjustedCurrent.X;
                anchorX = left;
                break;
            case SelectionHandle.BottomLeft:
                left = adjustedCurrent.X;
                bottom = adjustedCurrent.Y;
                anchorX = right;
                anchorY = top;
                break;
            case SelectionHandle.BottomCenter:
                bottom = adjustedCurrent.Y;
                anchorY = top;
                break;
            case SelectionHandle.BottomRight:
                right = adjustedCurrent.X;
                bottom = adjustedCurrent.Y;
                anchorX = left;
                anchorY = top;
                break;
        }

        // Detect flip BEFORE normalizing
        bool flipH = left > right;
        bool flipV = top > bottom;

        // Now normalize for the visual bounds
        if (flipH) (left, right) = (right, left);
        if (flipV) (top, bottom) = (bottom, top);

        var newLogicalBounds = new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        if (newLogicalBounds.Width < 1 || newLogicalBounds.Height < 1) return;

        // Calculate the anchor point position in the NEW logical bounds
        // The anchor's relative position should remain the same
        double anchorRelX = _state.OriginalBounds.Width > 0 ? (anchorX - _state.OriginalBounds.Left) / _state.OriginalBounds.Width : 0.5;
        double anchorRelY = _state.OriginalBounds.Height > 0 ? (anchorY - _state.OriginalBounds.Top) / _state.OriginalBounds.Height : 0.5;
        
        // Apply flip to anchor relative position
        if (flipH) anchorRelX = 1.0 - anchorRelX;
        if (flipV) anchorRelY = 1.0 - anchorRelY;
        
        double newAnchorX = newLogicalBounds.Left + anchorRelX * newLogicalBounds.Width;
        double newAnchorY = newLogicalBounds.Top + anchorRelY * newLogicalBounds.Height;

        // Calculate where the anchor point ends up in world space after rotation
        var originalAnchorWorld = Math.Abs(_state.RotationAngle) > 0.001
            ? GeometryHelpers.RotatePoint(new PointMm(anchorX, anchorY), new PointMm(centerX, centerY), _state.RotationAngle)
            : new PointMm(anchorX, anchorY);

        var newLogicalCenterX = newLogicalBounds.Left + newLogicalBounds.Width / 2;
        var newLogicalCenterY = newLogicalBounds.Top + newLogicalBounds.Height / 2;
        
        var newAnchorWorld = Math.Abs(_state.RotationAngle) > 0.001
            ? GeometryHelpers.RotatePoint(new PointMm(newAnchorX, newAnchorY), new PointMm(newLogicalCenterX, newLogicalCenterY), _state.RotationAngle)
            : new PointMm(newAnchorX, newAnchorY);

        // Calculate the offset needed to keep the anchor point in the same world position
        double offsetX = originalAnchorWorld.X - newAnchorWorld.X;
        double offsetY = originalAnchorWorld.Y - newAnchorWorld.Y;

        // Adjust the new logical bounds to compensate
        var adjustedNewLogicalBounds = new Rect(
            newLogicalBounds.Left + offsetX,
            newLogicalBounds.Top + offsetY,
            newLogicalBounds.Width,
            newLogicalBounds.Height
        );

        var adjustedNewLogicalCenterX = adjustedNewLogicalBounds.Left + adjustedNewLogicalBounds.Width / 2;
        var adjustedNewLogicalCenterY = adjustedNewLogicalBounds.Top + adjustedNewLogicalBounds.Height / 2;
        var originalCenter = new PointMm(centerX, centerY);
        var newCenter = new PointMm(adjustedNewLogicalCenterX, adjustedNewLogicalCenterY);

        // Transform strokes: unrotate original -> scale -> re-rotate around NEW center
        if (_state.OriginalStrokes != null)
        {
            var indices = _state.SelectedIndices.ToList();
            
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _state.OriginalStrokes.Count) continue;

                var original = _state.OriginalStrokes[i];
                
                // Step 1: Unrotate the original stroke back to logical space around the ORIGINAL center
                LineStroke unrotated;
                if (Math.Abs(_state.RotationAngle) > 0.001)
                {
                    unrotated = GeometryHelpers.RotateStroke(original, originalCenter, -_state.RotationAngle);
                }
                else
                {
                    unrotated = original;
                }
                
                // Step 2: Scale in logical space (using unadjusted bounds for scaling ratios)
                var scaled = GeometryHelpers.ScaleAndFlipStroke(unrotated, _state.OriginalBounds, newLogicalBounds, flipH, flipV);
                
                // Step 3: Translate by the offset to keep anchor stationary
                scaled = GeometryHelpers.TranslateStroke(scaled, offsetX, offsetY);
                
                // Step 4: Re-rotate around the adjusted NEW center
                if (Math.Abs(_state.RotationAngle) > 0.001)
                {
                    scaled = GeometryHelpers.RotateStroke(scaled, newCenter, _state.RotationAngle);
                }
                
                doc.Strokes[idx] = scaled;
            }
        }

        // Transform paint wells: unrotate -> scale -> translate -> re-rotate
        if (_state.OriginalPaintWellBounds != null)
        {
            foreach (var (id, originalWellBounds, originalRotation) in _state.OriginalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    // Step 1: Unrotate around ORIGINAL center
                    Rect unrotated;
                    if (Math.Abs(_state.RotationAngle) > 0.001)
                    {
                        unrotated = GeometryHelpers.RotateRect(originalWellBounds, originalCenter, -_state.RotationAngle);
                    }
                    else
                    {
                        unrotated = originalWellBounds;
                    }
                    
                    // Step 2: Scale
                    var scaled = GeometryHelpers.ScaleAndFlipRect(unrotated, _state.OriginalBounds, newLogicalBounds, flipH, flipV);
                    
                    
                    // Step 3: Translate
                    scaled = new Rect(scaled.Left + offsetX, scaled.Top + offsetY, scaled.Width, scaled.Height);
                    
                    // Step 4: Re-rotate around adjusted NEW center
                    if (Math.Abs(_state.RotationAngle) > 0.001)
                    {
                        scaled = GeometryHelpers.RotateRect(scaled, newCenter, _state.RotationAngle);
                    }
                    
                    // Convert back to document coordinates for storage
                    well.Bounds = new Rect(
                        scaled.Left - RULER_THICKNESS,
                        scaled.Top - RULER_THICKNESS,
                        scaled.Width,
                        scaled.Height);
                    // Rotation is preserved during resize (same as original)
                    well.Rotation = originalRotation;
                }
            }
        }

        // Update the logical bounds for rendering (use the adjusted bounds)
        _state.LogicalBounds = adjustedNewLogicalBounds;
        _requestRender();
    }

    private Rect CalculateNewBounds(PointMm current)
    {
        var left = _state.OriginalBounds.Left;
        var top = _state.OriginalBounds.Top;
        var right = _state.OriginalBounds.Right;
        var bottom = _state.OriginalBounds.Bottom;

        switch (_state.ActiveHandle)
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

    // ===== ROTATE =====

    private void UpdateRotate(PointMm current)
    {
        if (_state.OriginalBounds.IsEmpty) return;

        var doc = _getDocument();

        // Selection bounds are now in canvas coordinates, so no offset needed
        var centerX = _state.OriginalBounds.Left + _state.OriginalBounds.Width / 2;
        var centerY = _state.OriginalBounds.Top + _state.OriginalBounds.Height / 2;

        // Calculate current angle from center to mouse position (both in canvas coords)
        var currentAngle = Math.Atan2(current.Y - centerY, current.X - centerX);
        var deltaAngle = currentAngle - _state.OriginalAngle;

        // Update the selection rotation angle for visual display
        // Add base angle to support cumulative rotation across multiple operations
        _state.RotationAngle = _state.BaseRotationAngle + deltaAngle;

        // The transform center is now in canvas coordinates
        var transformCenter = new PointMm(centerX, centerY);

        // Calculate the total rotation from the original strokes (base + delta)
        var totalRotation = _state.RotationAngle;

        // Rotate strokes from their original positions
        if (_state.OriginalStrokes != null)
        {
            var indices = _state.SelectedIndices.ToList();
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _state.OriginalStrokes.Count) continue;

                var original = _state.OriginalStrokes[i];
                
                // First unrotate from base angle, then rotate to new total angle
                // This is equivalent to rotating by deltaAngle from current position
                var unrotated = Math.Abs(_state.BaseRotationAngle) > 0.001 
                    ? GeometryHelpers.RotateStroke(original, transformCenter, -_state.BaseRotationAngle) 
                    : original;
                doc.Strokes[idx] = GeometryHelpers.RotateStroke(unrotated, transformCenter, totalRotation);
            }
        }

        // Rotate paint wells (rotate their bounds around the selection center and set rotation angle)
        if (_state.OriginalPaintWellBounds != null)
        {
            foreach (var (id, originalWellBounds, originalRotation) in _state.OriginalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    // Original well bounds are saved in canvas coordinates
                    // First unrotate from base angle, then rotate to new total angle
                    var unrotated = Math.Abs(_state.BaseRotationAngle) > 0.001 
                        ? GeometryHelpers.RotateRect(originalWellBounds, transformCenter, -_state.BaseRotationAngle) 
                        : originalWellBounds;
                    var rotated = GeometryHelpers.RotateRect(unrotated, transformCenter, totalRotation);
                    
                    // Convert back to document coordinates for storage
                    well.Bounds = new Rect(
                        rotated.Left - RULER_THICKNESS,
                        rotated.Top - RULER_THICKNESS,
                        rotated.Width,
                        rotated.Height);
                    
                    // Update the paint well's rotation angle
                    // The delta from base angle is the amount we've rotated in this operation
                    well.Rotation = originalRotation + deltaAngle;
                }
            }
        }

        // The logical bounds stay the same during rotation - only the angle changes
        // Don't update _state.LogicalBounds here

        _requestRender();
    }

    // ===== SKEW =====

    private void BeginSkew(SelectionHandle handle, PointMm start)
    {
        _state.Mode = SelectionMode.Skewing;
        _state.ActiveHandle = handle;
        _state.DragStart = start;
        
        // Use logical bounds as the reference for skew
        _state.OriginalBounds = _state.LogicalBounds.IsEmpty ? _state.SelectionBounds : _state.LogicalBounds;
        SaveOriginalStrokes();
        _canvas.CaptureMouse();
    }

    private void UpdateSkew(PointMm current)
    {
        if (_state.OriginalBounds.IsEmpty) return;

        var doc = _getDocument();

        // Calculate mouse delta from drag start
        var dx = current.X - _state.DragStart.X;
        var dy = current.Y - _state.DragStart.Y;

        // Calculate skew factors based on which edge handle is being dragged
        // Skew factor is proportional to the mouse movement relative to bounds size
        double skewX = 0;
        double skewY = 0;

        var boundsWidth = Math.Max(1, _state.OriginalBounds.Width);
        var boundsHeight = Math.Max(1, _state.OriginalBounds.Height);

        switch (_state.ActiveHandle)
        {
            case SelectionHandle.TopCenter:
            case SelectionHandle.BottomCenter:
                // Horizontal skew: drag left/right to shear horizontally
                skewX = dx / boundsHeight;
                // Invert for bottom handle
                if (_state.ActiveHandle == SelectionHandle.BottomCenter)
                    skewX = -skewX;
                break;

            case SelectionHandle.MiddleLeft:
            case SelectionHandle.MiddleRight:
                // Vertical skew: drag up/down to shear vertically
                skewY = dy / boundsWidth;
                // Invert for right handle
                if (_state.ActiveHandle == SelectionHandle.MiddleRight)
                    skewY = -skewY;
                break;
        }

        // Apply skew to strokes
        if (_state.OriginalStrokes != null)
        {
            var indices = _state.SelectedIndices.ToList();
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _state.OriginalStrokes.Count) continue;

                var original = _state.OriginalStrokes[i];
                doc.Strokes[idx] = GeometryHelpers.SkewStroke(original, _state.OriginalBounds, skewX, skewY);
            }
        }

        // Apply skew to paint wells
        if (_state.OriginalPaintWellBounds != null)
        {
            foreach (var (id, originalWellBounds, originalRotation) in _state.OriginalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    var skewed = GeometryHelpers.SkewRect(originalWellBounds, _state.OriginalBounds, skewX, skewY);
                    
                    // Convert back to document coordinates for storage
                    well.Bounds = new Rect(
                        skewed.Left - RULER_THICKNESS,
                        skewed.Top - RULER_THICKNESS,
                        skewed.Width,
                        skewed.Height);
                    well.Rotation = originalRotation;
                }
            }
        }

        // Store skew values
        _state.SkewX = skewX;
        _state.SkewY = skewY;

        _requestRender();
    }

    // ===== HELPERS =====

    private void SaveOriginalStrokes()
    {
        var doc = _getDocument();
        _state.OriginalStrokes = _state.SelectedIndices
            .OrderBy(i => i)
            .Where(i => i >= 0 && i < doc.Strokes.Count)
            .Select(i => doc.Strokes[i])
            .ToList();

        // Save paint well bounds converted to canvas coordinates for consistency
        // (paint wells are stored in document coords, we add ruler offset to match canvas space)
        // Also save the rotation angle
        _state.OriginalPaintWellBounds = _state.SelectedPaintWellIds
            .Select(id => doc.PaintWells.FirstOrDefault(w => w.Id == id))
            .Where(w => w != null)
            .Select(w => (w!.Id, new Rect(
                RULER_THICKNESS + w.Bounds.Left,
                RULER_THICKNESS + w.Bounds.Top,
                w.Bounds.Width,
                w.Bounds.Height), w.Rotation))
            .ToList();
    }

    private List<LineStroke>? CommitTransform()
    {
        // Return the new strokes for undo purposes
        if (_state.OriginalStrokes == null || _state.SelectedIndices.Count == 0)
            return null;

        var doc = _getDocument();
        return _state.SelectedIndices
            .OrderBy(i => i)
            .Where(i => i >= 0 && i < doc.Strokes.Count)
            .Select(i => doc.Strokes[i])
            .ToList();
    }
    

}


