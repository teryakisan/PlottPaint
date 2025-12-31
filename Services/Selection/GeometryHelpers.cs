using System;
using System.Windows;
using NVSPlotter.Models;

namespace NVSPlotter.Services.Selection;

/// <summary>
/// Static geometry utilities for selection transformations.
/// </summary>
public static class GeometryHelpers
{
    /// <summary>
    /// Calculates the Euclidean distance between two points.
    /// </summary>
    public static double Distance(PointMm a, PointMm b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Calculates the shortest distance from a point to a line segment.
    /// </summary>
    public static double DistanceToSegment(PointMm point, PointMm a, PointMm b)
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

    /// <summary>
    /// Determines if two line segments intersect.
    /// </summary>
    public static bool SegmentsIntersect(PointMm a1, PointMm a2, PointMm b1, PointMm b2)
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

    /// <summary>
    /// Calculates the cross product of two 2D vectors.
    /// </summary>
    public static double CrossProduct(double ax, double ay, double bx, double by)
    {
        return ax * by - ay * bx;
    }

    /// <summary>
    /// Rotates a point around a center by the specified angle (in radians).
    /// </summary>
    public static PointMm RotatePoint(PointMm point, PointMm center, double angle)
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

    /// <summary>
    /// Rotates a stroke around a center point by the specified angle (in radians).
    /// </summary>
    public static LineStroke RotateStroke(LineStroke original, PointMm center, double angle)
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

    /// <summary>
    /// Rotates a rectangle's center around a point. The rectangle dimensions are preserved.
    /// </summary>
    public static Rect RotateRect(Rect original, PointMm center, double angle)
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

    /// <summary>
    /// Translates a stroke by the specified offset.
    /// </summary>
    public static LineStroke TranslateStroke(LineStroke stroke, double dx, double dy)
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

    /// <summary>
    /// Scales a stroke from old bounds to new bounds, optionally flipping horizontally or vertically.
    /// </summary>
    public static LineStroke ScaleAndFlipStroke(LineStroke original, Rect oldBounds, Rect newBounds, bool flipH, bool flipV)
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

    /// <summary>
    /// Scales a stroke from old bounds to new bounds without flipping.
    /// </summary>
    public static LineStroke ScaleStroke(LineStroke original, Rect oldBounds, Rect newBounds)
    {
        return ScaleAndFlipStroke(original, oldBounds, newBounds, false, false);
    }

    /// <summary>
    /// Scales a rectangle from old bounds to new bounds, optionally flipping horizontally or vertically.
    /// </summary>
    public static Rect ScaleAndFlipRect(Rect original, Rect oldBounds, Rect newBounds, bool flipH, bool flipV)
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

    /// <summary>
    /// Scales a rectangle from old bounds to new bounds without flipping.
    /// </summary>
    public static Rect ScaleRect(Rect original, Rect oldBounds, Rect newBounds)
    {
        return ScaleAndFlipRect(original, oldBounds, newBounds, false, false);
    }
}
