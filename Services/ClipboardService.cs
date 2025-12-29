using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using NVSPlotter.Models;

// Disambiguate between Windows.Forms and WPF types
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace NVSPlotter.Services;

/// <summary>
/// Manages clipboard operations for strokes and paint wells.
/// Supports copy, cut, and paste with proper offset for paste positioning.
/// </summary>
public sealed class ClipboardService
{
    private const string CLIPBOARD_FORMAT = "NVSPlotter.Selection";
    private const double PASTE_OFFSET = 20.0; // mm offset for each subsequent paste

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Data structure stored in clipboard
    /// </summary>
    private sealed class ClipboardData
    {
        public List<StrokeClipboardData> Strokes { get; set; } = new();
        public List<PaintWellClipboardData> PaintWells { get; set; } = new();
        public double BoundsX { get; set; }
        public double BoundsY { get; set; }
        public double BoundsWidth { get; set; }
        public double BoundsHeight { get; set; }
    }

    private sealed class StrokeClipboardData
    {
        public double Ax { get; set; }
        public double Ay { get; set; }
        public double Bx { get; set; }
        public double By { get; set; }
        public Guid? PaintWellId { get; set; }
        public Guid? GroupId { get; set; }
        public bool IsGroupStart { get; set; }
        public bool IsGroupEnd { get; set; }
    }

    private sealed class PaintWellClipboardData
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public byte ColorA { get; set; }
        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
        public double BoundsLeft { get; set; }
        public double BoundsTop { get; set; }
        public double BoundsWidth { get; set; }
        public double BoundsHeight { get; set; }
        public double DipDepth { get; set; }
        public int DwellTimeMs { get; set; }
        public double RefreshDistanceMinMm { get; set; }
        public double RefreshDistanceMaxMm { get; set; }
    }

    private int _pasteCount; // Tracks number of pastes for offset

    /// <summary>
    /// Copies the specified strokes and paint wells to the clipboard.
    /// </summary>
    public void CopyToClipboard(
        IEnumerable<LineStroke> strokes,
        IEnumerable<PaintWell> paintWells,
        Rect bounds)
    {
        var data = new ClipboardData
        {
            BoundsX = bounds.X,
            BoundsY = bounds.Y,
            BoundsWidth = bounds.Width,
            BoundsHeight = bounds.Height
        };

        // Convert strokes
        foreach (var stroke in strokes)
        {
            data.Strokes.Add(new StrokeClipboardData
            {
                Ax = stroke.A.X,
                Ay = stroke.A.Y,
                Bx = stroke.B.X,
                By = stroke.B.Y,
                PaintWellId = stroke.PaintWellId,
                GroupId = stroke.GroupId,
                IsGroupStart = stroke.IsGroupStart,
                IsGroupEnd = stroke.IsGroupEnd
            });
        }

        // Convert paint wells
        foreach (var well in paintWells)
        {
            data.PaintWells.Add(new PaintWellClipboardData
            {
                Id = well.Id,
                Name = well.Name,
                ColorA = well.Color.A,
                ColorR = well.Color.R,
                ColorG = well.Color.G,
                ColorB = well.Color.B,
                BoundsLeft = well.Bounds.Left,
                BoundsTop = well.Bounds.Top,
                BoundsWidth = well.Bounds.Width,
                BoundsHeight = well.Bounds.Height,
                DipDepth = well.DipDepth,
                DwellTimeMs = well.DwellTimeMs,
                RefreshDistanceMinMm = well.RefreshDistanceMinMm,
                RefreshDistanceMaxMm = well.RefreshDistanceMaxMm
            });
        }

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var dataObject = new DataObject();
            dataObject.SetData(CLIPBOARD_FORMAT, json);
            Clipboard.SetDataObject(dataObject, true);
            _pasteCount = 0; // Reset paste counter on new copy
        }
        catch (Exception)
        {
            // Clipboard operations can fail if clipboard is locked
        }
    }

    /// <summary>
    /// Checks if the clipboard contains pasteable data.
    /// </summary>
    public bool HasClipboardData()
    {
        try
        {
            var dataObject = Clipboard.GetDataObject();
            return dataObject?.GetDataPresent(CLIPBOARD_FORMAT) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pastes clipboard data, returning the new strokes and paint wells.
    /// Items are offset from their original position for visibility.
    /// </summary>
    public (List<LineStroke> Strokes, List<PaintWell> PaintWells)? PasteFromClipboard()
    {
        try
        {
            var dataObject = Clipboard.GetDataObject();
            if (dataObject?.GetData(CLIPBOARD_FORMAT) is not string json)
                return null;

            var data = JsonSerializer.Deserialize<ClipboardData>(json, JsonOptions);
            if (data == null)
                return null;

            _pasteCount++;
            var offset = PASTE_OFFSET * _pasteCount;

            var strokes = new List<LineStroke>();
            var paintWells = new List<PaintWell>();

            // Remap group IDs to new GUIDs so pasted items form their own groups
            var groupIdMap = new Dictionary<Guid, Guid>();

            foreach (var strokeData in data.Strokes)
            {
                // Remap group ID if present
                Guid? newGroupId = null;
                if (strokeData.GroupId.HasValue)
                {
                    if (!groupIdMap.TryGetValue(strokeData.GroupId.Value, out var mapped))
                    {
                        mapped = Guid.NewGuid();
                        groupIdMap[strokeData.GroupId.Value] = mapped;
                    }
                    newGroupId = mapped;
                }

                var stroke = new LineStroke(
                    new PointMm(strokeData.Ax + offset, strokeData.Ay + offset),
                    new PointMm(strokeData.Bx + offset, strokeData.By + offset))
                {
                    PaintWellId = strokeData.PaintWellId,
                    GroupId = newGroupId,
                    IsGroupStart = strokeData.IsGroupStart,
                    IsGroupEnd = strokeData.IsGroupEnd
                };
                strokes.Add(stroke);
            }

            // Process paint wells from clipboard data
            foreach (var wellData in data.PaintWells)
            {
                var well = new PaintWell
                {
                    Id = Guid.NewGuid(), // New ID for pasted well
                    Name = wellData.Name + " (copy)",
                    Color = System.Windows.Media.Color.FromArgb(
                        wellData.ColorA, wellData.ColorR, wellData.ColorG, wellData.ColorB),
                    Bounds = new Rect(
                        wellData.BoundsLeft + offset,
                        wellData.BoundsTop + offset,
                        wellData.BoundsWidth,
                        wellData.BoundsHeight),
                    DipDepth = wellData.DipDepth,
                    DwellTimeMs = wellData.DwellTimeMs,
                    RefreshDistanceMinMm = wellData.RefreshDistanceMinMm,
                    RefreshDistanceMaxMm = wellData.RefreshDistanceMaxMm
                };
                paintWells.Add(well);
            }

            return (strokes, paintWells);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resets the paste counter (called when selection changes or new copy is made).
    /// </summary>
    public void ResetPasteOffset()
    {
        _pasteCount = 0;
    }
}
