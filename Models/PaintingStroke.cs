using System;
using System.Collections.Generic;
using System.Windows.Media;

using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace NVSPlotter.Models;

/// <summary>
/// Represents a "painting stroke" - a continuous sequence of connected line segments 
/// that would be painted without lifting the brush (between pen-down and pen-up).
/// This is used in Paint Only view mode to display strokes as they will be painted.
/// </summary>
public sealed class PaintingStroke
{
    /// <summary>
    /// Unique identifier for this painting stroke.
    /// </summary>
    public int StrokeNumber { get; init; }
    
    /// <summary>
    /// The indices of LineStroke items in the document that make up this painting stroke.
    /// </summary>
    public List<int> SegmentIndices { get; init; } = new();
    
    /// <summary>
    /// The actual LineStroke segments that make up this painting stroke.
    /// </summary>
    public List<LineStroke> Segments { get; init; } = new();
    
    /// <summary>
    /// Paint well ID associated with this stroke (null if no color assigned).
    /// </summary>
    public Guid? PaintWellId { get; init; }
    
    /// <summary>
    /// Paint well name for display.
    /// </summary>
    public string? PaintWellName { get; init; }
    
    /// <summary>
    /// Display color for this stroke.
    /// </summary>
    public Color Color { get; init; } = Colors.DarkGray;
    
    /// <summary>
    /// Starting point of the painting stroke.
    /// </summary>
    public PointMm StartPoint => Segments.Count > 0 ? Segments[0].A : new PointMm(0, 0);
    
    /// <summary>
    /// Ending point of the painting stroke.
    /// </summary>
    public PointMm EndPoint => Segments.Count > 0 ? Segments[^1].B : new PointMm(0, 0);
    
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
                var dx = seg.B.X - seg.A.X;
                var dy = seg.B.Y - seg.A.Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
            return total;
        }
    }
    
    /// <summary>
    /// Set of brush profile names enabled for this painting stroke.
    /// Derived from the first segment's EnabledBrushProfiles.
    /// </summary>
    public HashSet<string>? EnabledBrushProfiles { get; init; }
    
    /// <summary>
    /// Whether this painting stroke has brush profiles assigned.
    /// </summary>
    public bool HasBrushProfiles => EnabledBrushProfiles != null && EnabledBrushProfiles.Count > 0;
    
    /// <summary>
    /// Whether this stroke is currently selected in Paint Only view.
    /// </summary>
    public bool IsSelected { get; set; }
    
    /// <summary>
    /// Whether this stroke is currently hovered in Paint Only view.
    /// </summary>
    public bool IsHovered { get; set; }
}
