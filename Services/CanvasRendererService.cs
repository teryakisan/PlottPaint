using NVSPlotter.Models;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// Avoid ambiguity with System.Drawing types
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace NVSPlotter.Services
{
    /// <summary>
    /// Configuration for canvas rendering.
    /// </summary>
    public sealed class RenderSettings
    {
        public bool IsGridVisible { get; set; }
        public bool IsMarginOverlayVisible { get; set; }
        public bool IsPaintModeEnabled { get; set; }
        public double GridSpacing { get; set; } = 10.0;
        public double SafeMarginMm { get; set; } = 50.0;
        
        /// <summary>
        /// When true, use individual margin values for each side.
        /// When false, use SafeMarginMm for all sides.
        /// </summary>
        public bool UseIndividualMargins { get; set; }
        
        /// <summary>Left margin in mm (used when UseIndividualMargins is true)</summary>
        public double MarginLeftMm { get; set; } = 50.0;
        
        /// <summary>Top margin in mm (used when UseIndividualMargins is true)</summary>
        public double MarginTopMm { get; set; } = 50.0;
        
        /// <summary>Right margin in mm (used when UseIndividualMargins is true)</summary>
        public double MarginRightMm { get; set; } = 50.0;
        
        /// <summary>Bottom margin in mm (used when UseIndividualMargins is true)</summary>
        public double MarginBottomMm { get; set; } = 50.0;
        
        /// <summary>
        /// When true, margins are calculated relative to the paint canvas area instead of the full document/plotter bed.
        /// </summary>
        public bool LockMarginsToCanvas { get; set; }
        
        /// <summary>
        /// The paint canvas area bounds (if defined). Used when LockMarginsToCanvas is true.
        /// </summary>
        public Rect? PaintCanvasArea { get; set; }
        
        /// <summary>Gets the effective left margin</summary>
        public double EffectiveMarginLeft => UseIndividualMargins ? MarginLeftMm : SafeMarginMm;
        
        /// <summary>Gets the effective top margin</summary>
        public double EffectiveMarginTop => UseIndividualMargins ? MarginTopMm : SafeMarginMm;
        
        /// <summary>Gets the effective right margin</summary>
        public double EffectiveMarginRight => UseIndividualMargins ? MarginRightMm : SafeMarginMm;
        
        /// <summary>Gets the effective bottom margin</summary>
        public double EffectiveMarginBottom => UseIndividualMargins ? MarginBottomMm : SafeMarginMm;
        
        public double ZoomScale { get; set; } = 1.0;
        public double CanvasRotationAngle { get; set; }
        public double BedX { get; set; }
        public double BedY { get; set; }
        public bool BedFromGrbl { get; set; }
        
        /// <summary>
        /// When true, renders the canvas in "Paint Only" view mode which shows
        /// painting strokes as numbered sequences with colors and allows selection.
        /// </summary>
        public bool IsPaintOnlyViewEnabled { get; set; }
    }

    /// <summary>
    /// Handles all canvas rendering logic including strokes, rulers, grids, overlays, and selection indicators.
    /// </summary>
    public sealed class CanvasRendererService
    {
        private readonly Canvas _drawCanvas;
        private readonly Canvas _rulerCanvas;
        private readonly Func<PlotDocument> _getDocument;
        private readonly SelectionController _selectionController;
        private readonly PaintWellController _paintWellController;
        private readonly ReferenceImageService _imageService;
        private readonly WorkingAreaManager _workingAreaManager;
        private readonly Action<string> _log;
        private readonly Func<HashSet<Guid>> _getShowIntermediatePointsForGroups;
        
        // Paint Only view mode support - uses G-code based painting strokes
        private GcodePaintingStrokeService? _gcodePaintingStrokeService;
        
        /// <summary>
        /// Selected painting strokes in Paint Only view mode.
        /// </summary>
        public HashSet<int> SelectedPaintingStrokeNumbers { get; } = new();
        
        /// <summary>
        /// Currently hovered painting stroke number in Paint Only view mode (-1 if none).
        /// </summary>
        public int HoveredPaintingStrokeNumber { get; set; } = -1;

        private const double RULER_THICKNESS = 18.0;
        private const double HANDLE_MARGIN = 100.0;

        /// <summary>
        /// Initializes the canvas renderer service.
        /// </summary>
        public CanvasRendererService(
            Canvas drawCanvas,
            Canvas rulerCanvas,
            Func<PlotDocument> getDocument,
            SelectionController selectionController,
            PaintWellController paintWellController,
            ReferenceImageService imageService,
            WorkingAreaManager workingAreaManager,
            Action<string> log,
            Func<HashSet<Guid>> getShowIntermediatePointsForGroups)
        {
            _drawCanvas = drawCanvas ?? throw new ArgumentNullException(nameof(drawCanvas));
            _rulerCanvas = rulerCanvas ?? throw new ArgumentNullException(nameof(rulerCanvas));
            _getDocument = getDocument ?? throw new ArgumentNullException(nameof(getDocument));
            _selectionController = selectionController ?? throw new ArgumentNullException(nameof(selectionController));
            _paintWellController = paintWellController ?? throw new ArgumentNullException(nameof(paintWellController));
            _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
            _workingAreaManager = workingAreaManager ?? throw new ArgumentNullException(nameof(workingAreaManager));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _getShowIntermediatePointsForGroups = getShowIntermediatePointsForGroups ?? throw new ArgumentNullException(nameof(getShowIntermediatePointsForGroups));
        }
        
        /// <summary>
        /// Sets the GcodePaintingStrokeService for Paint Only view mode rendering.
        /// </summary>
        public void SetGcodePaintingStrokeService(GcodePaintingStrokeService service)
        {
            _gcodePaintingStrokeService = service;
        }

        /// <summary>
        /// Renders the entire canvas including all elements.
        /// </summary>
        public void RenderAll(RenderSettings settings)
        {
            var doc = _getDocument();

            _drawCanvas.Children.Clear();
            _rulerCanvas.Children.Clear();

            // Size canvas to page + ruler thickness + handle margin on all sides
            _drawCanvas.Width = doc.WidthMm + RULER_THICKNESS + HANDLE_MARGIN * 2;
            _drawCanvas.Height = doc.HeightMm + RULER_THICKNESS + HANDLE_MARGIN * 2;
            _rulerCanvas.Width = doc.WidthMm + RULER_THICKNESS + HANDLE_MARGIN * 2;
            _rulerCanvas.Height = doc.HeightMm + RULER_THICKNESS + HANDLE_MARGIN * 2;

            // Page border (offset by ruler thickness)
            var pageRect = new Rectangle
            {
                Width = doc.WidthMm,
                Height = doc.HeightMm,
                Stroke = Brushes.Black,
                StrokeThickness = 1.0,
                Fill = Brushes.White
            };
            _drawCanvas.Children.Add(pageRect);
            Canvas.SetLeft(pageRect, RULER_THICKNESS);
            Canvas.SetTop(pageRect, RULER_THICKNESS);
            Panel.SetZIndex(pageRect, 0);

            DrawGrid(settings);
            DrawRulers(settings);
            DrawSafeMarginOverlay(settings);
            RenderReferenceImage();

            // Working area overlay (visual only)
            if (_workingAreaManager.DefinedArea is Rect definedArea)
            {
                var workRect = CreateWorkingAreaVisual();
                workRect.Width = definedArea.Width;
                workRect.Height = definedArea.Height;
                Canvas.SetLeft(workRect, definedArea.Left);
                Canvas.SetTop(workRect, definedArea.Top);
                Panel.SetZIndex(workRect, 3);
                _drawCanvas.Children.Add(workRect);
            }

            // Paint wells (render before strokes so strokes appear on top)
            // Hide paint wells in Paint Only view mode - we show strokes directly
            if (!settings.IsPaintOnlyViewEnabled)
            {
                _paintWellController.RenderPaintWells(settings.CanvasRotationAngle, settings.IsPaintModeEnabled);
            }

            // Render strokes - use Paint Only view if enabled
            if (settings.IsPaintOnlyViewEnabled && _gcodePaintingStrokeService != null && _gcodePaintingStrokeService.PaintingStrokes.Count > 0)
            {
                RenderGcodePaintOnlyView(settings);
            }
            else
            {
                // Normal stroke rendering
                RenderStrokes(settings);

                // Selection indicators (only in normal mode)
                DrawSelectionIndicators();

                // Selection visuals (bounding box, handles)
                _selectionController.RenderSelectionVisuals();
            }

            _log($"Doc: {doc.WidthMm:0} x {doc.HeightMm:0} mm, strokes={doc.Strokes.Count}, paintWells={doc.PaintWells.Count}");
            _log($"Bed: X={settings.BedX:0.###} Y={settings.BedY:0.###} {(settings.BedFromGrbl ? "(from $$)" : "(default)")}, margin={settings.SafeMarginMm:0.###}mm");
        }

        #region Strokes Rendering

        private void RenderStrokes(RenderSettings settings)
        {
            var doc = _getDocument();
            var paintModeEnabled = settings.IsPaintModeEnabled;

            // Calculate zoom-adaptive stroke thickness
            var currentZoom = settings.ZoomScale;
            const double minScreenThickness = 2.0;
            const double baseThickness = 1.2;
            var adaptiveThickness = Math.Max(baseThickness, minScreenThickness / currentZoom);

            foreach (var s in doc.Strokes)
            {
                Brush strokeBrush;
                if (paintModeEnabled && s.PaintWellId.HasValue)
                {
                    var color = _paintWellController.GetStrokeColor(s);
                    strokeBrush = new SolidColorBrush(color);
                }
                else
                {
                    strokeBrush = Brushes.Black;
                }

                var strokeThickness = adaptiveThickness;

                var ln = new Line
                {
                    X1 = s.A.X,
                    Y1 = s.A.Y,
                    X2 = s.B.X,
                    Y2 = s.B.Y,
                    Stroke = strokeBrush,
                    StrokeThickness = strokeThickness,
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(ln, 4);
                _drawCanvas.Children.Add(ln);
            }
        }

        #endregion

        #region Paint Only View Rendering (G-code Based)

        /// <summary>
        /// Renders the canvas in "Paint Only" view mode using G-code derived painting strokes.
        /// Shows painting strokes as numbered sequences with colors and selection support.
        /// G-code coordinates (work space) are transformed to document space for display.
        /// </summary>
        private void RenderGcodePaintOnlyView(RenderSettings settings)
        {
            if (_gcodePaintingStrokeService == null) return;
            
            var paintingStrokes = _gcodePaintingStrokeService.PaintingStrokes;
            if (paintingStrokes.Count == 0)
            {
                _log("[PAINT ONLY] No G-code painting strokes to display");
                return;
            }
            
            var doc = _getDocument();
            var currentZoom = settings.ZoomScale;
            const double baseThickness = 3.0;
            var adaptiveThickness = Math.Max(baseThickness, 4.0 / currentZoom);
            
            // Colors for selection/hover states
            var selectedColor = Color.FromRgb(255, 200, 0);
            var hoveredColor = Color.FromRgb(0, 200, 255);
            
            foreach (var paintingStroke in paintingStrokes)
            {
                // Determine stroke appearance based on selection/hover state
                var isSelected = SelectedPaintingStrokeNumbers.Contains(paintingStroke.StrokeNumber);
                var isHovered = HoveredPaintingStrokeNumber == paintingStroke.StrokeNumber;
                
                Color strokeColor;
                double thickness;
                
                if (isSelected)
                {
                    strokeColor = selectedColor;
                    thickness = adaptiveThickness * 1.5;
                }
                else if (isHovered)
                {
                    strokeColor = hoveredColor;
                    thickness = adaptiveThickness * 1.3;
                }
                else
                {
                    strokeColor = paintingStroke.Color;
                    thickness = adaptiveThickness;
                }
                
                var strokeBrush = new SolidColorBrush(strokeColor);
                
                // Draw glow effect behind strokes with brush profiles
                if (paintingStroke.HasBrushProfiles)
                {
                    foreach (var segment in paintingStroke.Segments)
                    {
                        // Transform G-code coords (X, -Y) to document coords
                        var (x1, y1) = TransformGcodeToDocument(segment.FromX, segment.FromY, doc.HeightMm);
                        var (x2, y2) = TransformGcodeToDocument(segment.ToX, segment.ToY, doc.HeightMm);
                        
                        var glowLine = new Line
                        {
                            X1 = x1,
                            Y1 = y1,
                            X2 = x2,
                            Y2 = y2,
                            Stroke = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                            StrokeThickness = thickness + 6.0 / currentZoom,
                            IsHitTestVisible = false
                        };
                        Panel.SetZIndex(glowLine, 3);
                        _drawCanvas.Children.Add(glowLine);
                    }
                }
                
                // Draw all segments in this painting stroke
                foreach (var segment in paintingStroke.Segments)
                {
                    // Transform G-code coords (X, -Y) to document coords
                    var (x1, y1) = TransformGcodeToDocument(segment.FromX, segment.FromY, doc.HeightMm);
                    var (x2, y2) = TransformGcodeToDocument(segment.ToX, segment.ToY, doc.HeightMm);
                    
                    var line = new Line
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Stroke = strokeBrush,
                        StrokeThickness = thickness,
                        Tag = paintingStroke.StrokeNumber,
                        IsHitTestVisible = true,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    Panel.SetZIndex(line, 4);
                    _drawCanvas.Children.Add(line);
                }
                
                // Transform start/end points for indicators and labels
                var startPt = paintingStroke.StartPoint;
                var endPt = paintingStroke.EndPoint;
                var (startX, startY) = TransformGcodeToDocument(startPt.X, startPt.Y, doc.HeightMm);
                var (endX, endY) = TransformGcodeToDocument(endPt.X, endPt.Y, doc.HeightMm);
                var startDocPoint = new PointMm(startX, startY);
                var endDocPoint = new PointMm(endX, endY);
                
                // Draw stroke number label at start of each painting stroke
                var label = CreateGcodePaintingStrokeLabel(
                    startDocPoint,
                    paintingStroke.StrokeNumber,
                    paintingStroke.PaintWellName,
                    strokeColor,
                    paintingStroke.HasBrushProfiles,
                    settings.CanvasRotationAngle);
                Panel.SetZIndex(label, 6);
                _drawCanvas.Children.Add(label);
                
                // Draw selection indicator (dashed box) around selected strokes
                if (isSelected)
                {
                    DrawPaintOnlySelectionIndicator(paintingStroke, doc.HeightMm, settings.ZoomScale);
                }
            }
            
            // Log status
            var totalStrokes = paintingStrokes.Count;
            var withProfiles = paintingStrokes.Count(s => s.HasBrushProfiles);
            _log($"[PAINT ONLY] Showing {totalStrokes} G-code painting strokes ({withProfiles} with brush profiles)");
        }
        
        /// <summary>
        /// Draws a dashed selection indicator box around a G-code painting stroke.
        /// </summary>
        private void DrawPaintOnlySelectionIndicator(GcodePaintingStroke stroke, double docHeight, double zoomScale)
        {
            // Calculate bounding box of all segments in the stroke
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            
            foreach (var segment in stroke.Segments)
            {
                var (x1, y1) = TransformGcodeToDocument(segment.FromX, segment.FromY, docHeight);
                var (x2, y2) = TransformGcodeToDocument(segment.ToX, segment.ToY, docHeight);
                
                minX = Math.Min(minX, Math.Min(x1, x2));
                maxX = Math.Max(maxX, Math.Max(x1, x2));
                minY = Math.Min(minY, Math.Min(y1, y2));
                maxY = Math.Max(maxY, Math.Max(y1, y2));
            }
            
            // Add padding around the bounding box
            const double padding = 5.0;
            minX -= padding;
            minY -= padding;
            maxX += padding;
            maxY += padding;
            
            var width = maxX - minX;
            var height = maxY - minY;
            
            // Selection indicator color (golden/yellow)
            var selectionColor = Color.FromRgb(255, 200, 0);
            
            // Draw dashed rectangle
            var selectionRect = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = new SolidColorBrush(selectionColor),
                StrokeThickness = Math.Max(1.5, 2.0 / zoomScale),
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(20, 255, 200, 0)),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            
            Canvas.SetLeft(selectionRect, minX);
            Canvas.SetTop(selectionRect, minY);
            Panel.SetZIndex(selectionRect, 5);
            _drawCanvas.Children.Add(selectionRect);
        }
        
        /// <summary>
        /// Transforms G-code work coordinates to document coordinates for display.
        /// G-code uses: X positive right, Y negative down (from top-left origin)
        /// Document uses: X positive right, Y positive down (standard screen coords)
        /// </summary>
        private static (double docX, double docY) TransformGcodeToDocument(double gcodeX, double gcodeY, double docHeight)
        {
            // G-code Y is negative, so negate it to get positive document Y
            // G-code X is already in document space
            return (gcodeX, -gcodeY);
        }
        
        /// <summary>
        /// Creates a label for a G-code painting stroke showing its number, paint well name, and brush profile indicator.
        /// </summary>
        private Border CreateGcodePaintingStrokeLabel(
            PointMm position,
            int strokeNumber,
            string? paintWellName,
            Color color,
            bool hasBrushProfiles,
            double canvasRotationAngle)
        {
            var displayText = paintWellName != null 
                ? $"{strokeNumber} ({paintWellName})"
                : strokeNumber.ToString();
            
            if (hasBrushProfiles)
            {
                displayText += " ???"; // Brush emoji to indicate brush profiles
            }
            
            // Create contrasting text color
            var brightness = (color.R * 0.299 + color.G * 0.587 + color.B * 0.114) / 255;
            var textColor = brightness > 0.5 ? Colors.Black : Colors.White;
            
            var textBlock = new TextBlock
            {
                Text = displayText,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(textColor),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            
            var border = new Border
            {
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2, 4, 2),
                Child = textBlock,
                IsHitTestVisible = false
            };
            
            // Apply counter-rotation so text stays readable when canvas is rotated
            if (canvasRotationAngle != 0)
            {
                border.RenderTransformOrigin = new Point(0, 0.5);
                border.RenderTransform = new RotateTransform(-canvasRotationAngle);
            }
            
            // Position at start point with small offset
            Canvas.SetLeft(border, position.X + 5);
            Canvas.SetTop(border, position.Y - 15);
            
            return border;
        }
        
        /// <summary>
        /// Clears selection in Paint Only view mode.
        /// </summary>
        public void ClearPaintOnlySelection()
        {
            SelectedPaintingStrokeNumbers.Clear();
            HoveredPaintingStrokeNumber = -1;
        }
        
        /// <summary>
        /// Selects a painting stroke in Paint Only view mode.
        /// </summary>
        public void SelectPaintingStroke(int strokeNumber, bool addToSelection = false)
        {
            if (!addToSelection)
            {
                SelectedPaintingStrokeNumbers.Clear();
            }
            SelectedPaintingStrokeNumbers.Add(strokeNumber);
        }
        
        /// <summary>
        /// Toggles selection of a painting stroke in Paint Only view mode.
        /// </summary>
        public void TogglePaintingStrokeSelection(int strokeNumber)
        {
            if (SelectedPaintingStrokeNumbers.Contains(strokeNumber))
            {
                SelectedPaintingStrokeNumbers.Remove(strokeNumber);
            }
            else
            {
                SelectedPaintingStrokeNumbers.Add(strokeNumber);
            }
        }
        
        /// <summary>
        /// Gets the selected G-code painting strokes.
        /// </summary>
        public List<GcodePaintingStroke> GetSelectedGcodePaintingStrokes()
        {
            if (_gcodePaintingStrokeService == null) return new List<GcodePaintingStroke>();
            
            return _gcodePaintingStrokeService.PaintingStrokes
                .Where(s => SelectedPaintingStrokeNumbers.Contains(s.StrokeNumber))
                .ToList();
        }
        
        /// <summary>
        /// Gets the G-code painting stroke service.
        /// </summary>
        public GcodePaintingStrokeService? GetGcodePaintingStrokeService() => _gcodePaintingStrokeService;

        #endregion

        #region Grid Rendering

        private void DrawGrid(RenderSettings settings)
        {
            if (!settings.IsGridVisible) return;

            var doc = _getDocument();
            var spacing = settings.GridSpacing;
            if (spacing <= 0) return;

            // Adaptive grid appearance based on zoom level
            var currentZoom = settings.ZoomScale;
            var gridOpacity = Math.Clamp(0.4 + 0.6 * Math.Min(1.0, currentZoom), 0.4, 1.0);
            var gridThickness = Math.Clamp(0.5 + 0.5 * Math.Min(1.0, currentZoom), 0.5, 1.0);

            var gridColor = Color.FromArgb((byte)(200 * gridOpacity), 200, 200, 200);
            var gridBrush = new SolidColorBrush(gridColor);

            // Draw vertical lines
            for (double x = 0; x <= doc.WidthMm; x += spacing)
            {
                var line = new Line
                {
                    X1 = RULER_THICKNESS + x,
                    Y1 = RULER_THICKNESS,
                    X2 = RULER_THICKNESS + x,
                    Y2 = RULER_THICKNESS + doc.HeightMm,
                    Stroke = gridBrush,
                    StrokeThickness = gridThickness,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                Panel.SetZIndex(line, 2);
                _drawCanvas.Children.Add(line);
            }

            // Draw horizontal lines
            for (double y = 0; y <= doc.HeightMm; y += spacing)
            {
                var line = new Line
                {
                    X1 = RULER_THICKNESS,
                    Y1 = RULER_THICKNESS + y,
                    X2 = RULER_THICKNESS + doc.WidthMm,
                    Y2 = RULER_THICKNESS + y,
                    Stroke = gridBrush,
                    StrokeThickness = gridThickness,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                Panel.SetZIndex(line, 2);
                _drawCanvas.Children.Add(line);
            }
        }

        #endregion

        #region Rulers Rendering

        private void DrawRulers(RenderSettings settings)
        {
            var doc = _getDocument();
            const double majorStep = 10.0;
            double labelStep = 50.0;

            Brush rulerFill = new SolidColorBrush(Color.FromRgb(248, 248, 248));
            Brush borderBrush = Brushes.LightGray;
            Brush tickBrush = Brushes.Gray;

            // Corner block
            var corner = CreateRulerBackground(RULER_THICKNESS, RULER_THICKNESS, rulerFill, borderBrush);
            Canvas.SetLeft(corner, 0);
            Canvas.SetTop(corner, 0);
            _rulerCanvas.Children.Add(corner);

            // Top ruler background
            var topBackground = CreateRulerBackground(doc.WidthMm, RULER_THICKNESS, rulerFill, borderBrush);
            Canvas.SetLeft(topBackground, RULER_THICKNESS);
            Canvas.SetTop(topBackground, 0);
            _rulerCanvas.Children.Add(topBackground);

            // Left ruler background
            var leftBackground = CreateRulerBackground(RULER_THICKNESS, doc.HeightMm, rulerFill, borderBrush);
            Canvas.SetLeft(leftBackground, 0);
            Canvas.SetTop(leftBackground, RULER_THICKNESS);
            _rulerCanvas.Children.Add(leftBackground);

            var gridSpacing = settings.GridSpacing;

            // Vertical ticks on top ruler
            for (double x = 0; x <= doc.WidthMm; x += gridSpacing)
            {
                bool isMajor = Math.Abs(x % majorStep) < 0.0001 || x == 0;
                bool showLabel = Math.Abs(x % labelStep) < 0.0001;
                double len = isMajor ? RULER_THICKNESS : RULER_THICKNESS / 2.5;
                double cx = RULER_THICKNESS + x;

                var line = new Line
                {
                    X1 = cx,
                    X2 = cx,
                    Y1 = RULER_THICKNESS - len,
                    Y2 = RULER_THICKNESS,
                    Stroke = tickBrush,
                    StrokeThickness = isMajor ? 1.0 : 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                _rulerCanvas.Children.Add(line);

                if (showLabel && x > 0)
                {
                    var label = CreateRulerLabel(x, rotate: false);
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var textWidth = label.DesiredSize.Width;
                    Canvas.SetLeft(label, cx - textWidth / 2);
                    Canvas.SetTop(label, 1);
                    _rulerCanvas.Children.Add(label);
                }
            }

            // Horizontal ticks on left ruler
            for (double y = 0; y <= doc.HeightMm; y += gridSpacing)
            {
                bool isMajor = Math.Abs(y % majorStep) < 0.0001 || y == 0;
                bool showLabel = Math.Abs(y % labelStep) < 0.0001;
                double len = isMajor ? RULER_THICKNESS : RULER_THICKNESS / 2.5;
                double cy = RULER_THICKNESS + y;

                var line = new Line
                {
                    X1 = RULER_THICKNESS - len,
                    X2 = RULER_THICKNESS,
                    Y1 = cy,
                    Y2 = cy,
                    Stroke = tickBrush,
                    StrokeThickness = isMajor ? 1.0 : 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                _rulerCanvas.Children.Add(line);

                if (showLabel && y > 0)
                {
                    var label = CreateRulerLabel(y, rotate: true);
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var textHeight = label.DesiredSize.Height;
                    Canvas.SetLeft(label, 1);
                    Canvas.SetTop(label, cy - textHeight / 2);
                    _rulerCanvas.Children.Add(label);
                }
            }
        }

        private static Rectangle CreateRulerBackground(double width, double height, Brush fill, Brush stroke)
        {
            return new Rectangle
            {
                Width = width,
                Height = height,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 0.5,
                IsHitTestVisible = false
            };
        }

        private static TextBlock CreateRulerLabel(double value, bool rotate)
        {
            var tb = new TextBlock
            {
                Text = value.ToString("0"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray,
                IsHitTestVisible = false
            };
            if (rotate)
            {
                tb.LayoutTransform = new RotateTransform(-90);
            }
            return tb;
        }

        #endregion

        #region Safe Margin Overlay

        private void DrawSafeMarginOverlay(RenderSettings settings)
        {
            if (!settings.IsMarginOverlayVisible) return;

            var doc = _getDocument();
            var marginBrush = new SolidColorBrush(Color.FromArgb(40, 255, 100, 100));
            var marginStroke = new SolidColorBrush(Color.FromArgb(80, 200, 50, 50));
            var docLeft = RULER_THICKNESS;
            var docTop = RULER_THICKNESS;
            var docWidth = doc.WidthMm;
            var docHeight = doc.HeightMm;

            // When LockMarginsToCanvas is enabled and a paint canvas is defined,
            // the "margin" is the entire area OUTSIDE the paint canvas (between canvas and bed edges)
            if (settings.LockMarginsToCanvas && settings.PaintCanvasArea is Rect canvasArea)
            {
                // Draw margin zones around the paint canvas area (filling space between canvas and document edges)
                
                // Top zone: from document top to canvas top (full width)
                var topHeight = canvasArea.Top - docTop;
                if (topHeight > 0)
                {
                    var topRect = CreateMarginRect(docWidth, topHeight, marginBrush, marginStroke);
                    Canvas.SetLeft(topRect, docLeft);
                    Canvas.SetTop(topRect, docTop);
                    Panel.SetZIndex(topRect, 1);
                    _drawCanvas.Children.Add(topRect);
                }

                // Bottom zone: from canvas bottom to document bottom (full width)
                var bottomTop = canvasArea.Bottom;
                var bottomHeight = (docTop + docHeight) - bottomTop;
                if (bottomHeight > 0)
                {
                    var bottomRect = CreateMarginRect(docWidth, bottomHeight, marginBrush, marginStroke);
                    Canvas.SetLeft(bottomRect, docLeft);
                    Canvas.SetTop(bottomRect, bottomTop);
                    Panel.SetZIndex(bottomRect, 1);
                    _drawCanvas.Children.Add(bottomRect);
                }

                // Left zone: from canvas left edge to document left (between top and bottom zones)
                var leftWidth = canvasArea.Left - docLeft;
                var middleTop = canvasArea.Top;
                var middleHeight = canvasArea.Height;
                if (leftWidth > 0 && middleHeight > 0)
                {
                    var leftRect = CreateMarginRect(leftWidth, middleHeight, marginBrush, marginStroke);
                    Canvas.SetLeft(leftRect, docLeft);
                    Canvas.SetTop(leftRect, middleTop);
                    Panel.SetZIndex(leftRect, 1);
                    _drawCanvas.Children.Add(leftRect);
                }

                // Right zone: from canvas right edge to document right (between top and bottom zones)
                var rightLeft = canvasArea.Right;
                var rightWidth = (docLeft + docWidth) - rightLeft;
                if (rightWidth > 0 && middleHeight > 0)
                {
                    var rightRect = CreateMarginRect(rightWidth, middleHeight, marginBrush, marginStroke);
                    Canvas.SetLeft(rightRect, rightLeft);
                    Canvas.SetTop(rightRect, middleTop);
                    Panel.SetZIndex(rightRect, 1);
                    _drawCanvas.Children.Add(rightRect);
                }

                // Dashed border around the paint canvas (the safe drawing area)
                var safeAreaBorder = new Rectangle
                {
                    Width = canvasArea.Width,
                    Height = canvasArea.Height,
                    Stroke = new SolidColorBrush(Color.FromRgb(200, 100, 100)),
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 2],
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(safeAreaBorder, canvasArea.Left);
                Canvas.SetTop(safeAreaBorder, canvasArea.Top);
                Panel.SetZIndex(safeAreaBorder, 1);
                _drawCanvas.Children.Add(safeAreaBorder);
                
                return;
            }
            
            // Standard margin mode: margins are inset from document edges
            var marginLeft = settings.EffectiveMarginLeft;
            var marginTop = settings.EffectiveMarginTop;
            var marginRight = settings.EffectiveMarginRight;
            var marginBottom = settings.EffectiveMarginBottom;
            
            // Skip if all margins are zero
            if (marginLeft <= 0 && marginTop <= 0 && marginRight <= 0 && marginBottom <= 0) return;

            // Top margin zone
            if (marginTop > 0 && marginTop < docHeight)
            {
                var topRect = CreateMarginRect(docWidth, Math.Min(marginTop, docHeight), marginBrush, marginStroke);
                Canvas.SetLeft(topRect, docLeft);
                Canvas.SetTop(topRect, docTop);
                Panel.SetZIndex(topRect, 1);
                _drawCanvas.Children.Add(topRect);
            }

            // Bottom margin zone
            if (marginBottom > 0 && marginBottom < docHeight)
            {
                var bottomRect = CreateMarginRect(docWidth, Math.Min(marginBottom, docHeight), marginBrush, marginStroke);
                Canvas.SetLeft(bottomRect, docLeft);
                Canvas.SetTop(bottomRect, docTop + docHeight - marginBottom);
                Panel.SetZIndex(bottomRect, 1);
                _drawCanvas.Children.Add(bottomRect);
            }

            // Left margin zone (between top and bottom margins)
            var verticalHeight = Math.Max(0, docHeight - marginTop - marginBottom);
            if (marginLeft > 0 && marginLeft < docWidth && verticalHeight > 0)
            {
                var leftRect = CreateMarginRect(Math.Min(marginLeft, docWidth), verticalHeight, marginBrush, marginStroke);
                Canvas.SetLeft(leftRect, docLeft);
                Canvas.SetTop(leftRect, docTop + marginTop);
                Panel.SetZIndex(leftRect, 1);
                _drawCanvas.Children.Add(leftRect);
            }

            // Right margin zone (between top and bottom margins)
            if (marginRight > 0 && marginRight < docWidth && verticalHeight > 0)
            {
                var rightRect = CreateMarginRect(Math.Min(marginRight, docWidth), verticalHeight, marginBrush, marginStroke);
                Canvas.SetLeft(rightRect, docLeft + docWidth - marginRight);
                Canvas.SetTop(rightRect, docTop + marginTop);
                Panel.SetZIndex(rightRect, 1);
                _drawCanvas.Children.Add(rightRect);
            }

            // Dashed inner border (safe area)
            var safeWidth = Math.Max(0, docWidth - marginLeft - marginRight);
            var safeHeight = Math.Max(0, docHeight - marginTop - marginBottom);
            
            if (safeWidth > 0 && safeHeight > 0)
            {
                var safeAreaBorder = new Rectangle
                {
                    Width = safeWidth,
                    Height = safeHeight,
                    Stroke = new SolidColorBrush(Color.FromRgb(200, 100, 100)),
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 2],
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(safeAreaBorder, docLeft + marginLeft);
                Canvas.SetTop(safeAreaBorder, docTop + marginTop);
                Panel.SetZIndex(safeAreaBorder, 1);
                _drawCanvas.Children.Add(safeAreaBorder);
            }
        }

        private static Rectangle CreateMarginRect(double width, double height, Brush fill, Brush stroke)
        {
            return new Rectangle
            {
                Width = width,
                Height = height,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 0.5,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
        }

        #endregion

        #region Selection Indicators

        private void DrawSelectionIndicators()
        {
            if (!_selectionController.HasSelection) return;

            var doc = _getDocument();
            const double indicatorSize = 18.0;
            const double closedLoopTolerance = 0.5;

            var selectedIndices = _selectionController.SelectedIndices.OrderBy(i => i).ToList();
            if (selectedIndices.Count == 0) return;

            // Group selected strokes by GroupId
            var groupedStrokes = new Dictionary<Guid, List<LineStroke>>();
            var ungroupedStrokes = new List<LineStroke>();

            foreach (var idx in selectedIndices)
            {
                if (idx < 0 || idx >= doc.Strokes.Count) continue;
                var stroke = doc.Strokes[idx];

                if (stroke.GroupId == null)
                {
                    ungroupedStrokes.Add(stroke);
                }
                else
                {
                    if (!groupedStrokes.TryGetValue(stroke.GroupId.Value, out var list))
                    {
                        list = new List<LineStroke>();
                        groupedStrokes[stroke.GroupId.Value] = list;
                    }
                    list.Add(stroke);
                }
            }

            // Add child groups if parent has intermediate points enabled
            var showIntermediatePointsForGroups = _getShowIntermediatePointsForGroups();
            var parentGroupIds = groupedStrokes.Keys.ToList();
            var allDescendantGroupIds = GetAllDescendantGroupIds(
                parentGroupIds.Where(id => showIntermediatePointsForGroups.Contains(id)), doc);

            foreach (var stroke in doc.Strokes)
            {
                if (stroke.GroupId.HasValue && allDescendantGroupIds.Contains(stroke.GroupId.Value))
                {
                    if (!groupedStrokes.TryGetValue(stroke.GroupId.Value, out var childList))
                    {
                        childList = new List<LineStroke>();
                        groupedStrokes[stroke.GroupId.Value] = childList;
                    }
                    if (!childList.Contains(stroke))
                    {
                        childList.Add(stroke);
                    }
                }
            }

            // Collect all start and end points
            var allStartPoints = new List<PointMm>();
            var allEndPoints = new List<PointMm>();

            foreach (var (_, strokes) in groupedStrokes)
            {
                var startStroke = strokes.FirstOrDefault(s => s.IsGroupStart);
                var endStroke = strokes.FirstOrDefault(s => s.IsGroupEnd);

                if (startStroke != null) allStartPoints.Add(startStroke.A);
                else if (strokes.Count > 0) allStartPoints.Add(strokes[0].A);

                if (endStroke != null) allEndPoints.Add(endStroke.B);
                else if (strokes.Count > 0) allEndPoints.Add(strokes[^1].B);
            }

            foreach (var stroke in ungroupedStrokes)
            {
                allStartPoints.Add(stroke.A);
                allEndPoints.Add(stroke.B);
            }

            // Find closed loop points
            var closedLoopPoints = new HashSet<(double X, double Y)>();
            foreach (var startPt in allStartPoints)
            {
                foreach (var endPt in allEndPoints)
                {
                    if (Math.Abs(startPt.X - endPt.X) < closedLoopTolerance &&
                        Math.Abs(startPt.Y - endPt.Y) < closedLoopTolerance)
                    {
                        closedLoopPoints.Add((Math.Round(startPt.X, 1), Math.Round(startPt.Y, 1)));
                    }
                }
            }

            // Draw indicators for each group
            foreach (var (_, strokes) in groupedStrokes)
            {
                DrawIndicatorsUsingMarkers(strokes, indicatorSize, closedLoopTolerance, closedLoopPoints, doc);
            }

            // Draw indicators for ungrouped strokes
            foreach (var stroke in ungroupedStrokes)
            {
                DrawIndicatorsUsingMarkers(new List<LineStroke> { stroke }, indicatorSize, closedLoopTolerance, closedLoopPoints, doc);
            }
        }

        private void DrawIndicatorsUsingMarkers(
            List<LineStroke> strokes,
            double indicatorSize,
            double closedLoopTolerance,
            HashSet<(double X, double Y)> closedLoopPoints,
            PlotDocument doc)
        {
            if (strokes.Count == 0) return;

            var showIntermediatePointsForGroups = _getShowIntermediatePointsForGroups();
            var groupId = strokes.FirstOrDefault(s => s.GroupId.HasValue)?.GroupId;
            var showIntermediatePoints = groupId.HasValue && HasAncestorWithIntermediatePointsEnabled(groupId.Value, doc, showIntermediatePointsForGroups);

            var startStroke = strokes.FirstOrDefault(s => s.IsGroupStart);
            var startPoint = startStroke?.A ?? strokes[0].A;
            var endStroke = strokes.FirstOrDefault(s => s.IsGroupEnd);
            var endPoint = endStroke?.B ?? strokes[^1].B;

            var startRounded = (Math.Round(startPoint.X, 1), Math.Round(startPoint.Y, 1));
            var endRounded = (Math.Round(endPoint.X, 1), Math.Round(endPoint.Y, 1));

            bool startIsClosed = closedLoopPoints.Contains(startRounded);
            bool endIsClosed = closedLoopPoints.Contains(endRounded);
            bool isSelfClosed = Math.Abs(startPoint.X - endPoint.X) < closedLoopTolerance &&
                               Math.Abs(startPoint.Y - endPoint.Y) < closedLoopTolerance;

            // Draw intermediate points if enabled
            if (showIntermediatePoints && strokes.Count > 1)
            {
                var allPoints = new List<PointMm>();
                var seenPoints = new HashSet<(double, double)>();

                allPoints.Add(strokes[0].A);
                seenPoints.Add((Math.Round(strokes[0].A.X, 2), Math.Round(strokes[0].A.Y, 2)));

                foreach (var stroke in strokes)
                {
                    var aKey = (Math.Round(stroke.A.X, 2), Math.Round(stroke.A.Y, 2));
                    var bKey = (Math.Round(stroke.B.X, 2), Math.Round(stroke.B.Y, 2));

                    if (!seenPoints.Contains(aKey))
                    {
                        allPoints.Add(stroke.A);
                        seenPoints.Add(aKey);
                    }
                    if (!seenPoints.Contains(bKey))
                    {
                        allPoints.Add(stroke.B);
                        seenPoints.Add(bKey);
                    }
                }

                const double intermediateSize = 10.0;
                for (int i = 1; i < allPoints.Count - 1; i++)
                {
                    var point = allPoints[i];
                    var progress = (double)i / (allPoints.Count - 1);
                    var (fillColor, strokeColor) = GetHeatMapColor(progress);

                    var indicator = new Ellipse
                    {
                        Width = intermediateSize,
                        Height = intermediateSize,
                        Fill = new SolidColorBrush(fillColor),
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = 1.5,
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    Canvas.SetLeft(indicator, point.X - intermediateSize / 2.0);
                    Canvas.SetTop(indicator, point.Y - intermediateSize / 2.0);
                    Panel.SetZIndex(indicator, 4);
                    _drawCanvas.Children.Add(indicator);
                }
            }

            // Draw start/end indicators
            if (isSelfClosed)
            {
                DrawClosedLoopIndicator(startPoint, indicatorSize);
            }
            else
            {
                // Start indicator
                if (startIsClosed)
                {
                    DrawClosedLoopIndicator(startPoint, indicatorSize);
                }
                else
                {
                    DrawStartIndicator(startPoint, indicatorSize);
                }

                // End indicator
                bool endSameAsStart = Math.Abs(startPoint.X - endPoint.X) < closedLoopTolerance &&
                                     Math.Abs(startPoint.Y - endPoint.Y) < closedLoopTolerance;

                if (!endSameAsStart)
                {
                    if (endIsClosed)
                    {
                        DrawClosedLoopIndicator(endPoint, indicatorSize);
                    }
                    else
                    {
                        DrawEndIndicator(endPoint, indicatorSize);
                    }
                }
            }
        }

        private void DrawStartIndicator(PointMm point, double size)
        {
            var indicator = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(180, 0, 180, 0)),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 0)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(indicator, point.X - size / 2.0);
            Canvas.SetTop(indicator, point.Y - size / 2.0);
            Panel.SetZIndex(indicator, 5);
            _drawCanvas.Children.Add(indicator);
        }

        private void DrawEndIndicator(PointMm point, double size)
        {
            var indicator = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(180, 220, 50, 50)),
                Stroke = new SolidColorBrush(Color.FromRgb(160, 30, 30)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(indicator, point.X - size / 2.0);
            Canvas.SetTop(indicator, point.Y - size / 2.0);
            Panel.SetZIndex(indicator, 5);
            _drawCanvas.Children.Add(indicator);
        }

        private void DrawClosedLoopIndicator(PointMm point, double size)
        {
            var indicator = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(180, 128, 0, 128)),
                Stroke = new SolidColorBrush(Color.FromRgb(100, 0, 100)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(indicator, point.X - size / 2.0);
            Canvas.SetTop(indicator, point.Y - size / 2.0);
            Panel.SetZIndex(indicator, 5);
            _drawCanvas.Children.Add(indicator);
        }

        #endregion

        #region Reference Image

        private void RenderReferenceImage()
        {
            if (_imageService.ProcessedImage == null || _imageService.ImageRect is not Rect rect) return;

            var image = new Image
            {
                Source = _imageService.ProcessedImage,
                Width = rect.Width,
                Height = rect.Height,
                Opacity = 0.65,
                IsHitTestVisible = false
            };
            _drawCanvas.Children.Add(image);
            Canvas.SetLeft(image, rect.Left);
            Canvas.SetTop(image, rect.Top);
            ApplyImageRotation(image, _imageService.Angle);
            Panel.SetZIndex(image, 1);

            var outline = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Stroke = Brushes.DimGray,
                StrokeThickness = 1,
                StrokeDashArray = [4, 2],
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            _drawCanvas.Children.Add(outline);
            Canvas.SetLeft(outline, rect.Left);
            Canvas.SetTop(outline, rect.Top);
            ApplyImageRotation(outline, _imageService.Angle);
            Panel.SetZIndex(outline, 6);

            // Note: Image manipulation handles are rendered by MainWindow since they require event handlers
        }

        private static void ApplyImageRotation(UIElement element, double angleDegrees)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new RotateTransform(angleDegrees);
        }

        #endregion

        #region Working Area Visual

        /// <summary>
        /// Creates a visual representation of the working area.
        /// </summary>
        public static Rectangle CreateWorkingAreaVisual()
        {
            return new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(32, 0, 122, 204)),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Calculates a heat map color gradient from green (t=0) through yellow (t=0.5) to red (t=1).
        /// </summary>
        public static (Color fill, Color stroke) GetHeatMapColor(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);

            byte r, g, b;

            if (t < 0.5)
            {
                var localT = t * 2.0;
                r = (byte)(localT * 220);
                g = 180;
                b = 0;
            }
            else
            {
                var localT = (t - 0.5) * 2.0;
                r = 220;
                g = (byte)(180 - localT * 130);
                b = (byte)(localT * 50);
            }

            var fill = Color.FromArgb(180, r, g, b);
            var stroke = Color.FromRgb(
                (byte)(r * 0.7),
                (byte)(g * 0.5),
                (byte)(b * 0.5));

            return (fill, stroke);
        }

        private static HashSet<Guid> GetAllDescendantGroupIds(IEnumerable<Guid> parentGroupIds, PlotDocument doc)
        {
            var result = new HashSet<Guid>(parentGroupIds);
            var toProcess = new Queue<Guid>(parentGroupIds);

            while (toProcess.Count > 0)
            {
                var currentParent = toProcess.Dequeue();

                foreach (var stroke in doc.Strokes)
                {
                    if (stroke.ParentGroupId == currentParent && stroke.GroupId.HasValue)
                    {
                        var childGroupId = stroke.GroupId.Value;
                        if (result.Add(childGroupId))
                        {
                            toProcess.Enqueue(childGroupId);
                        }
                    }
                }
            }

            return result;
        }

        private static bool HasAncestorWithIntermediatePointsEnabled(Guid groupId, PlotDocument doc, HashSet<Guid> showIntermediatePointsForGroups)
        {
            if (showIntermediatePointsForGroups.Contains(groupId))
                return true;

            var parentGroupId = doc.Strokes
                .FirstOrDefault(s => s.GroupId == groupId)?.ParentGroupId;

            var visited = new HashSet<Guid> { groupId };
            while (parentGroupId.HasValue && !visited.Contains(parentGroupId.Value))
            {
                if (showIntermediatePointsForGroups.Contains(parentGroupId.Value))
                    return true;

                visited.Add(parentGroupId.Value);
                parentGroupId = doc.Strokes
                    .FirstOrDefault(s => s.GroupId == parentGroupId.Value)?.ParentGroupId;
            }

            return false;
        }

        #endregion
    }
}
