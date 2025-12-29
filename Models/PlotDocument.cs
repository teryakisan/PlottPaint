using System;
using System.Collections.Generic;

namespace NVSPlotter.Models;

public sealed class PlotDocument
{
    public double WidthMm { get; private set; }
    public double HeightMm { get; private set; }
    public List<LineStroke> Strokes { get; } = new();
    public List<PaintWell> PaintWells { get; } = new();

    public PlotDocument(double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
    }

    public void Resize(double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
        Strokes.Clear();
        // Note: PaintWells are preserved on resize
    }

    public void ClearAll()
    {
        Strokes.Clear();
        PaintWells.Clear();
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
    /// Group ID that identifies strokes drawn as part of the same operation.
    /// Strokes with the same GroupId form a single selectable object.
    /// Null means the stroke is an individual object (e.g., a single line).
    /// </summary>
    public Guid? GroupId { get; set; }

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
                      bool isGroupStart = false, bool isGroupEnd = false)
    {
        A = a;
        B = b;
        PaintWellId = paintWellId;
        GroupId = groupId;
        IsGroupStart = isGroupStart;
        IsGroupEnd = isGroupEnd;
    }

    public LineStroke Reversed() => new LineStroke(B, A, PaintWellId, GroupId, IsGroupEnd, IsGroupStart);

    /// <summary>Creates a copy with a different paint well assignment</summary>
    public LineStroke WithPaintWell(Guid? paintWellId) => new LineStroke(A, B, paintWellId, GroupId, IsGroupStart, IsGroupEnd);

    /// <summary>Creates a copy with a different group assignment</summary>
    public LineStroke WithGroup(Guid? groupId) => new LineStroke(A, B, PaintWellId, groupId, IsGroupStart, IsGroupEnd);

    /// <summary>Creates a copy with new start/end markers</summary>
    public LineStroke WithMarkers(bool isGroupStart, bool isGroupEnd) => new LineStroke(A, B, PaintWellId, GroupId, isGroupStart, isGroupEnd);
}
