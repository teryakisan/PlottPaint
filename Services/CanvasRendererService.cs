using NVSPlotter.Models;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        public double ZoomScale { get; set; } = 1.0;
        public double CanvasRotationAngle { get; set; }
        public double BedX { get; set; }
        public double BedY { get; set; }
        public bool BedFromGrbl { get; set; }
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

        private const double RULER_THICKNESS = 18.0;
        private const double HANDLE_MARGIN = 100.0;
        private const double ROTATE_HANDLE_OFFSET = 40.0;
        private const double ROTATE_HANDLE_SIZE = 16.0;

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
            _paintWellController.RenderPaintWells(settings.CanvasRotationAngle, settings.IsPaintModeEnabled);

            // Render strokes
            RenderStrokes(settings);

            // Selection indicators
            DrawSelectionIndicators();

            // Selection visuals (bounding box, handles)
            _selectionController.RenderSelectionVisuals();

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

                var ln = new Line
                {
                    X1 = s.A.X,
                    Y1 = s.A.Y,
                    X2 = s.B.X,
                    Y2 = s.B.Y,
                    Stroke = strokeBrush,
                    StrokeThickness = adaptiveThickness,
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(ln, 4);
                _drawCanvas.Children.Add(ln);
            }
        }

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
            var margin = settings.SafeMarginMm;
            if (margin <= 0) return;

            var marginBrush = new SolidColorBrush(Color.FromArgb(40, 255, 100, 100));
            var marginStroke = new SolidColorBrush(Color.FromArgb(80, 200, 50, 50));

            var docLeft = RULER_THICKNESS;
            var docTop = RULER_THICKNESS;
            var docWidth = doc.WidthMm;
            var docHeight = doc.HeightMm;

            // Top margin zone
            if (margin < docHeight)
            {
                var topRect = CreateMarginRect(docWidth, Math.Min(margin, docHeight), marginBrush, marginStroke);
                Canvas.SetLeft(topRect, docLeft);
                Canvas.SetTop(topRect, docTop);
                Panel.SetZIndex(topRect, 1);
                _drawCanvas.Children.Add(topRect);
            }

            // Bottom margin zone
            if (margin < docHeight)
            {
                var bottomRect = CreateMarginRect(docWidth, Math.Min(margin, docHeight), marginBrush, marginStroke);
                Canvas.SetLeft(bottomRect, docLeft);
                Canvas.SetTop(bottomRect, docTop + docHeight - margin);
                Panel.SetZIndex(bottomRect, 1);
                _drawCanvas.Children.Add(bottomRect);
            }

            // Left margin zone
            var verticalHeight = Math.Max(0, docHeight - 2 * margin);
            if (margin < docWidth && verticalHeight > 0)
            {
                var leftRect = CreateMarginRect(Math.Min(margin, docWidth), verticalHeight, marginBrush, marginStroke);
                Canvas.SetLeft(leftRect, docLeft);
                Canvas.SetTop(leftRect, docTop + margin);
                Panel.SetZIndex(leftRect, 1);
                _drawCanvas.Children.Add(leftRect);
            }

            // Right margin zone
            if (margin < docWidth && verticalHeight > 0)
            {
                var rightRect = CreateMarginRect(Math.Min(margin, docWidth), verticalHeight, marginBrush, marginStroke);
                Canvas.SetLeft(rightRect, docLeft + docWidth - margin);
                Canvas.SetTop(rightRect, docTop + margin);
                Panel.SetZIndex(rightRect, 1);
                _drawCanvas.Children.Add(rightRect);
            }

            // Dashed inner border
            var safeAreaBorder = new Rectangle
            {
                Width = Math.Max(0, docWidth - 2 * margin),
                Height = Math.Max(0, docHeight - 2 * margin),
                Stroke = new SolidColorBrush(Color.FromRgb(200, 100, 100)),
                StrokeThickness = 1,
                StrokeDashArray = [4, 2],
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(safeAreaBorder, docLeft + margin);
            Canvas.SetTop(safeAreaBorder, docTop + margin);
            Panel.SetZIndex(safeAreaBorder, 1);
            _drawCanvas.Children.Add(safeAreaBorder);
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
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 2],
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
