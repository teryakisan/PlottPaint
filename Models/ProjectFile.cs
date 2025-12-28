using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NVSPlotter.Models;

/// <summary>
/// Serializable project file format for saving/loading NVSPlotter projects.
/// </summary>
public sealed class ProjectFile
{
    /// <summary>File format version for backward compatibility</summary>
    public int Version { get; set; } = 1;

    /// <summary>Document width in mm</summary>
    public double WidthMm { get; set; }

    /// <summary>Document height in mm</summary>
    public double HeightMm { get; set; }

    /// <summary>All strokes in the document</summary>
    public List<StrokeData> Strokes { get; set; } = new();

    /// <summary>All paint wells in the document</summary>
    public List<PaintWellData> PaintWells { get; set; } = new();
}

/// <summary>
/// Serializable stroke data
/// </summary>
public sealed class StrokeData
{
    public double Ax { get; set; }
    public double Ay { get; set; }
    public double Bx { get; set; }
    public double By { get; set; }
    public Guid? PaintWellId { get; set; }
}

/// <summary>
/// Serializable paint well data
/// </summary>
public sealed class PaintWellData
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Untitled";
    
    // Color stored as ARGB components
    public byte ColorA { get; set; }
    public byte ColorR { get; set; }
    public byte ColorG { get; set; }
    public byte ColorB { get; set; }
    
    // Bounds
    public double BoundsLeft { get; set; }
    public double BoundsTop { get; set; }
    public double BoundsWidth { get; set; }
    public double BoundsHeight { get; set; }
    
    // Paint parameters
    public double DipDepth { get; set; }
    public int DwellTimeMs { get; set; }
    public double RefreshDistanceMinMm { get; set; }
    public double RefreshDistanceMaxMm { get; set; }
}
