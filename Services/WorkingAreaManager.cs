using NVSPlotter.Models;
using System;
using System.Windows;

namespace NVSPlotter.Services;

public sealed class WorkingAreaManager
{
    public Rect? DefinedArea { get; private set; }
    public Rect? PreviewArea { get; private set; }
    public bool IsDefining { get; private set; }
    public bool IsDragging { get; private set; }
    public PointMm DragStart { get; private set; }

    public void BeginDefinition()
    {
        IsDefining = true;
        PreviewArea = null;
    }

    public void BeginDrag(PointMm start)
    {
        IsDragging = true;
        DragStart = start;
        PreviewArea = new Rect(start.X, start.Y, 0, 0);
    }

    public Rect UpdateDrag(PointMm current)
    {
        if (!IsDragging)
            throw new InvalidOperationException("Working area drag not started.");

        var left = Math.Min(DragStart.X, current.X);
        var top = Math.Min(DragStart.Y, current.Y);
        var width = Math.Max(1, Math.Abs(DragStart.X - current.X));
        var height = Math.Max(1, Math.Abs(DragStart.Y - current.Y));
        var rect = new Rect(left, top, width, height);
        PreviewArea = rect;
        return rect;
    }

    public Rect CompleteDrag(PointMm end)
    {
        if (!IsDragging)
            throw new InvalidOperationException("Working area drag not started.");

        var rect = UpdateDrag(end);
        DefinedArea = rect;
        IsDragging = false;
        IsDefining = false;
        return rect;
    }

    public void CancelDrag()
    {
        IsDragging = false;
        PreviewArea = null;
    }

    public void Clear()
    {
        DefinedArea = null;
        PreviewArea = null;
        IsDefining = false;
        IsDragging = false;
    }

    public void SetArea(Rect area)
    {
        DefinedArea = area;
        PreviewArea = null;
        IsDefining = false;
        IsDragging = false;
    }

    /// <summary>
    /// Sets the working area with boundary constraints to ensure it doesn't extend past the drawing area.
    /// The area is clamped and trimmed to fit within the document bounds.
    /// </summary>
    /// <param name="area">The desired area in canvas coordinates (includes ruler thickness offset)</param>
    /// <param name="docWidthMm">The document width in mm</param>
    /// <param name="docHeightMm">The document height in mm</param>
    public void SetArea(Rect area, double docWidthMm, double docHeightMm)
    {
        const double rulerThickness = 18.0;
        
        // Calculate the maximum bounds in canvas coordinates
        var maxRight = rulerThickness + docWidthMm;
        var maxBottom = rulerThickness + docHeightMm;
        
        // Clamp the position to be within the document area
        var x = Math.Max(rulerThickness, Math.Min(area.X, maxRight));
        var y = Math.Max(rulerThickness, Math.Min(area.Y, maxBottom));
        
        // Trim the width and height so the area doesn't extend past the document bounds
        var width = Math.Max(0, Math.Min(area.Width, maxRight - x));
        var height = Math.Max(0, Math.Min(area.Height, maxBottom - y));
        
        // Only set the area if it has positive dimensions
        if (width > 0 && height > 0)
        {
            DefinedArea = new Rect(x, y, width, height);
        }
        
        PreviewArea = null;
        IsDefining = false;
        IsDragging = false;
    }

    public string GetStatusText()
    {
        if (IsDefining)
        {
            return "Click and drag on canvas";
        }

        if (DefinedArea is Rect rect)
        {
            return $"{rect.Width:0.#} x {rect.Height:0.#} mm at ({rect.X:0.#}, {rect.Y:0.#})";
        }

        return "Not defined";
    }
}
