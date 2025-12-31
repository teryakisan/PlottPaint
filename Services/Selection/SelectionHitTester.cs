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
    /// Uses stored handle positions from the visual layer when available for accuracy.
    /// </summary>
    public SelectionHandle HitTestHandle(PointMm point, Rect bounds, double rotationAngle, SelectionMode currentMode, double skewX = 0, double skewY = 0, Point? storedRotateHandlePosition = null, IReadOnlyList<Point>? storedResizeHandlePositions = null)
    {
        if (bounds.IsEmpty) return SelectionHandle.None;

        // Don't allow clicking the rotation handle while already rotating
        if (currentMode == SelectionMode.Rotating)
            return SelectionHandle.None;

        // Check rotate handle first using stored position if available (most accurate)
        if (storedRotateHandlePosition.HasValue)
        {
            if (GeometryHelpers.Distance(point, new PointMm(storedRotateHandlePosition.Value.X, storedRotateHandlePosition.Value.Y)) <= ROTATE_HANDLE_HIT_RADIUS)
                return SelectionHandle.Rotate;
        }

        // Check resize handles using stored positions if available
        if (storedResizeHandlePositions != null && storedResizeHandlePositions.Count == 8)
        {
            // Handle order: TopLeft(0), TopCenter(1), TopRight(2), MiddleLeft(3), MiddleRight(4), 
            //               BottomLeft(5), BottomCenter(6), BottomRight(7)
            var handleTypes = new[] {
                SelectionHandle.TopLeft, SelectionHandle.TopCenter, SelectionHandle.TopRight,
                SelectionHandle.MiddleLeft, SelectionHandle.MiddleRight,
                SelectionHandle.BottomLeft, SelectionHandle.BottomCenter, SelectionHandle.BottomRight
            };

            for (int i = 0; i < storedResizeHandlePositions.Count; i++)
            {
                var handlePos = storedResizeHandlePositions[i];
                if (GeometryHelpers.Distance(point, new PointMm(handlePos.X, handlePos.Y)) <= HANDLE_HIT_RADIUS)
                    return handleTypes[i];
            }

            return SelectionHandle.None;
        }

        // Fallback: Calculate positions if stored positions not available
        var paddedLeft = bounds.Left - SELECTION_PADDING;
        var paddedTop = bounds.Top - SELECTION_PADDING;
        var paddedRight = bounds.Right + SELECTION_PADDING;
        var paddedBottom = bounds.Bottom + SELECTION_PADDING;
        var paddedCenterX = (paddedLeft + paddedRight) / 2;
        var paddedCenterY = (paddedTop + paddedBottom) / 2;

        // Calculate corner positions with skew and rotation applied
        var topLeft = SkewAndRotatePoint(paddedLeft, paddedTop, paddedCenterX, paddedCenterY, skewX, skewY, rotationAngle);
        var topRight = SkewAndRotatePoint(paddedRight, paddedTop, paddedCenterX, paddedCenterY, skewX, skewY, rotationAngle);
        var bottomRight = SkewAndRotatePoint(paddedRight, paddedBottom, paddedCenterX, paddedCenterY, skewX, skewY, rotationAngle);
        var bottomLeft = SkewAndRotatePoint(paddedLeft, paddedBottom, paddedCenterX, paddedCenterY, skewX, skewY, rotationAngle);

        // Calculate rotation handle position
        var topCenter = new Point((topLeft.X + topRight.X) / 2, (topLeft.Y + topRight.Y) / 2);
        double upX = Math.Sin(rotationAngle);
        double upY = -Math.Cos(rotationAngle);
        var rotateHandlePos = new Point(topCenter.X + upX * ROTATE_HANDLE_OFFSET, topCenter.Y + upY * ROTATE_HANDLE_OFFSET);

        // Check rotate handle
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
    /// Applies skew then rotation to a point around a center.
    /// </summary>
    private static Point SkewAndRotatePoint(double x, double y, double centerX, double centerY, double skewX, double skewY, double rotationAngle)
    {
        // Get position relative to center
        var relX = x - centerX;
        var relY = y - centerY;

        // Apply skew: x' = x + skewX * y, y' = y + skewY * x
        var skewedX = relX + skewX * relY;
        var skewedY = relY + skewY * relX;

        // Apply rotation if needed
        if (Math.Abs(rotationAngle) > 0.001)
        {
            var cos = Math.Cos(rotationAngle);
            var sin = Math.Sin(rotationAngle);
            var rotatedX = skewedX * cos - skewedY * sin;
            var rotatedY = skewedX * sin + skewedY * cos;
            return new Point(centerX + rotatedX, centerY + rotatedY);
        }

        return new Point(centerX + skewedX, centerY + skewedY);
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
    /// Determines if a handle is an edge handle (used for skew operations).
    /// Edge handles are: TopCenter, BottomCenter, MiddleLeft, MiddleRight
    /// </summary>
    public static bool IsEdgeHandle(SelectionHandle handle)
    {
        return handle == SelectionHandle.TopCenter ||
               handle == SelectionHandle.BottomCenter ||
               handle == SelectionHandle.MiddleLeft ||
               handle == SelectionHandle.MiddleRight;
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
}
