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
