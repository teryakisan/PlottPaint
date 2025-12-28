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

    public LineStroke(PointMm a, PointMm b, Guid? paintWellId = null)
    {
        A = a;
        B = b;
        PaintWellId = paintWellId;
    }

    public LineStroke Reversed() => new LineStroke(B, A, PaintWellId);

    /// <summary>Creates a copy with a different paint well assignment</summary>
    public LineStroke WithPaintWell(Guid? paintWellId) => new LineStroke(A, B, paintWellId);
}
