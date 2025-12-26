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
        private PlotDocument _doc = new PlotDocument(Settings.Default.bedX, Settings.Default.bedY);

        private readonly ReferenceImageService _imageService = new();
        private readonly WorkingAreaManager _workingAreaManager = new();
        private readonly ReferenceImageManipulator _imageManipulator;

        // Canvas transforms
        private readonly ScaleTransform _zoom = new ScaleTransform(1.0, 1.0);
        private readonly RotateTransform _canvasRotation = new RotateTransform(0);

        // Working area overlay visuals
        private Rectangle? _workingAreaPreviewRect;
        private Line? _workingAreaCrosshairHorizontal;
        private Line? _workingAreaCrosshairVertical;

        // Reference image manipulation state
        private bool _suppressRotateSlider;
        private bool _suppressImageFilterChange;
        private const double ROTATE_HANDLE_OFFSET = 40.0;
        private const double ROTATE_HANDLE_SIZE = 16.0;
        private const int CIRCLE_SEGMENTS = 64;

        // Drawing state
        private readonly MeasurementOverlay _measurementOverlay;
        private readonly ShapeDrawingController _shapeController;

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

            _measurementOverlay = new MeasurementOverlay(
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                MeasurementStatus);

            _imageManipulator = new ReferenceImageManipulator(
                _imageService,
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                CanvasScroll ?? throw new InvalidOperationException("CanvasScroll control is missing."),
                () => _doc,
                RenderAll,
                UpdateReferenceUiState);

            _shapeController = new ShapeDrawingController(
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                stroke => { _doc.Strokes.Add(stroke); _lastGcode = ""; },
                RenderAll);

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
            _imageManipulator.End();
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
                _imageManipulator.End();
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

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("Export G-code not implemented yet.");
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("Copy G-code not implemented yet.");
        }

        private void RefreshPortsBtn_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
            AppendLog("Ports refreshed.");
        }

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_grbl?.IsOpen == true)
            {
                await DisconnectAsync();
                UpdateConnStatus();
                return;
            }

            var port = GetSelectedPort();
            if (string.IsNullOrWhiteSpace(port))
            {
                AppendLog("Select a serial port first.");
                return;
            }

            var baud = GetSelectedBaud();

            try
            {
                _grbl?.Dispose();
                _grbl = new GrblConnection(port, baud, AppendLog);

                UpdateConnStatus();
                await _grbl.OpenAsync();
                _sendCts = new CancellationTokenSource();

                AppendLog("Connected to GRBL.");
                await LoadGrblSettingsAsync();
            }
            catch (Exception ex)
            {
                AppendLog("Connect failed: " + ex.Message);
                await DisconnectAsync();
            }
            finally
            {
                UpdateConnStatus();
            }
        }

        private string? GetSelectedPort()
        {
            var port = PortCombo?.SelectedItem as string ?? PortCombo?.SelectedValue as string;
            return string.IsNullOrWhiteSpace(port) ? null : port;
        }

        private int GetSelectedBaud()
        {
            var value = (BaudCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString()
                        ?? BaudCombo?.SelectedValue?.ToString();

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud))
            {
                return baud;
            }

            return 115200;
        }

        private async Task DisconnectAsync()
        {
            _sendCts?.Cancel();
            _sendCts?.Dispose();
            _sendCts = null;

            if (_grbl != null)
            {
                try
                {
                    await _grbl.CloseAsync();
                }
                catch (Exception ex)
                {
                    AppendLog("Error while closing port: " + ex.Message);
                }
                finally
                {
                    _grbl.Dispose();
                    _grbl = null;
                }

                AppendLog("Disconnected.");
            }
        }

        private async Task LoadGrblSettingsAsync()
        {
            if (_grbl == null) return;

            try
            {
                var lines = await _grbl.SendAndCollectAsync("$$", TimeSpan.FromSeconds(3));

                double bedX = _bedX;
                double bedY = _bedY;
                int homingMask = _homingDirMask;

                foreach (var line in lines)
                {
                    if (line.StartsWith("$130=") && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    {
                        bedX = x;
                        _bedFromGrbl = true;
                    }
                    else if (line.StartsWith("$131=") && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    {
                        bedY = y;
                        _bedFromGrbl = true;
                    }
                    else if (line.StartsWith("$23=") && int.TryParse(line[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
                    {
                        homingMask = mask;
                    }
                }

                _bedX = bedX;
                _bedY = bedY;
                _homingDirMask = homingMask;
                _homeAtMaxX = (_homingDirMask & 0x01) != 0;
                _homeAtMaxY = (_homingDirMask & 0x02) != 0;

                AppendLog($"Read $$: $130={_bedX:0.###}, $131={_bedY:0.###}, $23={_homingDirMask}");
                RenderAll();
            }
            catch (Exception ex)
            {
                AppendLog("Failed to query $$: " + ex.Message);
            }
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
            if (_imageManipulator.IsManipulating)
            {
                _imageManipulator.End();
            }
            _shapeController.CancelAll();
            if (_measurementOverlay.IsMeasuring)
            {
                _measurementOverlay.Reset();
            }
        }

        private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_imageManipulator.IsManipulating)
            {
                _imageManipulator.Update(e);
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

            if (_measurementOverlay.IsMeasuring)
            {
                var measurePoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _measurementOverlay.Update(measurePoint);
                e.Handled = true;
                return;
            }
 
            var mm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            if (_shapeController.Update(mm))
            {
                e.Handled = true;
                return;
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
                var point = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _shapeController.HandlePolylineClick(point, e.ClickCount >= 2);
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Measure)
            {
                var start = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _measurementOverlay.Begin(start);
                DrawCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Pan || _isPanning) return; // Pan tool

            var startPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _shapeController.BeginDraw(tool, startPoint);
            e.Handled = true;
        }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_imageManipulator.IsManipulating)
            {
                _imageManipulator.End();
                e.Handled = true;
                return;
            }

            if (_workingAreaManager.IsDragging)
            {
                CompleteWorkingAreaDrag(e);
                return;
            }

            if (_measurementOverlay.IsMeasuring)
            {
                var measureEnd = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _measurementOverlay.Complete(measureEnd, Distance);
                e.Handled = true;
                return;
            }

            if (_shapeController.IsDrawing)
            {
                var endMm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _shapeController.CompleteDraw(endMm);
                RenderAll();
                e.Handled = true;
                return;
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

        private void DrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_measurementOverlay.IsMeasuring || (GetCurrentTool() == ToolMode.Measure && _measurementOverlay.HasMeasurement))
            {
                _measurementOverlay.Reset();
                e.Handled = true;
                return;
            }

            if (_shapeController.TryFinishPolyline())
            {
                e.Handled = true;
            }
        }

        private void ToolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_measurementOverlay == null) return;

            var tool = GetCurrentTool();

            if (tool != ToolMode.Measure && (_measurementOverlay.IsMeasuring || _measurementOverlay.HasMeasurement))
            {
                _measurementOverlay.Reset();
            }

            if (tool != ToolMode.Polyline)
            {
                _shapeController.FinishPolyline();
            }
            if (_shapeController.IsDrawing && tool != ToolMode.Polyline)
            {
                _shapeController.CancelAll();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_measurementOverlay.IsMeasuring && e.Key == Key.Escape)
            {
                _measurementOverlay.Reset();
                e.Handled = true;
                return;
            }

            if (_shapeController.IsPolylineActive && (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Return))
            {
                _shapeController.FinishPolyline();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Z)
            {
                PerformUndo();
                e.Handled = true;
            }
        }

        private void AppendLog(string message)
        {
            if (ConsoleBox == null) return;

            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            ConsoleBox.AppendText(line + Environment.NewLine);
            ConsoleBox.ScrollToEnd();
        }

        private void RefreshPorts()
        {
            if (PortCombo == null) return;

            var previous = PortCombo.SelectedItem as string ?? PortCombo.SelectedValue as string;
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToList();

            PortCombo.ItemsSource = ports;

            if (previous != null && ports.Contains(previous))
            {
                PortCombo.SelectedItem = previous;
            }
            else if (ports.Count > 0)
            {
                PortCombo.SelectedIndex = 0;
            }

            UpdateConnStatus();
        }

        private void UpdateConnStatus()
        {
            var isConnected = _grbl?.IsOpen == true;

            if (ConnStatusLabel != null)
            {
                ConnStatusLabel.Text = isConnected
                    ? $"Connected: {_grbl!.PortName} @ {_grbl.BaudRate}"
                    : "Disconnected";
            }

            if (ConnectBtn != null)
            {
                ConnectBtn.Content = isConnected ? "Disconnect" : "Connect";
            }

            if (PortCombo != null)
            {
                PortCombo.IsEnabled = !isConnected;
            }

            if (BaudCombo != null)
            {
                BaudCombo.IsEnabled = !isConnected;
            }

            if (SendBtn != null)
            {
                SendBtn.IsEnabled = isConnected;
            }

            if (StopBtn != null)
            {
                StopBtn.IsEnabled = isConnected;
            }
        }

        private void BeginWorkingAreaDrag(MouseEventArgs e)
        {
            var start = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _workingAreaManager.BeginDrag(start);

            RemoveWorkingAreaCrosshair();
            EnsureWorkingAreaPreview();
            UpdateWorkingAreaPreview(_workingAreaManager.PreviewArea ?? new Rect(start.X, start.Y, 0, 0));

            DrawCanvas?.CaptureMouse();
            e.Handled = true;
        }

        private void UpdateWorkingAreaDrag(MouseEventArgs e)
        {
            var current = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            var rect = _workingAreaManager.UpdateDrag(current);
            UpdateWorkingAreaPreview(rect);
            UpdateWorkingAreaStatus();
            e.Handled = true;
        }

        private void CompleteWorkingAreaDrag(MouseEventArgs e)
        {
            var end = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            _ = _workingAreaManager.CompleteDrag(end);

            RemoveWorkingAreaPreview();
            RemoveWorkingAreaCrosshair();
            UpdateWorkingAreaStatus();
            RenderAll();

            DrawCanvas?.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void CancelWorkingAreaDrag()
        {
            _workingAreaManager.CancelDrag();
            RemoveWorkingAreaPreview();
            RemoveWorkingAreaCrosshair();
            UpdateWorkingAreaStatus();
            RenderAll();
        }

        private void EnsureWorkingAreaPreview()
        {
            if (DrawCanvas == null || _workingAreaPreviewRect != null) return;

            _workingAreaPreviewRect = CreateWorkingAreaVisual();
            DrawCanvas.Children.Add(_workingAreaPreviewRect);
            Panel.SetZIndex(_workingAreaPreviewRect, 12);
        }

        private void UpdateWorkingAreaPreview(Rect rect)
        {
            if (DrawCanvas == null) return;

            EnsureWorkingAreaPreview();
            if (_workingAreaPreviewRect == null) return;

            _workingAreaPreviewRect.Width = rect.Width;
            _workingAreaPreviewRect.Height = rect.Height;
            Canvas.SetLeft(_workingAreaPreviewRect, rect.Left);
            Canvas.SetTop(_workingAreaPreviewRect, rect.Top);
        }

        private void RemoveWorkingAreaPreview()
        {
            if (DrawCanvas == null || _workingAreaPreviewRect == null) return;
            DrawCanvas.Children.Remove(_workingAreaPreviewRect);
            _workingAreaPreviewRect = null;
        }

        private void UpdateWorkingAreaCrosshair(PointMm pos)
        {
            if (DrawCanvas == null) return;

            if (_workingAreaCrosshairHorizontal == null)
            {
                _workingAreaCrosshairHorizontal = new Line
                {
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                DrawCanvas.Children.Add(_workingAreaCrosshairHorizontal);
                Panel.SetZIndex(_workingAreaCrosshairHorizontal, 11);
            }

            if (_workingAreaCrosshairVertical == null)
            {
                _workingAreaCrosshairVertical = new Line
                {
                    Stroke = Brushes.DeepSkyBlue,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                DrawCanvas.Children.Add(_workingAreaCrosshairVertical);
                Panel.SetZIndex(_workingAreaCrosshairVertical, 11);
            }

            _workingAreaCrosshairHorizontal.X1 = 0;
            _workingAreaCrosshairHorizontal.X2 = _doc.WidthMm;
            _workingAreaCrosshairHorizontal.Y1 = pos.Y;
            _workingAreaCrosshairHorizontal.Y2 = pos.Y;

            _workingAreaCrosshairVertical.X1 = pos.X;
            _workingAreaCrosshairVertical.X2 = pos.X;
            _workingAreaCrosshairVertical.Y1 = 0;
            _workingAreaCrosshairVertical.Y2 = _doc.HeightMm;
        }

        private void RemoveWorkingAreaCrosshair()
        {
            if (DrawCanvas != null && _workingAreaCrosshairHorizontal != null)
            {
                DrawCanvas.Children.Remove(_workingAreaCrosshairHorizontal);
                _workingAreaCrosshairHorizontal = null;
            }

            if (DrawCanvas != null && _workingAreaCrosshairVertical != null)
            {
                DrawCanvas.Children.Remove(_workingAreaCrosshairVertical);
                _workingAreaCrosshairVertical = null;
            }
        }

        private Rectangle CreateWorkingAreaVisual()
        {
            return new Rectangle
            {
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(32, 0, 122, 204)),
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
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

            _imageManipulator.BeginHandle(e, handle);
        }

        private async void HomeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            try
            {
                await _grbl!.SendLineWaitOkAsync("$H", TimeSpan.FromSeconds(30), _sendCts?.Token ?? CancellationToken.None);
                _isHomed = true;
                AppendLog("Homing completed.");
            }
            catch (Exception ex)
            {
                AppendLog("Homing failed: " + ex.Message);
            }
        }

        private async void UnlockBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            try
            {
                await _grbl!.SendLineWaitOkAsync("$X", TimeSpan.FromSeconds(5), _sendCts?.Token ?? CancellationToken.None);
                AppendLog("Machine unlocked.");
            }
            catch (Exception ex)
            {
                AppendLog("Unlock failed: " + ex.Message);
            }
        }

        private async void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            try
            {
                await _grbl!.SoftResetAsync();
                _isHomed = false;
                AppendLog("Soft reset sent.");
            }
            catch (Exception ex)
            {
                AppendLog("Reset failed: " + ex.Message);
            }
        }

        private void SendBtn_Click(object sender, RoutedEventArgs e)
        {
            AppendLog("Send G-code not implemented yet.");
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_sendCts == null)
            {
                AppendLog("Nothing to cancel.");
                return;
            }

            _sendCts.Cancel();
            _sendCts.Dispose();
            _sendCts = new CancellationTokenSource();
            AppendLog("Operation cancelled.");
        }

        private async void ManualSendBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;

            var cmd = ManualCmdBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(cmd))
            {
                AppendLog("Enter a GRBL command first.");
                return;
            }

            try
            {
                await _grbl!.SendAndCollectAsync(cmd, TimeSpan.FromSeconds(5), _sendCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppendLog("Manual send failed: " + ex.Message);
            }
        }

        private bool EnsureConnected()
        {
            if (_grbl?.IsOpen == true)
            {
                return true;
            }

            AppendLog("Not connected to GRBL.");
            return false;
        }

    }
 }
