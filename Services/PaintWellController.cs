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
/// Event arguments for paint well collection changes.
/// </summary>
public sealed class PaintWellsChangedEventArgs : EventArgs
{
    /// <summary>
    /// The type of change that occurred.
    /// </summary>
    public PaintWellChangeType ChangeType { get; }

    /// <summary>
    /// The affected paint well, if applicable.
    /// </summary>
    public PaintWell? AffectedWell { get; }

    public PaintWellsChangedEventArgs(PaintWellChangeType changeType, PaintWell? affectedWell = null)
    {
        ChangeType = changeType;
        AffectedWell = affectedWell;
    }
}

/// <summary>
/// Types of changes to the paint wells collection.
/// </summary>
public enum PaintWellChangeType
{
    /// <summary>A new well was created.</summary>
    WellCreated,
    /// <summary>A well was removed.</summary>
    WellRemoved,
    /// <summary>All wells were cleared.</summary>
    WellsCleared,
    /// <summary>The selected well changed.</summary>
    SelectionChanged,
    /// <summary>The active color well changed.</summary>
    ActiveColorChanged,
    /// <summary>A well's properties were updated.</summary>
    WellUpdated
}

/// <summary>
/// Manages paint wells (color areas) for the painting mode feature.
/// Handles creation, selection, dragging, and resizing of paint wells.
/// </summary>
public sealed class PaintWellController
{
    private const double HANDLE_SIZE = 12.0;      // Visual size of handles in pixels
    private const double HANDLE_HIT_RADIUS = 15.0; // Hit test radius in mm (generous for easier clicking)
    private const double BOUNDS_HIT_MARGIN = 10.0; // Extra margin around paint well bounds for easier clicking
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

    /// <summary>
    /// Raised when the paint wells collection changes (add, remove, clear).
    /// </summary>
    public event EventHandler<PaintWellsChangedEventArgs>? PaintWellsChanged;

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
    /// Raises the PaintWellsChanged event.
    /// </summary>
    private void OnPaintWellsChanged(PaintWellChangeType changeType, PaintWell? affectedWell = null)
    {
        PaintWellsChanged?.Invoke(this, new PaintWellsChangedEventArgs(changeType, affectedWell));
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
        OnPaintWellsChanged(PaintWellChangeType.WellCreated, well);
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
        OnPaintWellsChanged(PaintWellChangeType.WellRemoved, well);
    }

    /// <summary>
    /// Sets the active color well for painting new strokes.
    /// </summary>
    public void SetActiveColor(PaintWell? well)
    {
        if (_activeColorWell == well) return;
        _activeColorWell = well;
        OnPaintWellsChanged(PaintWellChangeType.ActiveColorChanged, well);
    }

    /// <summary>
    /// Selects a paint well for editing.
    /// </summary>
    public void SelectWell(PaintWell? well)
    {
        if (_selectedWell == well) return;
        _selectedWell = well;
        _requestRender();
        OnPaintWellsChanged(PaintWellChangeType.SelectionChanged, well);
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

    /// <summary>
    /// Tests if a point (in document coordinates, not canvas coordinates) hits a paint well.
    /// Returns the hit paint well, or null if no well was hit.
    /// This allows clicking on paint wells to select them as active color from any tool.
    /// </summary>
    public PaintWell? TryHitTestPaintWell(PointMm canvasPoint)
    {
        // Convert from canvas coordinates (with ruler) to document coordinates
        var point = CanvasToDocument(canvasPoint);
        var doc = _getDocument();

        // Check paint wells in reverse order (top-most first)
        foreach (var well in doc.PaintWells.AsEnumerable().Reverse())
        {
            // Check if point is inside the well bounds with extra margin for easier clicking
            var expandedBounds = new Rect(
                well.Bounds.Left - BOUNDS_HIT_MARGIN,
                well.Bounds.Top - BOUNDS_HIT_MARGIN,
                well.Bounds.Width + (BOUNDS_HIT_MARGIN * 2),
                well.Bounds.Height + (BOUNDS_HIT_MARGIN * 2));
            
            if (expandedBounds.Contains(new Point(point.X, point.Y)))
            {
                return well;
            }
        }

        return null;
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

            // Check if inside bounds with extra margin for easier clicking
            var expandedBounds = new Rect(
                well.Bounds.Left - BOUNDS_HIT_MARGIN,
                well.Bounds.Top - BOUNDS_HIT_MARGIN,
                well.Bounds.Width + (BOUNDS_HIT_MARGIN * 2),
                well.Bounds.Height + (BOUNDS_HIT_MARGIN * 2));
            
            if (expandedBounds.Contains(new Point(point.X, point.Y)))
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
        var color = GetDefaultPaintWellColor(index);
        var name = $"Paint {index}";

        CreateWell(bounds, color, name);
    }

    /// <summary>
    /// Gets a default color for a new paint well based on its index.
    /// Cycles through a palette of 8 distinct colors.
    /// </summary>
    /// <param name="index">The 1-based index of the paint well</param>
    /// <returns>A color from the palette</returns>
    public static Color GetDefaultPaintWellColor(int index)
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
    /// <param name="isPaintModeEnabled">When false, wells are grayed out with an X indicator</param>
    public void RenderPaintWells(double canvasRotationAngle = 0, bool isPaintModeEnabled = true)
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

            // Determine colors based on paint mode enabled state
            Color wellColor;
            Color fillColor;
            Color strokeColor;
            
            if (isPaintModeEnabled)
            {
                // Normal colors
                wellColor = well.Color;
                fillColor = Color.FromArgb(isWashWell ? (byte)60 : (byte)80, well.Color.R, well.Color.G, well.Color.B);
                strokeColor = well.Color;
            }
            else
            {
                // Grayed out colors when paint mode is disabled
                var grayLevel = (byte)((well.Color.R + well.Color.G + well.Color.B) / 3);
                wellColor = Color.FromRgb(grayLevel, grayLevel, grayLevel);
                fillColor = Color.FromArgb(40, grayLevel, grayLevel, grayLevel);
                strokeColor = Color.FromRgb((byte)(grayLevel * 0.7), (byte)(grayLevel * 0.7), (byte)(grayLevel * 0.7));
            }

            if (isWashWell)
            {
                // Render wash wells as circles (they're typically round cups)
                // Use the smaller dimension as the diameter to fit within bounds
                var diameter = Math.Min(well.Bounds.Width, well.Bounds.Height);
                
                var circle = new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = new SolidColorBrush(fillColor),
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = isPaintModeEnabled ? (isSelected ? 6 : (isActive ? 5 : 4)) : 3,
                    SnapsToDevicePixels = true
                };

                if (isPaintModeEnabled && isActive)
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
                    Fill = new SolidColorBrush(fillColor),
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = isPaintModeEnabled ? (isSelected ? 3 : (isActive ? 2.5 : 1.5)) : 1.5,
                    SnapsToDevicePixels = true
                };

                if (isPaintModeEnabled && isActive)
                {
                    rect.StrokeDashArray = [6, 3];
                }

                // Apply rotation transform if the paint well has been rotated
                if (Math.Abs(well.Rotation) > 0.001)
                {
                    // Rotation is around the center of the rectangle
                    rect.RenderTransform = new RotateTransform(
                        well.Rotation * 180 / Math.PI,  // Convert radians to degrees
                        well.Bounds.Width / 2,
                        well.Bounds.Height / 2);
                }

                Canvas.SetLeft(rect, rulerThickness + well.Bounds.Left);
                Canvas.SetTop(rect, rulerThickness + well.Bounds.Top);
                Panel.SetZIndex(rect, 8);
                _canvas.Children.Add(rect);
            }

            if (isPaintModeEnabled)
            {
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
            }
            else
            {
                // When paint mode is disabled, show an X icon instead of the center indicator
                // Scale the X based on the well size
                var xSize = Math.Min(well.Bounds.Width, well.Bounds.Height) * 0.4;
                xSize = Math.Max(xSize, 15); // Minimum size
                xSize = Math.Min(xSize, 50); // Maximum size
                
                var xColor = Color.FromArgb(120, 128, 128, 128); // Semi-transparent gray
                var strokeThickness = Math.Max(2, xSize / 10);
                
                // Draw X using two lines
                var line1 = new Line
                {
                    X1 = rulerThickness + centerX - xSize / 2,
                    Y1 = rulerThickness + centerY - xSize / 2,
                    X2 = rulerThickness + centerX + xSize / 2,
                    Y2 = rulerThickness + centerY + xSize / 2,
                    Stroke = new SolidColorBrush(xColor),
                    StrokeThickness = strokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(line1, 0);
                Canvas.SetTop(line1, 0);
                Panel.SetZIndex(line1, 9);
                _canvas.Children.Add(line1);
                
                var line2 = new Line
                {
                    X1 = rulerThickness + centerX + xSize / 2,
                    Y1 = rulerThickness + centerY - xSize / 2,
                    X2 = rulerThickness + centerX - xSize / 2,
                    Y2 = rulerThickness + centerY + xSize / 2,
                    Stroke = new SolidColorBrush(xColor),
                    StrokeThickness = strokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(line2, 0);
                Canvas.SetTop(line2, 0);
                Panel.SetZIndex(line2, 9);
                _canvas.Children.Add(line2);
            }

            // Label with name - positioned at top-left of well
            // Labels rotate with the paint well to stay associated
            var label = new TextBlock
            {
                Text = well.Name,
                FontSize = 10,
                FontWeight = System.Windows.FontWeights.Bold,
                Foreground = new SolidColorBrush(isPaintModeEnabled ? well.Color : Colors.Gray),
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Padding = new Thickness(2, 1, 2, 1),
                IsHitTestVisible = false
            };
            
            // Position label at the top-left of the well bounds
            double labelX = rulerThickness + well.Bounds.Left + 4;
            double labelY = rulerThickness + well.Bounds.Top + 4;
            
            // Apply rotation if the paint well is rotated
            if (Math.Abs(well.Rotation) > 0.001)
            {
                // Calculate the label position relative to the well center
                var wellCenterCanvasX = rulerThickness + centerX;
                var wellCenterCanvasY = rulerThickness + centerY;
                
                // Rotate the label's position around the well center
                var cos = Math.Cos(well.Rotation);
                var sin = Math.Sin(well.Rotation);
                var dx = labelX - wellCenterCanvasX;
                var dy = labelY - wellCenterCanvasY;
                labelX = wellCenterCanvasX + dx * cos - dy * sin;
                labelY = wellCenterCanvasY + dx * sin + dy * cos;
                
                // Also rotate the label text itself
                label.RenderTransform = new RotateTransform(well.Rotation * 180 / Math.PI);
            }
            
            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, labelY);
            Panel.SetZIndex(label, 9);
            _canvas.Children.Add(label);

            // Draw resize handles if selected and paint mode is enabled
            if (isSelected && isPaintModeEnabled)
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
        OnPaintWellsChanged(PaintWellChangeType.WellsCleared);
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
        OnPaintWellsChanged(PaintWellChangeType.WellUpdated, _selectedWell);
    }

    /// <summary>
    /// Creates a quick setup of 5 paint wells (Red, Green, Blue, Wash, Wipe)
    /// positioned at the bottom of the canvas, scaled to fit the bed dimensions.
    /// </summary>
    /// <param name="docWidth">Document width in mm</param>
    /// <param name="docHeight">Document height in mm</param>
    /// <returns>Number of wells created</returns>
    public int QuickSetupWells(double docWidth, double docHeight)
    {
        // Clear existing wells first
        ClearAll();

        const int numWells = 5;
        const double marginFromEdge = 20;
        const double minSpacing = 10;
        
        // Reference dimensions (designed for A0 size: 841 x 1189 mm)
        const double referenceWidth = 841.0;
        const double referenceWellWidth = 140.0;
        const double referenceWellHeight = 100.0;
        
        // Calculate scale factor based on document width compared to reference
        // This ensures wells scale proportionally to the bed size
        double scaleFactor = docWidth / referenceWidth;
        
        // Clamp scale factor to reasonable bounds (don't make wells too small or too large)
        scaleFactor = Math.Clamp(scaleFactor, 0.25, 2.0);
        
        // Calculate scaled well dimensions
        double wellWidth = referenceWellWidth * scaleFactor;
        double wellHeight = referenceWellHeight * scaleFactor;
        
        // Ensure minimum well size for usability
        const double minWellSize = 40.0;
        wellWidth = Math.Max(wellWidth, minWellSize);
        wellHeight = Math.Max(wellHeight, minWellSize);
        
        // Calculate available width and check if wells fit
        double availableWidth = docWidth - (2 * marginFromEdge);
        double totalWellsWidth = numWells * wellWidth;
        
        // If wells don't fit, scale them down to fit
        if (totalWellsWidth + (minSpacing * (numWells - 1)) > availableWidth)
        {
            // Calculate maximum well width that fits
            double maxTotalWellWidth = availableWidth - (minSpacing * (numWells - 1));
            wellWidth = maxTotalWellWidth / numWells;
            wellWidth = Math.Max(wellWidth, minWellSize); // Maintain minimum size
            
            // Scale height proportionally
            wellHeight = wellWidth * (referenceWellHeight / referenceWellWidth);
            wellHeight = Math.Max(wellHeight, minWellSize);
        }
        
        // Recalculate total width and spacing
        totalWellsWidth = numWells * wellWidth;
        double spacing = (availableWidth - totalWellsWidth) / (numWells - 1);
        spacing = Math.Max(spacing, minSpacing);

        // Ensure wells don't exceed the document height
        if (wellHeight + marginFromEdge * 2 > docHeight)
        {
            wellHeight = docHeight - marginFromEdge * 2;
            wellHeight = Math.Max(wellHeight, minWellSize);
        }

        // Position at bottom of canvas
        double startY = docHeight - wellHeight - marginFromEdge;
        
        // Ensure startY is not negative
        startY = Math.Max(startY, marginFromEdge);

        // Red well
        CreateWell(
            new Rect(marginFromEdge, startY, wellWidth, wellHeight),
            Colors.Red,
            "Red");

        // Green well
        CreateWell(
            new Rect(marginFromEdge + (wellWidth + spacing) * 1, startY, wellWidth, wellHeight),
            Colors.Green,
            "Green");

        // Blue well
        CreateWell(
            new Rect(marginFromEdge + (wellWidth + spacing) * 2, startY, wellWidth, wellHeight),
            Colors.Blue,
            "Blue");

        // Wash well (black - for cleaning/washing brush)
        CreateWell(
            new Rect(marginFromEdge + (wellWidth + spacing) * 3, startY, wellWidth, wellHeight),
            Colors.Black,
            "Wash");

        // Wipe well (light gray - for wiping/drying brush)
        CreateWell(
            new Rect(marginFromEdge + (wellWidth + spacing) * 4, startY, wellWidth, wellHeight),
            Color.FromRgb(192, 192, 192),
            "Wipe");

        return numWells;
    }
}
