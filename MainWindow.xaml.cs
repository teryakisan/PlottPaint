using Microsoft.Win32;
using NVSPlotter.Models;
using NVSPlotter.Properties;
using NVSPlotter.Services;
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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace NVSPlotter
{
    public partial class MainWindow : Window
    {
        // --- Document is stored in mm; canvas is "px" where 1 px = 1 mm before zoom ---
        private PlotDocument _doc = new PlotDocument(841, 1189);

        private readonly ReferenceImageService _imageService = new();
        private readonly WorkingAreaManager _workingAreaManager = new();

        // Canvas transforms
        private readonly ScaleTransform _zoom = new ScaleTransform(1.0, 1.0);
        private readonly RotateTransform _canvasRotation = new RotateTransform(0);

        // Working area overlay visuals
        private Rectangle? _workingAreaPreviewRect;
        private Line? _workingAreaCrosshairHorizontal;
        private Line? _workingAreaCrosshairVertical;

        // Reference image manipulation state
        private ImageHandle _activeImageHandle = ImageHandle.None;
        private PointMm _imageDragStart;
        private Rect _imageRectStart;
        private const double MIN_IMAGE_SIZE = 20.0;
        private double _imageRotateStartAngle;
        private double _imageRotateStartVectorAngle;
        private PointMm _imageRotateCenter;
        private bool _suppressRotateSlider;
        private bool _suppressImageFilterChange;
        private const double ROTATE_HANDLE_OFFSET = 40.0;
        private const double ROTATE_HANDLE_SIZE = 16.0;
        private const int CIRCLE_SEGMENTS = 64;
        private const double MEASUREMENT_MARKER_RADIUS = 4.0;

        // Drawing state
        private bool _isDrawing;
        private PointMm _startMm;
        private Line? _previewLine;
        private Shape? _previewShape;
        private ToolMode _activeDrawingTool = ToolMode.Line;
        private bool _isPolylineActive;
        private PointMm _polylineLastPoint;
        private bool _isMeasuring;
        private bool _hasMeasurement;
        private PointMm _measurementStart;
        private PointMm _measurementEnd;
        private Line? _measurementLine;
        private Ellipse? _measurementStartMarker;
        private Ellipse? _measurementEndMarker;

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
        private double _bedX = Settings.Default.bedX;   // $130
        private double _bedY = Settings.Default.bedY;  // $131
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
            UpdateReferenceUiState();
            RefreshPorts();

            RenderAll();
            UpdateConnStatus();
        }

        private void UpdateReferenceUiState()
        {
            var hasImage = _imageService.HasImage;

            if (FindName("ImageLockCheck") is CheckBox lockCheck)
            {
                lockCheck.IsEnabled = hasImage;
                lockCheck.IsChecked = hasImage && _imageService.IsLocked;
            }

            if (FindName("ImageRotateSlider") is Slider rotateSlider)
            {
                _suppressRotateSlider = true;
                rotateSlider.Value = hasImage ? _imageService.Angle : 0.0;
                rotateSlider.IsEnabled = hasImage && !_imageService.IsLocked;
                _suppressRotateSlider = false;
            }

            if (FindName("ImageRotateValue") is TextBlock rotateLabel)
            {
                rotateLabel.Text = hasImage ? $"{_imageService.Angle:0.#}°" : "—";
            }

            if (FindName("ImageRotateResetBtn") is Button resetBtn)
            {
                resetBtn.IsEnabled = hasImage && !_imageService.IsLocked && Math.Abs(_imageService.Angle) > 0.0001;
            }

            if (FindName("ImageFilterCombo") is ComboBox filterCombo)
            {
                filterCombo.IsEnabled = hasImage;
                _suppressImageFilterChange = true;
                filterCombo.SelectedValue = _imageService.CurrentFilter.ToString();
                _suppressImageFilterChange = false;
            }
        }

        private static double NormalizeAngle(double angle)
        {
            var normalized = angle % 360.0;
            if (normalized <= -180.0) normalized += 360.0;
            if (normalized > 180.0) normalized -= 360.0;
            return normalized;
        }

        private static Vector RotateVector(Vector v, double angleDegrees)
        {
            var radians = angleDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        private static void ApplyImageRotation(UIElement element, double angleDegrees)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new RotateTransform(angleDegrees);
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
            _workingAreaManager.BeginDefinition();
            _workingAreaManager.CancelDrag();
            RemoveWorkingAreaPreview();
            UpdateWorkingAreaStatus();

            if (DrawCanvas != null)
            {
                DrawCanvas.Cursor = Cursors.Cross;
            }

            var pos = GetCurrentCanvasPointerOrCenter();
            UpdateWorkingAreaCrosshair(pos);
        }

        private void ImportImageBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                if (_imageService.TryLoadFromFile(dlg.FileName, _doc.WidthMm, _doc.HeightMm, out var error))
                {
                    SetImageLocked(false);
                    UpdateReferenceUiState();
                    RenderAll();
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    AppendLog("Image load failed: " + error);
                }
            }
        }

        private void ClearImageBtn_Click(object sender, RoutedEventArgs e)
        {
            _imageService.Clear();
            SetImageLocked(false);
            EndImageManipulation();
            UpdateReferenceUiState();
            RenderAll();
        }

        private void ImageLockCheck_Click(object sender, RoutedEventArgs e)
        {
            var locked = (sender as CheckBox)?.IsChecked == true;
            SetImageLocked(locked, syncCheckbox: false);
            RenderAll();
        }

        private void ImageRotateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressRotateSlider) return;
            if (!_imageService.HasImage) return;
            if (_imageService.IsLocked) return;

            _imageService.Angle = e.NewValue;
            UpdateReferenceUiState();
            RenderAll();
        }

        private void ImageRotateResetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_imageService.HasImage) return;
            _imageService.Angle = 0;
            UpdateReferenceUiState();
            RenderAll();
        }

        private void ImageFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressImageFilterChange) return;
            if (_imageService.OriginalImage == null) return;

            var filter = ParseImageFilter((sender as ComboBox)?.SelectedValue);
            if (filter == _imageService.CurrentFilter) return;

            _imageService.SetFilter(filter);
            UpdateReferenceUiState();
            RenderAll();
        }

        private static ImageFilter ParseImageFilter(object? value)
        {
            if (value is string tag && Enum.TryParse(tag, true, out ImageFilter filter))
            {
                return filter;
            }
            return ImageFilter.None;
        }

        private void SetImageLocked(bool locked, bool syncCheckbox = true)
        {
            _imageService.SetLocked(locked);
            if (syncCheckbox && FindName("ImageLockCheck") is CheckBox cb)
            {
                cb.IsChecked = locked;
            }

            if (locked)
            {
                EndImageManipulation();
            }

            UpdateReferenceUiState();
        }

        private void ClearWorkingAreaBtn_Click(object sender, RoutedEventArgs e)
        {
            _workingAreaManager.Clear();
            RemoveWorkingAreaPreview();
            RemoveWorkingAreaCrosshair();
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
            PerformUndo();
        }

        private void PerformUndo()
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
            if (_workingAreaManager.IsDragging)
            {
                CancelWorkingAreaDrag();
            }
            if (_activeImageHandle != ImageHandle.None)
            {
                EndImageManipulation();
            }
            if (_isDrawing)
            {
                CancelPreview();
            }
            if (_isMeasuring)
            {
                ResetMeasurement();
            }
        }

        private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_activeImageHandle != ImageHandle.None)
            {
                UpdateImageManipulation(e);
                return;
            }

            if (_workingAreaManager.IsDragging)
            {
                UpdateWorkingAreaDrag(e);
                return;
            }

            if (_workingAreaManager.IsDefining)
            {
                var hover = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                UpdateWorkingAreaCrosshair(hover);
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

            if (_isMeasuring)
            {
                var measurePoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                UpdateMeasurement(measurePoint);
                e.Handled = true;
                return;
            }
 
            if (!_isDrawing)
            {
                if (_isPolylineActive)
                {
                    var polyPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                    UpdatePolylinePreview(polyPoint);
                }
                return;
            }

            var mm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));

            switch (_activeDrawingTool)
            {
                case ToolMode.Rectangle:
                    UpdateRectanglePreview(mm);
                    break;
                case ToolMode.Circle:
                    UpdateCirclePreview(mm);
                    break;
                default:
                    if (_previewLine != null)
                    {
                        _previewLine.X2 = mm.X;
                        _previewLine.Y2 = mm.Y;
                    }
                    break;
            }
        }

        private void DrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_workingAreaManager.IsDefining)
            {
                BeginWorkingAreaDrag(e);
                return;
            }

            var tool = GetCurrentTool();

            if (tool == ToolMode.Polyline)
            {
                HandlePolylineClick(e);
                return;
            }

            if (tool == ToolMode.Measure)
            {
                var start = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                BeginMeasurement(start);
                DrawCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Pan || _isPanning) return; // Pan tool

            _activeDrawingTool = tool;
            _isDrawing = true;
            _startMm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));

            switch (tool)
            {
                case ToolMode.Rectangle:
                    BeginRectanglePreview(_startMm);
                    break;
                case ToolMode.Circle:
                    BeginCirclePreview(_startMm);
                    break;
                default:
                    BeginLinePreview(_startMm);
                    break;
            }

            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_workingAreaManager.IsDragging)
            {
                CompleteWorkingAreaDrag(e);
                return;
            }

            if (_isMeasuring)
            {
                var measureEnd = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                CompleteMeasurement(measureEnd);
                e.Handled = true;
                return;
            }

            if (!_isDrawing) return;

            var endMm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));

            switch (_activeDrawingTool)
            {
                case ToolMode.Rectangle:
                    FinalizeRectangle(_startMm, endMm);
                    break;
                case ToolMode.Circle:
                    FinalizeEllipse(_startMm, endMm);
                    break;
                default:
                    AddStroke(_startMm, endMm);
                    break;
            }

            CancelPreview();

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
            if (_previewShape != null)
            {
                DrawCanvas.Children.Remove(_previewShape);
                _previewShape = null;
            }
             DrawCanvas.ReleaseMouseCapture();
         }

        private void AddStroke(PointMm start, PointMm end)
        {
            if (Distance(start, end) < 0.25) return;
            _doc.Strokes.Add(new LineStroke(start, end));
            _lastGcode = "";
        }
        
        private void BeginMeasurement(PointMm start)
        {
            _isMeasuring = true;
            _hasMeasurement = false;
            _measurementStart = start;
            _measurementEnd = start;

            ClearMeasurementVisuals();

            _measurementLine = new Line
            {
                Stroke = Brushes.MediumPurple,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                SnapsToDevicePixels = true
            };
            DrawCanvas.Children.Add(_measurementLine);
            Panel.SetZIndex(_measurementLine, 18);

            _measurementStartMarker = CreateMeasurementMarker();
            _measurementEndMarker = CreateMeasurementMarker();
            DrawCanvas.Children.Add(_measurementStartMarker);
            DrawCanvas.Children.Add(_measurementEndMarker);
            Panel.SetZIndex(_measurementStartMarker, 19);
            Panel.SetZIndex(_measurementEndMarker, 19);

            UpdateMeasurement(start);
        }

        private void UpdateMeasurement(PointMm current)
        {
            _measurementEnd = current;

            if (_measurementLine != null)
            {
                _measurementLine.X1 = _measurementStart.X;
                _measurementLine.Y1 = _measurementStart.Y;
                _measurementLine.X2 = _measurementEnd.X;
                _measurementLine.Y2 = _measurementEnd.Y;
            }

            SetMarkerPosition(_measurementStartMarker, _measurementStart);
            SetMarkerPosition(_measurementEndMarker, _measurementEnd);

            UpdateMeasurementStatus(_measurementStart, _measurementEnd);
        }

        private void CompleteMeasurement(PointMm end)
        {
            _measurementEnd = end;
            var dist = Distance(_measurementStart, _measurementEnd);
            if (dist < 0.25)
            {
                ResetMeasurement();
                return;
            }

            UpdateMeasurement(_measurementEnd);
            _isMeasuring = false;
            _hasMeasurement = true;
            if (DrawCanvas.IsMouseCaptured)
            {
                DrawCanvas.ReleaseMouseCapture();
            }
        }

        private void ResetMeasurement()
        {
            _isMeasuring = false;
            _hasMeasurement = false;
            ClearMeasurementVisuals();
            UpdateMeasurementStatusText("—");
            if (DrawCanvas.IsMouseCaptured)
            {
                DrawCanvas.ReleaseMouseCapture();
            }
        }

        private void ClearMeasurementVisuals()
        {
            if (_measurementLine != null)
            {
                DrawCanvas.Children.Remove(_measurementLine);
                _measurementLine = null;
            }
            if (_measurementStartMarker != null)
            {
                DrawCanvas.Children.Remove(_measurementStartMarker);
                _measurementStartMarker = null;
            }
            if (_measurementEndMarker != null)
            {
                DrawCanvas.Children.Remove(_measurementEndMarker);
                _measurementEndMarker = null;
            }
        }

        private static Ellipse CreateMeasurementMarker()
        {
            return new Ellipse
            {
                Width = MEASUREMENT_MARKER_RADIUS * 2,
                Height = MEASUREMENT_MARKER_RADIUS * 2,
                Stroke = Brushes.MediumPurple,
                StrokeThickness = 1,
                Fill = Brushes.White,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
        }

        private static void SetMarkerPosition(Ellipse? marker, PointMm position)
        {
            if (marker == null) return;
            Canvas.SetLeft(marker, position.X - MEASUREMENT_MARKER_RADIUS);
            Canvas.SetTop(marker, position.Y - MEASUREMENT_MARKER_RADIUS);
        }

        private void UpdateMeasurementStatus(PointMm start, PointMm end)
        {
            var dist = Distance(start, end);
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180.0 / Math.PI;
            if (angle < 0) angle += 360.0;
            UpdateMeasurementStatusText($"{dist:0.##} mm @ {angle:0.#}°");
        }

        private void UpdateMeasurementStatusText(string text)
        {
            if (MeasurementStatus != null)
            {
                MeasurementStatus.Text = text;
            }
        }

        private ToolMode GetCurrentTool()
        {
            var value = ToolCombo?.SelectedValue;
            if (value is ToolMode mode)
            {
                return mode;
            }

            if (value is string tag && Enum.TryParse(tag, true, out ToolMode parsed))
            {
                return parsed;
            }

            return ToolMode.Line;
        }

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

        private PointMm GetCurrentCanvasPointerOrCenter()
        {
            if (CanvasScroll != null && DrawCanvas != null)
            {
                try
                {
                    var viewportPoint = Mouse.GetPosition(CanvasScroll);
                    return ClampToPage(MouseToMm(viewportPoint));
                }
                catch
                {
                    // fall back to center if mouse position unavailable
                }
            }
            return new PointMm(_doc.WidthMm / 2.0, _doc.HeightMm / 2.0);
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
            Panel.SetZIndex(pageRect, 0);

            DrawRulers();
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
                Panel.SetZIndex(ln, 4);
                DrawCanvas.Children.Add(ln);
            }

            AppendLog($"Doc: {_doc.WidthMm:0} x {_doc.HeightMm:0} mm, strokes={_doc.Strokes.Count}");
            AppendLog($"Bed: X={_bedX:0.###} Y={_bedY:0.###} {(_bedFromGrbl ? "(from $$)" : "(default)")}, margin={SAFE_MARGIN_MM:0.###}mm");
            AppendLog($"$23={_homingDirMask} => HomeAtMax: X={_homeAtMaxX}, Y={_homeAtMaxY}, homed={_isHomed}");
            AppendLog("Work convention: X ALWAYS positive, Y ALWAYS negative.");

            UpdateZoomHost();
        }

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
            DrawCanvas.Children.Add(image);
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
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            DrawCanvas.Children.Add(outline);
            Canvas.SetLeft(outline, rect.Left);
            Canvas.SetTop(outline, rect.Top);
            ApplyImageRotation(outline, _imageService.Angle);
            Panel.SetZIndex(outline, 6);

            if (_imageService.IsLocked)
            {
                return;
            }

            var hitBox = new Rectangle
            {
                Width = rect.Width,
                Height = rect.Height,
                Fill = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Tag = ImageHandle.Move
            };
            hitBox.MouseDown += ReferenceHandle_MouseDown;
            DrawCanvas.Children.Add(hitBox);
            Canvas.SetLeft(hitBox, rect.Left);
            Canvas.SetTop(hitBox, rect.Top);
            Panel.SetZIndex(hitBox, 5);

            void AddHandle(ImageHandle handle, double cx, double cy, Cursor cursor)
            {
                const double size = 10;
                var handleRect = new Rectangle
                {
                    Width = size,
                    Height = size,
                    Fill = Brushes.White,
                    Stroke = Brushes.DodgerBlue,
                    StrokeThickness = 1,
                    Cursor = cursor,
                    Tag = handle
                };
                handleRect.MouseDown += ReferenceHandle_MouseDown;
                DrawCanvas.Children.Add(handleRect);
                Canvas.SetLeft(handleRect, cx - size / 2.0);
                Canvas.SetTop(handleRect, cy - size / 2.0);
                Panel.SetZIndex(handleRect, 7);
            }

            AddHandle(ImageHandle.Nw, rect.Left, rect.Top, Cursors.SizeNWSE);
            AddHandle(ImageHandle.Ne, rect.Right, rect.Top, Cursors.SizeNESW);
            AddHandle(ImageHandle.Se, rect.Right, rect.Bottom, Cursors.SizeNWSE);
            AddHandle(ImageHandle.Sw, rect.Left, rect.Bottom, Cursors.SizeNESW);

            var center = new Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);
            var rotatedVector = RotateVector(new Vector(0, -ROTATE_HANDLE_OFFSET), _imageService.Angle);
            var handleCenter = new Point(center.X + rotatedVector.X, center.Y + rotatedVector.Y);

            var connector = new Line
            {
                X1 = center.X,
                Y1 = center.Y,
                X2 = handleCenter.X,
                Y2 = handleCenter.Y,
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false
            };
            DrawCanvas.Children.Add(connector);
            Panel.SetZIndex(connector, 6);

            var rotateHandle = new Ellipse
            {
                Width = ROTATE_HANDLE_SIZE,
                Height = ROTATE_HANDLE_SIZE,
                Fill = Brushes.White,
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 1.2,
                Cursor = Cursors.Hand,
                Tag = ImageHandle.Rotate
            };

            rotateHandle.MouseDown += ReferenceHandle_MouseDown;
            DrawCanvas.Children.Add(rotateHandle);
            Canvas.SetLeft(rotateHandle, handleCenter.X - ROTATE_HANDLE_SIZE / 2.0);
            Canvas.SetTop(rotateHandle, handleCenter.Y - ROTATE_HANDLE_SIZE / 2.0);
            Panel.SetZIndex(rotateHandle, 8);
        }

        private void ReferenceHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_imageService.ImageRect is null || _imageService.IsLocked) return;
            if (sender is not FrameworkElement fe || fe.Tag is not ImageHandle handle) return;

            _activeImageHandle = handle;
            _imageRectStart = _imageService.ImageRect.Value;

            if (handle == ImageHandle.Rotate)
            {
                _imageRotateCenter = new PointMm(
                    _imageRectStart.Left + _imageRectStart.Width / 2.0,
                    _imageRectStart.Top + _imageRectStart.Height / 2.0);

                var start = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _imageRotateStartVectorAngle = Math.Atan2(start.Y - _imageRotateCenter.Y, start.X - _imageRotateCenter.X) * 180.0 / Math.PI;
                _imageRotateStartAngle = _imageService.Angle;
            }
            else
            {
                _imageDragStart = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            }

            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void UpdateImageManipulation(MouseEventArgs e)
        {
            if (_imageService.IsLocked) return;
            if (_activeImageHandle == ImageHandle.None || _imageService.ImageRect is null) return;

            if (_activeImageHandle == ImageHandle.Rotate)
            {
                var rotatePoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                var currentAngle = Math.Atan2(rotatePoint.Y - _imageRotateCenter.Y, rotatePoint.X - _imageRotateCenter.X) * 180.0 / Math.PI;
                var delta = currentAngle - _imageRotateStartVectorAngle;
                _imageService.Angle = NormalizeAngle(_imageRotateStartAngle + delta);
                UpdateReferenceUiState();
                RenderAll();
                return;
            }

            var current = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            var dx = current.X - _imageDragStart.X;
            var dy = current.Y - _imageDragStart.Y;

            Rect rect;
            if (_activeImageHandle == ImageHandle.Move)
            {
                var left = Math.Clamp(_imageRectStart.Left + dx, 0, _doc.WidthMm - _imageRectStart.Width);
                var top = Math.Clamp(_imageRectStart.Top + dy, 0, _doc.HeightMm - _imageRectStart.Height);
                rect = new Rect(left, top, _imageRectStart.Width, _imageRectStart.Height);
            }
            else
            {
                rect = ResizeReferenceRect(_activeImageHandle, _imageRectStart, dx, dy);
            }

            _imageService.ImageRect = rect;
            RenderAll();
        }

        private Rect ResizeReferenceRect(ImageHandle handle, Rect start, double dx, double dy)
        {
            double left = start.Left;
            double right = start.Right;
            double top = start.Top;
            double bottom = start.Bottom;

            switch (handle)
            {
                case ImageHandle.Nw:
                    left = Math.Clamp(left + dx, 0, right - MIN_IMAGE_SIZE);
                    top = Math.Clamp(top + dy, 0, bottom - MIN_IMAGE_SIZE);
                    break;
                case ImageHandle.Ne:
                    right = Math.Clamp(right + dx, left + MIN_IMAGE_SIZE, _doc.WidthMm);
                    top = Math.Clamp(top + dy, 0, bottom - MIN_IMAGE_SIZE);
                    break;
                case ImageHandle.Se:
                    right = Math.Clamp(right + dx, left + MIN_IMAGE_SIZE, _doc.WidthMm);
                    bottom = Math.Clamp(bottom + dy, top + MIN_IMAGE_SIZE, _doc.HeightMm);
                    break;
                case ImageHandle.Sw:
                    left = Math.Clamp(left + dx, 0, right - MIN_IMAGE_SIZE);
                    bottom = Math.Clamp(bottom + dy, top + MIN_IMAGE_SIZE, _doc.HeightMm);
                    break;
            }

            var width = Math.Max(MIN_IMAGE_SIZE, right - left);
            var height = Math.Max(MIN_IMAGE_SIZE, bottom - top);
            left = Math.Clamp(left, 0, _doc.WidthMm - width);
            top = Math.Clamp(top, 0, _doc.HeightMm - height);

            return new Rect(left, top, width, height);
        }

        private void EndImageManipulation()
        {
            if (_activeImageHandle == ImageHandle.None) return;
            _activeImageHandle = ImageHandle.None;
            if (DrawCanvas.IsMouseCaptured)
                DrawCanvas.ReleaseMouseCapture();
        }

        private void BeginWorkingAreaDrag(MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var start = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _workingAreaManager.BeginDrag(start);

            if (_workingAreaPreviewRect == null)
            {
                _workingAreaPreviewRect = CreateWorkingAreaVisual();
                DrawCanvas.Children.Add(_workingAreaPreviewRect);
            }

            if (_workingAreaManager.PreviewArea is Rect preview)
            {
                UpdateWorkingAreaPreview(preview);
            }

            UpdateWorkingAreaCrosshair(start);
            DrawCanvas.Cursor = Cursors.Cross;
            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void UpdateWorkingAreaDrag(MouseEventArgs e)
        {
            if (!_workingAreaManager.IsDragging) return;

            var current = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            var rect = _workingAreaManager.UpdateDrag(current);
            UpdateWorkingAreaPreview(rect);
            UpdateWorkingAreaCrosshair(current);
            e.Handled = true;
        }

        private void CompleteWorkingAreaDrag(MouseButtonEventArgs e)
        {
            if (!_workingAreaManager.IsDragging) return;

            var end = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _workingAreaManager.CompleteDrag(end);
            DrawCanvas.ReleaseMouseCapture();
            DrawCanvas.ClearValue(CursorProperty);

            RemoveWorkingAreaPreview();
            RemoveWorkingAreaCrosshair();
            UpdateWorkingAreaStatus();
            RenderAll();
            e.Handled = true;
        }

        private void CancelWorkingAreaDrag()
        {
            if (!_workingAreaManager.IsDragging) return;

            _workingAreaManager.CancelDrag();
            DrawCanvas.ReleaseMouseCapture();
            DrawCanvas.ClearValue(CursorProperty);
            RemoveWorkingAreaPreview();
            RemoveWorkingAreaCrosshair();
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

        private void UpdateWorkingAreaPreview(Rect rect)
        {
            if (_workingAreaPreviewRect == null) return;
            _workingAreaPreviewRect.Width = rect.Width;
            _workingAreaPreviewRect.Height = rect.Height;
            Canvas.SetLeft(_workingAreaPreviewRect, rect.Left);
            Canvas.SetTop(_workingAreaPreviewRect, rect.Top);
        }

        private void UpdateWorkingAreaCrosshair(PointMm position)
        {
            if (DrawCanvas == null) return;

            Line EnsureLine(ref Line? line)
            {
                if (line == null)
                {
                    line = new Line
                    {
                        Stroke = Brushes.DodgerBlue,
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 4, 4 },
                        SnapsToDevicePixels = true,
                        IsHitTestVisible = false
                    };
                    RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                    DrawCanvas.Children.Add(line);
                    Panel.SetZIndex(line, 9);
                }
                return line;
            }

            var horizontal = EnsureLine(ref _workingAreaCrosshairHorizontal);
            horizontal.X1 = 0;
            horizontal.X2 = _doc.WidthMm;
            horizontal.Y1 = position.Y;
            horizontal.Y2 = position.Y;

            var vertical = EnsureLine(ref _workingAreaCrosshairVertical);
            vertical.X1 = position.X;
            vertical.X2 = position.X;
            vertical.Y1 = 0;
            vertical.Y2 = _doc.HeightMm;
        }

        private void RemoveWorkingAreaCrosshair()
        {
            if (DrawCanvas != null)
            {
                if (_workingAreaCrosshairHorizontal != null)
                {
                    DrawCanvas.Children.Remove(_workingAreaCrosshairHorizontal);
                }
                if (_workingAreaCrosshairVertical != null)
                {
                    DrawCanvas.Children.Remove(_workingAreaCrosshairVertical);
                }
            }
            _workingAreaCrosshairHorizontal = null;
            _workingAreaCrosshairVertical = null;
            if (DrawCanvas != null && !_workingAreaManager.IsDragging && !_workingAreaManager.IsDefining)
            {
                DrawCanvas.ClearValue(CursorProperty);
            }
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
            WorkingAreaStatus.Text = _workingAreaManager.GetStatusText();
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

        private enum ImageHandle
        {
            None,
            Move,
            Nw,
            Ne,
            Se,
            Sw,
            Rotate
        }

        private enum ToolMode
        {
            Line,
            Pan,
            Rectangle,
            Circle,
            Polyline,
            Measure,
            Guides,
            Text,
            Select,
            Crop,
            ImageAlign
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

        private void BeginLinePreview(PointMm start)
        {
            _previewLine = new Line
            {
                X1 = start.X,
                Y1 = start.Y,
                X2 = start.X,
                Y2 = start.Y,
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                SnapsToDevicePixels = true
            };

            DrawCanvas.Children.Add(_previewLine);
            Panel.SetZIndex(_previewLine, 15);
        }

        private void BeginRectanglePreview(PointMm start)
        {
            var rect = new Rectangle
            {
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                SnapsToDevicePixels = true
            };

            _previewShape = rect;
            DrawCanvas.Children.Add(rect);
            Panel.SetZIndex(rect, 15);
            Canvas.SetLeft(rect, start.X);
            Canvas.SetTop(rect, start.Y);
            rect.Width = 0;
            rect.Height = 0;
        }

        private void BeginCirclePreview(PointMm start)
        {
            var ellipse = new Ellipse
            {
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = Brushes.Transparent,
                SnapsToDevicePixels = true
            };

            _previewShape = ellipse;
            DrawCanvas.Children.Add(ellipse);
            Panel.SetZIndex(ellipse, 15);
            Canvas.SetLeft(ellipse, start.X);
            Canvas.SetTop(ellipse, start.Y);
            ellipse.Width = 0;
            ellipse.Height = 0;
        }

        private void UpdateRectanglePreview(PointMm current)
        {
            if (_previewShape is not Rectangle rect) return;

            var left = Math.Min(_startMm.X, current.X);
            var top = Math.Min(_startMm.Y, current.Y);
            var width = Math.Abs(current.X - _startMm.X);
            var height = Math.Abs(current.Y - _startMm.Y);

            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            rect.Width = width;
            rect.Height = height;
        }

        private void UpdateCirclePreview(PointMm current)
        {
            if (_previewShape is not Ellipse ellipse) return;

            var left = Math.Min(_startMm.X, current.X);
            var top = Math.Min(_startMm.Y, current.Y);
            var width = Math.Abs(current.X - _startMm.X);
            var height = Math.Abs(current.Y - _startMm.Y);

            Canvas.SetLeft(ellipse, left);
            Canvas.SetTop(ellipse, top);
            ellipse.Width = width;
            ellipse.Height = height;
        }

        private void FinalizeRectangle(PointMm start, PointMm end)
        {
            var left = Math.Min(start.X, end.X);
            var right = Math.Max(start.X, end.X);
            var top = Math.Min(start.Y, end.Y);
            var bottom = Math.Max(start.Y, end.Y);

            if ((right - left) < 0.25 || (bottom - top) < 0.25) return;

            var topLeft = new PointMm(left, top);
            var topRight = new PointMm(right, top);
            var bottomRight = new PointMm(right, bottom);
            var bottomLeft = new PointMm(left, bottom);

            AddStroke(topLeft, topRight);
            AddStroke(topRight, bottomRight);
            AddStroke(bottomRight, bottomLeft);
            AddStroke(bottomLeft, topLeft);
        }

        private void FinalizeEllipse(PointMm start, PointMm end)
        {
            var left = Math.Min(start.X, end.X);
            var right = Math.Max(start.X, end.X);
            var top = Math.Min(start.Y, end.Y);
            var bottom = Math.Max(start.Y, end.Y);

            if ((right - left) < 0.25 || (bottom - top) < 0.25) return;

            var centerX = (left + right) / 2.0;
            var centerY = (top + bottom) / 2.0;
            var radiusX = Math.Max((right - left) / 2.0, 0.1);
            var radiusY = Math.Max((bottom - top) / 2.0, 0.1);

            PointMm? firstPoint = null;
            PointMm? prevPoint = null;

            for (int i = 0; i < CIRCLE_SEGMENTS; i++)
            {
                var angle = 2.0 * Math.PI * i / CIRCLE_SEGMENTS;
                var x = centerX + radiusX * Math.Cos(angle);
                var y = centerY + radiusY * Math.Sin(angle);
                var point = new PointMm(x, y);

                if (prevPoint is PointMm prev)
                {
                    AddStroke(prev, point);
                }
                else
                {
                    firstPoint = point;
                }

                prevPoint = point;
            }

            if (prevPoint is PointMm last && firstPoint is PointMm first)
            {
                AddStroke(last, first);
            }
        }

        private void HandlePolylineClick(MouseButtonEventArgs e)
        {
            var point = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            var isDoubleClick = e.ClickCount >= 2;

            if (!_isPolylineActive)
            {
                _isPolylineActive = true;
                _activeDrawingTool = ToolMode.Polyline;
                _polylineLastPoint = point;
                BeginLinePreview(point);
                DrawCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (Distance(_polylineLastPoint, point) >= 0.25)
            {
                CancelPreview();
                AddStroke(_polylineLastPoint, point);
                _polylineLastPoint = point;
                RenderAll();
            }
            else
            {
                _polylineLastPoint = point;
            }

            if (isDoubleClick)
            {
                FinishPolyline();
            }
            else
            {
                BeginLinePreview(_polylineLastPoint);
                DrawCanvas.CaptureMouse();
            }

            e.Handled = true;
        }

        private void UpdatePolylinePreview(PointMm current)
        {
            if (!_isPolylineActive) return;

            if (_previewLine == null)
            {
                BeginLinePreview(_polylineLastPoint);
            }

            _previewLine!.X1 = _polylineLastPoint.X;
            _previewLine.Y1 = _polylineLastPoint.Y;
            _previewLine.X2 = current.X;
            _previewLine.Y2 = current.Y;
        }

        private void FinishPolyline()
        {
            if (!_isPolylineActive) return;

            _isPolylineActive = false;
            _activeDrawingTool = ToolMode.Line;
            CancelPreview();
        }

        private bool TryFinishPolyline()
        {
            if (!_isPolylineActive)
            {
                return false;
            }

            FinishPolyline();
            return true;
        }

        private void ToolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tool = GetCurrentTool();

            if (tool != ToolMode.Measure && (_isMeasuring || _hasMeasurement))
            {
                ResetMeasurement();
            }

            if (tool != ToolMode.Polyline)
            {
                FinishPolyline();
            }

            if (_isDrawing && tool != _activeDrawingTool)
            {
                CancelPreview();
            }
        }

        private void DrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isMeasuring || (GetCurrentTool() == ToolMode.Measure && _hasMeasurement))
            {
                ResetMeasurement();
                e.Handled = true;
                return;
            }

             if (TryFinishPolyline())
             {
                 e.Handled = true;
             }
         }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isMeasuring && e.Key == Key.Escape)
            {
                ResetMeasurement();
                e.Handled = true;
                return;
            }

            if (_isPolylineActive && (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Return))
            {
                FinishPolyline();
                e.Handled = true;
                return;
            }
 
             if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Z)
             {
                 PerformUndo();
                 e.Handled = true;
             }
         }
    }
 }
