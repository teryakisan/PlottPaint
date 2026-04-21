using System;
using System.Collections.Generic;
using System.Windows.Media;

using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace NVSPlotter.Models;

/// <summary>
/// Represents a painting stroke extracted from parsed G-code.
/// This is a continuous sequence of G1 feed moves between pen-down and pen-up,
/// as it will actually be executed by the plotter.
/// </summary>
public sealed class GcodePaintingStroke
{
    /// <summary>
    /// Sequential stroke number (1-based) as it appears in painting order.
    /// </summary>
    public int StrokeNumber { get; init; }
    
    /// <summary>
    /// Indices into the original LineStroke collection that this painting stroke was generated from.
    /// Used to synchronize brush profiles between document strokes and G-code painting strokes.
    /// A single painting stroke may come from multiple connected LineStrokes.
    /// </summary>
    public List<int> SourceStrokeIndices { get; init; } = new();
    
    /// <summary>
    /// The segments that make up this painting stroke (in work coordinates).
    /// Each segment is a line from point A to point B.
    /// </summary>
    public List<GcodeStrokeSegment> Segments { get; init; } = new();
    
    /// <summary>
    /// Name of the paint well this stroke uses (null if no color assigned).
    /// </summary>
    public string? PaintWellName { get; init; }
    
    /// <summary>
    /// Display color for this stroke.
    /// </summary>
    public Color Color { get; init; } = Colors.DarkGray;
    
    /// <summary>
    /// Starting point of the painting stroke (in work coordinates).
    /// </summary>
    public PointMm StartPoint => Segments.Count > 0 
        ? new PointMm(Segments[0].FromX, Segments[0].FromY) 
        : new PointMm(0, 0);
    
    /// <summary>
    /// Ending point of the painting stroke (in work coordinates).
    /// </summary>
    public PointMm EndPoint => Segments.Count > 0 
        ? new PointMm(Segments[^1].ToX, Segments[^1].ToY) 
        : new PointMm(0, 0);
    
    /// <summary>
    /// Total length of all segments in this painting stroke.
    /// </summary>
    public double TotalLength
    {
        get
        {
            double total = 0;
            foreach (var seg in Segments)
            {
                total += seg.Length;
            }
            return total;
        }
    }
    
    /// <summary>
    /// Whether this stroke has brush profiles assigned.
    /// </summary>
    public bool HasBrushProfiles { get; set; }
    
    /// <summary>
    /// Set of brush profile names enabled for this stroke.
    /// </summary>
    public HashSet<string>? EnabledBrushProfiles { get; set; }
}

/// <summary>
/// A single line segment within a G-code painting stroke.
/// Coordinates are in work space (X positive, Y negative convention).
/// </summary>
public sealed class GcodeStrokeSegment
{
    public double FromX { get; init; }
    public double FromY { get; init; }
    public double ToX { get; init; }
    public double ToY { get; init; }
    
    /// <summary>
    /// The G-code line number this segment came from.
    /// </summary>
    public int GcodeLineNumber { get; init; }
    
    /// <summary>
    /// Length of this segment in mm.
    /// </summary>
    public double Length => Math.Sqrt(
        Math.Pow(ToX - FromX, 2) + Math.Pow(ToY - FromY, 2));
}
