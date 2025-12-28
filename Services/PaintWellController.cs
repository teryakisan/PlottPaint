using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NVSPlotter.Models;

// Avoid ambiguity with System.Drawing types from WindowsForms
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Panel = System.Windows.Controls.Panel;

namespace NVSPlotter.Services;

/// <summary>
/// Manages paint wells (color areas) for the painting mode feature.
/// Handles creation, selection, dragging, and resizing of paint wells.
/// </summary>
public sealed class PaintWellController
{
    private const double HANDLE_SIZE = 10.0;      // Visual size of handles in pixels
    private const double HANDLE_HIT_RADIUS = 8.0; // Hit test radius in mm (more generous)
    private const double MIN_WELL_SIZE = 10.0;
    private const double RULER_THICKNESS = 18.0;  // Must match the ruler offset used in rendering

    private readonly Canvas _canvas;
    private readonly Func<PlotDocument> _getDocument;
    private readonly Action _requestRender;

    // Selection state
    private PaintWell? _selectedWell;
    private PaintWell? _activeColorWell; // Currently selected for painting new strokes

    // Drag/resize state
    private enum DragMode { None, Move, ResizeNW, ResizeNE, ResizeSE, ResizeSW }
    private DragMode _dragMode = DragMode.None;
    private PointMm _dragStart;
    private Rect _originalBounds;

    // Creation state
    private bool _isCreating;
    private PointMm _createStart;
    private Rectangle? _createPreview;

    public PaintWell? SelectedWell => _selectedWell;
    public PaintWell? ActiveColorWell => _activeColorWell;
    public bool IsCreating => _isCreating;
    public bool IsDragging => _dragMode != DragMode.None;

    public PaintWellController(Canvas canvas, Func<PlotDocument> getDocument, Action requestRender)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
    }

    /// <summary>
    /// Creates a new paint well with the specified properties.
    /// </summary>
    public PaintWell CreateWell(Rect bounds, Color color, string name)
    {
        var well = new PaintWell(name, color, bounds);
        _getDocument().PaintWells.Add(well);
        _selectedWell = well;
        _requestRender();
        return well;
    }

    /// <summary>
    /// Removes a paint well and clears any stroke associations to it.
    /// </summary>
    public void RemoveWell(Guid id)
    {
        var doc = _getDocument();
        var well = doc.PaintWells.FirstOrDefault(w => w.Id == id);
        if (well == null) return;

        // Clear associations from strokes
        foreach (var stroke in doc.Strokes.Where(s => s.PaintWellId == id))
        {
            stroke.PaintWellId = null;
        }

        doc.PaintWells.Remove(well);

        if (_selectedWell == well) _selectedWell = null;
        if (_activeColorWell == well) _activeColorWell = null;

        _requestRender();
    }

    /// <summary>
    /// Sets the active color well for painting new strokes.
    /// </summary>
    public void SetActiveColor(PaintWell? well)
    {
        _activeColorWell = well;
    }

    /// <summary>
    /// Selects a paint well for editing.
    /// </summary>
    public void SelectWell(PaintWell? well)
    {
        _selectedWell = well;
        _requestRender();
    }

    /// <summary>
    /// Applies the active color to the currently selected strokes.
    /// </summary>
    public void ApplyColorToSelection(SelectionController selection)
    {
        var doc = _getDocument();
        var paintWellId = _activeColorWell?.Id;

        foreach (var idx in selection.SelectedIndices)
        {
            if (idx >= 0 && idx < doc.Strokes.Count)
            {
                doc.Strokes[idx].PaintWellId = paintWellId;
            }
        }
        _requestRender();
    }

    /// <summary>
    /// Gets the display color for a stroke based on its paint well assignment.
    /// </summary>
    public Color GetStrokeColor(LineStroke stroke)
    {
        if (stroke.PaintWellId == null) return Colors.Black;

        var doc = _getDocument();
        var well = doc.PaintWells.FirstOrDefault(w => w.Id == stroke.PaintWellId);
        return well?.Color ?? Colors.Black;
    }

    /// <summary>
    /// Gets a paint well by ID.
    /// </summary>
    public PaintWell? GetWellById(Guid id)
    {
        return _getDocument().PaintWells.FirstOrDefault(w => w.Id == id);
    }

    // ===== MOUSE HANDLING =====

    /// <summary>
    /// Converts canvas coordinates (which include ruler offset) to document coordinates.
    /// </summary>
    private static PointMm CanvasToDocument(PointMm canvasPoint)
    {
        return new PointMm(canvasPoint.X - RULER_THICKNESS, canvasPoint.Y - RULER_THICKNESS);
    }

    /// <summary>
    /// Handles mouse down for the PaintWell tool mode.
    /// </summary>
    public bool HandleMouseDown(PointMm canvasPoint, bool isShiftHeld)
    {
        // Convert from canvas coordinates (with ruler) to document coordinates
        var point = CanvasToDocument(canvasPoint);
        var doc = _getDocument();

        // Check if clicking on an existing well's handle or body
        foreach (var well in doc.PaintWells.AsEnumerable().Reverse()) // Reverse to hit top wells first
        {
            var handle = GetHandleAtPoint(well, point);
            if (handle != DragMode.None)
            {
                _selectedWell = well;
                _dragMode = handle;
                _dragStart = point;
                _originalBounds = well.Bounds;
                _canvas.CaptureMouse();
                _requestRender();
                return true;
            }

            if (well.Bounds.Contains(new Point(point.X, point.Y)))
            {
                _selectedWell = well;
                _dragMode = DragMode.Move;
                _dragStart = point;
                _originalBounds = well.Bounds;
                _canvas.CaptureMouse();
                _requestRender();
                return true;
            }
        }

        // Start creating a new well
        _isCreating = true;
        _createStart = point;
        BeginCreatePreview(point);
        _canvas.CaptureMouse();
        return true;
    }

    /// <summary>
    /// Handles mouse move for the PaintWell tool mode.
    /// </summary>
    public bool HandleMouseMove(PointMm canvasPoint)
    {
        // Convert from canvas coordinates (with ruler) to document coordinates
        var current = CanvasToDocument(canvasPoint);

        if (_isCreating)
        {
            UpdateCreatePreview(current);
            return true;
        }

        if (_dragMode != DragMode.None && _selectedWell != null)
        {
            UpdateDrag(current);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles mouse up for the PaintWell tool mode.
    /// </summary>
    public bool HandleMouseUp(PointMm canvasPoint)
    {
        // Convert from canvas coordinates (with ruler) to document coordinates
        var point = CanvasToDocument(canvasPoint);

        if (_isCreating)
        {
            CompleteCreate(point);
            return true;
        }

        if (_dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            _canvas.ReleaseMouseCapture();
            _requestRender();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cancels any active operation.
    /// </summary>
    public void Cancel()
    {
        if (_isCreating)
        {
            _isCreating = false;
            RemoveCreatePreview();
        }

        if (_dragMode != DragMode.None)
        {
            // Revert to original bounds
            if (_selectedWell != null)
            {
                _selectedWell.Bounds = _originalBounds;
            }
            _dragMode = DragMode.None;
        }

        _canvas.ReleaseMouseCapture();
        _requestRender();
    }

    // ===== CREATE WELL =====

    private void BeginCreatePreview(PointMm start)
    {
        _createPreview = new Rectangle
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 2,
            StrokeDashArray = [4, 2],
            Fill = new SolidColorBrush(Color.FromArgb(64, 30, 144, 255)),
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        _canvas.Children.Add(_createPreview);
        Panel.SetZIndex(_createPreview, 25);
        // Position with ruler offset for visual consistency
        Canvas.SetLeft(_createPreview, RULER_THICKNESS + start.X);
        Canvas.SetTop(_createPreview, RULER_THICKNESS + start.Y);
        _createPreview.Width = 0;
        _createPreview.Height = 0;
    }

    private void UpdateCreatePreview(PointMm current)
    {
        if (_createPreview == null) return;

        var left = Math.Min(_createStart.X, current.X);
        var top = Math.Min(_createStart.Y, current.Y);
        var width = Math.Abs(current.X - _createStart.X);
        var height = Math.Abs(current.Y - _createStart.Y);

        // Position with ruler offset for visual consistency
        Canvas.SetLeft(_createPreview, RULER_THICKNESS + left);
        Canvas.SetTop(_createPreview, RULER_THICKNESS + top);
        _createPreview.Width = width;
        _createPreview.Height = height;
    }

    private void RemoveCreatePreview()
    {
        if (_createPreview != null)
        {
            _canvas.Children.Remove(_createPreview);
            _createPreview = null;
        }
    }

    private void CompleteCreate(PointMm end)
    {
        RemoveCreatePreview();
        _isCreating = false;
        _canvas.ReleaseMouseCapture();

        var left = Math.Min(_createStart.X, end.X);
        var top = Math.Min(_createStart.Y, end.Y);
        var width = Math.Abs(end.X - _createStart.X);
        var height = Math.Abs(end.Y - _createStart.Y);

        // Require minimum size
        if (width < MIN_WELL_SIZE || height < MIN_WELL_SIZE)
        {
            _requestRender();
            return;
        }

        var bounds = new Rect(left, top, width, height);
        var doc = _getDocument();
        var index = doc.PaintWells.Count + 1;
        var color = GetDefaultColor(index);
        var name = $"Paint {index}";

        CreateWell(bounds, color, name);
    }

    private static Color GetDefaultColor(int index)
    {
        // Cycle through a palette of distinct colors
        return (index % 8) switch
        {
            1 => Colors.Red,
            2 => Colors.Blue,
            3 => Colors.Green,
            4 => Colors.Orange,
            5 => Colors.Purple,
            6 => Colors.Cyan,
            7 => Colors.Magenta,
            _ => Colors.Brown
        };
    }

    // ===== DRAG/RESIZE =====

    private DragMode GetHandleAtPoint(PaintWell well, PointMm point)
    {
        var bounds = well.Bounds;

        // Check corner handles with generous hit radius
        if (IsNearPoint(point, bounds.Left, bounds.Top, HANDLE_HIT_RADIUS)) return DragMode.ResizeNW;
        if (IsNearPoint(point, bounds.Right, bounds.Top, HANDLE_HIT_RADIUS)) return DragMode.ResizeNE;
        if (IsNearPoint(point, bounds.Right, bounds.Bottom, HANDLE_HIT_RADIUS)) return DragMode.ResizeSE;
        if (IsNearPoint(point, bounds.Left, bounds.Bottom, HANDLE_HIT_RADIUS)) return DragMode.ResizeSW;

        return DragMode.None;
    }

    private static bool IsNearPoint(PointMm test, double x, double y, double tolerance)
    {
        return Math.Abs(test.X - x) <= tolerance && Math.Abs(test.Y - y) <= tolerance;
    }

    private void UpdateDrag(PointMm current)
    {
        if (_selectedWell == null) return;

        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;

        switch (_dragMode)
        {
            case DragMode.Move:
                _selectedWell.Bounds = new Rect(
                    _originalBounds.Left + dx,
                    _originalBounds.Top + dy,
                    _originalBounds.Width,
                    _originalBounds.Height);
                break;

            case DragMode.ResizeNW:
                {
                    var newLeft = _originalBounds.Left + dx;
                    var newTop = _originalBounds.Top + dy;
                    var newWidth = Math.Max(MIN_WELL_SIZE, _originalBounds.Width - dx);
                    var newHeight = Math.Max(MIN_WELL_SIZE, _originalBounds.Height - dy);
                    _selectedWell.Bounds = new Rect(newLeft, newTop, newWidth, newHeight);
                }
                break;

            case DragMode.ResizeNE:
                {
                    var newTop = _originalBounds.Top + dy;
                    var newWidth = Math.Max(MIN_WELL_SIZE, _originalBounds.Width + dx);
                    var newHeight = Math.Max(MIN_WELL_SIZE, _originalBounds.Height - dy);
                    _selectedWell.Bounds = new Rect(_originalBounds.Left, newTop, newWidth, newHeight);
                }
                break;

            case DragMode.ResizeSE:
                {
                    var newWidth = Math.Max(MIN_WELL_SIZE, _originalBounds.Width + dx);
                    var newHeight = Math.Max(MIN_WELL_SIZE, _originalBounds.Height + dy);
                    _selectedWell.Bounds = new Rect(_originalBounds.Left, _originalBounds.Top, newWidth, newHeight);
                }
                break;

            case DragMode.ResizeSW:
                {
                    var newLeft = _originalBounds.Left + dx;
                    var newWidth = Math.Max(MIN_WELL_SIZE, _originalBounds.Width - dx);
                    var newHeight = Math.Max(MIN_WELL_SIZE, _originalBounds.Height + dy);
                    _selectedWell.Bounds = new Rect(newLeft, _originalBounds.Top, newWidth, newHeight);
                }
                break;
        }

        _requestRender();
    }

    // ===== RENDERING =====

    /// <summary>
    /// Checks if a paint well is a "wash" type well (water cup for rinsing).
    /// These are rendered as circles since they typically represent round cups.
    /// </summary>
    private static bool IsWashWell(PaintWell well)
    {
        var name = well.Name;
        return name.Equals("Wash", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("wash", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("rinse", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("water", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("clean", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renders all paint wells on the canvas.
    /// </summary>
    /// <param name="canvasRotationAngle">Current canvas rotation angle (0, 90, 180, 270) to counter-rotate labels</param>
    public void RenderPaintWells(double canvasRotationAngle = 0)
    {
        var doc = _getDocument();
        const double rulerThickness = 18.0;
        const double centerIndicatorSize = 20.0; // Diameter of center indicator

        foreach (var well in doc.PaintWells)
        {
            var isSelected = well == _selectedWell;
            var isActive = well == _activeColorWell;
            var isWashWell = IsWashWell(well);

            // Calculate center and dimensions
            var centerX = well.Bounds.Left + well.Bounds.Width / 2.0;
            var centerY = well.Bounds.Top + well.Bounds.Height / 2.0;

            if (isWashWell)
            {
                // Render wash wells as circles (they're typically round cups)
                // Use the smaller dimension as the diameter to fit within bounds
                var diameter = Math.Min(well.Bounds.Width, well.Bounds.Height);
                
                var circle = new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = new SolidColorBrush(Color.FromArgb(60, well.Color.R, well.Color.G, well.Color.B)),
                    Stroke = new SolidColorBrush(well.Color),
                    StrokeThickness = isSelected ? 6 : (isActive ? 5 : 4), // Wide stroke for cup appearance
                    SnapsToDevicePixels = true
                };

                if (isActive)
                {
                    circle.StrokeDashArray = [6, 3];
                }

                // Center the circle within the bounds
                Canvas.SetLeft(circle, rulerThickness + centerX - diameter / 2);
                Canvas.SetTop(circle, rulerThickness + centerY - diameter / 2);
                Panel.SetZIndex(circle, 8);
                _canvas.Children.Add(circle);
            }
            else
            {
                // Regular paint well rectangle
                var rect = new Rectangle
                {
                    Width = well.Bounds.Width,
                    Height = well.Bounds.Height,
                    Fill = new SolidColorBrush(Color.FromArgb(80, well.Color.R, well.Color.G, well.Color.B)),
                    Stroke = new SolidColorBrush(well.Color),
                    StrokeThickness = isSelected ? 3 : (isActive ? 2.5 : 1.5),
                    SnapsToDevicePixels = true
                };

                if (isActive)
                {
                    rect.StrokeDashArray = [6, 3];
                }

                Canvas.SetLeft(rect, rulerThickness + well.Bounds.Left);
                Canvas.SetTop(rect, rulerThickness + well.Bounds.Top);
                Panel.SetZIndex(rect, 8);
                _canvas.Children.Add(rect);
            }

            // Create a darker shade of the well color for the center indicator border
            var darkerColor = Color.FromArgb(
                255,
                (byte)Math.Max(0, well.Color.R - 60),
                (byte)Math.Max(0, well.Color.G - 60),
                (byte)Math.Max(0, well.Color.B - 60));
            
            // Center indicator - shows where the brush will dip
            var centerIndicator = new Ellipse
            {
                Width = centerIndicatorSize,
                Height = centerIndicatorSize,
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(darkerColor),
                StrokeThickness = 4,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(centerIndicator, rulerThickness + centerX - centerIndicatorSize / 2);
            Canvas.SetTop(centerIndicator, rulerThickness + centerY - centerIndicatorSize / 2);
            Panel.SetZIndex(centerIndicator, 9);
            _canvas.Children.Add(centerIndicator);

            // Label with name - positioned at top-left of well
            // Labels rotate with the canvas, which keeps them associated with their wells
            var label = new TextBlock
            {
                Text = well.Name,
                FontSize = 10,
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = new SolidColorBrush(well.Color),
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Padding = new Thickness(2, 1, 2, 1),
                IsHitTestVisible = false
            };
            
            // Position label at the top-left of the well bounds
            double labelX = rulerThickness + well.Bounds.Left + 4;
            double labelY = rulerThickness + well.Bounds.Top + 4;
            
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, labelY);
            Panel.SetZIndex(label, 9);
            _canvas.Children.Add(label);

            // Draw resize handles if selected (always at rectangle corners for consistent resizing)
            if (isSelected)
            {
                DrawHandle(well.Bounds.Left, well.Bounds.Top, well.Color, rulerThickness);
                DrawHandle(well.Bounds.Right, well.Bounds.Top, well.Color, rulerThickness);
                DrawHandle(well.Bounds.Right, well.Bounds.Bottom, well.Color, rulerThickness);
                DrawHandle(well.Bounds.Left, well.Bounds.Bottom, well.Color, rulerThickness);
            }
        }
    }

    private void DrawHandle(double x, double y, Color color, double rulerThickness)
    {
        var handle = new Rectangle
        {
            Width = HANDLE_SIZE,
            Height = HANDLE_SIZE,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            SnapsToDevicePixels = true
        };
        Canvas.SetLeft(handle, rulerThickness + x - HANDLE_SIZE / 2);
        Canvas.SetTop(handle, rulerThickness + y - HANDLE_SIZE / 2);
        Panel.SetZIndex(handle, 10);
        _canvas.Children.Add(handle);
    }

    /// <summary>
    /// Clears all paint wells.
    /// </summary>
    public void ClearAll()
    {
        var doc = _getDocument();

        // Clear stroke associations
        foreach (var stroke in doc.Strokes)
        {
            stroke.PaintWellId = null;
        }

        doc.PaintWells.Clear();
        _selectedWell = null;
        _activeColorWell = null;
        _requestRender();
    }

    /// <summary>
    /// Updates the properties of the selected well.
    /// </summary>
    public void UpdateSelectedWell(string? name = null, Color? color = null, double? dipDepth = null, int? dwellTimeMs = null, double? refreshDistanceMinMm = null, double? refreshDistanceMaxMm = null)
    {
        if (_selectedWell == null) return;

        if (name != null) _selectedWell.Name = name;
        if (color.HasValue) _selectedWell.Color = color.Value;
        if (dipDepth.HasValue) _selectedWell.DipDepth = dipDepth.Value;
        if (dwellTimeMs.HasValue) _selectedWell.DwellTimeMs = dwellTimeMs.Value;
        if (refreshDistanceMinMm.HasValue) _selectedWell.RefreshDistanceMinMm = refreshDistanceMinMm.Value;
        if (refreshDistanceMaxMm.HasValue) _selectedWell.RefreshDistanceMaxMm = refreshDistanceMaxMm.Value;

        _requestRender();
    }
}
