using NVSPlotter.Models;
using NVSPlotter.Util;
using NVSPlotter.Windows;
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
        
        /// <summary>
        /// Collection of available brush profiles that can be applied to strokes.
        /// When a stroke has enabled profiles, one is randomly selected to apply Z curves.
        /// </summary>
        public IReadOnlyList<BrushProfile> AvailableBrushProfiles { get; set; } = Array.Empty<BrushProfile>();
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
        
        // Random instance for selecting brush profiles
        private readonly Random _profileRandom = new();

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
            
            // In paint mode, sort strokes by paint order to respect the sequence in which colors were assigned
            // Strokes with PaintOrder=0 (no order assigned) are processed in their original position
            if (settings.PaintModeEnabled && doc.PaintWells.Count > 0)
            {
                // Stable sort: strokes with the same PaintOrder maintain their relative order
                // PaintOrder=0 means "use original position", so we treat it as infinite (process last in original order)
                strokes = strokes
                    .Select((s, i) => (Stroke: s, OriginalIndex: i))
                    .OrderBy(x => x.Stroke.PaintOrder == 0 ? long.MaxValue : x.Stroke.PaintOrder)
                    .ThenBy(x => x.OriginalIndex) // Preserve original order for same PaintOrder
                    .Select(x => x.Stroke)
                    .ToList();
            }
            
            if (settings.Optimize)
            {
                if (settings.PaintModeEnabled && doc.PaintWells.Count > 0)
                {
                    // In paint mode, only optimize stroke direction (start/end point)
                    // but preserve the paint order sequence
                    strokes = StrokeOptimizer.OptimizeDirectionOnly(strokes);
                }
                else
                {
                    // In normal mode, full optimization reorders strokes for shortest travel
                    strokes = StrokeOptimizer.OptimizeNearest(strokes);
                }
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
                // PAINTING MODE: Process strokes in creation order, inserting wash/wipe/dip sequences on color changes
                BuildPaintingModeGcode(sb, strokes, doc.PaintWells, fit, zUpCmd, zDownCmd, settings.FeedXY, joinTol, settings.AutoWashWipeEnabled, settings.AvailableBrushProfiles);
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
            bool autoWashWipeEnabled,
            IReadOnlyList<BrushProfile> availableProfiles)
        {
            // STEP 1: Pre-transform ALL stroke endpoints to work coordinates
            // This eliminates any floating-point issues from repeated transformations
            // We also preserve PaintOrder to ensure strokes with same color but different paint orders are not grouped
            // And we preserve EnabledBrushProfiles for applying Z curves
            var transformedStrokes = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>();
            foreach (var stroke in strokes)
            {
                var aWork = _coordTransform.BedToWork(_coordTransform.DocToBed(stroke.A, fit));
                var bWork = _coordTransform.BedToWork(_coordTransform.DocToBed(stroke.B, fit));
                transformedStrokes.Add((aWork, bWork, stroke.PaintWellId, stroke.PaintOrder, stroke.EnabledBrushProfiles));
            }

            Guid? currentWellId = null;
            PaintWell? currentWell = null;
            double distanceTraveled = 0;
            double currentRefreshTarget = 0;
            var random = new Random();

            double GetRandomRefreshDistance(PaintWell well)
            {
                var min = well.RefreshDistanceMinMm;
                var max = well.RefreshDistanceMaxMm;
                if (max <= min || max <= 0) return max;
                return min + random.NextDouble() * (max - min);
            }

            // Find wash and wipe wells
            var washWell = paintWells.FirstOrDefault(w =>
                w.Name.Contains("wash", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("rinse", StringComparison.OrdinalIgnoreCase));

            var wipeWell = paintWells.FirstOrDefault(w =>
                w.Name.Contains("wipe", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("dry", StringComparison.OrdinalIgnoreCase));

            sb.AppendLine("; Processing strokes in paint order");
            sb.AppendLine($"; Auto wash/wipe: {(autoWashWipeEnabled ? "ENABLED" : "DISABLED")}");

            // STEP 2: Build paths from pre-transformed strokes
            // Check if any strokes have PaintOrder set (> 0). If not, fall back to color-based grouping.
            var hasPaintOrders = transformedStrokes.Any(s => s.PaintOrder > 0);
            
            List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>> paths;
            if (hasPaintOrders)
            {
                // Paths are broken when PaintOrder changes (even for same color) to respect painting sequence
                paths = BuildTransformedPathsByPaintOrderWithProfiles(transformedStrokes, joinTol);
            }
            else
            {
                // Fallback: group by color (old behavior for projects without PaintOrder)
                paths = BuildTransformedPathsByColorWithProfiles(transformedStrokes, joinTol);
            }

            // Build a lookup for available profiles by name
            var profileLookup = availableProfiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var path in paths)
            {
                if (path.Count == 0) continue;

                var pathWellId = path[0].PaintWellId;
                var pathPaintOrder = path[0].PaintOrder;
                var pathWell = pathWellId.HasValue ? paintWells.FirstOrDefault(w => w.Id == pathWellId) : null;

                // Handle color change - this happens when either the paint well changes OR the paint order changes
                // (same color but assigned at different time = different painting action)
                if (pathWellId != currentWellId)
                {
                    // Actual color change - need wash/wipe
                    if (currentWell != null && autoWashWipeEnabled)
                    {
                        sb.AppendLine($"; === Color change: {currentWell.Name} -> {pathWell?.Name ?? "Black"} ===");
                        if (washWell != null) GenerateWashSwirlPattern(sb, washWell, fit, zUpCmd, feedXY);
                        if (wipeWell != null) GenerateWipeZigZagPattern(sb, wipeWell, fit, zUpCmd, zDownCmd, feedXY);
                    }

                    if (pathWell != null)
                    {
                        sb.AppendLine($"; === Paint Well: {pathWell.Name} (Order: {pathPaintOrder}) ===");
                        GeneratePaintDipSequence(sb, pathWell, fit, zUpCmd, feedXY);
                        currentRefreshTarget = GetRandomRefreshDistance(pathWell);
                    }
                    else
                    {
                        sb.AppendLine($"; === No Paint Well (Order: {pathPaintOrder}) ===");
                        currentRefreshTarget = 0;
                    }

                    currentWellId = pathWellId;
                    currentWell = pathWell;
                    distanceTraveled = 0;
                }
                else if (pathWell != null)
                {
                    // Same color but new paint order - just re-dip without wash/wipe
                    // This handles the case where multiple objects have the same color assigned at different times
                    sb.AppendLine($"; === Paint Well: {pathWell.Name} (Order: {pathPaintOrder}, continuing) ===");
                    // Re-dip to ensure fresh paint for this stroke group
                    GeneratePaintDipSequence(sb, pathWell, fit, zUpCmd, feedXY);
                    currentRefreshTarget = GetRandomRefreshDistance(pathWell);
                    distanceTraveled = 0;
                }

                // STEP 3: Draw the path - with brush profile support
                // Check if any stroke in this path has brush profiles enabled
                var firstStrokeWithProfiles = path.FirstOrDefault(s => s.EnabledBrushProfiles != null && s.EnabledBrushProfiles.Count > 0);
                BrushProfile? selectedProfile = null;
                
                if (firstStrokeWithProfiles.EnabledBrushProfiles != null)
                {
                    // Find matching profiles from available profiles
                    var matchingProfiles = firstStrokeWithProfiles.EnabledBrushProfiles
                        .Where(name => profileLookup.ContainsKey(name))
                        .Select(name => profileLookup[name])
                        .ToList();
                    
                    if (matchingProfiles.Count > 0)
                    {
                        // Randomly select one of the enabled profiles
                        selectedProfile = matchingProfiles[_profileRandom.Next(matchingProfiles.Count)];
                        sb.AppendLine($"; Using brush profile: {selectedProfile.Name}");
                    }
                }
                
                // Move to start of path
                var pathStart = path[0].A;
                sb.AppendLine($"G0 X{Fmt(pathStart.X)} Y{Fmt(pathStart.Y)}");
                
                if (selectedProfile != null)
                {
                    // Draw path with brush profile Z curve
                    DrawPathWithBrushProfile(sb, path, selectedProfile, zUpCmd, zDownCmd, feedXY, currentWell, ref distanceTraveled, ref currentRefreshTarget, GetRandomRefreshDistance, fit);
                }
                else
                {
                    // Normal drawing - simple pen down/up
                    sb.AppendLine($"G0 Z{Fmt(zDownCmd)}");

                    bool firstMove = true;

                // Draw each segment - just go from A to B
                foreach (var seg in path)
                {
                    // Calculate segment length (from A to B, NOT from "last position")
                    var segLength = Math.Sqrt(
                        Math.Pow(seg.B.X - seg.A.X, 2) + 
                        Math.Pow(seg.B.Y - seg.A.Y, 2));

                    // Check if we need paint refresh during this segment
                    if (currentWell != null && currentRefreshTarget > 0 && segLength > 0.1)
                    {
                        // Calculate unit vector for THIS segment (A to B)
                        var dx = seg.B.X - seg.A.X;
                        var dy = seg.B.Y - seg.A.Y;
                        var unitX = segLength > 0.001 ? dx / segLength : 0;
                        var unitY = segLength > 0.001 ? dy / segLength : 0;

                        var distanceAlongSegment = 0.0;
                        var reachedSegmentEnd = false;

                        while (distanceAlongSegment < segLength - 0.01)
                        {
                            var distanceUntilRefresh = currentRefreshTarget - distanceTraveled;
                            var remainingInSegment = segLength - distanceAlongSegment;

                            if (remainingInSegment <= distanceUntilRefresh)
                            {
                                // Draw to segment end
                                if (firstMove)
                                {
                                    sb.AppendLine($"G1 X{Fmt(seg.B.X)} Y{Fmt(seg.B.Y)} F{Fmt(feedXY)}");
                                    firstMove = false;
                                }
                                else
                                {
                                    sb.AppendLine($"G1 X{Fmt(seg.B.X)} Y{Fmt(seg.B.Y)}");
                                }
                                distanceTraveled += remainingInSegment;
                                reachedSegmentEnd = true;
                                break; // Done with this segment
                            }
                            else
                            {
                                // Need to stop for refresh
                                var breakX = seg.A.X + unitX * (distanceAlongSegment + distanceUntilRefresh);
                                var breakY = seg.A.Y + unitY * (distanceAlongSegment + distanceUntilRefresh);

                                if (firstMove)
                                {
                                    sb.AppendLine($"G1 X{Fmt(breakX)} Y{Fmt(breakY)} F{Fmt(feedXY)}");
                                    firstMove = false;
                                }
                                else
                                {
                                    sb.AppendLine($"G1 X{Fmt(breakX)} Y{Fmt(breakY)}");
                                }

                                // Refresh paint
                                sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Paint refresh");
                                GeneratePaintDipSequence(sb, currentWell, fit, zUpCmd, feedXY);
                                sb.AppendLine($"G0 X{Fmt(breakX)} Y{Fmt(breakY)} ; Return");
                                sb.AppendLine($"G0 Z{Fmt(zDownCmd)} ; Lower");

                                distanceAlongSegment += distanceUntilRefresh;
                                distanceTraveled = 0;
                                currentRefreshTarget = GetRandomRefreshDistance(currentWell);
                            }
                        }

                        // CRITICAL: Ensure we always complete the move to seg.B
                        // The while loop can exit without drawing to seg.B if distanceAlongSegment >= segLength - 0.01
                        // after a paint refresh. This would leave the brush short of the endpoint, causing
                        // tangent lines when the next segment starts from its actual A point.
                        if (!reachedSegmentEnd)
                        {
                            if (firstMove)
                            {
                                sb.AppendLine($"G1 X{Fmt(seg.B.X)} Y{Fmt(seg.B.Y)} F{Fmt(feedXY)}");
                                firstMove = false;
                            }
                            else
                            {
                                sb.AppendLine($"G1 X{Fmt(seg.B.X)} Y{Fmt(seg.B.Y)}");
                            }
                            // Add the remaining tiny distance to distanceTraveled
                            var remainingDistance = segLength - distanceAlongSegment;
                            if (remainingDistance > 0)
                            {
                                distanceTraveled += remainingDistance;
                            }
                        }
                    }
                    else
                    {
                        // No paint refresh - just draw directly to B
                        if (firstMove)
                        {
                            sb.AppendLine($"G1 X{Fmt(seg.B.X)} Y{Fmt(seg.B.Y)} F{Fmt(feedXY)}");
                            firstMove = false;
                        }
                        else
                        {
                            sb.AppendLine($"G1 X{Fmt(seg.B.X)} Y{Fmt(seg.B.Y)}");
                        }

                        if (currentWell != null && currentRefreshTarget > 0)
                        {
                            distanceTraveled += segLength;
                        }
                    }
                }

                sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
                        } // End of normal drawing (no brush profile)
                    }
                }
        
                /// <summary>
                /// Draws a path using a brush profile's Z curve instead of simple pen up/down.
                /// The brush profile defines how Z varies along the stroke (pressure curve).
                /// </summary>
                private void DrawPathWithBrushProfile(
                    StringBuilder sb,
                    List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> path,
                    BrushProfile profile,
                    double zUpCmd,
                    double zDownCmd,
                    double feedXY,
                    PaintWell? currentWell,
                    ref double distanceTraveled,
                    ref double currentRefreshTarget,
                    Func<PaintWell, double> getRandomRefreshDistance,
                    CoordinateTransformService.FitSpec fit)
                {
                    // Calculate total path length for normalizing the profile curve
                    double totalPathLength = 0;
                    foreach (var seg in path)
                    {
                        var segLen = Math.Sqrt(Math.Pow(seg.B.X - seg.A.X, 2) + Math.Pow(seg.B.Y - seg.A.Y, 2));
                        totalPathLength += segLen;
                    }
            
                    if (totalPathLength < 0.001)
                    {
                        // Path too short, just do a simple pen down/up
                        sb.AppendLine($"; Path too short for brush profile, using simple pen");
                        sb.AppendLine($"G0 Z{Fmt(zDownCmd)}");
                        sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
                        return;
                    }
            
                    // Use profile's feed rate if specified, otherwise use default
                    var strokeSpeed = profile.StrokeSpeed > 0 ? profile.StrokeSpeed : feedXY;
            
                    // Generate Z samples along the path
                    var sampleCount = Math.Max(20, Math.Min(profile.SampleCount, 200)); // Reasonable range
            
                    sb.AppendLine($"; Brush profile: {profile.Name}, {sampleCount} samples over {totalPathLength:F1}mm");
            
                    bool firstMove = true;
            
                    for (int sample = 0; sample <= sampleCount; sample++)
                    {
                        // Calculate normalized position (0 to 1) along the path
                        var t = (double)sample / sampleCount;
                
                        // Get Z value from profile curve (0=top/up, 1=bottom/down)
                        var profileY = profile.InterpolateY(t);
                
                        // Map profile Y to actual Z command
                        // profileY=0 should give us zUpCmd (pen up), profileY=1 should give us zDownCmd (pen down)
                        var zCmd = zUpCmd + profileY * (zDownCmd - zUpCmd);
                
                        // Calculate XY position at this sample point
                        var targetDistance = t * totalPathLength;
                
                        // Find which segment this distance falls into
                        double accumulatedLength = 0;
                        PointMm pos = path[0].A;
                
                        for (int i = 0; i < path.Count; i++)
                        {
                            var seg = path[i];
                            var segLen = Math.Sqrt(Math.Pow(seg.B.X - seg.A.X, 2) + Math.Pow(seg.B.Y - seg.A.Y, 2));
                    
                            if (accumulatedLength + segLen >= targetDistance || i == path.Count - 1)
                            {
                                // This segment contains our target point
                                var distIntoSeg = targetDistance - accumulatedLength;
                                var segT = segLen > 0.001 ? Math.Min(1.0, distIntoSeg / segLen) : 0;
                        
                                pos = new PointMm(
                                    seg.A.X + segT * (seg.B.X - seg.A.X),
                                    seg.A.Y + segT * (seg.B.Y - seg.A.Y)
                                );
                                break;
                            }
                            accumulatedLength += segLen;
                        }
                
                        // Output the G-code move
                        if (firstMove)
                        {
                            sb.AppendLine($"G1 X{Fmt(pos.X)} Y{Fmt(pos.Y)} Z{Fmt(zCmd)} F{Fmt(strokeSpeed)} ; t={t:F3}");
                            firstMove = false;
                        }
                        else
                        {
                            sb.AppendLine($"G1 X{Fmt(pos.X)} Y{Fmt(pos.Y)} Z{Fmt(zCmd)} ; t={t:F3}");
                        }
                    }
            
                    // Ensure we end at the path endpoint with pen up
                    var lastSeg = path[^1];
                    sb.AppendLine($"G0 X{Fmt(lastSeg.B.X)} Y{Fmt(lastSeg.B.Y)} ; Ensure endpoint");
                    sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
            
                    // Update distance traveled for paint refresh logic
                    distanceTraveled += totalPathLength;
                }

                /// <summary>
                /// Builds paths from pre-transformed strokes with brush profiles, grouping by PaintOrder.
                /// </summary>
                private static List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>> BuildTransformedPathsByPaintOrderWithProfiles(
                    List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> strokes, 
                    double tol)
                {
                    var result = new List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>>();
                    List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>? current = null;
                    long currentPaintOrder = -1;

                    foreach (var stroke in strokes)
                    {
                        if (current == null)
                        {
                            current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> { stroke };
                            currentPaintOrder = stroke.PaintOrder;
                            result.Add(current);
                            continue;
                        }

                        if (stroke.PaintOrder != currentPaintOrder)
                        {
                            current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> { stroke };
                            currentPaintOrder = stroke.PaintOrder;
                            result.Add(current);
                            continue;
                        }

                        var prev = current[^1];
                        var dist = Math.Sqrt(Math.Pow(prev.B.X - stroke.A.X, 2) + Math.Pow(prev.B.Y - stroke.A.Y, 2));
                        if (dist <= tol)
                        {
                            current.Add(stroke);
                        }
                        else
                        {
                            current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> { stroke };
                            result.Add(current);
                        }
                    }

                    return result;
                }

                /// <summary>
                /// Builds paths from pre-transformed strokes with brush profiles, grouping by color.
                /// </summary>
                private static List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>> BuildTransformedPathsByColorWithProfiles(
                    List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> strokes, 
                    double tol)
                {
                    var result = new List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>>();
                    List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)>? current = null;
                    Guid? currentColorId = null;

                    foreach (var stroke in strokes)
                    {
                        if (current == null)
                        {
                            current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> { stroke };
                            currentColorId = stroke.PaintWellId;
                            result.Add(current);
                            continue;
                        }

                        if (stroke.PaintWellId != currentColorId)
                        {
                            current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> { stroke };
                            currentColorId = stroke.PaintWellId;
                            result.Add(current);
                            continue;
                        }

                        var prev = current[^1];
                        var dist = Math.Sqrt(Math.Pow(prev.B.X - stroke.A.X, 2) + Math.Pow(prev.B.Y - stroke.A.Y, 2));
                        if (dist <= tol)
                        {
                            current.Add(stroke);
                        }
                        else
                        {
                            current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder, HashSet<string>? EnabledBrushProfiles)> { stroke };
                            result.Add(current);
                        }
                    }

                    return result;
                }

                /// <summary>
                /// Builds paths from pre-transformed strokes, grouping connected strokes with same PaintOrder.
                /// This ensures that strokes painted at different times are kept separate even if they have the same color.
                /// </summary>
                private static List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)>> BuildTransformedPathsByPaintOrder(
            List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> strokes, 
            double tol)
        {
            var result = new List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)>>();
            List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)>? current = null;
            long currentPaintOrder = -1;

            foreach (var stroke in strokes)
            {
                if (current == null)
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> { stroke };
                    currentPaintOrder = stroke.PaintOrder;
                    result.Add(current);
                    continue;
                }

                // PaintOrder changed? This means a different color assignment action
                if (stroke.PaintOrder != currentPaintOrder)
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> { stroke };
                    currentPaintOrder = stroke.PaintOrder;
                    result.Add(current);
                    continue;
                }

                // Same PaintOrder - check if connected
                var prev = current[^1];
                var dist = Math.Sqrt(Math.Pow(prev.B.X - stroke.A.X, 2) + Math.Pow(prev.B.Y - stroke.A.Y, 2));
                if (dist <= tol)
                {
                    current.Add(stroke);
                }
                else
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> { stroke };
                    result.Add(current);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds paths from pre-transformed strokes, grouping connected strokes with same color.
        /// This is the fallback for old projects that don't have PaintOrder set.
        /// </summary>
        private static List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)>> BuildTransformedPathsByColor(
            List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> strokes, 
            double tol)
        {
            var result = new List<List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)>>();
            List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)>? current = null;
            Guid? currentColorId = null;

            foreach (var stroke in strokes)
            {
                if (current == null)
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Color changed?
                if (stroke.PaintWellId != currentColorId)
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Same color - check if connected
                var prev = current[^1];
                var dist = Math.Sqrt(Math.Pow(prev.B.X - stroke.A.X, 2) + Math.Pow(prev.B.Y - stroke.A.Y, 2));
                if (dist <= tol)
                {
                    current.Add(stroke);
                }
                else
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId, long PaintOrder)> { stroke };
                    result.Add(current);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds paths from pre-transformed strokes, grouping connected strokes with same color.
        /// </summary>
        private static List<List<(PointMm A, PointMm B, Guid? PaintWellId)>> BuildTransformedPaths(
            List<(PointMm A, PointMm B, Guid? PaintWellId)> strokes, 
            double tol)
        {
            var result = new List<List<(PointMm A, PointMm B, Guid? PaintWellId)>>();
            List<(PointMm A, PointMm B, Guid? PaintWellId)>? current = null;
            Guid? currentColorId = null;

            foreach (var stroke in strokes)
            {
                if (current == null)
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId)> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Color changed?
                if (stroke.PaintWellId != currentColorId)
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId)> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Same color - check if connected
                var prev = current[^1];
                var dist = Math.Sqrt(Math.Pow(prev.B.X - stroke.A.X, 2) + Math.Pow(prev.B.Y - stroke.A.Y, 2));
                if (dist <= tol)
                {
                    current.Add(stroke);
                }
                else
                {
                    current = new List<(PointMm A, PointMm B, Guid? PaintWellId)> { stroke };
                    result.Add(current);
                }
            }

            return result;
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
