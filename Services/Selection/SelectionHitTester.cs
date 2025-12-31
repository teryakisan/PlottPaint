using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using NVSPlotter.Models;

using Point = System.Windows.Point;

namespace NVSPlotter.Services.Selection;

/// <summary>
/// Handles all hit testing operations for selection.
/// Determines what element (stroke, paint well, handle) is at a given point.
/// </summary>
public sealed class SelectionHitTester
{
    // Hit testing tolerances
    private const double HIT_TOLERANCE = 8.0; // mm - distance to click on a stroke
    private const double HANDLE_HIT_RADIUS = 20.0; // Hit test radius for handles
    private const double ROTATE_HANDLE_HIT_RADIUS = 25.0; // Hit test radius for rotation handle
    private const double PAINT_WELL_HIT_MARGIN = 10.0; // Extra margin around paint well bounds
    private const double SELECTION_PADDING = 20.0; // Must match SelectionVisuals.Padding
    private const double ROTATE_HANDLE_OFFSET = 45.0; // Must match SelectionVisuals.RotateHandleOffsetValue

    /// <summary>
    /// Finds the index of the stroke at the given point, or -1 if none.
    /// Checks from top-most to bottom-most.
    /// </summary>
    public int HitTestStroke(PointMm point, List<LineStroke> strokes)
    {
        for (int i = strokes.Count - 1; i >= 0; i--) // Top-most first
        {
            var stroke = strokes[i];
            if (GeometryHelpers.DistanceToSegment(point, stroke.A, stroke.B) <= HIT_TOLERANCE)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Finds the paint well at the given point, or null if none.
    /// Point should be in document coordinates (not canvas coordinates).
    /// </summary>
    public PaintWell? HitTestPaintWell(PointMm point, List<PaintWell> paintWells)
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

    /// <summary>
    /// Determines which selection handle (if any) is at the given point.
    /// </summary>
    public SelectionHandle HitTestHandle(PointMm point, Rect bounds, double rotationAngle, SelectionMode currentMode)
    {
        if (bounds.IsEmpty) return SelectionHandle.None;

        // Don't allow clicking the rotation handle while already rotating
        if (currentMode == SelectionMode.Rotating)
            return SelectionHandle.None;

        // Calculate padded bounds matching the visual display
        var paddedLeft = bounds.Left - SELECTION_PADDING;
        var paddedTop = bounds.Top - SELECTION_PADDING;
        var paddedRight = bounds.Right + SELECTION_PADDING;
        var paddedBottom = bounds.Bottom + SELECTION_PADDING;
        var paddedCenterX = (paddedLeft + paddedRight) / 2;
        var paddedCenterY = (paddedTop + paddedBottom) / 2;

        // Create rotation transform if we have a rotation angle
        RotateTransform? rotateTransform = null;
        if (Math.Abs(rotationAngle) > 0.001)
        {
            rotateTransform = new RotateTransform(rotationAngle * 180 / Math.PI, paddedCenterX, paddedCenterY);
        }

        // Calculate rotated corner positions
        var topLeft = TransformPoint(paddedLeft, paddedTop, rotateTransform);
        var topRight = TransformPoint(paddedRight, paddedTop, rotateTransform);
        var bottomRight = TransformPoint(paddedRight, paddedBottom, rotateTransform);
        var bottomLeft = TransformPoint(paddedLeft, paddedBottom, rotateTransform);

        // Calculate rotation handle position
        var topCenter = new Point((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2);
        var topEdgeDx = topRight.X - topLeft.X;
        var topEdgeDy = topRight.Y - topLeft.Y;
        var topEdgeLength = Math.Sqrt(topEdgeDx * topEdgeDx + topEdgeDy * topEdgeDy);
        double perpX = topEdgeLength > 0 ? topEdgeDy / topEdgeLength : 0;
        double perpY = topEdgeLength > 0 ? -topEdgeDx / topEdgeLength : -1;
        var rotateHandlePos = new Point(topCenter.X + perpX * ROTATE_HANDLE_OFFSET, topCenter.Y + perpY * ROTATE_HANDLE_OFFSET);

        // Check rotate handle first - use larger hit radius for easier clicking
        if (GeometryHelpers.Distance(point, new PointMm(rotateHandlePos.X, rotateHandlePos.Y)) <= ROTATE_HANDLE_HIT_RADIUS)
            return SelectionHandle.Rotate;

        // Check corner handles
        if (GeometryHelpers.Distance(point, new PointMm(topLeft.X, topLeft.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.TopLeft;
        if (GeometryHelpers.Distance(point, new PointMm(topRight.X, topRight.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.TopRight;
        if (GeometryHelpers.Distance(point, new PointMm(bottomLeft.X, bottomLeft.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.BottomLeft;
        if (GeometryHelpers.Distance(point, new PointMm(bottomRight.X, bottomRight.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.BottomRight;

        // Check edge handles (midpoints of edges)
        var topCenterHandle = new Point((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2);
        var bottomCenterHandle = new Point((bottomLeft.X + bottomRight.X) / 2, (bottomLeft.Y + bottomRight.Y) / 2);
        var middleLeftHandle = new Point((topLeft.X + bottomLeft.X) / 2, (topLeft.Y + bottomLeft.Y) / 2);
        var middleRightHandle = new Point((topRight.X + bottomRight.X) / 2, (topRight.Y + bottomRight.Y) / 2);

        if (GeometryHelpers.Distance(point, new PointMm(topCenterHandle.X, topCenterHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.TopCenter;
        if (GeometryHelpers.Distance(point, new PointMm(bottomCenterHandle.X, bottomCenterHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.BottomCenter;
        if (GeometryHelpers.Distance(point, new PointMm(middleLeftHandle.X, middleLeftHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.MiddleLeft;
        if (GeometryHelpers.Distance(point, new PointMm(middleRightHandle.X, middleRightHandle.Y)) <= HANDLE_HIT_RADIUS)
            return SelectionHandle.MiddleRight;

        return SelectionHandle.None;
    }

    /// <summary>
    /// Checks if a point is inside the selection bounds (including padding), accounting for rotation.
    /// </summary>
    public bool IsPointInBounds(PointMm point, Rect bounds, double rotationAngle)
    {
        if (bounds.IsEmpty) return false;

        // Include the padding area as part of the movable selection region
        var paddedLeft = bounds.Left - SELECTION_PADDING;
        var paddedTop = bounds.Top - SELECTION_PADDING;
        var paddedWidth = bounds.Width + (SELECTION_PADDING * 2);
        var paddedHeight = bounds.Height + (SELECTION_PADDING * 2);

        // If we have a rotation, we need to transform the point back to the unrotated space
        double testX = point.X;
        double testY = point.Y;

        if (Math.Abs(rotationAngle) > 0.001)
        {
            // Calculate center of the padded bounds
            var centerX = paddedLeft + paddedWidth / 2;
            var centerY = paddedTop + paddedHeight / 2;

            // Rotate the test point in the opposite direction around the same center
            var cos = Math.Cos(-rotationAngle);
            var sin = Math.Sin(-rotationAngle);
            var dx = point.X - centerX;
            var dy = point.Y - centerY;
            testX = centerX + dx * cos - dy * sin;
            testY = centerY + dx * sin + dy * cos;
        }

        return testX >= paddedLeft && testX <= paddedLeft + paddedWidth &&
               testY >= paddedTop && testY <= paddedTop + paddedHeight;
    }

    /// <summary>
    /// Checks if the mouse is hovering over any resize handle.
    /// Returns the handle index if hovering, or -1 if not.
    /// </summary>
    public (bool IsHovering, int HandleIndex) CheckResizeHandleHover(PointMm point, IReadOnlyList<Point> handlePositions)
    {
        for (int i = 0; i < handlePositions.Count; i++)
        {
            var handlePos = handlePositions[i];
            var distance = GeometryHelpers.Distance(point, new PointMm(handlePos.X, handlePos.Y));
            if (distance <= HANDLE_HIT_RADIUS)
            {
                return (true, i);
            }
        }
        return (false, -1);
    }

    /// <summary>
    /// Checks if a point is hovering over the rotation handle.
    /// </summary>
    public bool IsHoveringRotateHandle(PointMm point, Point rotateHandlePosition)
    {
        var distance = GeometryHelpers.Distance(point, new PointMm(rotateHandlePosition.X, rotateHandlePosition.Y));
        return distance <= ROTATE_HANDLE_HIT_RADIUS;
    }

    /// <summary>
    /// Finds all strokes that belong to the same group as the stroke at the given index.
    /// If the stroke has no GroupId, only that stroke is returned (individual line).
    /// </summary>
    public HashSet<int> FindGroupedStrokes(int startIndex, List<LineStroke> strokes)
    {
        var result = new HashSet<int> { startIndex };

        if (startIndex < 0 || startIndex >= strokes.Count)
            return result;

        var hitStroke = strokes[startIndex];

        // If this stroke has no group, it's an individual object
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

    /// <summary>
    /// Checks if a stroke intersects with a rectangle.
    /// Used for marquee selection.
    /// </summary>
    public static bool StrokeIntersectsRect(LineStroke stroke, Rect rect)
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

        return GeometryHelpers.SegmentsIntersect(stroke.A, stroke.B, tl, tr) ||
               GeometryHelpers.SegmentsIntersect(stroke.A, stroke.B, tr, br) ||
               GeometryHelpers.SegmentsIntersect(stroke.A, stroke.B, br, bl) ||
               GeometryHelpers.SegmentsIntersect(stroke.A, stroke.B, bl, tl);
    }

    /// <summary>
    /// Checks if a paint well intersects with a rectangle.
    /// Used for marquee selection.
    /// </summary>
    public static bool PaintWellIntersectsRect(PaintWell well, Rect marquee)
    {
        return well.Bounds.IntersectsWith(marquee);
    }

    // ===== PRIVATE HELPERS =====

    private static Point TransformPoint(double x, double y, RotateTransform? transform)
    {
        var point = new Point(x, y);
        return transform?.Transform(point) ?? point;
    }
}
