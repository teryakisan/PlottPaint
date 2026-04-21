using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using NVSPlotter.Models;

using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace NVSPlotter.Services;

/// <summary>
/// Service that parses G-code and extracts painting strokes.
/// A painting stroke is a continuous sequence of G1 feed moves between pen-down and pen-up.
/// This provides the data needed for "Paint Only" view mode.
/// </summary>
public sealed class GcodePaintingStrokeService
{
    private readonly Action<string> _log;
    
    /// <summary>
    /// The painting strokes extracted from the last parsed G-code.
    /// </summary>
    public List<GcodePaintingStroke> PaintingStrokes { get; private set; } = new();
    
    /// <summary>
    /// Whether the last parsed G-code was in painting mode.
    /// </summary>
    public bool IsPaintingModeGcode { get; private set; }
    
    /// <summary>
    /// Paint wells parsed from the G-code.
    /// </summary>
    public Dictionary<string, ParsedPaintWellInfo> PaintWells { get; } = new();
    
    public GcodePaintingStrokeService(Action<string> log)
    {
        _log = log ?? (_ => { });
    }
    
    /// <summary>
    /// Parses G-code and extracts painting strokes.
    /// Call this after generating G-code but before rendering Paint Only view.
    /// </summary>
    public void ParseGcode(string gcode)
    {
        PaintingStrokes.Clear();
        PaintWells.Clear();
        IsPaintingModeGcode = false;
        
        if (string.IsNullOrWhiteSpace(gcode))
        {
            _log("[GCODE PAINT] No G-code to parse");
            return;
        }
        
        var lines = gcode.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        double currentX = 0, currentY = 0, currentZ = 0;
        bool isRapid = true;
        int lineNumber = 0;
        
        // Paint well tracking
        string? currentPaintWellName = null;
        int paintingStrokeNumber = 0;
        bool penIsDown = false;
        double zUpValue = 0;
        double zDownValue = 0;
        bool zValuesDetected = false;
        
        // Regex patterns
        var xPattern = new Regex(@"X(-?\d+\.?\d*)", RegexOptions.IgnoreCase);
        var yPattern = new Regex(@"Y(-?\d+\.?\d*)", RegexOptions.IgnoreCase);
        var zPattern = new Regex(@"Z(-?\d+\.?\d*)", RegexOptions.IgnoreCase);
        
        var paintWellPattern = new Regex(@";\s*Paint\s*Well:\s*(\w+)\s+at\s*\((-?\d+\.?\d*),\s*(-?\d+\.?\d*)\)", RegexOptions.IgnoreCase);
        var paintWellSwitchPattern = new Regex(@";\s*===\s*Paint\s*Well:\s*(\w+)(?:\s*\([^)]*\))?\s*===", RegexOptions.IgnoreCase);
        var colorChangePattern = new Regex(@";\s*===\s*Color\s*change:.*->\s*(\w+)(?:\s*\([^)]*\))?\s*===", RegexOptions.IgnoreCase);
        var noPaintWellPattern = new Regex(@";\s*===\s*No\s*Paint\s*Well", RegexOptions.IgnoreCase);
        
        // First pass: detect painting mode and Z values
        var zValues = new List<double>();
        string? pendingWellName = null;
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            if (trimmed.Contains("PAINTING MODE ENABLED", StringComparison.OrdinalIgnoreCase))
            {
                IsPaintingModeGcode = true;
            }
            
            var wellMatch = paintWellPattern.Match(trimmed);
            if (wellMatch.Success)
            {
                var wellName = wellMatch.Groups[1].Value;
                if (!PaintWells.ContainsKey(wellName))
                {
                    PaintWells[wellName] = new ParsedPaintWellInfo
                    {
                        Name = wellName,
                        Color = GetColorForPaintWellName(wellName)
                    };
                }
            }
            
            if (trimmed.Contains("; Dip in paint well:", StringComparison.OrdinalIgnoreCase))
            {
                var dipMatch = Regex.Match(trimmed, @";\s*Dip\s+in\s+paint\s+well:\s*(\w+)", RegexOptions.IgnoreCase);
                if (dipMatch.Success)
                {
                    pendingWellName = dipMatch.Groups[1].Value;
                }
            }
            
            if (trimmed.Contains("; Move to paint well", StringComparison.OrdinalIgnoreCase) && pendingWellName != null)
            {
                var commandPart = trimmed.Split(';')[0].Trim();
                if (!string.IsNullOrEmpty(commandPart))
                {
                    var xMatch = xPattern.Match(commandPart);
                    var yMatch = yPattern.Match(commandPart);
                    if (xMatch.Success && yMatch.Success &&
                        double.TryParse(xMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wx) &&
                        double.TryParse(yMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wy))
                    {
                        if (PaintWells.TryGetValue(pendingWellName, out var well))
                        {
                            well.X = wx;
                            well.Y = wy;
                        }
                    }
                }
                pendingWellName = null;
            }
            
            if (!trimmed.StartsWith(";"))
            {
                var zMatch = zPattern.Match(trimmed);
                if (zMatch.Success && double.TryParse(zMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    if (!zValues.Contains(z))
                        zValues.Add(z);
                }
            }
        }
        
        // Determine Z up/down values
        if (zValues.Count >= 2)
        {
            zValues.Sort();
            zUpValue = zValues.First();
            zDownValue = zValues.Last();
            zValuesDetected = true;
        }
        
        // Second pass: extract segments and group into strokes
        var currentStrokeSegments = new List<GcodeStrokeSegment>();
        string? currentStrokePaintWell = null;
        
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            
            // Track paint well switches
            var wellSwitchMatch = paintWellSwitchPattern.Match(line);
            if (wellSwitchMatch.Success)
            {
                currentPaintWellName = wellSwitchMatch.Groups[1].Value;
                if (!PaintWells.ContainsKey(currentPaintWellName))
                {
                    PaintWells[currentPaintWellName] = new ParsedPaintWellInfo
                    {
                        Name = currentPaintWellName,
                        Color = GetColorForPaintWellName(currentPaintWellName)
                    };
                }
                continue;
            }
            
            var colorChangeMatch = colorChangePattern.Match(line);
            if (colorChangeMatch.Success)
            {
                currentPaintWellName = colorChangeMatch.Groups[1].Value;
                if (!PaintWells.ContainsKey(currentPaintWellName))
                {
                    PaintWells[currentPaintWellName] = new ParsedPaintWellInfo
                    {
                        Name = currentPaintWellName,
                        Color = GetColorForPaintWellName(currentPaintWellName)
                    };
                }
                continue;
            }
            
            if (noPaintWellPattern.IsMatch(line))
            {
                currentPaintWellName = null;
                continue;
            }
            
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";"))
                continue;
            
            var commentIndex = line.IndexOf(';');
            var command = commentIndex >= 0 ? line.Substring(0, commentIndex).Trim() : line;
            
            if (string.IsNullOrWhiteSpace(command))
                continue;
            
            // Track G0/G1
            if (command.StartsWith("G0", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("G00", StringComparison.OrdinalIgnoreCase))
            {
                isRapid = true;
            }
            else if (command.StartsWith("G1", StringComparison.OrdinalIgnoreCase) ||
                     command.StartsWith("G01", StringComparison.OrdinalIgnoreCase))
            {
                isRapid = false;
            }
            
            // Parse coordinates
            double? newX = null, newY = null, newZ = null;
            
            var xMatch = xPattern.Match(command);
            if (xMatch.Success && double.TryParse(xMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                newX = x;
            
            var yMatch = yPattern.Match(command);
            if (yMatch.Success && double.TryParse(yMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                newY = y;
            
            var zMatch = zPattern.Match(command);
            if (zMatch.Success && double.TryParse(zMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                newZ = z;
            
            // Track pen up/down state
            if (newZ.HasValue && zValuesDetected)
            {
                var wasPenDown = penIsDown;
                penIsDown = newZ.Value > zUpValue + 0.5;
                
                // Pen going down - start new stroke
                if (!wasPenDown && penIsDown)
                {
                    paintingStrokeNumber++;
                    currentStrokePaintWell = currentPaintWellName;
                    currentStrokeSegments.Clear();
                }
                
                // Pen going up - finish current stroke
                if (wasPenDown && !penIsDown && currentStrokeSegments.Count > 0)
                {
                    FinishStroke(paintingStrokeNumber, currentStrokePaintWell, currentStrokeSegments);
                    currentStrokeSegments = new List<GcodeStrokeSegment>();
                }
            }
            
            // Create segment if we have X/Y movement while pen is down
            if ((newX.HasValue || newY.HasValue) && !isRapid && penIsDown)
            {
                var fromX = currentX;
                var fromY = currentY;
                
                if (newX.HasValue) currentX = newX.Value;
                if (newY.HasValue) currentY = newY.Value;
                if (newZ.HasValue) currentZ = newZ.Value;
                
                currentStrokeSegments.Add(new GcodeStrokeSegment
                {
                    FromX = fromX,
                    FromY = fromY,
                    ToX = currentX,
                    ToY = currentY,
                    GcodeLineNumber = lineNumber
                });
            }
            else if (newX.HasValue || newY.HasValue)
            {
                // Update position even for rapids
                if (newX.HasValue) currentX = newX.Value;
                if (newY.HasValue) currentY = newY.Value;
            }
            
            if (newZ.HasValue)
                currentZ = newZ.Value;
        }
        
        // Finish any remaining stroke
        if (currentStrokeSegments.Count > 0)
        {
            FinishStroke(paintingStrokeNumber, currentStrokePaintWell, currentStrokeSegments);
        }
        
        _log($"[GCODE PAINT] Parsed {PaintingStrokes.Count} painting strokes from G-code");
    }
    
    private void FinishStroke(int strokeNumber, string? paintWellName, List<GcodeStrokeSegment> segments)
    {
        if (segments.Count == 0) return;
        
        Color color = Colors.DarkGray;
        if (paintWellName != null && PaintWells.TryGetValue(paintWellName, out var well))
        {
            color = well.Color;
        }
        
        PaintingStrokes.Add(new GcodePaintingStroke
        {
            StrokeNumber = strokeNumber,
            Segments = new List<GcodeStrokeSegment>(segments),
            PaintWellName = paintWellName,
            Color = color
        });
    }
    
    /// <summary>
    /// Hit tests a point against the painting strokes.
    /// Returns the stroke that was hit, or null if no stroke was hit.
    /// </summary>
    public GcodePaintingStroke? HitTest(double x, double y, double tolerance = 5.0)
    {
        foreach (var stroke in PaintingStrokes)
        {
            foreach (var segment in stroke.Segments)
            {
                if (PointToSegmentDistance(x, y, segment) <= tolerance)
                {
                    return stroke;
                }
            }
        }
        return null;
    }
    
    private static double PointToSegmentDistance(double px, double py, GcodeStrokeSegment seg)
    {
        var dx = seg.ToX - seg.FromX;
        var dy = seg.ToY - seg.FromY;
        var lengthSquared = dx * dx + dy * dy;
        
        if (lengthSquared < 0.0001)
        {
            return Math.Sqrt((px - seg.FromX) * (px - seg.FromX) + (py - seg.FromY) * (py - seg.FromY));
        }
        
        var t = Math.Max(0, Math.Min(1, ((px - seg.FromX) * dx + (py - seg.FromY) * dy) / lengthSquared));
        var projX = seg.FromX + t * dx;
        var projY = seg.FromY + t * dy;
        
        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }
    
    /// <summary>
    /// Gets a color for a paint well based on its name.
    /// </summary>
    public static Color GetColorForPaintWellName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "red" => Colors.Red,
            "blue" => Colors.Blue,
            "green" => Colors.Green,
            "orange" => Colors.Orange,
            "purple" => Colors.Purple,
            "cyan" => Colors.Cyan,
            "magenta" => Colors.Magenta,
            "yellow" => Colors.Yellow,
            "brown" => Colors.Brown,
            "pink" => Colors.HotPink,
            "black" => Colors.Black,
            "white" => Colors.White,
            "gray" or "grey" => Colors.Gray,
            "wash" => Colors.DarkBlue,
            "wipe" => Colors.DarkGray,
            _ => Colors.DarkGray
        };
    }
}

/// <summary>
/// Paint well information parsed from G-code.
/// </summary>
public sealed class ParsedPaintWellInfo
{
    public string Name { get; set; } = "";
    public Color Color { get; set; } = Colors.Black;
    public double X { get; set; }
    public double Y { get; set; }
    public bool HasPosition => X != 0 || Y != 0;
}
