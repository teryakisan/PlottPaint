using NVSPlotter.Models;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Configuration settings for G-code generation.
    /// </summary>
    public sealed class GcodeSettings
    {
        public double FeedXY { get; set; } = 3000;
        public double ZUp { get; set; } = 10;
        public double ZDown { get; set; } = 2;
        public double SafeMarginMm { get; set; } = 50;
        public double BedX { get; set; } = 300;
        public double BedY { get; set; } = 400;
        public bool Optimize { get; set; } = false;
        public bool PaintModeEnabled { get; set; } = false;
        public bool AutoWashWipeEnabled { get; set; } = true;
    }

    /// <summary>
    /// Handles all G-code generation logic including normal mode and painting mode.
    /// </summary>
    public sealed class GcodeGeneratorService
    {
        private readonly CoordinateTransformService _coordTransform;
        private readonly Func<PlotDocument> _getDocument;
        private readonly Action<string> _log;

        /// <summary>
        /// Initializes the G-code generator service.
        /// </summary>
        /// <param name="coordTransform">Coordinate transformation service for bed/work conversions</param>
        /// <param name="getDocument">Function to get the current document</param>
        /// <param name="log">Action to log messages</param>
        public GcodeGeneratorService(
            CoordinateTransformService coordTransform,
            Func<PlotDocument> getDocument,
            Action<string> log)
        {
            _coordTransform = coordTransform ?? throw new ArgumentNullException(nameof(coordTransform));
            _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Builds complete G-code from the current document with the given settings.
        /// </summary>
        /// <param name="settings">G-code generation settings</param>
        /// <returns>Complete G-code string</returns>
        public string BuildGcode(GcodeSettings settings)
        {
            var doc = _getDocument();

            // Hardware Z is inverted: flip the commanded values
            var zUpCmd = -settings.ZUp;
            var zDownCmd = -settings.ZDown;

            var strokes = doc.Strokes.ToList();
            if (settings.Optimize)
            {
                strokes = StrokeOptimizer.OptimizeNearest(strokes);
            }

            // Fit doc into bed (with margin), possibly rotating CW to better fit.
            var fit = _coordTransform.ComputeFit(doc.WidthMm, doc.HeightMm, settings.BedX, settings.BedY, settings.SafeMarginMm);

            _log($"G-code fit={fit.Mode}, scale={fit.Scale:0.###}, usableBed=({settings.BedX - 2 * settings.SafeMarginMm:0.###} x {settings.BedY - 2 * settings.SafeMarginMm:0.###})");
            _log("G-code convention: X>=0, Y<=0");
            if (settings.PaintModeEnabled && doc.PaintWells.Count > 0)
            {
                _log($"Paint mode: {doc.PaintWells.Count} paint well(s) defined");
            }

            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("; NVSPlotter");
            sb.AppendLine("; Units: mm");
            sb.AppendLine("; Work convention: X positive, Y negative");
            if (settings.PaintModeEnabled && doc.PaintWells.Count > 0)
            {
                sb.AppendLine("; PAINTING MODE ENABLED");
                foreach (var well in doc.PaintWells)
                {
                    sb.AppendLine($"; Paint Well: {well.Name} at ({well.Center.X:0.#}, {well.Center.Y:0.#}), dip={well.DipDepth:0.#}mm, dwell={well.DwellTimeMs}ms");
                }
            }
            sb.AppendLine("G21");           // mm
            sb.AppendLine("G90");           // absolute
            sb.AppendLine("G54");
            sb.AppendLine("G92.1");         // clear G92 offsets
            sb.AppendLine("G10 L20 P1 X0 Y0"); // set G54 so current position (home) is work 0,0
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");

            const double joinTol = 0.01; // mm

            if (settings.PaintModeEnabled && doc.PaintWells.Count > 0)
            {
                // PAINTING MODE: Group strokes by paint well, insert paint refresh sequences
                BuildPaintingModeGcode(sb, strokes, doc.PaintWells, fit, zUpCmd, zDownCmd, settings.FeedXY, joinTol, settings.AutoWashWipeEnabled);
            }
            else
            {
                // NORMAL MODE: Build paths without paint considerations
                var paths = BuildPaths(strokes, joinTol);
                BuildNormalGcode(sb, paths, fit, zUpCmd, zDownCmd, settings.FeedXY);
            }

            sb.AppendLine("G0 X0 Y0"); // back to home (work origin)
            sb.AppendLine("M2");

            var gcode = sb.ToString();
            _log($"Built G-code: lines={gcode.Split('\n').Length}");
            return gcode;
        }

        #region Normal Mode G-code

        private void BuildNormalGcode(StringBuilder sb, List<List<LineStroke>> paths, CoordinateTransformService.FitSpec fit, double zUpCmd, double zDownCmd, double feedXY)
        {
            foreach (var path in paths)
            {
                if (path.Count == 0) continue;

                var first = path[0];
                var startWork = _coordTransform.BedToWork(_coordTransform.DocToBed(first.A, fit));

                sb.AppendLine($"G0 X{Fmt(startWork.X)} Y{Fmt(startWork.Y)}");
                sb.AppendLine($"G0 Z{Fmt(zDownCmd)}");

                bool firstMove = true;
                foreach (var seg in path)
                {
                    var endWork = _coordTransform.BedToWork(_coordTransform.DocToBed(seg.B, fit));
                    if (firstMove)
                    {
                        sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)} F{Fmt(feedXY)}");
                        firstMove = false;
                    }
                    else
                    {
                        sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)}");
                    }
                }

                sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
            }
        }

        #endregion

        #region Painting Mode G-code

        private void BuildPaintingModeGcode(
            StringBuilder sb,
            List<LineStroke> strokes,
            List<PaintWell> paintWells,
            CoordinateTransformService.FitSpec fit,
            double zUpCmd,
            double zDownCmd,
            double feedXY,
            double joinTol,
            bool autoWashWipeEnabled)
        {
            // Process strokes in DRAWING ORDER (not grouped by color)
            // This allows wash/wipe between color changes

            Guid? currentWellId = null;
            PaintWell? currentWell = null;
            double distanceTraveled = 0;
            double currentRefreshTarget = 0; // Randomized target for next refresh
            var random = new Random(); // For natural-looking refresh intervals

            // Helper to get a random refresh distance within the well's range
            double GetRandomRefreshDistance(PaintWell well)
            {
                var min = well.RefreshDistanceMinMm;
                var max = well.RefreshDistanceMaxMm;
                if (max <= min || max <= 0) return max; // No range, use max
                return min + random.NextDouble() * (max - min);
            }

            // Find wash and wipe wells by name (case-insensitive)
            var washWell = paintWells.FirstOrDefault(w =>
                w.Name.Equals("Wash", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("wash", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("rinse", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("clean", StringComparison.OrdinalIgnoreCase));

            var wipeWell = paintWells.FirstOrDefault(w =>
                w.Name.Equals("Wipe", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("wipe", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("dry", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("towel", StringComparison.OrdinalIgnoreCase));

            sb.AppendLine("; Processing strokes in drawing order with color change sequences");
            sb.AppendLine($"; Auto wash/wipe: {(autoWashWipeEnabled ? "ENABLED" : "DISABLED")}");
            if (washWell != null) sb.AppendLine($"; Wash well: {washWell.Name} (swirl pattern)");
            if (wipeWell != null) sb.AppendLine($"; Wipe well: {wipeWell.Name} (zig-zag pattern)");

            // Build continuous paths from strokes while respecting color boundaries
            var paths = BuildPathsWithColorBoundaries(strokes, joinTol);

            foreach (var path in paths)
            {
                if (path.Count == 0) continue;

                var pathWellId = path[0].PaintWellId;
                var pathWell = pathWellId.HasValue ? paintWells.FirstOrDefault(w => w.Id == pathWellId) : null;

                // Check if color changed - need to do wash/wipe/dip sequence
                if (pathWellId != currentWellId)
                {
                    // Color is changing!
                    if (currentWell != null && autoWashWipeEnabled)
                    {
                        sb.AppendLine($"; === Color change: {currentWell.Name} -> {pathWell?.Name ?? "Black"} ===");

                        // Wash sequence with swirl pattern (if wash well exists and we had a previous color)
                        if (washWell != null)
                        {
                            sb.AppendLine("; Wash brush with swirl pattern");
                            GenerateWashSwirlPattern(sb, washWell, fit, zUpCmd, feedXY);
                        }

                        // Wipe sequence with zig-zag pattern (if wipe well exists)
                        if (wipeWell != null)
                        {
                            sb.AppendLine("; Wipe brush with zig-zag pattern");
                            GenerateWipeZigZagPattern(sb, wipeWell, fit, zUpCmd, zDownCmd, feedXY);
                        }
                    }
                    else if (currentWell != null)
                    {
                        sb.AppendLine($"; === Color change: {currentWell.Name} -> {pathWell?.Name ?? "Black"} (auto wash/wipe disabled) ===");
                    }

                    // Dip in new color (if it's a paint well, not black)
                    if (pathWell != null)
                    {
                        sb.AppendLine($"; === Paint Well: {pathWell.Name} ===");
                        GeneratePaintDipSequence(sb, pathWell, fit, zUpCmd, feedXY);
                        // Set randomized refresh target for this color
                        currentRefreshTarget = GetRandomRefreshDistance(pathWell);
                        sb.AppendLine($"; Next refresh at ~{currentRefreshTarget:F0}mm (range: {pathWell.RefreshDistanceMinMm:F0}-{pathWell.RefreshDistanceMaxMm:F0}mm)");
                    }
                    else
                    {
                        sb.AppendLine("; === No Paint Well (black) ===");
                        currentRefreshTarget = 0;
                    }

                    currentWellId = pathWellId;
                    currentWell = pathWell;
                    distanceTraveled = 0;
                }

                // Draw this path
                var first = path[0];
                var startWork = _coordTransform.BedToWork(_coordTransform.DocToBed(first.A, fit));

                sb.AppendLine($"G0 X{Fmt(startWork.X)} Y{Fmt(startWork.Y)}");
                sb.AppendLine($"G0 Z{Fmt(zDownCmd)}");

                bool firstMove = true;
                PointMm lastPosition = startWork;

                foreach (var seg in path)
                {
                    var endWork = _coordTransform.BedToWork(_coordTransform.DocToBed(seg.B, fit));

                    // Calculate stroke length
                    var strokeLength = Math.Sqrt(
                        Math.Pow(endWork.X - lastPosition.X, 2) +
                        Math.Pow(endWork.Y - lastPosition.Y, 2));

                    // Check for paint refresh (within same color) - use randomized target
                    if (currentWell != null && currentRefreshTarget > 0 && strokeLength > 0.1)
                    {
                        var segmentStart = lastPosition;
                        var segmentEnd = endWork;
                        var remainingLength = strokeLength;

                        while (remainingLength > 0.1)
                        {
                            var distanceUntilRefresh = currentRefreshTarget - distanceTraveled;

                            if (remainingLength <= distanceUntilRefresh)
                            {
                                // Complete this segment without refresh
                                if (firstMove)
                                {
                                    sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)} F{Fmt(feedXY)}");
                                    firstMove = false;
                                }
                                else
                                {
                                    sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)}");
                                }
                                distanceTraveled += remainingLength;
                                lastPosition = segmentEnd;
                                remainingLength = 0;
                            }
                            else
                            {
                                // Need to stop partway for refresh
                                // But if remaining distance after this refresh would be less than half the max,
                                // skip the refresh and continue (avoids robotic appearance)
                                var remainingAfterRefresh = remainingLength - distanceUntilRefresh;
                                var skipThreshold = currentWell.RefreshDistanceMaxMm / 2.0;

                                if (remainingAfterRefresh < skipThreshold && remainingAfterRefresh > 0.1)
                                {
                                    // Skip this refresh, just complete the segment
                                    if (firstMove)
                                    {
                                        sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)} F{Fmt(feedXY)} ; Skip refresh, only {remainingAfterRefresh:F0}mm left");
                                        firstMove = false;
                                    }
                                    else
                                    {
                                        sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)} ; Skip refresh, only {remainingAfterRefresh:F0}mm left");
                                    }
                                    distanceTraveled += remainingLength;
                                    lastPosition = segmentEnd;
                                    remainingLength = 0;
                                    continue;
                                }

                                var ratio = distanceUntilRefresh / remainingLength;
                                var breakPoint = new PointMm(
                                    segmentStart.X + (segmentEnd.X - segmentStart.X) * ratio,
                                    segmentStart.Y + (segmentEnd.Y - segmentStart.Y) * ratio);

                                if (firstMove)
                                {
                                    sb.AppendLine($"G1 X{Fmt(breakPoint.X)} Y{Fmt(breakPoint.Y)} F{Fmt(feedXY)}");
                                    firstMove = false;
                                }
                                else
                                {
                                    sb.AppendLine($"G1 X{Fmt(breakPoint.X)} Y{Fmt(breakPoint.Y)}");
                                }

                                distanceTraveled += distanceUntilRefresh;

                                // Refresh paint (same color, no wash/wipe needed)
                                sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift for paint refresh (traveled {distanceTraveled:F1}mm)");
                                GeneratePaintDipSequence(sb, currentWell, fit, zUpCmd, feedXY);
                                sb.AppendLine($"G0 X{Fmt(breakPoint.X)} Y{Fmt(breakPoint.Y)} ; Return to position");
                                sb.AppendLine($"G0 Z{Fmt(zDownCmd)} ; Lower to continue");

                                // Reset distance and get NEW random target for natural variation
                                distanceTraveled = 0;
                                currentRefreshTarget = GetRandomRefreshDistance(currentWell);
                                sb.AppendLine($"; Next refresh at ~{currentRefreshTarget:F0}mm");

                                remainingLength -= distanceUntilRefresh;
                                segmentStart = breakPoint;
                                lastPosition = breakPoint;
                            }
                        }
                    }
                    else
                    {
                        // No refresh tracking - just draw
                        if (firstMove)
                        {
                            sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)} F{Fmt(feedXY)}");
                            firstMove = false;
                        }
                        else
                        {
                            sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)}");
                        }

                        if (currentWell != null && currentRefreshTarget > 0)
                        {
                            distanceTraveled += strokeLength;
                        }
                        lastPosition = endWork;
                    }
                }

                sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
            }
        }

        #endregion

        #region Paint Well Patterns

        private void GeneratePaintDipSequence(StringBuilder sb, PaintWell well, CoordinateTransformService.FitSpec fit, double zUpCmd, double feedXY)
        {
            // Paint wells should NOT be clamped to safe margin - they're physical locations
            // that may be outside the drawing area
            var wellCenter = _coordTransform.BedToWork(_coordTransform.DocToBed(well.Center, fit, clamp: false));
            // DipDepth is how deep to go into paint (positive value like 5mm)
            // We negate it to get the Z command (negative Z = down into paint)
            var dipDepthCmd = -well.DipDepth;

            sb.AppendLine($"; Dip in paint well: {well.Name}");
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift before moving to paint well");
            sb.AppendLine($"G0 X{Fmt(wellCenter.X)} Y{Fmt(wellCenter.Y)} ; Move to paint well");
            sb.AppendLine($"G0 Z{Fmt(dipDepthCmd)} ; Dip into paint");

            if (well.DwellTimeMs > 0)
            {
                // GRBL 1.1 uses G4 P<seconds> (not milliseconds!)
                // Convert ms to seconds for the dwell command
                var dwellSeconds = well.DwellTimeMs / 1000.0;
                sb.AppendLine($"G4 P{Fmt(dwellSeconds)} ; Dwell in paint ({well.DwellTimeMs}ms)");
            }

            // Re-assert feed rate after dwell to avoid "Feed rate not yet set" errors
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} F{Fmt(feedXY)} ; Lift from paint well");
        }

        /// <summary>
        /// Generates a swirl/spiral pattern for washing the brush in a rinse well.
        /// The pattern stays within the well bounds and makes multiple quick circular motions.
        /// </summary>
        private void GenerateWashSwirlPattern(StringBuilder sb, PaintWell washWell, CoordinateTransformService.FitSpec fit, double zUpCmd, double feedXY)
        {
            var wellCenter = _coordTransform.BedToWork(_coordTransform.DocToBed(washWell.Center, fit, clamp: false));
            var dipDepthCmd = -washWell.DipDepth;

            // Wash wells are rendered as circles using the smaller dimension as diameter
            // We need to stay INSIDE that circle to avoid knocking over the cup
            var circleDiameter = Math.Min(washWell.Bounds.Width, washWell.Bounds.Height);
            var circleRadius = circleDiameter / 2.0;

            // Use 60% of the radius for safety margin (stay well inside the cup)
            var maxRadius = circleRadius * 0.6;

            // Number of swirl loops and segments per loop
            const int numLoops = 3;
            const int segmentsPerLoop = 12;
            const double swirlFeedRate = 5000; // Fast swirl motion

            sb.AppendLine($"; === Wash swirl pattern in: {washWell.Name} ===");
            sb.AppendLine($"; Circle diameter: {circleDiameter:F1}mm, safe swirl radius: {maxRadius:F1}mm");
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift before moving to wash well");
            sb.AppendLine($"G0 X{Fmt(wellCenter.X)} Y{Fmt(wellCenter.Y)} ; Move to wash well center");
            sb.AppendLine($"G0 Z{Fmt(dipDepthCmd)} ; Lower into wash");

            // Generate swirl pattern - spiral outward then inward
            for (int loop = 0; loop < numLoops; loop++)
            {
                // Spiral outward
                for (int seg = 0; seg <= segmentsPerLoop; seg++)
                {
                    var t = (double)seg / segmentsPerLoop;
                    var radius = maxRadius * t; // Grow radius
                    var angle = t * 2 * Math.PI; // One full rotation per spiral

                    var x = wellCenter.X + radius * Math.Cos(angle);
                    var y = wellCenter.Y + radius * Math.Sin(angle);

                    if (seg == 0)
                        sb.AppendLine($"G1 X{Fmt(x)} Y{Fmt(y)} F{Fmt(swirlFeedRate)}");
                    else
                        sb.AppendLine($"G1 X{Fmt(x)} Y{Fmt(y)}");
                }

                // Spiral inward
                for (int seg = segmentsPerLoop; seg >= 0; seg--)
                {
                    var t = (double)seg / segmentsPerLoop;
                    var radius = maxRadius * t;
                    var angle = (1 - t) * 2 * Math.PI + Math.PI; // Reverse direction

                    var x = wellCenter.X + radius * Math.Cos(angle);
                    var y = wellCenter.Y + radius * Math.Sin(angle);

                    sb.AppendLine($"G1 X{Fmt(x)} Y{Fmt(y)}");
                }
            }

            // Return to center and lift
            sb.AppendLine($"G0 X{Fmt(wellCenter.X)} Y{Fmt(wellCenter.Y)} ; Return to center");
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} F{Fmt(feedXY)} ; Lift from wash well");
        }

        /// <summary>
        /// Generates a zig-zag wiping pattern for drying the brush on a paper towel.
        /// The pattern stays within the well bounds and makes multiple back-and-forth passes.
        /// </summary>
        private void GenerateWipeZigZagPattern(StringBuilder sb, PaintWell wipeWell, CoordinateTransformService.FitSpec fit, double zUpCmd, double zDownCmd, double feedXY)
        {
            // Get the well bounds in work coordinates
            var topLeft = _coordTransform.BedToWork(_coordTransform.DocToBed(new PointMm(wipeWell.Bounds.Left, wipeWell.Bounds.Top), fit, clamp: false));
            var bottomRight = _coordTransform.BedToWork(_coordTransform.DocToBed(new PointMm(wipeWell.Bounds.Right, wipeWell.Bounds.Bottom), fit, clamp: false));

            // Calculate the actual bounds (work coords may be inverted)
            var minX = Math.Min(topLeft.X, bottomRight.X);
            var maxX = Math.Max(topLeft.X, bottomRight.X);
            var minY = Math.Min(topLeft.Y, bottomRight.Y);
            var maxY = Math.Max(topLeft.Y, bottomRight.Y);

            // Add margin to stay inside the well
            var margin = 10.0; // 10mm margin from edges
            minX += margin;
            maxX -= margin;
            minY += margin;
            maxY -= margin;

            // Ensure we have valid bounds
            if (maxX <= minX || maxY <= minY)
            {
                // Well too small, fall back to simple dip
                GeneratePaintDipSequence(sb, wipeWell, fit, zUpCmd, feedXY);
                return;
            }

            // Zig-zag parameters
            const int numPasses = 4; // Number of back-and-forth passes
            const double wipeFeedRate = 4000; // Moderate speed for wiping
            var dipDepthCmd = -wipeWell.DipDepth;
            var stepY = (maxY - minY) / (numPasses * 2 - 1); // Vertical step between zig-zag lines

            sb.AppendLine($"; === Wipe zig-zag pattern in: {wipeWell.Name} ===");

            // Move to starting position (top-left of wipe area)
            var startX = minX;
            var startY = maxY;
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift before moving to wipe area");
            sb.AppendLine($"G0 X{Fmt(startX)} Y{Fmt(startY)} ; Move to wipe start");
            sb.AppendLine($"G0 Z{Fmt(dipDepthCmd)} ; Lower onto wipe surface");

            // Generate zig-zag pattern
            var currentY = startY;
            var goingRight = true;

            for (int pass = 0; pass < numPasses * 2; pass++)
            {
                if (goingRight)
                {
                    // Move right
                    sb.AppendLine(pass == 0
                        ? $"G1 X{Fmt(maxX)} Y{Fmt(currentY)} F{Fmt(wipeFeedRate)}"
                        : $"G1 X{Fmt(maxX)} Y{Fmt(currentY)}");
                }
                else
                {
                    // Move left
                    sb.AppendLine($"G1 X{Fmt(minX)} Y{Fmt(currentY)}");
                }

                // Step down for next pass (except on last pass)
                if (pass < numPasses * 2 - 1)
                {
                    currentY -= stepY;
                    currentY = Math.Max(currentY, minY); // Don't go below minY
                    sb.AppendLine($"G1 X{Fmt(goingRight ? maxX : minX)} Y{Fmt(currentY)}");
                }

                goingRight = !goingRight;
            }

            // Lift from wipe surface
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} F{Fmt(feedXY)} ; Lift from wipe surface");
        }

        #endregion

        #region Path Building

        /// <summary>
        /// Builds paths from strokes, breaking at color boundaries.
        /// Strokes with the same color that are connected are grouped together.
        /// When color changes, a new path starts.
        /// </summary>
        public static List<List<LineStroke>> BuildPathsWithColorBoundaries(List<LineStroke> input, double tol)
        {
            var result = new List<List<LineStroke>>();
            List<LineStroke>? current = null;
            Guid? currentColorId = null;

            foreach (var stroke in input)
            {
                if (current == null)
                {
                    // Start first path
                    current = new List<LineStroke> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Check if color changed
                if (stroke.PaintWellId != currentColorId)
                {
                    // Color changed - start new path
                    current = new List<LineStroke> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Same color - check if connected to previous stroke
                var prev = current[^1];
                if (Utility.Distance(prev.B, stroke.A) <= tol)
                {
                    // Connected - add to current path
                    current.Add(stroke);
                }
                else
                {
                    // Not connected but same color - start new path (no wash/wipe needed)
                    current = new List<LineStroke> { stroke };
                    result.Add(current);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds continuous paths from strokes by grouping connected strokes together.
        /// </summary>
        public static List<List<LineStroke>> BuildPaths(List<LineStroke> input, double tol)
        {
            var result = new List<List<LineStroke>>();
            List<LineStroke>? current = null;

            foreach (var stroke in input)
            {
                if (current == null)
                {
                    current = new List<LineStroke> { stroke };
                    result.Add(current);
                    continue;
                }

                var prev = current[^1];
                if (Utility.Distance(prev.B, stroke.A) <= tol)
                {
                    current.Add(stroke);
                }
                else
                {
                    current = new List<LineStroke> { stroke };
                    result.Add(current);
                }
            }

            return result;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Formats a double value for G-code output using invariant culture.
        /// </summary>
        public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        #endregion
    }
}
