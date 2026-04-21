using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using NVSPlotter.Models;

using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace NVSPlotter.Services;

/// <summary>
/// Service that computes "painting strokes" from the document's LineStroke collection.
/// A painting stroke is a sequence of connected line segments that would be painted 
/// as a single brush stroke (between pen-down and pen-up).
/// </summary>
public sealed class PaintingStrokeService
{
    private readonly Func<PlotDocument> _getDocument;
    private readonly PaintWellController _paintWellController;
    private readonly Action<string> _log;
    
    private const double JoinTolerance = 0.5; // mm tolerance for connecting segments
    
    /// <summary>
    /// The computed painting strokes, updated when ComputePaintingStrokes is called.
    /// </summary>
    public List<PaintingStroke> PaintingStrokes { get; private set; } = new();
    
    public PaintingStrokeService(
        Func<PlotDocument> getDocument,
        PaintWellController paintWellController,
        Action<string> log)
    {
        _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
        _paintWellController = paintWellController ?? throw new ArgumentNullException(nameof(paintWellController));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }
    
    /// <summary>
    /// Computes painting strokes from the current document's strokes.
    /// Groups connected segments into continuous paths based on connectivity and paint order.
    /// </summary>
    public void ComputePaintingStrokes()
    {
        var doc = _getDocument();
        var strokes = doc.Strokes;
        PaintingStrokes.Clear();
        
        if (strokes.Count == 0)
            return;
        
        // Sort strokes by PaintOrder (then by original index for stability)
        var orderedStrokes = strokes
            .Select((stroke, index) => (stroke, index))
            .OrderBy(x => x.stroke.PaintOrder)
            .ThenBy(x => x.index)
            .ToList();
        
        var usedIndices = new HashSet<int>();
        int strokeNumber = 1;
        
        foreach (var (startStroke, startIndex) in orderedStrokes)
        {
            if (usedIndices.Contains(startIndex))
                continue;
            
            // Start a new painting stroke
            var paintingStroke = BuildPaintingStroke(
                startStroke, 
                startIndex, 
                orderedStrokes, 
                usedIndices,
                strokeNumber++);
            
            if (paintingStroke.Segments.Count > 0)
            {
                PaintingStrokes.Add(paintingStroke);
            }
        }
        
        _log($"[PAINT STROKE] Computed {PaintingStrokes.Count} painting strokes from {strokes.Count} segments");
    }
    
    /// <summary>
    /// Builds a single painting stroke starting from the given stroke.
    /// Follows connected segments with the same PaintWellId.
    /// </summary>
    private PaintingStroke BuildPaintingStroke(
        LineStroke startStroke,
        int startIndex,
        List<(LineStroke stroke, int index)> orderedStrokes,
        HashSet<int> usedIndices,
        int strokeNumber)
    {
        var segments = new List<LineStroke> { startStroke };
        var segmentIndices = new List<int> { startIndex };
        usedIndices.Add(startIndex);
        
        var currentEnd = startStroke.B;
        var paintWellId = startStroke.PaintWellId;
        var groupId = startStroke.GroupId;
        
        // Follow connected segments in the same group/paint well
        bool foundNext;
        do
        {
            foundNext = false;
            
            foreach (var (stroke, index) in orderedStrokes)
            {
                if (usedIndices.Contains(index))
                    continue;
                
                // Must have same paint well to continue the stroke
                if (stroke.PaintWellId != paintWellId)
                    continue;
                
                // Check connectivity - segment A must connect to current end
                if (IsConnected(currentEnd, stroke.A))
                {
                    segments.Add(stroke);
                    segmentIndices.Add(index);
                    usedIndices.Add(index);
                    currentEnd = stroke.B;
                    foundNext = true;
                    break;
                }
            }
        }
        while (foundNext);
        
        // Get color from paint well
        Color color = Colors.DarkGray;
        string? paintWellName = null;
        
        if (paintWellId.HasValue)
        {
            color = _paintWellController.GetStrokeColor(startStroke);
            var doc = _getDocument();
            var well = doc.PaintWells.FirstOrDefault(w => w.Id == paintWellId.Value);
            paintWellName = well?.Name;
        }
        
        return new PaintingStroke
        {
            StrokeNumber = strokeNumber,
            SegmentIndices = segmentIndices,
            Segments = segments,
            PaintWellId = paintWellId,
            PaintWellName = paintWellName,
            Color = color,
            EnabledBrushProfiles = null // Brush profiles are now assigned in Paint Only mode, not from LineStroke
        };
    }
    
    /// <summary>
    /// Checks if two points are connected within tolerance.
    /// </summary>
    private static bool IsConnected(PointMm a, PointMm b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return dx < JoinTolerance && dy < JoinTolerance;
    }
    
    /// <summary>
    /// Finds the painting stroke that contains the given segment index.
    /// </summary>
    public PaintingStroke? FindStrokeContainingSegment(int segmentIndex)
    {
        return PaintingStrokes.FirstOrDefault(ps => ps.SegmentIndices.Contains(segmentIndex));
    }
    
    /// <summary>
    /// Finds the painting stroke at the given point.
    /// </summary>
    public PaintingStroke? HitTestPaintingStroke(PointMm point, double tolerance = 5.0)
    {
        foreach (var paintingStroke in PaintingStrokes)
        {
            foreach (var segment in paintingStroke.Segments)
            {
                if (PointToSegmentDistance(point, segment.A, segment.B) <= tolerance)
                {
                    return paintingStroke;
                }
            }
        }
        return null;
    }
    
    /// <summary>
    /// Calculates the distance from a point to a line segment.
    /// </summary>
    private static double PointToSegmentDistance(PointMm p, PointMm a, PointMm b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        
        if (lengthSquared < 0.0001)
        {
            // Segment is effectively a point
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        }
        
        // Project point onto line, clamping to segment
        var t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared));
        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;
        
        return Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
    }
}
