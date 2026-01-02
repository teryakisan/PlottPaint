using NVSPlotter.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

// Avoid ambiguity with System.Drawing types
using Color = System.Windows.Media.Color;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Result of a project load operation.
    /// </summary>
    public sealed class ProjectLoadResult
    {
        public PlotDocument Document { get; set; } = null!;
        public List<PaintWell> PaintWells { get; set; } = new();
        public ProjectSettings Settings { get; set; } = new();
        public Rect? WorkingArea { get; set; }
    }

    /// <summary>
    /// Handles project file save/load operations.
    /// </summary>
    public sealed class ProjectFileService
    {
        private readonly Action<string> _log;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Initializes the project file service.
        /// </summary>
        /// <param name="log">Logging callback</param>
        public ProjectFileService(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Saves a project to a file.
        /// </summary>
        /// <param name="filePath">Path to save to</param>
        /// <param name="document">The document to save</param>
        /// <param name="settings">Project settings</param>
        /// <param name="workingArea">Optional working area</param>
        public void SaveProject(
            string filePath,
            PlotDocument document,
            ProjectSettings settings,
            Rect? workingArea = null)
        {
            var projectFile = new ProjectFile
            {
                Version = 1,
                WidthMm = document.WidthMm,
                HeightMm = document.HeightMm
            };

            // Convert strokes to serializable format
            foreach (var stroke in document.Strokes)
            {
                projectFile.Strokes.Add(new StrokeData
                {
                    Ax = stroke.A.X,
                    Ay = stroke.A.Y,
                    Bx = stroke.B.X,
                    By = stroke.B.Y,
                    PaintWellId = stroke.PaintWellId,
                    GroupId = stroke.GroupId,
                    PaintOrder = stroke.PaintOrder,
                    IsGroupStart = stroke.IsGroupStart,
                    IsGroupEnd = stroke.IsGroupEnd
                });
            }

            // Convert paint wells to serializable format
            foreach (var well in document.PaintWells)
            {
                projectFile.PaintWells.Add(new PaintWellData
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

            // Save settings
            projectFile.Settings = settings;

            // Save working area if defined
            if (workingArea is Rect area)
            {
                projectFile.Settings.WorkingArea = new WorkingAreaData
                {
                    Left = area.Left,
                    Top = area.Top,
                    Width = area.Width,
                    Height = area.Height
                };
            }

            var json = JsonSerializer.Serialize(projectFile, JsonOptions);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            _log($"Project saved: {filePath} ({document.Strokes.Count} strokes, {document.PaintWells.Count} paint wells)");
        }

        /// <summary>
        /// Loads a project from a file.
        /// </summary>
        /// <param name="filePath">Path to load from</param>
        /// <returns>The loaded project data</returns>
        public ProjectLoadResult LoadProject(string filePath)
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var projectFile = JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions);

            if (projectFile == null)
            {
                throw new InvalidOperationException("Failed to parse project file.");
            }

            // Create new document with loaded dimensions
            var document = new PlotDocument(projectFile.WidthMm, projectFile.HeightMm);

            // Load paint wells first (strokes reference them by ID)
            var paintWells = new List<PaintWell>();
            foreach (var wellData in projectFile.PaintWells)
            {
                var well = new PaintWell
                {
                    Id = wellData.Id,
                    Name = wellData.Name,
                    Color = Color.FromArgb(wellData.ColorA, wellData.ColorR, wellData.ColorG, wellData.ColorB),
                    Bounds = new Rect(wellData.BoundsLeft, wellData.BoundsTop, wellData.BoundsWidth, wellData.BoundsHeight),
                    DipDepth = wellData.DipDepth,
                    DwellTimeMs = wellData.DwellTimeMs,
                    RefreshDistanceMinMm = wellData.RefreshDistanceMinMm,
                    RefreshDistanceMaxMm = wellData.RefreshDistanceMaxMm
                };
                document.PaintWells.Add(well);
                paintWells.Add(well);
            }

            // Load strokes
            foreach (var strokeData in projectFile.Strokes)
            {
                var stroke = new LineStroke(
                    new PointMm(strokeData.Ax, strokeData.Ay),
                    new PointMm(strokeData.Bx, strokeData.By),
                    strokeData.PaintWellId,
                    strokeData.GroupId,
                    strokeData.IsGroupStart,
                    strokeData.IsGroupEnd,
                    parentGroupId: null,
                    paintOrder: strokeData.PaintOrder);
                document.Strokes.Add(stroke);
            }

            // Extract working area if defined
            Rect? workingArea = null;
            if (projectFile.Settings?.WorkingArea != null)
            {
                var wa = projectFile.Settings.WorkingArea;
                workingArea = new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
            }

            _log($"Project loaded: {filePath} ({document.Strokes.Count} strokes, {document.PaintWells.Count} paint wells)");

            return new ProjectLoadResult
            {
                Document = document,
                PaintWells = paintWells,
                Settings = projectFile.Settings ?? new ProjectSettings(),
                WorkingArea = workingArea
            };
        }

        /// <summary>
        /// Gets the project name from a file path (file name without extension).
        /// </summary>
        /// <param name="filePath">The file path</param>
        /// <returns>The project name, or "Untitled" if path is null/empty</returns>
        public static string GetProjectName(string? filePath)
        {
            return string.IsNullOrEmpty(filePath)
                ? "Untitled"
                : Path.GetFileName(filePath);
        }
    }
}
