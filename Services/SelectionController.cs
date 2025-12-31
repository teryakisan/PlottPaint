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
    private const double ROTATE_HANDLE_SIZE = 18.0; // Visual size of rotation handle (larger for visibility)
    private const double HANDLE_HIT_RADIUS = 20.0; // Hit test radius for handles (generous for easier clicking)
    private const double ROTATE_HANDLE_HIT_RADIUS = 25.0; // Hit test radius for rotation handle (even more generous)
    private const double ROTATE_HANDLE_OFFSET = 45.0; // Distance from top of selection to rotation handle (outside the box)
    private const double RULER_THICKNESS = 18.0;  // Must match the ruler offset used in rendering
    private const double SELECTION_PADDING = 20.0; // Padding between selection handles and content
    private const double PAINT_WELL_HIT_MARGIN = 10.0; // Extra margin around paint well bounds for easier clicking
    private const double MAX_ICON_SIZE = 60.0; // Maximum size for hover icons
    private const double MIN_ICON_SIZE = 20.0; // Minimum size for hover icons
    private const double ICON_SIZE_RATIO = 0.5; // Icon size as a ratio of the smaller selection dimension

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
    private double _baseRotationAngle; // Rotation angle at the start of the current rotation operation
    private Point _selectionRotationCenter;
    
    // Logical bounds tracking - the unrotated bounding box that we scale relative to
    // This is separate from _selectionBounds which is the axis-aligned bounds of the actual (rotated) strokes
    private Rect _logicalBounds;

    // Preview visuals
    private Rectangle? _marqueeRect;
    private Path? _boundsPath;  // Changed from Rectangle to Path for proper rotation
    private readonly List<Rectangle> _handles = new();
    private Ellipse? _rotateHandle;
    private Ellipse? _rotateHandleHoverRing; // Dashed green ring shown when hovering over rotation handle
    private ImageAwesome? _rotateIcon; // FontAwesome rotation icon shown on hover at center
    private Line? _rotateConnector;
    private Point _rotateHandlePosition; // Store position for hover detection
    private Point _selectionCenterPosition; // Store center for icon placement
    
    // Resize handle hover visuals
    private Border? _resizeHandleHoverRing; // Highlight ring for resize handles
    private ImageAwesome? _resizeIcon; // FontAwesome arrows-alt icon shown on hover
    private readonly List<Point> _resizeHandlePositions = new(); // Store positions of all resize handles

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
                    
                // Reset rotation when selection changes - pivot point needs to be recalculated
                _selectionRotationAngle = 0;
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
            _logicalBounds = _selectionBounds; // Initialize logical bounds for new selection
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
                
                // Reset rotation when selection changes - pivot point needs to be recalculated
                _selectionRotationAngle = 0;
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
            _logicalBounds = _selectionBounds; // Initialize logical bounds for new selection
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
        if (_rotateHandle == null) return;

        // Check rotation handle hover
        var rotateDistance = Distance(point, new PointMm(_rotateHandlePosition.X, _rotateHandlePosition.Y));
        var isHoveringRotate = rotateDistance <= ROTATE_HANDLE_HIT_RADIUS;
        
        // Check resize handle hover
        var (isHoveringResize, hoveredResizeHandleIndex) = CheckResizeHandleHover(point);

        // Handle rotation hover
        if (isHoveringRotate && _rotateHandleHoverRing == null)
        {
            // Clear any resize hover first
            ClearResizeHoverVisuals();
            
            // Create a large radiating glow effect - using LimeGreen (50,205,50) for consistency with line start indicators
            const double GLOW_SIZE = 70; // Large glow size for high visibility
            
            // Create radial gradient that fades from bright LimeGreen center to fully transparent edge
            var glowBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            // Use LimeGreen (50,205,50) - same as line start indicators for visual consistency
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 50, 205, 50), 0.0));   // Solid LimeGreen core
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 50, 205, 50), 0.15)); // Still very solid
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(160, 50, 205, 50), 0.3));  // Starting to fade
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, 50, 205, 50), 0.5));   // Mid fade
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(40, 50, 205, 50), 0.7));   // Mostly faded
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(10, 50, 205, 50), 0.85));  // Nearly transparent
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 50, 205, 50), 1.0));    // Fully transparent at edge
            
            _rotateHandleHoverRing = new Ellipse
            {
                Width = GLOW_SIZE,
                Height = GLOW_SIZE,
                Fill = glowBrush,
                Stroke = Brushes.Transparent,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_rotateHandleHoverRing, _rotateHandlePosition.X - GLOW_SIZE / 2);
            Canvas.SetTop(_rotateHandleHoverRing, _rotateHandlePosition.Y - GLOW_SIZE / 2);
            Panel.SetZIndex(_rotateHandleHoverRing, 18);
            _canvas.Children.Add(_rotateHandleHoverRing);
            
            // Show the FontAwesome refresh icon at the selection center, scaled to selection size
            var iconSize = CalculateIconSize();
            _rotateIcon = new ImageAwesome
            {
                Icon = FontAwesomeIcon.Refresh,
                Width = iconSize,
                Height = iconSize,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 30, 144, 255)), // Semi-transparent DodgerBlue (never solid)
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_rotateIcon, _selectionCenterPosition.X - iconSize / 2);
            Canvas.SetTop(_rotateIcon, _selectionCenterPosition.Y - iconSize / 2);
            Panel.SetZIndex(_rotateIcon, 3); // Above grid (2) but below strokes (4)
            _canvas.Children.Add(_rotateIcon);
        }
        else if (!isHoveringRotate && _mode != SelectionMode.Rotating)
        {
            // Hide the rotation hover visuals when not hovering and not rotating
            ClearRotateHoverVisuals();
        }
        
        // Handle resize hover (only if not hovering rotate)
        if (!isHoveringRotate && isHoveringResize && _resizeHandleHoverRing == null)
        {
            // Create a large radiating glow effect - using LimeGreen (50,205,50) for consistency
            var handlePos = _resizeHandlePositions[hoveredResizeHandleIndex];
            const double GLOW_SIZE = 55; // Large glow size for high visibility
            
            // Create radial gradient that fades from bright LimeGreen center to fully transparent edge
            var glowBrush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            // Use LimeGreen (50,205,50) - same as line start indicators for visual consistency
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 50, 205, 50), 0.0));   // Solid LimeGreen core
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, 50, 205, 50), 0.15)); // Still very solid
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(160, 50, 205, 50), 0.3));  // Starting to fade
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, 50, 205, 50), 0.5));   // Mid fade
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(40, 50, 205, 50), 0.7));   // Mostly faded
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(10, 50, 205, 50), 0.85));  // Nearly transparent
            glowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 50, 205, 50), 1.0));    // Fully transparent at edge
            
            // Use an Ellipse for proper radial gradient rendering
            var glowEllipse = new Ellipse
            {
                Width = GLOW_SIZE,
                Height = GLOW_SIZE,
                Fill = glowBrush,
                Stroke = Brushes.Transparent,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            
            // Wrap in a border so we can reuse the existing field type
            _resizeHandleHoverRing = new Border
            {
                Width = GLOW_SIZE,
                Height = GLOW_SIZE,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                IsHitTestVisible = false,
                Child = glowEllipse
            };
            Canvas.SetLeft(_resizeHandleHoverRing, handlePos.X - GLOW_SIZE / 2);
            Canvas.SetTop(_resizeHandleHoverRing, handlePos.Y - GLOW_SIZE / 2);
            Panel.SetZIndex(_resizeHandleHoverRing, 18);
            _canvas.Children.Add(_resizeHandleHoverRing);
            
            // Show the FontAwesome arrows-alt icon at the selection center, scaled to selection size
            var iconSize = CalculateIconSize();
            _resizeIcon = new ImageAwesome
            {
                Icon = FontAwesomeIcon.ArrowsAlt,
                Width = iconSize,
                Height = iconSize,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 30, 144, 255)), // Semi-transparent DodgerBlue (never solid)
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_resizeIcon, _selectionCenterPosition.X - iconSize / 2);
            Canvas.SetTop(_resizeIcon, _selectionCenterPosition.Y - iconSize / 2);
            Panel.SetZIndex(_resizeIcon, 3); // Above grid (2) but below strokes (4)
            _canvas.Children.Add(_resizeIcon);
        }
        else if (!isHoveringResize && _mode != SelectionMode.Resizing)
        {
            // Hide the resize hover visuals when not hovering and not resizing
            ClearResizeHoverVisuals();
        }
        
        // During rotation, make the icon more transparent to indicate active rotation
        if (_mode == SelectionMode.Rotating && _rotateIcon != null)
        {
            _rotateIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 30, 144, 255)); // More transparent during rotation (never solid)
        }
        
        // During resizing, make the icon more transparent to indicate active resizing
        if (_mode == SelectionMode.Resizing && _resizeIcon != null)
        {
            _resizeIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 30, 144, 255)); // More transparent during resizing (never solid)
        }
    }
    
    /// <summary>
    /// Checks if the mouse is hovering over any resize handle.
    /// </summary>
    private (bool IsHovering, int HandleIndex) CheckResizeHandleHover(PointMm point)
    {
        for (int i = 0; i < _resizeHandlePositions.Count; i++)
        {
            var handlePos = _resizeHandlePositions[i];
            var distance = Distance(point, new PointMm(handlePos.X, handlePos.Y));
            if (distance <= HANDLE_HIT_RADIUS)
            {
                return (true, i);
            }
        }
        return (false, -1);
    }
    
    /// <summary>
    /// Clears the rotation hover visuals.
    /// </summary>
    private void ClearRotateHoverVisuals()
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
    /// Clears the resize hover visuals.
    /// </summary>
    private void ClearResizeHoverVisuals()
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
        _logicalBounds = Rect.Empty;
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
    /// Gets the currently selected strokes.
    /// </summary>
    public List<LineStroke> GetSelectedStrokes()
    {
        var doc = _getDocument();
        return _selectedIndices
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
        return _selectedPaintWellIds
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
                _selectedIndices.Add(idx);
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
                _selectedPaintWellIds.Add(id);
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
        RemoveSelectionVisuals();

        if (!HasSelection) return;

        var doc = _getDocument();
        
        // Determine which bounds to use for the visual display:
        // - During rotation, use the original bounds to keep the box size constant
        // - When we have a rotation angle, use the logical bounds (unrotated reference frame)
        // - Otherwise, recalculate from current stroke positions
        Rect boundsToUse;
        if (_mode == SelectionMode.Rotating && !_originalBounds.IsEmpty)
        {
            boundsToUse = _originalBounds;
        }
        else if (Math.Abs(_selectionRotationAngle) > 0.001 && !_logicalBounds.IsEmpty)
        {
            // Use logical bounds when we have a rotation - this is the unrotated reference frame
            boundsToUse = _logicalBounds;
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
        
        // Perpendicular direction pointing OUTWARD from the box (away from center)
        // In screen coordinates (Y down), we need to rotate 90° clockwise to point "up" relative to the edge
        double perpX = topEdgeDy / topEdgeLength;   // Flipped sign to point outward
        double perpY = -topEdgeDx / topEdgeLength;  // Flipped sign to point outward
        
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
        
        // Store the rotation center for hit testing
        _selectionRotationCenter = new Point(centerX, centerY);
        
        // If we're actively rotating, show the icon at the center (more transparent), scaled to selection size
        if (_mode == SelectionMode.Rotating && _rotateIcon == null)
        {
            var iconSize = CalculateIconSize();
            _rotateIcon = new ImageAwesome
            {
                Icon = FontAwesomeIcon.Refresh,
                Width = iconSize,
                Height = iconSize,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 30, 144, 255)), // More transparent during rotation (never solid)
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_rotateIcon, centerX - iconSize / 2);
            Canvas.SetTop(_rotateIcon, centerY - iconSize / 2);
            Panel.SetZIndex(_rotateIcon, 3); // Above grid (2) but below strokes (4)
            _canvas.Children.Add(_rotateIcon);
        }
        
        // If we're actively resizing, show the resize icon at the center (more transparent), scaled to selection size
        if (_mode == SelectionMode.Resizing && _resizeIcon == null)
        {
            var iconSize = CalculateIconSize();
            _resizeIcon = new ImageAwesome
            {
                Icon = FontAwesomeIcon.ArrowsAlt,
                Width = iconSize,
                Height = iconSize,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 30, 144, 255)), // More transparent during resizing (never solid)
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_resizeIcon, centerX - iconSize / 2);
            Canvas.SetTop(_resizeIcon, centerY - iconSize / 2);
            Panel.SetZIndex(_resizeIcon, 3); // Above grid (2) but below strokes (4)
            _canvas.Children.Add(_resizeIcon);
        }
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
        if (_selectedIndices.Count == 0) return false;

        var doc = _getDocument();

        // Group selected indices by their original GroupId
        var selectedByGroup = new Dictionary<Guid, List<int>>();

        foreach (var idx in _selectedIndices)
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
        _logicalBounds = _selectionBounds;
        _selectionRotationAngle = 0; // Reset rotation for new selection

        _requestRender();
        return true;
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
        
        // Store position for hover detection
        _resizeHandlePositions.Add(new Point(x, y));
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
        _resizeHandlePositions.Clear();

        if (_rotateHandle != null)
        {
            _canvas.Children.Remove(_rotateHandle);
            _rotateHandle = null;
        }

        if (_rotateIcon != null)
        {
            _canvas.Children.Remove(_rotateIcon);
            _rotateIcon = null;
        }

        if (_rotateHandleHoverRing != null)
        {
            _canvas.Children.Remove(_rotateHandleHoverRing);
            _rotateHandleHoverRing = null;
        }

        if (_rotateConnector != null)
        {
            _canvas.Children.Remove(_rotateConnector);
            _rotateConnector = null;
        }
        
        // Clear resize hover visuals
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
        // Use logical bounds when we have a rotation, otherwise use selection bounds
        Rect boundsToUse;
        if (_mode == SelectionMode.Rotating && !_originalBounds.IsEmpty)
        {
            boundsToUse = _originalBounds;
        }
        else if (Math.Abs(_selectionRotationAngle) > 0.001 && !_logicalBounds.IsEmpty)
        {
            boundsToUse = _logicalBounds;
        }
        else
        {
            boundsToUse = _selectionBounds;
        }
            
        if (boundsToUse.IsEmpty) return SelectionHandle.None;
        
        // Don't allow clicking the rotation handle while already rotating
        // This prevents the second click from disrupting the selection box orientation
        if (_mode == SelectionMode.Rotating)
            return SelectionHandle.None;

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
        // Perpendicular pointing OUTWARD (same direction as in RenderSelectionVisuals)
        double perpX = topEdgeLength > 0 ? topEdgeDy / topEdgeLength : 0;
        double perpY = topEdgeLength > 0 ? -topEdgeDx / topEdgeLength : -1;
        var rotateHandlePos = new Point(topCenter.X + perpX * ROTATE_HANDLE_OFFSET, topCenter.Y + perpY * ROTATE_HANDLE_OFFSET);

        // Check rotate handle first - use larger hit radius for easier clicking
        if (Distance(point, new PointMm(rotateHandlePos.X, rotateHandlePos.Y)) <= ROTATE_HANDLE_HIT_RADIUS)
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
        // Use logical bounds when we have a rotation
        Rect boundsToUse;
        if (_mode == SelectionMode.Rotating && !_originalBounds.IsEmpty)
        {
            boundsToUse = _originalBounds;
        }
        else if (Math.Abs(_selectionRotationAngle) > 0.001 && !_logicalBounds.IsEmpty)
        {
            boundsToUse = _logicalBounds;
        }
        else
        {
            boundsToUse = bounds;
        }
            
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
        _logicalBounds = _selectionBounds; // Initialize logical bounds for new selection
        _selectionRotationAngle = 0; // Reset rotation - pivot point is recalculated for new selection
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
        
        // When we have a rotation, use _logicalBounds as the reference for move operations
        // This ensures the rotated selection box stays aligned with the object
        _originalBounds = Math.Abs(_selectionRotationAngle) > 0.001 && !_logicalBounds.IsEmpty 
            ? _logicalBounds 
            : _selectionBounds;
        
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
        
        // When we have a rotation, also update the logical bounds by the same delta
        // This ensures the rotated selection box follows the object during move
        if (Math.Abs(_selectionRotationAngle) > 0.001 && !_originalBounds.IsEmpty)
        {
            _logicalBounds = new Rect(
                _originalBounds.Left + dx,
                _originalBounds.Top + dy,
                _originalBounds.Width,
                _originalBounds.Height);
        }
        
        _requestRender();
    }

    // ===== RESIZE =====

    private void BeginHandleOperation(SelectionHandle handle, PointMm start)
    {
        _activeHandle = handle;
        _dragStart = start;
        _canvas.CaptureMouse();

        if (handle == SelectionHandle.Rotate)
        {
            _mode = SelectionMode.Rotating;
            
            // Use logical bounds as the reference for rotation
            _originalBounds = _logicalBounds.IsEmpty ? _selectionBounds : _logicalBounds;
            SaveOriginalStrokes();
            
            // Save the current rotation angle as the base for this rotation operation
            // This allows cumulative rotation across multiple drag operations
            _baseRotationAngle = _selectionRotationAngle;
            
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
            
            // For resize after rotation, we need to work in the logical (unrotated) coordinate space
            // Use the logical bounds as our reference frame
            _originalBounds = _logicalBounds.IsEmpty ? _selectionBounds : _logicalBounds;
            SaveOriginalStrokes();
        }
    }

    private void UpdateResize(PointMm current)
    {
        if (_originalBounds.IsEmpty) return;

        var doc = _getDocument();

        // Calculate the center of the logical bounds (used for rotation)
        var offset = _selectedPaintWellIds.Count > 0 ? RULER_THICKNESS : 0;
        var originalLogicalCenterX = _originalBounds.Left + _originalBounds.Width / 2;
        var originalLogicalCenterY = _originalBounds.Top + _originalBounds.Height / 2;
        var visualCenterX = offset + originalLogicalCenterX;
        var visualCenterY = offset + originalLogicalCenterY;

        // Transform the mouse position into the unrotated coordinate space
        // This allows resizing to work along the rotated selection box axes
        PointMm transformedCurrent = current;
        if (Math.Abs(_selectionRotationAngle) > 0.001)
        {
            // Rotate the mouse position backwards (negative angle) around the center
            // to get coordinates in the unrotated space
            var cos = Math.Cos(-_selectionRotationAngle);
            var sin = Math.Sin(-_selectionRotationAngle);
            var dx = current.X - visualCenterX;
            var dy = current.Y - visualCenterY;
            transformedCurrent = new PointMm(
                visualCenterX + dx * cos - dy * sin,
                visualCenterY + dx * sin + dy * cos
            );
        }

        // Get raw edge positions without normalization (in logical/unrotated space)
        double left = _originalBounds.Left;
        double top = _originalBounds.Top;
        double right = _originalBounds.Right;
        double bottom = _originalBounds.Bottom;

        // Adjust for paint well offset when comparing to mouse position
        var adjustedCurrent = new PointMm(transformedCurrent.X - offset, transformedCurrent.Y - offset);

        // Determine the anchor point (opposite corner/edge from the handle being dragged)
        // This anchor point should remain stationary in world space
        double anchorX = originalLogicalCenterX;
        double anchorY = originalLogicalCenterY;

        switch (_activeHandle)
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
        double anchorRelX = _originalBounds.Width > 0 ? (anchorX - _originalBounds.Left) / _originalBounds.Width : 0.5;
        double anchorRelY = _originalBounds.Height > 0 ? (anchorY - _originalBounds.Top) / _originalBounds.Height : 0.5;
        
        // Apply flip to anchor relative position
        if (flipH) anchorRelX = 1.0 - anchorRelX;
        if (flipV) anchorRelY = 1.0 - anchorRelY;
        
        double newAnchorX = newLogicalBounds.Left + anchorRelX * newLogicalBounds.Width;
        double newAnchorY = newLogicalBounds.Top + anchorRelY * newLogicalBounds.Height;

        // Calculate where the anchor point ends up in world space after rotation
        var originalAnchorWorld = Math.Abs(_selectionRotationAngle) > 0.001
            ? RotatePoint(new PointMm(anchorX, anchorY), new PointMm(originalLogicalCenterX, originalLogicalCenterY), _selectionRotationAngle)
            : new PointMm(anchorX, anchorY);

        var newLogicalCenterX = newLogicalBounds.Left + newLogicalBounds.Width / 2;
        var newLogicalCenterY = newLogicalBounds.Top + newLogicalBounds.Height / 2;
        
        var newAnchorWorld = Math.Abs(_selectionRotationAngle) > 0.001
            ? RotatePoint(new PointMm(newAnchorX, newAnchorY), new PointMm(newLogicalCenterX, newLogicalCenterY), _selectionRotationAngle)
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
        var originalCenter = new PointMm(originalLogicalCenterX, originalLogicalCenterY);
        var newCenter = new PointMm(adjustedNewLogicalCenterX, adjustedNewLogicalCenterY);

        // Transform strokes: unrotate original -> scale -> re-rotate around NEW center
        if (_originalStrokes != null)
        {
            var indices = _selectedIndices.ToList();
            
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _originalStrokes.Count) continue;

                var original = _originalStrokes[i];
                
                // Step 1: Unrotate the original stroke back to logical space around the ORIGINAL center
                LineStroke unrotated;
                if (Math.Abs(_selectionRotationAngle) > 0.001)
                {
                    unrotated = RotateStroke(original, originalCenter, -_selectionRotationAngle);
                }
                else
                {
                    unrotated = original;
                }
                
                // Step 2: Scale in logical space (using unadjusted bounds for scaling ratios)
                var scaled = ScaleAndFlipStroke(unrotated, _originalBounds, newLogicalBounds, flipH, flipV);
                
                // Step 3: Translate by the offset to keep anchor stationary
                scaled = TranslateStroke(scaled, offsetX, offsetY);
                
                // Step 4: Re-rotate around the adjusted NEW center
                if (Math.Abs(_selectionRotationAngle) > 0.001)
                {
                    scaled = RotateStroke(scaled, newCenter, _selectionRotationAngle);
                }
                
                doc.Strokes[idx] = scaled;
            }
        }

        // Transform paint wells: unrotate -> scale -> translate -> re-rotate
        if (_originalPaintWellBounds != null)
        {
            foreach (var (id, originalWellBounds) in _originalPaintWellBounds)
            {
                var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
                if (well != null)
                {
                    // Step 1: Unrotate around ORIGINAL center
                    Rect unrotated;
                    if (Math.Abs(_selectionRotationAngle) > 0.001)
                    {
                        unrotated = RotateRect(originalWellBounds, originalCenter, -_selectionRotationAngle);
                    }
                    else
                    {
                        unrotated = originalWellBounds;
                    }
                    
                    // Step 2: Scale
                    var scaled = ScaleAndFlipRect(unrotated, _originalBounds, newLogicalBounds, flipH, flipV);
                    
                    // Step 3: Translate
                    scaled = new Rect(scaled.Left + offsetX, scaled.Top + offsetY, scaled.Width, scaled.Height);
                    
                    // Step 4: Re-rotate around adjusted NEW center
                    if (Math.Abs(_selectionRotationAngle) > 0.001)
                    {
                        scaled = RotateRect(scaled, newCenter, _selectionRotationAngle);
                    }
                    
                    well.Bounds = scaled;
                }
            }
        }

        // Update the logical bounds for rendering (use the adjusted bounds)
        _logicalBounds = adjustedNewLogicalBounds;
        _requestRender();
    }
    
    private static LineStroke TranslateStroke(LineStroke stroke, double dx, double dy)
    {
        return new LineStroke(
            new PointMm(stroke.A.X + dx, stroke.A.Y + dy),
            new PointMm(stroke.B.X + dx, stroke.B.Y + dy)
        )
        {
            PaintWellId = stroke.PaintWellId,
            GroupId = stroke.GroupId,
            IsGroupStart = stroke.IsGroupStart,
            IsGroupEnd = stroke.IsGroupEnd
        };
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
        // Add base angle to support cumulative rotation across multiple operations
        _selectionRotationAngle = _baseRotationAngle + deltaAngle;

        // The actual rotation center for transforming objects is in document coordinates
        var transformCenter = new PointMm(
            _originalBounds.Left + _originalBounds.Width / 2,
            _originalBounds.Top + _originalBounds.Height / 2);

        // Calculate the total rotation from the original strokes (base + delta)
        var totalRotation = _selectionRotationAngle;

        // Rotate strokes from their original positions
        if (_originalStrokes != null)
        {
            var indices = _selectedIndices.ToList();
            for (int i = 0; i < indices.Count; i++)
            {
                var idx = indices[i];
                if (i >= _originalStrokes.Count) continue;

                var original = _originalStrokes[i];
                
                // First unrotate from base angle, then rotate to new total angle
                // This is equivalent to rotating by deltaAngle from current position
                var unrotated = Math.Abs(_baseRotationAngle) > 0.001 
                    ? RotateStroke(original, transformCenter, -_baseRotationAngle) 
                    : original;
                doc.Strokes[idx] = RotateStroke(unrotated, transformCenter, totalRotation);
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
                    // First unrotate from base angle, then rotate to new total angle
                    var unrotated = Math.Abs(_baseRotationAngle) > 0.001 
                        ? RotateRect(originalWellBounds, transformCenter, -_baseRotationAngle) 
                        : originalWellBounds;
                    well.Bounds = RotateRect(unrotated, transformCenter, totalRotation);
                }
            }
        }

        // The logical bounds stay the same during rotation - only the angle changes
        // Don't update _logicalBounds here

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
    
    /// <summary>
    /// Calculates the appropriate icon size based on the selection bounds.
    /// The icon scales with the selection but is capped at MAX_ICON_SIZE and has a minimum of MIN_ICON_SIZE.
    /// </summary>
    private double CalculateIconSize()
    {
        if (_selectionBounds.IsEmpty) return MAX_ICON_SIZE;
        
        // Use the smaller dimension to determine icon size
        var smallerDimension = Math.Min(_selectionBounds.Width, _selectionBounds.Height);
        
        // Add padding to get the visual bounds size
        var visualSize = smallerDimension + (SELECTION_PADDING * 2);
        
        // Calculate icon size as a ratio of the visual bounds
        var iconSize = visualSize * ICON_SIZE_RATIO;
        
        // Clamp to min/max bounds while maintaining aspect ratio (it's square so aspect ratio is always 1:1)
        return Math.Clamp(iconSize, MIN_ICON_SIZE, MAX_ICON_SIZE);
    }
}
