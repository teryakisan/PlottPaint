using System;
using System.Collections.Generic;
using System.Windows;
using NVSPlotter.Models;

// Avoid ambiguity with System.Drawing types
using Point = System.Windows.Point;

namespace NVSPlotter.Services.Selection;

/// <summary>
/// Defines the types of selection handles available.
/// </summary>
public enum SelectionHandle
{
    None,
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
    Rotate,
    Body // For moving the entire selection
}

/// <summary>
/// Defines the current selection operation mode.
/// </summary>
public enum SelectionMode
{
    Idle,
    MarqueeSelecting,
    Moving,
    Resizing,
    Rotating
}

/// <summary>
/// Holds all mutable state for the selection system.
/// Separates state management from selection behavior.
/// </summary>
public sealed class SelectionState
{
    // Selection state - strokes
    private readonly HashSet<int> _selectedIndices = new();
    
    // Selection state - paint wells
    private readonly HashSet<Guid> _selectedPaintWellIds = new();

    /// <summary>
    /// Gets the set of selected stroke indices.
    /// </summary>
    public HashSet<int> SelectedIndices => _selectedIndices;

    /// <summary>
    /// Gets the set of selected paint well IDs.
    /// </summary>
    public HashSet<Guid> SelectedPaintWellIds => _selectedPaintWellIds;

    /// <summary>
    /// Gets or sets the axis-aligned bounding box of the current selection.
    /// </summary>
    public Rect SelectionBounds { get; set; }

    /// <summary>
    /// Gets or sets the current selection operation mode.
    /// </summary>
    public SelectionMode Mode { get; set; } = SelectionMode.Idle;

    /// <summary>
    /// Gets or sets the currently active selection handle.
    /// </summary>
    public SelectionHandle ActiveHandle { get; set; } = SelectionHandle.None;

    /// <summary>
    /// Gets or sets the starting point of a drag operation.
    /// </summary>
    public PointMm DragStart { get; set; }

    /// <summary>
    /// Gets or sets the original bounds before a transform operation.
    /// </summary>
    public Rect OriginalBounds { get; set; }

    /// <summary>
    /// Gets or sets the original strokes saved for undo/cancel.
    /// </summary>
    public List<LineStroke>? OriginalStrokes { get; set; }

    /// <summary>
    /// Gets or sets the original paint well bounds saved for undo/cancel.
    /// </summary>
    public List<(Guid Id, Rect Bounds, double Rotation)>? OriginalPaintWellBounds { get; set; }

    /// <summary>
    /// Gets or sets the original angle at the start of a rotation operation.
    /// </summary>
    public double OriginalAngle { get; set; }

    /// <summary>
    /// Gets or sets the cumulative rotation angle for the selection box display.
    /// </summary>
    public double RotationAngle { get; set; }

    /// <summary>
    /// Gets or sets the base rotation angle at the start of a rotation operation.
    /// </summary>
    public double BaseRotationAngle { get; set; }

    /// <summary>
    /// Gets or sets the center point for rotation operations.
    /// </summary>
    public Point RotationCenter { get; set; }

    /// <summary>
    /// Gets or sets the logical (unrotated) bounds used for scaling operations.
    /// </summary>
    public Rect LogicalBounds { get; set; }

    /// <summary>
    /// Gets whether there is an active selection.
    /// </summary>
    public bool HasSelection => _selectedIndices.Count > 0 || _selectedPaintWellIds.Count > 0;

    /// <summary>
    /// Gets whether a selection operation is in progress.
    /// </summary>
    public bool IsActive => Mode != SelectionMode.Idle;

    /// <summary>
    /// Clears all selection state to defaults.
    /// </summary>
    public void Clear()
    {
        _selectedIndices.Clear();
        _selectedPaintWellIds.Clear();
        SelectionBounds = Rect.Empty;
        LogicalBounds = Rect.Empty;
        RotationAngle = 0;
        Mode = SelectionMode.Idle;
        ActiveHandle = SelectionHandle.None;
        ClearDragState();
    }

    /// <summary>
    /// Clears only the drag-related state.
    /// </summary>
    public void ClearDragState()
    {
        OriginalStrokes = null;
        OriginalPaintWellBounds = null;
        OriginalBounds = Rect.Empty;
        OriginalAngle = 0;
    }

    /// <summary>
    /// Resets the operation state without clearing selection.
    /// </summary>
    public void ResetOperationState()
    {
        Mode = SelectionMode.Idle;
        ActiveHandle = SelectionHandle.None;
        ClearDragState();
    }
}
