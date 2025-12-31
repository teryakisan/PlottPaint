using NVSPlotter.Models;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Indicates what type of snap occurred.
    /// </summary>
    public enum SnapType
    {
        /// <summary>No snap occurred</summary>
        None,
        /// <summary>Snapped to the start point (A) of a stroke</summary>
        Start,
        /// <summary>Snapped to the end point (B) of a stroke</summary>
        End
    }

    /// <summary>
    /// Handles all snapping logic for the drawing canvas:
    /// - Endpoint snapping (snap to nearest stroke start/end point)
    /// - Grid snapping (snap to grid intersections)
    /// - Combined snapping with priority (endpoints first, then grid)
    /// </summary>
    public sealed class SnappingService
    {
        private readonly Func<PlotDocument> _getDocument;
        private readonly Func<bool> _getIsSnapEnabled;
        private readonly Func<double> _getSnapRadius;
        private readonly Func<bool> _getIsSnapToGridEnabled;
        private readonly Func<double> _getGridSpacing;

        private const double RULER_THICKNESS = 18.0;

        /// <summary>
        /// Initializes the snapping service.
        /// </summary>
        /// <param name="getDocument">Function to get the current document</param>
        /// <param name="getIsSnapEnabled">Function to check if endpoint snapping is enabled</param>
        /// <param name="getSnapRadius">Function to get the snap radius (mm)</param>
        /// <param name="getIsSnapToGridEnabled">Function to check if grid snapping is enabled</param>
        /// <param name="getGridSpacing">Function to get the grid spacing (mm)</param>
        public SnappingService(
            Func<PlotDocument> getDocument,
            Func<bool> getIsSnapEnabled,
            Func<double> getSnapRadius,
            Func<bool> getIsSnapToGridEnabled,
            Func<double> getGridSpacing)
        {
            _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
            _getIsSnapEnabled = getIsSnapEnabled ?? throw new ArgumentNullException(nameof(getIsSnapEnabled));
            _getSnapRadius = getSnapRadius ?? throw new ArgumentNullException(nameof(getSnapRadius));
            _getIsSnapToGridEnabled = getIsSnapToGridEnabled ?? throw new ArgumentNullException(nameof(getIsSnapToGridEnabled));
            _getGridSpacing = getGridSpacing ?? throw new ArgumentNullException(nameof(getGridSpacing));
        }

        #region Endpoint Snapping

        /// <summary>
        /// Snaps a point to the nearest stroke endpoint if within snap radius.
        /// Returns the snapped point (or original if no snap), and sets snapType to indicate
        /// whether it snapped to a line start (A) or end (B).
        /// </summary>
        /// <param name="raw">The raw point to snap</param>
        /// <param name="snappedTo">Output: the point that was snapped to, or null if no snap</param>
        /// <param name="snapType">Output: the type of snap (Start, End, or None)</param>
        /// <returns>The snapped point, or the original if no snap occurred</returns>
        public PointMm SnapToEndpoint(PointMm raw, out PointMm? snappedTo, out SnapType snapType)
        {
            snappedTo = null;
            snapType = SnapType.None;

            if (!_getIsSnapEnabled()) return raw;

            var radius = _getSnapRadius();
            if (radius <= 0) return raw;

            var doc = _getDocument();
            double bestDist = double.MaxValue;
            PointMm? bestPoint = null;
            SnapType bestSnapType = SnapType.None;

            foreach (var stroke in doc.Strokes)
            {
                var distA = Utility.Distance(raw, stroke.A);
                if (distA < bestDist && distA <= radius)
                {
                    bestDist = distA;
                    bestPoint = stroke.A;
                    bestSnapType = SnapType.Start;
                }

                var distB = Utility.Distance(raw, stroke.B);
                if (distB < bestDist && distB <= radius)
                {
                    bestDist = distB;
                    bestPoint = stroke.B;
                    bestSnapType = SnapType.End;
                }
            }

            if (bestPoint is PointMm pt)
            {
                snappedTo = pt;
                snapType = bestSnapType;
                return pt;
            }

            return raw;
        }

        /// <summary>
        /// Snaps a point to the nearest stroke endpoint if within snap radius.
        /// Overload for cases where snap type isn't needed.
        /// </summary>
        /// <param name="raw">The raw point to snap</param>
        /// <param name="snappedTo">Output: the point that was snapped to, or null if no snap</param>
        /// <returns>The snapped point, or the original if no snap occurred</returns>
        public PointMm SnapToEndpoint(PointMm raw, out PointMm? snappedTo)
        {
            return SnapToEndpoint(raw, out snappedTo, out _);
        }

        #endregion

        #region Grid Snapping

        /// <summary>
        /// Snaps a point to the nearest grid intersection if snap-to-grid is enabled.
        /// </summary>
        /// <param name="raw">The raw point to snap</param>
        /// <returns>The snapped point, or the original if grid snapping is disabled</returns>
        public PointMm SnapToGrid(PointMm raw)
        {
            if (!_getIsSnapToGridEnabled()) return raw;

            var spacing = _getGridSpacing();
            if (spacing <= 0) return raw;

            // Grid lines are drawn at rulerThickness + (n * spacing) positions
            // To snap to grid intersections, we need to account for this offset

            // Convert to document coordinates (relative to ruler), snap, then convert back
            var docX = raw.X - RULER_THICKNESS;
            var docY = raw.Y - RULER_THICKNESS;

            var snappedDocX = Math.Round(docX / spacing) * spacing;
            var snappedDocY = Math.Round(docY / spacing) * spacing;

            // Convert back to canvas coordinates
            return new PointMm(snappedDocX + RULER_THICKNESS, snappedDocY + RULER_THICKNESS);
        }

        #endregion

        #region Combined Snapping

        /// <summary>
        /// Applies combined snapping: endpoints first (higher priority), then grid.
        /// Returns the final snapped point and snap info for indicator display.
        /// </summary>
        /// <param name="raw">The raw point to snap</param>
        /// <param name="endpointSnap">Output: the endpoint that was snapped to, or null</param>
        /// <param name="snapType">Output: the type of endpoint snap (Start, End, or None)</param>
        /// <param name="gridSnapped">Output: true if grid snapping was applied (only when no endpoint snap)</param>
        /// <returns>The final snapped point</returns>
        public PointMm ApplySnapping(PointMm raw, out PointMm? endpointSnap, out SnapType snapType, out bool gridSnapped)
        {
            // First try endpoint snapping (higher priority)
            var result = SnapToEndpoint(raw, out endpointSnap, out snapType);

            // If no endpoint snap, try grid snap
            if (endpointSnap == null && _getIsSnapToGridEnabled())
            {
                var gridSnap = SnapToGrid(raw);
                gridSnapped = gridSnap.X != raw.X || gridSnap.Y != raw.Y;
                return gridSnap;
            }

            gridSnapped = false;
            return result;
        }

        #endregion

        #region Snap Point Progress Calculation

        /// <summary>
        /// Calculates the progress (0.0 to 1.0) of a snap point along its stroke group.
        /// 0.0 = group start, 1.0 = group end.
        /// For ungrouped strokes, returns 0.0 for point A and 1.0 for point B.
        /// This is used for heat map coloring of snap indicators.
        /// </summary>
        /// <param name="snapPoint">The point that was snapped to</param>
        /// <returns>Progress value from 0.0 to 1.0</returns>
        public double CalculateSnapPointProgress(PointMm snapPoint)
        {
            const double tolerance = 0.5; // mm tolerance for point matching

            var doc = _getDocument();

            // Find which stroke(s) contain this point
            LineStroke? matchedStroke = null;
            bool isPointA = false;

            foreach (var stroke in doc.Strokes)
            {
                if (Math.Abs(stroke.A.X - snapPoint.X) < tolerance && Math.Abs(stroke.A.Y - snapPoint.Y) < tolerance)
                {
                    matchedStroke = stroke;
                    isPointA = true;
                    break;
                }
                if (Math.Abs(stroke.B.X - snapPoint.X) < tolerance && Math.Abs(stroke.B.Y - snapPoint.Y) < tolerance)
                {
                    matchedStroke = stroke;
                    isPointA = false;
                    break;
                }
            }

            if (matchedStroke == null)
                return 0.5; // Default to middle if not found

            // If stroke has no group, it's a single stroke: A = 0.0, B = 1.0
            if (matchedStroke.GroupId == null)
            {
                return isPointA ? 0.0 : 1.0;
            }

            // Find all strokes in the same group
            var groupId = matchedStroke.GroupId.Value;
            var groupStrokes = doc.Strokes.Where(s => s.GroupId == groupId).ToList();

            if (groupStrokes.Count == 0)
                return isPointA ? 0.0 : 1.0;

            // Build ordered list of all points in the group
            // Start from the stroke marked as IsGroupStart
            var points = new List<PointMm>();
            var startStroke = groupStrokes.FirstOrDefault(s => s.IsGroupStart) ?? groupStrokes[0];
            points.Add(startStroke.A);
            foreach (var stroke in groupStrokes)
            {
                points.Add(stroke.B);
            }

            // Find which point index matches the snap point
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (Math.Abs(pt.X - snapPoint.X) < tolerance && Math.Abs(pt.Y - snapPoint.Y) < tolerance)
                {
                    // Return progress: 0.0 for first point, 1.0 for last point
                    return points.Count > 1 ? (double)i / (points.Count - 1) : 0.0;
                }
            }

            // Fallback: use the matched stroke's position
            return isPointA ? 0.0 : 1.0;
        }

        #endregion

        #region Helper Properties

        /// <summary>
        /// Gets whether endpoint snapping is currently enabled.
        /// </summary>
        public bool IsSnapEnabled => _getIsSnapEnabled();

        /// <summary>
        /// Gets whether grid snapping is currently enabled.
        /// </summary>
        public bool IsSnapToGridEnabled => _getIsSnapToGridEnabled();

        /// <summary>
        /// Gets the current snap radius.
        /// </summary>
        public double SnapRadius => _getSnapRadius();

        /// <summary>
        /// Gets the current grid spacing.
        /// </summary>
        public double GridSpacing => _getGridSpacing();

        #endregion
    }
}
