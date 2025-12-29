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

    /// <summary>Project settings</summary>
    public ProjectSettings Settings { get; set; } = new();
}

/// <summary>
/// All project-specific settings that should be saved/loaded with the project
/// </summary>
public sealed class ProjectSettings
{
    // Document settings
    public int PagePresetIndex { get; set; } = 0;
    public int HomeCornerIndex { get; set; } = 0;

    // Snap settings
    public bool SnapEnabled { get; set; } = true;
    public double SnapRadius { get; set; } = 5.0;

    // Grid settings
    public bool ShowGrid { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public double GridSpacing { get; set; } = 5.0;

    // View settings
    public double Zoom { get; set; } = 0.6;
    public double CanvasRotation { get; set; } = 0;

    // Painting mode settings
    public bool PaintModeEnabled { get; set; } = false;
    public bool AutoWashWipe { get; set; } = true;

    // G-code settings
    public double FeedXY { get; set; } = 3000;
    public double ZUp { get; set; } = 10;
    public double ZDown { get; set; } = 2;
    public double SafeMargin { get; set; } = 50;
    public bool ShowMarginOverlay { get; set; } = true;
    public bool OptimizeStrokes { get; set; } = false;
    public string StartGcode { get; set; } = "G21 ; Set units to mm\nG90 ; Absolute positioning\nG94 ; Set feed rate mode to mm/min";
    public string EndGcode { get; set; } = "G0 Z10 ; Raise Z axis\nG0 X0 Y0 ; Return to home";

    // Working area (null if not defined)
    public WorkingAreaData? WorkingArea { get; set; }
}

/// <summary>
/// Serializable working area data
/// </summary>
public sealed class WorkingAreaData
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
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
    public Guid? GroupId { get; set; }
    
    /// <summary>Indicates this stroke contains the START point of a grouped object</summary>
    public bool IsGroupStart { get; set; }
    
    /// <summary>Indicates this stroke contains the END point of a grouped object</summary>
    public bool IsGroupEnd { get; set; }
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
