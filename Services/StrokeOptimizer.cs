using NVSPlotter.Models;
using System;
using System.Collections.Generic;

namespace NVSPlotter.Services;

public static class StrokeOptimizer
{
    public static List<LineStroke> OptimizeNearest(IReadOnlyList<LineStroke> strokes)
    {
        if (strokes.Count <= 2) return new List<LineStroke>(strokes);

        var remaining = new List<LineStroke>(strokes);
        var result = new List<LineStroke>(strokes.Count);
        var current = new PointMm(0, 0);

        while (remaining.Count > 0)
        {
            var (idx, reverse) = FindNearestStroke(remaining, current);
            var chosen = remaining[idx];
            remaining.RemoveAt(idx);

            if (reverse)
            {
                chosen = chosen.Reversed();
            }

            result.Add(chosen);
            current = chosen.B;
        }

        return result;
    }

    private static (int idx, bool reverse) FindNearestStroke(List<LineStroke> strokes, PointMm current)
    {
        int bestIdx = 0;
        bool reverse = false;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < strokes.Count; i++)
        {
            var stroke = strokes[i];
            var dStart = Distance(current, stroke.A);
            if (dStart < bestDistance)
            {
                bestDistance = dStart;
                bestIdx = i;
                reverse = false;
            }

            var dEnd = Distance(current, stroke.B);
            if (dEnd < bestDistance)
            {
                bestDistance = dEnd;
                bestIdx = i;
                reverse = true;
            }
        }

        return (bestIdx, reverse);
    }

    private static double Distance(PointMm a, PointMm b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
