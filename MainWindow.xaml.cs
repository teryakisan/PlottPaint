using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NVSPlotter
{
    public partial class MainWindow : Window
    {
        // --- Document is stored in mm; canvas is "px" where 1 px = 1 mm before zoom ---
        private PlotDocument _doc = new PlotDocument(841, 1189);

        // Canvas transforms
        private readonly ScaleTransform _zoom = new ScaleTransform(1.0, 1.0);
        private readonly RotateTransform _canvasRotation = new RotateTransform(0);

        // Working area overlay
        private Rect? _workingAreaRect;
        private bool _isDefiningWorkingArea;
        private bool _isWorkingAreaDragging;
        private PointMm _workingAreaDragStart;
        private Rectangle? _workingAreaPreviewRect;

        // Drawing state
        private bool _isDrawing;
        private PointMm _startMm;
        private Line? _previewLine;

        // Undo
        private readonly Stack<LineStroke> _undo = new Stack<LineStroke>();

        // Pan state (middle mouse)
        private bool _isPanning;
        private Point _panStartMouse;
        private double _panStartH;
        private double _panStartV;

        // G-code cache
        private string _lastGcode = "";

        // Plotter connection
        private GrblConnection? _grbl;
        private CancellationTokenSource? _sendCts;

        // --- Machine / GRBL state ---
        private double _bedX = 841;   // $130
        private double _bedY = 1189;  // $131
        private bool _bedFromGrbl;

        // $23 homing dir invert mask => bit set means home toward + (i.e., home at MAX end)
        private int _homingDirMask = 0;
        private bool _homeAtMaxX = false; // derived from $23 bit0
        private bool _homeAtMaxY = false; // derived from $23 bit1

        private bool _isHomed;

        private const double SAFE_MARGIN_MM = 50.0;

        public MainWindow()
        {
            InitializeComponent();

            DrawCanvas.RenderTransform = _zoom;
            DrawCanvas.RenderTransformOrigin = new Point(0, 0);
            if (RulerCanvas != null)
            {
                RulerCanvas.RenderTransform = _zoom;
                RulerCanvas.RenderTransformOrigin = new Point(0, 0);
            }
            CanvasHost.LayoutTransform = _canvasRotation;

            if (ZoomLabel != null) ZoomLabel.Text = "100%";
            UpdateWorkingAreaStatus();
            RefreshPorts();

            RenderAll();
            UpdateConnStatus();
        }

        private void CanvasScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateZoomHost();
        }

        private void UpdateZoomHost()
        {
            if (!IsLoaded) return;
            if (CanvasHost == null || CanvasScroll == null || DrawCanvas == null) return;

            var z = _zoom.ScaleX;
            if (z <= 0) z = 1;

            var scaledW = _doc.WidthMm * z;
            var scaledH = _doc.HeightMm * z;

            var hostW = Math.Max(CanvasScroll.ViewportWidth, scaledW);
            var hostH = Math.Max(CanvasScroll.ViewportHeight, scaledH);

            if (double.IsNaN(hostW) || double.IsInfinity(hostW) || hostW <= 0) hostW = scaledW;
            if (double.IsNaN(hostH) || double.IsInfinity(hostH) || hostH <= 0) hostH = scaledH;

            CanvasHost.Width = hostW;
            CanvasHost.Height = hostH;

            var left = (hostW - scaledW) / 2.0;
            var top = (hostH - scaledH) / 2.0;

            if (left < 0) left = 0;
            if (top < 0) top = 0;

            var margin = new Thickness(left, top, 0, 0);
            DrawCanvas.Margin = margin;
            if (RulerCanvas != null)
                RulerCanvas.Margin = margin;
        }

        // ----------------------------
        // UI: Document / View
        // ----------------------------
        private void PagePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;

            var idx = PagePresetCombo.SelectedIndex;
            _doc = idx == 1 ? new PlotDocument(1189, 841) : new PlotDocument(841, 1189);
            _undo.Clear();
            _lastGcode = "";
            RenderAll();
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            SetZoom(ZoomSlider.Value);
        }

        private void DrawCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Wheel nudges zoom slider
            var delta = e.Delta > 0 ? 0.1 : -0.1;
            var next = Math.Clamp(ZoomSlider.Value + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);
            ZoomSlider.Value = next;
            e.Handled = true;
        }

        private void FitBtn_Click(object sender, RoutedEventArgs e)
        {
            // Fit page into viewport (rough, but works)
            var viewportW = CanvasScroll.ViewportWidth;
            var viewportH = CanvasScroll.ViewportHeight;
            if (viewportW <= 0 || viewportH <= 0) return;

            var zx = viewportW / _doc.WidthMm;
            var zy = viewportH / _doc.HeightMm;
            var z = Math.Clamp(Math.Min(zx, zy) * 0.95, ZoomSlider.Minimum, ZoomSlider.Maximum);
            ZoomSlider.Value = z;
        }

        private void RotateCanvasBtn_Click(object sender, RoutedEventArgs e)
        {
            var next = (_canvasRotation.Angle + 90) % 360;
            _canvasRotation.Angle = next;
            UpdateZoomHost();
        }

        private void DefineWorkingAreaBtn_Click(object sender, RoutedEventArgs e)
        {
            _isDefiningWorkingArea = true;
            _isWorkingAreaDragging = false;
            RemoveWorkingAreaPreview();
            UpdateWorkingAreaStatus();
        }

        private void ClearWorkingAreaBtn_Click(object sender, RoutedEventArgs e)
        {
            _isDefiningWorkingArea = false;
            _isWorkingAreaDragging = false;
            _workingAreaRect = null;
            RemoveWorkingAreaPreview();
            UpdateWorkingAreaStatus();
            RenderAll();
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            _doc.Strokes.Clear();
            _undo.Clear();
            _lastGcode = "";
            RenderAll();
        }

        private void UndoBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_doc.Strokes.Count == 0) return;
            var last = _doc.Strokes[^1];
            _doc.Strokes.RemoveAt(_doc.Strokes.Count - 1);
            _undo.Push(last);
            _lastGcode = "";
            RenderAll();
        }

        private void SetZoom(double z)
        {
            if (z <= 0) z = 1;

            var old = _zoom.ScaleX;
            if (old <= 0) old = 1;

            // Keep viewport center fixed during zoom
            var cx = CanvasScroll.ViewportWidth / 2.0;
            var cy = CanvasScroll.ViewportHeight / 2.0;

            // If viewport not ready yet, just set zoom and center host.
            if (cx <= 0 || cy <= 0)
            {
                _zoom.ScaleX = z;
                _zoom.ScaleY = z;
                if (ZoomLabel != null) ZoomLabel.Text = $"{(int)Math.Round(z * 100)}%";
                UpdateZoomHost();
                return;
            }

            var contentCenterX = (CanvasScroll.HorizontalOffset + cx) / old;
            var contentCenterY = (CanvasScroll.VerticalOffset + cy) / old;

            _zoom.ScaleX = z;
            _zoom.ScaleY = z;
            if (ZoomLabel != null) ZoomLabel.Text = $"{(int)Math.Round(z * 100)}%";

            UpdateZoomHost();
            CanvasScroll.UpdateLayout();

            var newOffX = contentCenterX * z - cx;
            var newOffY = contentCenterY * z - cy;

            CanvasScroll.ScrollToHorizontalOffset(newOffX);
            CanvasScroll.ScrollToVerticalOffset(newOffY);
        }


        // ----------------------------
        // Canvas: Draw / Pan
        // ----------------------------
        private void DrawCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStartMouse = e.GetPosition(CanvasScroll);
                _panStartH = CanvasScroll.HorizontalOffset;
                _panStartV = CanvasScroll.VerticalOffset;
                DrawCanvas.CaptureMouse();
                e.Handled = true;
            }
        }

        private void DrawCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning && e.ChangedButton == MouseButton.Middle)
            {
                _isPanning = false;
                DrawCanvas.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void DrawCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                DrawCanvas.ReleaseMouseCapture();
            }
            if (_isWorkingAreaDragging)
            {
                CancelWorkingAreaDrag();
            }
            if (_isDrawing)
            {
                CancelPreview();
            }
        }

        private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isWorkingAreaDragging)
            {
                UpdateWorkingAreaDrag(e);
                return;
            }

            if (_isPanning && e.MiddleButton == MouseButtonState.Pressed)
            {
                var cur = e.GetPosition(CanvasScroll);
                var dx = cur.X - _panStartMouse.X;
                var dy = cur.Y - _panStartMouse.Y;
                CanvasScroll.ScrollToHorizontalOffset(_panStartH - dx);
                CanvasScroll.ScrollToVerticalOffset(_panStartV - dy);
                e.Handled = true;
                return;
            }

            if (!_isDrawing) return;

            var mm = MouseToMm(e.GetPosition(CanvasScroll));
            mm = ClampToPage(mm);

            if (_previewLine != null)
            {
                _previewLine.X2 = mm.X;
                _previewLine.Y2 = mm.Y;
            }
        }

        private void DrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDefiningWorkingArea)
            {
                BeginWorkingAreaDrag(e);
                return;
            }

            if ((ToolCombo.SelectedIndex == 1) || _isPanning) return; // Pan tool

            _isDrawing = true;
            _startMm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));

            _previewLine = new Line
            {
                X1 = _startMm.X,
                Y1 = _startMm.Y,
                X2 = _startMm.X,
                Y2 = _startMm.Y,
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                SnapsToDevicePixels = true
            };

            DrawCanvas.Children.Add(_previewLine);
            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isWorkingAreaDragging)
            {
                CompleteWorkingAreaDrag(e);
                return;
            }

            if (!_isDrawing) return;

            var endMm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));

            CancelPreview();

            // Ignore tiny clicks
            if (Distance(_startMm, endMm) < 0.25) return;

            var stroke = new LineStroke(_startMm, endMm);
            _doc.Strokes.Add(stroke);
            _lastGcode = "";

            RenderAll();
            e.Handled = true;
        }

        private void CancelPreview()
        {
            _isDrawing = false;
            if (_previewLine != null)
            {
                DrawCanvas.Children.Remove(_previewLine);
                _previewLine = null;
            }
            DrawCanvas.ReleaseMouseCapture();
        }

        // Converts mouse pos to mm in canvas logical space (1 unit = 1 mm),
        // compensating for zoom (RenderTransform).
        private PointMm MouseToMm(Point pViewportSpace)
        {
            var pCanvas = CanvasScroll.TranslatePoint(pViewportSpace, DrawCanvas);
            return new PointMm(pCanvas.X, pCanvas.Y);
        }

        private PointMm ClampToPage(PointMm p)
        {
            var x = Math.Clamp(p.X, 0, _doc.WidthMm);
            var y = Math.Clamp(p.Y, 0, _doc.HeightMm);
            return new PointMm(x, y);
        }

        private static double Distance(PointMm a, PointMm b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ----------------------------
        // Render
        // ----------------------------
        private void RenderAll()
        {
            if (DrawCanvas == null || RulerCanvas == null) return;

            DrawCanvas.Children.Clear();
            RulerCanvas.Children.Clear();

            // Size canvas to page
            DrawCanvas.Width = _doc.WidthMm;
            DrawCanvas.Height = _doc.HeightMm;
            RulerCanvas.Width = _doc.WidthMm;
            RulerCanvas.Height = _doc.HeightMm;

            // Page border
            var pageRect = new Rectangle
            {
                Width = _doc.WidthMm,
                Height = _doc.HeightMm,
                Stroke = Brushes.Black,
                StrokeThickness = 1.0,
                Fill = Brushes.White
            };
            DrawCanvas.Children.Add(pageRect);
            Canvas.SetLeft(pageRect, 0);
            Canvas.SetTop(pageRect, 0);

            DrawRulers();

            // Working area overlay (visual only)
            if (_workingAreaRect is Rect definedArea)
            {
                var workRect = CreateWorkingAreaVisual();
                workRect.Width = definedArea.Width;
                workRect.Height = definedArea.Height;
                Canvas.SetLeft(workRect, definedArea.Left);
                Canvas.SetTop(workRect, definedArea.Top);
                DrawCanvas.Children.Add(workRect);
            }

            // Existing strokes
            foreach (var s in _doc.Strokes)
            {
                var ln = new Line
                {
                    X1 = s.A.X,
                    Y1 = s.A.Y,
                    X2 = s.B.X,
                    Y2 = s.B.Y,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.2,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(ln, EdgeMode.Aliased);
                DrawCanvas.Children.Add(ln);
            }

            AppendLog($"Doc: {_doc.WidthMm:0} x {_doc.HeightMm:0} mm, strokes={_doc.Strokes.Count}");
            AppendLog($"Bed: X={_bedX:0.###} Y={_bedY:0.###} {(_bedFromGrbl ? "(from $$)" : "(default)")}, margin={SAFE_MARGIN_MM:0.###}mm");
            AppendLog($"$23={_homingDirMask} => HomeAtMax: X={_homeAtMaxX}, Y={_homeAtMaxY}, homed={_isHomed}");
            AppendLog("Work convention: X ALWAYS positive, Y ALWAYS negative.");

            UpdateZoomHost();
        }

        private void BeginWorkingAreaDrag(MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var start = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _workingAreaDragStart = start;
            _isWorkingAreaDragging = true;

            if (_workingAreaPreviewRect == null)
            {
                _workingAreaPreviewRect = CreateWorkingAreaVisual();
                DrawCanvas.Children.Add(_workingAreaPreviewRect);
            }

            UpdateWorkingAreaPreview(start, start);
            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void UpdateWorkingAreaDrag(MouseEventArgs e)
        {
            if (!_isWorkingAreaDragging || _workingAreaPreviewRect == null) return;

            var current = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            UpdateWorkingAreaPreview(_workingAreaDragStart, current);
            e.Handled = true;
        }

        private void CompleteWorkingAreaDrag(MouseButtonEventArgs e)
        {
            if (!_isWorkingAreaDragging) return;

            var end = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _isWorkingAreaDragging = false;
            DrawCanvas.ReleaseMouseCapture();

            var left = Math.Min(_workingAreaDragStart.X, end.X);
            var top = Math.Min(_workingAreaDragStart.Y, end.Y);
            var width = Math.Max(1, Math.Abs(_workingAreaDragStart.X - end.X));
            var height = Math.Max(1, Math.Abs(_workingAreaDragStart.Y - end.Y));

            _workingAreaRect = new Rect(left, top, width, height);
            _isDefiningWorkingArea = false;
            RemoveWorkingAreaPreview();
            UpdateWorkingAreaStatus();
            RenderAll();
            e.Handled = true;
        }

        private void CancelWorkingAreaDrag()
        {
            _isWorkingAreaDragging = false;
            DrawCanvas.ReleaseMouseCapture();
            RemoveWorkingAreaPreview();
            UpdateWorkingAreaStatus();
        }

        private void RemoveWorkingAreaPreview()
        {
            if (_workingAreaPreviewRect != null)
            {
                DrawCanvas.Children.Remove(_workingAreaPreviewRect);
                _workingAreaPreviewRect = null;
            }
        }

        private void UpdateWorkingAreaPreview(PointMm start, PointMm current)
        {
            if (_workingAreaPreviewRect == null) return;
            var left = Math.Min(start.X, current.X);
            var top = Math.Min(start.Y, current.Y);
            var width = Math.Max(1, Math.Abs(start.X - current.X));
            var height = Math.Max(1, Math.Abs(start.Y - current.Y));
            _workingAreaPreviewRect.Width = width;
            _workingAreaPreviewRect.Height = height;
            Canvas.SetLeft(_workingAreaPreviewRect, left);
            Canvas.SetTop(_workingAreaPreviewRect, top);
        }

        private Rectangle CreateWorkingAreaVisual()
        {
            return new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                StrokeDashArray = new DoubleCollection { 6, 2 },
                IsHitTestVisible = false
            };
        }

        private void UpdateWorkingAreaStatus()
        {
            if (WorkingAreaStatus == null) return;
            if (_isDefiningWorkingArea)
            {
                WorkingAreaStatus.Text = "Click and drag on canvas";
            }
            else if (_workingAreaRect is Rect rect)
            {
                WorkingAreaStatus.Text = $"Defined: {rect.Width:0.#} × {rect.Height:0.#} mm";
            }
            else
            {
                WorkingAreaStatus.Text = "Not defined";
            }
        }

        private void DrawRulers()
        {
            if (RulerCanvas == null) return;

            const double rulerThickness = 18.0;
            const double minorStep = 1.0;
            const double majorStep = 10.0;
            const double labelStep = 50.0;

            Brush rulerFill = new SolidColorBrush(Color.FromRgb(248, 248, 248));
            Brush borderBrush = Brushes.LightGray;
            Brush tickBrush = Brushes.Gray;

            Rectangle CreateBackground(double width, double height)
            {
                return new Rectangle
                {
                    Width = width,
                    Height = height,
                    Fill = rulerFill,
                    Stroke = borderBrush,
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false
                };
            }

            var topBackground = CreateBackground(_doc.WidthMm, rulerThickness);
            Canvas.SetLeft(topBackground, 0);
            Canvas.SetTop(topBackground, 0);
            RulerCanvas.Children.Add(topBackground);

            var leftBackground = CreateBackground(rulerThickness, _doc.HeightMm);
            Canvas.SetLeft(leftBackground, 0);
            Canvas.SetTop(leftBackground, 0);
            RulerCanvas.Children.Add(leftBackground);

            void AddVerticalTick(double x)
            {
                bool isMajor = Math.Abs(x % majorStep) < 0.0001;
                bool showLabel = isMajor && Math.Abs(x % labelStep) < 0.0001;
                double len = isMajor ? rulerThickness : rulerThickness / 2.5;

                var line = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = len,
                    Stroke = tickBrush,
                    StrokeThickness = 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                RulerCanvas.Children.Add(line);

                if (showLabel)
                {
                    var label = CreateRulerLabel(x, rotate: false);
                    Canvas.SetLeft(label, x + 2);
                    Canvas.SetTop(label, 1);
                    RulerCanvas.Children.Add(label);
                }
            }

            void AddHorizontalTick(double y)
            {
                bool isMajor = Math.Abs(y % majorStep) < 0.0001;
                bool showLabel = isMajor && Math.Abs(y % labelStep) < 0.0001;
                double len = isMajor ? rulerThickness : rulerThickness / 2.5;

                var line = new Line
                {
                    X1 = 0,
                    X2 = len,
                    Y1 = y,
                    Y2 = y,
                    Stroke = tickBrush,
                    StrokeThickness = 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                RulerCanvas.Children.Add(line);

                if (showLabel)
                {
                    var label = CreateRulerLabel(y, rotate: true);
                    Canvas.SetLeft(label, 1);
                    Canvas.SetTop(label, y + 2);
                    RulerCanvas.Children.Add(label);
                }
            }

            for (double x = 0; x <= _doc.WidthMm; x += minorStep)
            {
                AddVerticalTick(x);
            }

            for (double y = 0; y <= _doc.HeightMm; y += minorStep)
            {
                AddHorizontalTick(y);
            }
        }

        private TextBlock CreateRulerLabel(double value, bool rotate)
        {
            var tb = new TextBlock
            {
                Text = value.ToString("0"),
                FontSize = 9,
                Foreground = Brushes.Gray,
                IsHitTestVisible = false
            };
            if (rotate)
            {
                tb.LayoutTransform = new RotateTransform(-90);
            }
            return tb;
        }

        // ----------------------------
        // G-code export
        // ----------------------------
        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            var g = BuildGcode();
            var dlg = new SaveFileDialog
            {
                Filter = "G-code (*.gcode;*.nc;*.txt)|*.gcode;*.nc;*.txt|All files (*.*)|*.*",
                FileName = "plot.gcode"
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, g, Encoding.UTF8);
                AppendLog($"Saved: {dlg.FileName}");
            }
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            var g = BuildGcode();
            Clipboard.SetText(g);
            AppendLog("G-code copied to clipboard.");
        }

        private string BuildGcode()
        {
            var feedXY = ParseDouble(FeedXYBox.Text, 3000);
            var zUp = ParseDouble(ZUpBox.Text, 10);
            var zDown = ParseDouble(ZDownBox.Text, 2);
            var optimize = OptimizeCheck.IsChecked == true;

            var strokes = _doc.Strokes.ToList();
            if (optimize) strokes = StrokeOptimizer.OptimizeNearest(strokes);

            // Fit doc into bed (with margin), possibly rotating CW to better fit.
            var fit = ComputeFit(_doc.WidthMm, _doc.HeightMm, _bedX, _bedY, SAFE_MARGIN_MM);

            AppendLog($"G-code fit={fit.Mode}, scale={fit.Scale:0.###}, usableBed=({_bedX - 2 * SAFE_MARGIN_MM:0.###} x {_bedY - 2 * SAFE_MARGIN_MM:0.###})");
            AppendLog("G-code convention: X>=0, Y<=0");

            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("; NVSPlotter");
            sb.AppendLine("; Units: mm");
            sb.AppendLine("; Work convention: X positive, Y negative");
            sb.AppendLine("G21");           // mm
            sb.AppendLine("G90");           // absolute
            sb.AppendLine("G54");
            sb.AppendLine("G92.1");         // clear G92 offsets
            sb.AppendLine("G10 L20 P1 X0 Y0"); // set G54 so current position (home) is work 0,0
            sb.AppendLine($"G0 Z{Fmt(zUp)}");

            foreach (var s in strokes)
            {
                var aBed = DocToBed(s.A, fit);
                var bBed = DocToBed(s.B, fit);

                var aWork = BedToWork(aBed);
                var bWork = BedToWork(bBed);

                sb.AppendLine($"G0 X{Fmt(aWork.X)} Y{Fmt(aWork.Y)}");
                sb.AppendLine($"G0 Z{Fmt(zDown)}");
                sb.AppendLine($"G1 X{Fmt(bWork.X)} Y{Fmt(bWork.Y)} F{Fmt(feedXY)}");
                sb.AppendLine($"G0 Z{Fmt(zUp)}");
            }

            sb.AppendLine("G0 X0 Y0"); // back to home (work origin)
            sb.AppendLine("M2");

            _lastGcode = sb.ToString();
            AppendLog($"Built G-code: lines={_lastGcode.Split('\n').Length}");
            return _lastGcode;
        }

        private enum FitMode { None, RotateCW }
        private readonly record struct FitSpec(FitMode Mode, double Scale, double Margin, double DocW, double DocH);

        private FitSpec ComputeFit(double docW, double docH, double bedX, double bedY, double margin)
        {
            var ux = Math.Max(1.0, bedX - margin * 2);
            var uy = Math.Max(1.0, bedY - margin * 2);

            // No-rotate scale
            var s0 = Math.Min(ux / docW, uy / docH);
            // Rotate CW scale
            var s1 = Math.Min(ux / docH, uy / docW);

            // Never scale up above 1.0
            s0 = Math.Min(s0, 1.0);
            s1 = Math.Min(s1, 1.0);

            if (s1 > s0) return new FitSpec(FitMode.RotateCW, s1, margin, docW, docH);
            return new FitSpec(FitMode.None, s0, margin, docW, docH);
        }

        // Map doc point into bed coordinates (origin = bed min/min corner), inside margins.
        private PointMm DocToBed(PointMm p, FitSpec fit)
        {
            var m = fit.Margin;
            var s = fit.Scale;

            return fit.Mode switch
            {
                FitMode.None =>
                    new PointMm(
                        ClampBedX(m + p.X * s),
                        ClampBedY(m + p.Y * s)
                    ),

                // CW rotation about doc:
                // x' = y
                // y' = docW - x
                FitMode.RotateCW =>
                    new PointMm(
                        ClampBedX(m + p.Y * s),
                        ClampBedY(m + (fit.DocW - p.X) * s)
                    ),

                _ =>
                    new PointMm(
                        ClampBedX(m + p.X * s),
                        ClampBedY(m + p.Y * s)
                    )
            };
        }

        private double ClampBedX(double x) => Math.Clamp(x, SAFE_MARGIN_MM, _bedX - SAFE_MARGIN_MM);
        private double ClampBedY(double y) => Math.Clamp(y, SAFE_MARGIN_MM, _bedY - SAFE_MARGIN_MM);

        // Convert bed-local positive coords into WORK coords relative to home (0,0).
        // REQUIRED BY USER: X ALWAYS positive, Y ALWAYS negative.
        //
        // We still compute "distance into the bed from HOME" using $23, because home might be at min or max.
        // Then we apply the requested sign convention:
        //   X = +distance, Y = -distance
        private PointMm BedToWork(PointMm bed)
        {
            // Distance from HOME along each axis into the bed (always positive)
            var distX = _homeAtMaxX ? (_bedX - bed.X) : bed.X;
            var distY = _homeAtMaxY ? (_bedY - bed.Y) : bed.Y;

            // Enforce requested sign convention
            var wx = Math.Max(0, distX);      // ALWAYS >= 0
            var wy = -Math.Max(0, distY);     // ALWAYS <= 0

            return new PointMm(wx, wy);
        }

        private static double ParseDouble(string? s, double fallback)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return v;
            return fallback;
        }

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ----------------------------
        // Plotter connection
        // ----------------------------
        private void RefreshPortsBtn_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        private void RefreshPorts()
        {
            PortCombo.Items.Clear();
            foreach (var p in SerialPort.GetPortNames().OrderBy(x => x))
                PortCombo.Items.Add(p);

            if (PortCombo.Items.Count > 0 && PortCombo.SelectedIndex < 0)
                PortCombo.SelectedIndex = 0;

            AppendLog("Ports refreshed.");
        }

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_grbl != null && _grbl.IsOpen)
            {
                await DisconnectAsync();
                return;
            }

            var port = PortCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(port))
            {
                AppendLog("No COM port selected.");
                return;
            }

            var baud = int.Parse(((ComboBoxItem)BaudCombo.SelectedItem).Content.ToString()!);

            try
            {
                _grbl = new GrblConnection(port, baud, AppendLog);
                await _grbl.OpenAsync();
                AppendLog("Connected.");

                _isHomed = false;

                // Load bed + homing mask from $$
                await LoadMachineSettingsAsync();

                RenderAll();
            }
            catch (Exception ex)
            {
                AppendLog("Connect failed: " + ex.Message);
                _grbl = null;
            }
            finally
            {
                UpdateConnStatus();
            }
        }

        private async Task LoadMachineSettingsAsync()
        {
            if (_grbl == null || !_grbl.IsOpen) return;

            try
            {
                AppendLog("Querying GRBL $$...");
                var lines = await _grbl.SendAndCollectAsync("$$", TimeSpan.FromSeconds(20));

                if (TryParseSetting(lines, "$130", out var x) && x > 0) { _bedX = x; _bedFromGrbl = true; }
                if (TryParseSetting(lines, "$131", out var y) && y > 0) { _bedY = y; _bedFromGrbl = true; }

                if (TryParseSettingInt(lines, "$23", out var m))
                {
                    _homingDirMask = m;
                    _homeAtMaxX = (m & 0x01) != 0;
                    _homeAtMaxY = (m & 0x02) != 0;
                }

                AppendLog($"Settings: $130={_bedX:0.###}, $131={_bedY:0.###}, $23={_homingDirMask} => HomeAtMax X={_homeAtMaxX} Y={_homeAtMaxY}");
            }
            catch (Exception ex)
            {
                AppendLog("$$ read failed: " + ex.Message);
            }
        }

        private static bool TryParseSetting(List<string> lines, string key, out double value)
        {
            value = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (!line.StartsWith(key + "=", StringComparison.Ordinal)) continue;

                var s = line.Substring(key.Length + 1).Trim();
                var paren = s.IndexOf('(');
                if (paren >= 0) s = s.Substring(0, paren).Trim();

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return true;
            }
            return false;
        }

        private static bool TryParseSettingInt(List<string> lines, string key, out int value)
        {
            value = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (!line.StartsWith(key + "=", StringComparison.Ordinal)) continue;

                var s = line.Substring(key.Length + 1).Trim();
                var paren = s.IndexOf('(');
                if (paren >= 0) s = s.Substring(0, paren).Trim();

                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)) return true;
            }
            return false;
        }

        private async Task DisconnectAsync()
        {
            try
            {
                _sendCts?.Cancel();
                _sendCts = null;

                if (_grbl != null)
                {
                    await _grbl.CloseAsync();
                    _grbl = null;
                }
                _isHomed = false;

                AppendLog("Disconnected.");
            }
            catch (Exception ex)
            {
                AppendLog("Disconnect error: " + ex.Message);
            }
            finally
            {
                UpdateConnStatus();
            }
        }

        private void UpdateConnStatus()
        {
            var ok = _grbl != null && _grbl.IsOpen;
            ConnectBtn.Content = ok ? "Disconnect" : "Connect";
            ConnStatusLabel.Text = ok ? $"Connected: {_grbl!.PortName} @ {_grbl.BaudRate}" : "Not connected.";
            SendBtn.IsEnabled = ok;
            StopBtn.IsEnabled = ok;
        }

        private async void HomeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            try
            {
                AppendLog("Homing: $H");
                await _grbl!.SendLineWaitOkAsync("$H", TimeSpan.FromSeconds(120));
                _isHomed = true;
                AppendLog("Home complete.");
            }
            catch (Exception ex)
            {
                _isHomed = false;
                AppendLog("Home failed: " + ex.Message);
            }
            finally
            {
                RenderAll();
            }
        }

        private async void UnlockBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try { await _grbl!.SendLineWaitOkAsync("$X", TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { AppendLog("$X failed: " + ex.Message); }
        }

        private async void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                await _grbl!.SoftResetAsync();
                _isHomed = false;
            }
            catch (Exception ex)
            {
                AppendLog("Reset failed: " + ex.Message);
            }
            finally
            {
                RenderAll();
            }
        }

        private async void ManualSendBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            var cmd = (ManualCmdBox.Text ?? "").Trim();
            if (cmd.Length == 0) return;

            ManualCmdBox.Text = "";
            try
            {
                await _grbl!.SendLineWaitOkAsync(cmd, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                AppendLog("Manual send failed: " + ex.Message);
            }
        }

        private async void SendBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            if (!_isHomed)
            {
                AppendLog("Refusing to send: not homed. Click Home first.");
                return;
            }

            var g = string.IsNullOrWhiteSpace(_lastGcode) ? BuildGcode() : _lastGcode;

            var lines = g.Split('\n')
                         .Select(x => x.Trim())
                         .Where(x => x.Length > 0 && !x.StartsWith(";"))
                         .ToList();

            if (lines.Count == 0)
            {
                AppendLog("No G-code to send.");
                return;
            }

            if (_sendCts != null)
            {
                AppendLog("Send already in progress.");
                return;
            }

            _sendCts = new CancellationTokenSource();
            var token = _sendCts.Token;

            AppendLog($"Sending {lines.Count} lines...");
            try
            {
                int sent = 0;
                foreach (var line in lines)
                {
                    token.ThrowIfCancellationRequested();
                    await _grbl!.SendLineWaitOkAsync(line, TimeSpan.FromSeconds(20), token);
                    sent++;
                    if (sent % 25 == 0) AppendLog($"Progress: {sent}/{lines.Count}");
                }
                AppendLog("Send complete.");
            }
            catch (OperationCanceledException)
            {
                AppendLog("Send canceled.");
            }
            catch (Exception ex)
            {
                AppendLog("Send error: " + ex.Message);
            }
            finally
            {
                _sendCts?.Dispose();
                _sendCts = null;
            }
        }

        private async void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            try
            {
                _sendCts?.Cancel();
                await _grbl!.SoftResetAsync();
                _isHomed = false;
                AppendLog("Stop: canceled + reset sent.");
            }
            catch (Exception ex)
            {
                AppendLog("Stop error: " + ex.Message);
            }
            finally
            {
                RenderAll();
            }
        }

        private bool EnsureConnected()
        {
            if (_grbl == null || !_grbl.IsOpen)
            {
                AppendLog("Not connected.");
                UpdateConnStatus();
                return false;
            }
            return true;
        }

        // ----------------------------
        // Logging
        // ----------------------------
        private void AppendLog(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                ConsoleBox.AppendText(line + Environment.NewLine);
                ConsoleBox.ScrollToEnd();
            });
        }

        // ----------------------------
        // Models / Helpers
        // ----------------------------
        private sealed class PlotDocument
        {
            public double WidthMm { get; }
            public double HeightMm { get; }
            public List<LineStroke> Strokes { get; } = new List<LineStroke>();

            public PlotDocument(double wMm, double hMm)
            {
                WidthMm = wMm;
                HeightMm = hMm;
            }
        }

        private readonly record struct PointMm(double X, double Y);

        private sealed class LineStroke
        {
            public PointMm A { get; }
            public PointMm B { get; }
            public LineStroke(PointMm a, PointMm b) { A = a; B = b; }
            public LineStroke Reversed() => new LineStroke(B, A);
        }

        private static class StrokeOptimizer
        {
            public static List<LineStroke> OptimizeNearest(List<LineStroke> strokes)
            {
                if (strokes.Count <= 2) return strokes;

                var remaining = new List<LineStroke>(strokes);
                var result = new List<LineStroke>(strokes.Count);

                var cur = new PointMm(0, 0);

                while (remaining.Count > 0)
                {
                    int bestIdx = 0;
                    bool bestReverse = false;
                    double bestDist = double.MaxValue;

                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var s = remaining[i];
                        var d1 = Dist(cur, s.A);
                        var d2 = Dist(cur, s.B);

                        if (d1 < bestDist)
                        {
                            bestDist = d1;
                            bestIdx = i;
                            bestReverse = false;
                        }
                        if (d2 < bestDist)
                        {
                            bestDist = d2;
                            bestIdx = i;
                            bestReverse = true;
                        }
                    }

                    var chosen = remaining[bestIdx];
                    remaining.RemoveAt(bestIdx);

                    if (bestReverse) chosen = chosen.Reversed();
                    result.Add(chosen);
                    cur = chosen.B;
                }

                return result;
            }

            private static double Dist(PointMm a, PointMm b)
            {
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        private sealed class GrblConnection : IDisposable
        {
            private readonly SerialPort _port;
            private readonly Action<string> _log;

            private readonly object _rxLock = new object();
            private readonly StringBuilder _rx = new StringBuilder(8192);
            private readonly Queue<string> _lines = new Queue<string>();

            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

            public string PortName => _port.PortName;
            public int BaudRate => _port.BaudRate;
            public bool IsOpen => _port.IsOpen;

            public GrblConnection(string portName, int baudRate, Action<string> log)
            {
                _log = log;
                _port = new SerialPort(portName, baudRate)
                {
                    NewLine = "\n",
                    DtrEnable = true,
                    RtsEnable = true,
                    ReadTimeout = 2000,
                    WriteTimeout = 2000
                };
                _port.DataReceived += Port_DataReceived;
            }

            public async Task OpenAsync()
            {
                _port.Open();

                // Wake GRBL
                _port.Write("\r\n\r\n");
                await Task.Delay(200);
                try { _port.DiscardInBuffer(); } catch { }

                _log($"Opened {_port.PortName} @ {_port.BaudRate}");
            }

            public Task CloseAsync()
            {
                try
                {
                    if (_port.IsOpen) _port.Close();
                }
                catch { }
                return Task.CompletedTask;
            }

            public async Task SoftResetAsync()
            {
                // Ctrl+X
                if (!_port.IsOpen) return;
                _port.Write(new char[] { (char)0x18 }, 0, 1);
                await Task.Delay(200);
                _log("Sent Ctrl+X (soft reset).");
            }

            public async Task SendLineWaitOkAsync(string line, TimeSpan timeout, CancellationToken ct = default)
            {
                _ = await SendAndCollectAsync(line, timeout, ct);
            }

            public async Task<List<string>> SendAndCollectAsync(string line, TimeSpan timeout, CancellationToken ct = default)
            {
                line = line.Trim();
                if (line.Length == 0) return new List<string>();

                var collected = new List<string>();

                await _sendLock.WaitAsync(ct);
                try
                {
                    _log($"> {line}");
                    _port.Write(line + "\n");

                    var deadline = DateTime.UtcNow + timeout;

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (DateTime.UtcNow > deadline)
                            throw new TimeoutException($"Timeout waiting for ok after: {line}");

                        var resp = await ReadLineAsync(ct);
                        if (resp == null) continue;

                        resp = resp.Trim();
                        if (resp.Length == 0) continue;

                        _log($"< {resp}");

                        if (resp.Equals("ok", StringComparison.OrdinalIgnoreCase))
                            return collected;

                        if (resp.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
                            resp.StartsWith("alarm", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("GRBL: " + resp);

                        collected.Add(resp);
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            private async Task<string?> ReadLineAsync(CancellationToken ct)
            {
                // Simple polling read from the queued lines built by DataReceived.
                for (int i = 0; i < 500; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    lock (_rxLock)
                    {
                        if (_lines.Count > 0)
                            return _lines.Dequeue();
                    }

                    await Task.Delay(10, ct);
                }
                return null;
            }

            private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
            {
                try
                {
                    var s = _port.ReadExisting();
                    if (string.IsNullOrEmpty(s)) return;

                    lock (_rxLock)
                    {
                        _rx.Append(s);

                        while (true)
                        {
                            var str = _rx.ToString();
                            var idx = str.IndexOf('\n');
                            if (idx < 0) break;

                            var line = str.Substring(0, idx).Trim('\r');
                            _lines.Enqueue(line);

                            _rx.Clear();
                            _rx.Append(str.Substring(idx + 1));
                        }
                    }
                }
                catch
                {
                    // ignore read errors during close/reset
                }
            }

            public void Dispose()
            {
                try { _port.DataReceived -= Port_DataReceived; } catch { }
                try { if (_port.IsOpen) _port.Close(); } catch { }
                _port.Dispose();
                _sendLock.Dispose();
            }
        }
    }
}
