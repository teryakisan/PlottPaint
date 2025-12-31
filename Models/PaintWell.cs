using System;
using System.Windows;
using System.Windows.Media;

// Avoid ambiguity with System.Drawing types from WindowsForms
using Color = System.Windows.Media.Color;

namespace NVSPlotter.Models;

/// <summary>
/// Represents a paint well (color area) on the canvas that the plotter
/// can return to for paint refresh operations.
/// </summary>
public sealed class PaintWell
{
    /// <summary>Unique identifier for the paint well</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name for the paint well (e.g., "Red", "Blue #1")</summary>
    public string Name { get; set; } = "Untitled";

    /// <summary>Visual color for display and stroke association</summary>
    public Color Color { get; set; } = Colors.Black;

    /// <summary>Position and size on the canvas (in mm)</summary>
    public Rect Bounds { get; set; }

    /// <summary>Rotation angle in radians around the center of bounds</summary>
    public double Rotation { get; set; }

    /// <summary>Z depth to dip into the paint (mm, positive value that will be negated in G-code)</summary>
    public double DipDepth { get; set; } = 5.0;

    /// <summary>Dwell time at dip depth (ms) - how long to hold in paint</summary>
    public int DwellTimeMs { get; set; } = 500;

    /// <summary>Minimum distance in mm before requiring paint refresh (0 = manual/never)</summary>
    public double RefreshDistanceMinMm { get; set; } = 50.0;

    /// <summary>Maximum distance in mm before requiring paint refresh</summary>
    public double RefreshDistanceMaxMm { get; set; } = 150.0;

    /// <summary>
    /// Legacy property for backward compatibility. Gets/sets the max refresh distance.
    /// </summary>
    public double RefreshDistanceMm
    {
        get => RefreshDistanceMaxMm;
        set => RefreshDistanceMaxMm = value;
    }

    /// <summary>Gets the center point of the paint well bounds</summary>
    public PointMm Center => new(Bounds.Left + Bounds.Width / 2.0, Bounds.Top + Bounds.Height / 2.0);

    public PaintWell() { }

    public PaintWell(string name, Color color, Rect bounds)
    {
        Name = name;
        Color = color;
        Bounds = bounds;
    }
}
