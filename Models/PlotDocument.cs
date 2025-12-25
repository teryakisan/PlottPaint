using System.Collections.Generic;

namespace NVSPlotter.Models;

public sealed class PlotDocument
{
    public double WidthMm { get; private set; }
    public double HeightMm { get; private set; }
    public List<LineStroke> Strokes { get; } = new();

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
    }
}

public readonly record struct PointMm(double X, double Y);

public sealed class LineStroke
{
    public PointMm A { get; }
    public PointMm B { get; }

    public LineStroke(PointMm a, PointMm b)
    {
        A = a;
        B = b;
    }

    public LineStroke Reversed() => new LineStroke(B, A);
}
