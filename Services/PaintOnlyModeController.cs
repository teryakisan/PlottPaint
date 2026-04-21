using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using NVSPlotter.Models;

namespace NVSPlotter.Services;

/// <summary>
/// Controller for handling mouse interactions in Paint Only view mode.
/// Manages selection, hover, and context menu for G-code painting strokes.
/// </summary>
public sealed class PaintOnlyModeController
{
    private readonly Func<GcodePaintingStrokeService?> _getGcodePaintingStrokeService;
    private readonly CanvasRendererService _rendererService;
    private readonly Func<PlotDocument> _getDocument;
    private readonly Action _requestRedraw;
    private readonly Action<string> _log;
    
    // Marquee selection state
    private bool _isMarqueeSelecting;
    private PointMm _marqueeStart;
    private PointMm _marqueeEnd;
    
    /// <summary>
    /// Event raised when the selection changes.
    /// </summary>
    public event EventHandler? SelectionChanged;
    
    /// <summary>
    /// Event raised when a context menu should be shown.
    /// </summary>
    public event EventHandler<PaintOnlyContextMenuEventArgs>? ContextMenuRequested;
    
    /// <summary>
    /// Event raised when the marquee rectangle needs to be updated.
    /// </summary>
    public event EventHandler<PaintOnlyMarqueeEventArgs>? MarqueeUpdated;
    
    /// <summary>
    /// Gets whether Paint Only mode is currently active.
    /// </summary>
    public bool IsActive { get; private set; }
    
    /// <summary>
    /// Gets whether a marquee selection is in progress.
    /// </summary>
    public bool IsMarqueeSelecting => _isMarqueeSelecting;
    
    /// <summary>
    /// Gets the currently selected painting stroke numbers.
    /// </summary>
    public IReadOnlySet<int> SelectedStrokeNumbers => _rendererService.SelectedPaintingStrokeNumbers;
    
    /// <summary>
    /// Gets whether there are selected strokes.
    /// </summary>
    public bool HasSelection => _rendererService.SelectedPaintingStrokeNumbers.Count > 0;
    
    public PaintOnlyModeController(
        Func<GcodePaintingStrokeService?> getGcodePaintingStrokeService,
        CanvasRendererService rendererService,
        Func<PlotDocument> getDocument,
        Action requestRedraw,
        Action<string> log)
    {
        _getGcodePaintingStrokeService = getGcodePaintingStrokeService ?? throw new ArgumentNullException(nameof(getGcodePaintingStrokeService));
        _rendererService = rendererService ?? throw new ArgumentNullException(nameof(rendererService));
        _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
        _requestRedraw = requestRedraw ?? throw new ArgumentNullException(nameof(requestRedraw));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }
    
    /// <summary>
    /// Activates Paint Only mode.
    /// </summary>
    public void Activate()
    {
        if (IsActive) return;
        
        IsActive = true;
        _rendererService.ClearPaintOnlySelection();
        
        var service = _getGcodePaintingStrokeService();
        var strokeCount = service?.PaintingStrokes.Count ?? 0;
        _log($"[PAINT ONLY] Mode activated with {strokeCount} G-code painting strokes");
    }
    
    /// <summary>
    /// Deactivates Paint Only mode.
    /// </summary>
    public void Deactivate()
    {
        if (!IsActive) return;
        
        IsActive = false;
        _rendererService.ClearPaintOnlySelection();
        _log("[PAINT ONLY] Mode deactivated");
    }
    
    /// <summary>
    /// Refreshes the painting strokes - clears any invalid selections.
    /// Call this when G-code is regenerated.
    /// </summary>
    public void RefreshPaintingStrokes()
    {
        if (!IsActive) return;
        
        var service = _getGcodePaintingStrokeService();
        if (service == null) return;
        
        // Clear selection of strokes that no longer exist
        var validStrokeNumbers = service.PaintingStrokes
            .Select(s => s.StrokeNumber)
            .ToHashSet();
        
        var invalidSelections = _rendererService.SelectedPaintingStrokeNumbers
            .Where(n => !validStrokeNumbers.Contains(n))
            .ToList();
        
        foreach (var invalid in invalidSelections)
        {
            _rendererService.SelectedPaintingStrokeNumbers.Remove(invalid);
        }
        
        if (invalidSelections.Count > 0)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    
    /// <summary>
    /// Handles mouse down in Paint Only view mode.
    /// </summary>
    public bool HandleMouseDown(PointMm point, bool isShiftHeld, bool isRightClick)
    {
        if (!IsActive) return false;
        
        var service = _getGcodePaintingStrokeService();
        if (service == null) return false;
        
        // Transform document coordinates to G-code work coordinates for hit test
        var gcodeX = point.X;
        var gcodeY = -point.Y; // Negate Y for G-code coordinate system
        
        var hitStroke = service.HitTest(gcodeX, gcodeY, tolerance: 10.0);
        
        if (isRightClick)
        {
            // Right-click for context menu
            if (hitStroke != null)
            {
                // If clicking on unselected stroke, select it first
                if (!_rendererService.SelectedPaintingStrokeNumbers.Contains(hitStroke.StrokeNumber))
                {
                    _rendererService.ClearPaintOnlySelection();
                    _rendererService.SelectPaintingStroke(hitStroke.StrokeNumber);
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    _requestRedraw();
                }
                
                // Raise context menu event
                var selectedStrokes = _rendererService.GetSelectedGcodePaintingStrokes();
                ContextMenuRequested?.Invoke(this, new PaintOnlyContextMenuEventArgs(selectedStrokes, point));
                return true;
            }
            else if (HasSelection)
            {
                // Right-click on empty space but have selection - show context menu for selection
                var selectedStrokes = _rendererService.GetSelectedGcodePaintingStrokes();
                ContextMenuRequested?.Invoke(this, new PaintOnlyContextMenuEventArgs(selectedStrokes, point));
                return true;
            }
        }
        else
        {
            // Left-click for selection
            if (hitStroke != null)
            {
                if (isShiftHeld)
                {
                    // Toggle selection
                    _rendererService.TogglePaintingStrokeSelection(hitStroke.StrokeNumber);
                }
                else
                {
                    // Single selection
                    _rendererService.ClearPaintOnlySelection();
                    _rendererService.SelectPaintingStroke(hitStroke.StrokeNumber);
                }
                
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                _requestRedraw();
                return true;
            }
            else
            {
                // Click on empty space - start marquee selection
                if (!isShiftHeld)
                {
                    _rendererService.ClearPaintOnlySelection();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
                
                // Begin marquee selection
                // Note: We don't call _requestRedraw() here because that would clear the canvas
                // and remove the marquee rectangle. The marquee is drawn as an overlay.
                _isMarqueeSelecting = true;
                _marqueeStart = point;
                _marqueeEnd = point;
                MarqueeUpdated?.Invoke(this, new PaintOnlyMarqueeEventArgs(_marqueeStart, _marqueeEnd, true));
                return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Handles mouse move in Paint Only view mode.
    /// </summary>
    public bool HandleMouseMove(PointMm point)
    {
        if (!IsActive) return false;
        
        // Handle marquee selection dragging
        if (_isMarqueeSelecting)
        {
            _marqueeEnd = point;
            MarqueeUpdated?.Invoke(this, new PaintOnlyMarqueeEventArgs(_marqueeStart, _marqueeEnd, true));
            return true;
        }
        
        var service = _getGcodePaintingStrokeService();
        if (service == null) return false;
        
        // Transform document coordinates to G-code work coordinates for hit test
        var gcodeX = point.X;
        var gcodeY = -point.Y; // Negate Y for G-code coordinate system
        
        var hitStroke = service.HitTest(gcodeX, gcodeY, tolerance: 10.0);
        var newHoveredNumber = hitStroke?.StrokeNumber ?? -1;
        
        if (newHoveredNumber != _rendererService.HoveredPaintingStrokeNumber)
        {
            _rendererService.HoveredPaintingStrokeNumber = newHoveredNumber;
            _requestRedraw();
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Handles mouse up in Paint Only view mode.
    /// </summary>
    public bool HandleMouseUp(PointMm point, bool isShiftHeld)
    {
        if (!IsActive) return false;
        
        if (_isMarqueeSelecting)
        {
            _marqueeEnd = point;
            CompleteMarqueeSelection(isShiftHeld);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Completes the marquee selection, selecting all strokes within the rectangle.
    /// </summary>
    private void CompleteMarqueeSelection(bool addToSelection)
    {
        _isMarqueeSelecting = false;
        MarqueeUpdated?.Invoke(this, new PaintOnlyMarqueeEventArgs(_marqueeStart, _marqueeEnd, false));
        
        var service = _getGcodePaintingStrokeService();
        if (service == null) return;
        
        // Calculate marquee rectangle in document coordinates
        var minX = Math.Min(_marqueeStart.X, _marqueeEnd.X);
        var maxX = Math.Max(_marqueeStart.X, _marqueeEnd.X);
        var minY = Math.Min(_marqueeStart.Y, _marqueeEnd.Y);
        var maxY = Math.Max(_marqueeStart.Y, _marqueeEnd.Y);
        
        // Only select if the marquee has some size
        if (maxX - minX < 2 && maxY - minY < 2) return;
        
        var doc = _getDocument();
        
        // Find all strokes that intersect the marquee rectangle
        var strokesInMarquee = new List<int>();
        foreach (var paintingStroke in service.PaintingStrokes)
        {
            if (StrokeIntersectsRect(paintingStroke, minX, maxX, minY, maxY, doc.HeightMm))
            {
                strokesInMarquee.Add(paintingStroke.StrokeNumber);
            }
        }
        
        // Select the strokes
        if (!addToSelection)
        {
            _rendererService.ClearPaintOnlySelection();
        }
        
        foreach (var strokeNumber in strokesInMarquee)
        {
            _rendererService.SelectPaintingStroke(strokeNumber, addToSelection: true);
        }
        
        if (strokesInMarquee.Count > 0)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            _log($"[PAINT ONLY] Marquee selected {strokesInMarquee.Count} strokes");
        }
        
        _requestRedraw();
    }
    
    /// <summary>
    /// Tests if a painting stroke intersects a rectangle (in document coordinates).
    /// </summary>
    private static bool StrokeIntersectsRect(GcodePaintingStroke stroke, double minX, double maxX, double minY, double maxY, double docHeight)
    {
        foreach (var segment in stroke.Segments)
        {
            // Transform G-code coords to document coords
            var x1 = segment.FromX;
            var y1 = -segment.FromY;
            var x2 = segment.ToX;
            var y2 = -segment.ToY;
            
            // Check if line segment intersects rectangle
            if (LineIntersectsRect(x1, y1, x2, y2, minX, minY, maxX, maxY))
            {
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Tests if a line segment intersects a rectangle.
    /// </summary>
    private static bool LineIntersectsRect(double x1, double y1, double x2, double y2, 
        double rectLeft, double rectTop, double rectRight, double rectBottom)
    {
        // Check if either endpoint is inside the rectangle
        if (PointInRect(x1, y1, rectLeft, rectTop, rectRight, rectBottom) ||
            PointInRect(x2, y2, rectLeft, rectTop, rectRight, rectBottom))
        {
            return true;
        }
        
        // Check if line intersects any of the rectangle's edges
        if (LinesIntersect(x1, y1, x2, y2, rectLeft, rectTop, rectRight, rectTop)) return true;   // Top
        if (LinesIntersect(x1, y1, x2, y2, rectLeft, rectBottom, rectRight, rectBottom)) return true; // Bottom
        if (LinesIntersect(x1, y1, x2, y2, rectLeft, rectTop, rectLeft, rectBottom)) return true;  // Left
        if (LinesIntersect(x1, y1, x2, y2, rectRight, rectTop, rectRight, rectBottom)) return true; // Right
        
        return false;
    }
    
    private static bool PointInRect(double x, double y, double left, double top, double right, double bottom)
    {
        return x >= left && x <= right && y >= top && y <= bottom;
    }
    
    private static bool LinesIntersect(double x1, double y1, double x2, double y2, 
        double x3, double y3, double x4, double y4)
    {
        var d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(d) < 0.0001) return false;
        
        var t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d;
        var u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / d;
        
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }
    
    /// <summary>
    /// Clears all selection.
    /// </summary>
    public void ClearSelection()
    {
        if (!HasSelection) return;
        
        _rendererService.ClearPaintOnlySelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    
    /// <summary>
    /// Cancels any active operation (like marquee selection).
    /// </summary>
    public void Cancel()
    {
        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            MarqueeUpdated?.Invoke(this, new PaintOnlyMarqueeEventArgs(_marqueeStart, _marqueeEnd, false));
            _requestRedraw();
        }
    }
    
    /// <summary>
    /// Selects all painting strokes.
    /// </summary>
    public void SelectAll()
    {
        if (!IsActive) return;
        
        var service = _getGcodePaintingStrokeService();
        if (service == null) return;
        
        _rendererService.SelectedPaintingStrokeNumbers.Clear();
        foreach (var stroke in service.PaintingStrokes)
        {
            _rendererService.SelectedPaintingStrokeNumbers.Add(stroke.StrokeNumber);
        }
        
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _requestRedraw();
    }
    
    /// <summary>
    /// Applies brush profiles to the selected G-code painting strokes.
    /// Note: This updates the EnabledBrushProfiles property on the GcodePaintingStroke objects.
    /// </summary>
    public void ApplyBrushProfilesToSelection(HashSet<string>? profiles)
    {
        if (!HasSelection) return;
        
        var selectedStrokes = _rendererService.GetSelectedGcodePaintingStrokes();
        foreach (var stroke in selectedStrokes)
        {
            stroke.HasBrushProfiles = profiles != null && profiles.Count > 0;
            stroke.EnabledBrushProfiles = profiles;
        }
        
        _log($"[PAINT ONLY] Applied brush profiles to {selectedStrokes.Count} G-code painting strokes");
        _requestRedraw();
    }
    
    /// <summary>
    /// Clears brush profiles from the selected painting strokes.
    /// </summary>
    public void ClearBrushProfilesFromSelection()
    {
        ApplyBrushProfilesToSelection(null);
    }
    
    /// <summary>
    /// Gets the selected G-code painting strokes.
    /// </summary>
    public List<GcodePaintingStroke> GetSelectedStrokes()
    {
        return _rendererService.GetSelectedGcodePaintingStrokes();
    }
}

/// <summary>
/// Event arguments for Paint Only context menu requests.
/// </summary>
public class PaintOnlyContextMenuEventArgs : EventArgs
{
    /// <summary>
    /// The selected G-code painting strokes.
    /// </summary>
    public IReadOnlyList<GcodePaintingStroke> SelectedStrokes { get; }
    
    /// <summary>
    /// The mouse position where the context menu should appear.
    /// </summary>
    public PointMm Position { get; }
    
    public PaintOnlyContextMenuEventArgs(IReadOnlyList<GcodePaintingStroke> selectedStrokes, PointMm position)
    {
        SelectedStrokes = selectedStrokes;
        Position = position;
    }
}

/// <summary>
/// Event arguments for Paint Only marquee selection updates.
/// </summary>
public class PaintOnlyMarqueeEventArgs : EventArgs
{
    /// <summary>
    /// The start point of the marquee rectangle.
    /// </summary>
    public PointMm Start { get; }
    
    /// <summary>
    /// The end point of the marquee rectangle.
    /// </summary>
    public PointMm End { get; }
    
    /// <summary>
    /// Whether the marquee is currently visible/active.
    /// </summary>
    public bool IsVisible { get; }
    
    public PaintOnlyMarqueeEventArgs(PointMm start, PointMm end, bool isVisible)
    {
        Start = start;
        End = end;
        IsVisible = isVisible;
    }
}
