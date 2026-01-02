using NVSPlotter.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NVSPlotter.Services;

public static class StrokeOptimizer
{
    /// <summary>
    /// Optimizes stroke order using nearest-neighbor algorithm.
    /// Does NOT respect paint well colors - use OptimizeNearestByColor for painting mode.
    /// </summary>
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

    /// <summary>
    /// Optimizes stroke order while respecting object creation order for painting.
    /// Strokes are processed in their original order - this method only optimizes
    /// the direction (start vs end point) of each stroke to minimize travel.
    /// This preserves the user's intended painting sequence.
    /// </summary>
    public static List<LineStroke> OptimizeDirectionOnly(IReadOnlyList<LineStroke> strokes)
    {
        if (strokes.Count <= 1) return new List<LineStroke>(strokes);

        var result = new List<LineStroke>(strokes.Count);
        var current = new PointMm(0, 0);

        foreach (var stroke in strokes)
        {
            // Calculate distance to both endpoints
            var distToA = Distance(current, stroke.A);
            var distToB = Distance(current, stroke.B);

            // Choose the closest endpoint as the start
            if (distToB < distToA)
            {
                result.Add(stroke.Reversed());
                current = stroke.A; // After reversing, original A is now B
            }
            else
            {
                result.Add(stroke);
                current = stroke.B;
            }
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
