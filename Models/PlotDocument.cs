using System;
using System.Collections.Generic;

namespace NVSPlotter.Models;

public sealed class PlotDocument
{
    public double WidthMm { get; private set; }
    public double HeightMm { get; private set; }
    public List<LineStroke> Strokes { get; } = new();
    public List<PaintWell> PaintWells { get; } = new();
    
    /// <summary>
    /// Counter for assigning paint order to strokes.
    /// Incremented each time a color is assigned to a stroke.
    /// </summary>
    private long _nextPaintOrder = 1;

    public PlotDocument(double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
    }

    /// <summary>
    /// Gets the next paint order value and increments the counter.
    /// </summary>
    public long GetNextPaintOrder() => _nextPaintOrder++;

    public void Resize(double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
        Strokes.Clear();
        _nextPaintOrder = 1; // Reset paint order counter
        // Note: PaintWells are preserved on resize
    }

    /// <summary>
    /// Resizes the document while preserving all strokes and paint wells.
    /// Use this for machine settings changes where content should be kept.
    /// </summary>
    public void ResizePreserveContent(double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
        // Strokes, PaintWells, and paint order counter are preserved
    }

    public void ClearAll()
    {
        Strokes.Clear();
        PaintWells.Clear();
        _nextPaintOrder = 1; // Reset paint order counter
    }
}

public readonly record struct PointMm(double X, double Y);

public sealed class LineStroke
{
    public PointMm A { get; }
    public PointMm B { get; }

    /// <summary>Associated paint well ID, or null for default (black) color</summary>
    public Guid? PaintWellId { get; set; }

    /// <summary>
    /// Order in which paint was assigned to this stroke.
    /// Used in paint mode to determine the painting sequence.
    /// Higher values are painted later. 0 means no order assigned (use stroke index).
    /// </summary>
    public long PaintOrder { get; set; }

    /// <summary>
    /// Group ID that identifies strokes drawn as part of the same operation.
    /// Strokes with the same GroupId form a single selectable object.
    /// Null means the stroke is an individual object (e.g., a single line).
    /// </summary>
    public Guid? GroupId { get; set; }

    /// <summary>
    /// Parent Group ID for hierarchical grouping (used for subdivisions).
    /// When a portion of a grouped stroke is subdivided, the new group becomes
    /// a child of the original group. This allows showing all intermediate points
    /// when the parent group is selected.
    /// </summary>
    public Guid? ParentGroupId { get; set; }

    /// <summary>
    /// Indicates this stroke contains the START point of a grouped object.
    /// The start point is this stroke's A point. Used for selection indicators.
    /// For individual strokes (GroupId == null), this is always true.
    /// </summary>
    public bool IsGroupStart { get; set; }

    /// <summary>
    /// Indicates this stroke contains the END point of a grouped object.
    /// The end point is this stroke's B point. Used for selection indicators.
    /// For individual strokes (GroupId == null), this is always true.
    /// </summary>
    public bool IsGroupEnd { get; set; }

    public LineStroke(PointMm a, PointMm b, Guid? paintWellId = null, Guid? groupId = null, 
                          bool isGroupStart = false, bool isGroupEnd = false, Guid? parentGroupId = null,
                          long paintOrder = 0)
        {
            A = a;
            B = b;
            PaintWellId = paintWellId;
            PaintOrder = paintOrder;
            GroupId = groupId;
            ParentGroupId = parentGroupId;
            IsGroupStart = isGroupStart;
            IsGroupEnd = isGroupEnd;
        }

        public LineStroke Reversed() => new LineStroke(B, A, PaintWellId, GroupId, IsGroupEnd, IsGroupStart, ParentGroupId, PaintOrder);

        /// <summary>Creates a copy with a different paint well assignment and optional new paint order</summary>
        public LineStroke WithPaintWell(Guid? paintWellId, long paintOrder = 0) => 
            new LineStroke(A, B, paintWellId, GroupId, IsGroupStart, IsGroupEnd, ParentGroupId, paintOrder > 0 ? paintOrder : PaintOrder);

        /// <summary>Creates a copy with a different group assignment</summary>
        public LineStroke WithGroup(Guid? groupId) => new LineStroke(A, B, PaintWellId, groupId, IsGroupStart, IsGroupEnd, ParentGroupId, PaintOrder);

        /// <summary>Creates a copy with a new parent group assignment</summary>
        public LineStroke WithParentGroup(Guid? parentGroupId) => new LineStroke(A, B, PaintWellId, GroupId, IsGroupStart, IsGroupEnd, parentGroupId, PaintOrder);

        /// <summary>Creates a copy with new start/end markers</summary>
        public LineStroke WithMarkers(bool isGroupStart, bool isGroupEnd) => new LineStroke(A, B, PaintWellId, GroupId, isGroupStart, isGroupEnd, ParentGroupId, PaintOrder);
    }
