using Microsoft.Win32;
using NVSPlotter.Models;
using NVSPlotter.Properties;
using NVSPlotter.Services;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NVSPlotter
{
    public partial class MainWindow : Window
    {

        // --- CONFIG
        private readonly Configuration _config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

        // --- Document is stored in mm; canvas is "px" where 1 px = 1 mm before zoom ---
        public PlotDocument _doc = new(Settings.Default.bedX, Settings.Default.bedY);
        private readonly Utility _util = new();
        private readonly RulerFactory _rulerFactory = new();

        private readonly ReferenceImageService _imageService = new();
        private readonly WorkingAreaManager _workingAreaManager = new();
        private readonly ReferenceImageManipulator _imageManipulator;

        // Canvas transforms
        private readonly ScaleTransform _zoom = new(1.0, 1.0);
        private readonly RotateTransform _canvasRotation = new(0);

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
        private readonly SelectionController _selectionController;
        private readonly PaintWellController _paintWellController;

        // Undo
        private readonly Stack<LineStroke> _undo = new();

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

        private bool _isHomed = false;

        private const double SAFE_MARGIN_MM = 50.0; // Default fallback, actual value from Settings
        private double SafeMarginMm => Settings.Default.safeMarginMm;

        // Snap indicator visual
        private Ellipse? _snapIndicator;
        private const double SNAP_INDICATOR_SIZE = 10.0;

        private readonly DispatcherTimer _filterThrottle;
        private ConsoleWindow? _consoleWindow;

        // Project file state
        private string? _currentProjectPath;

        public MainWindow()
        {
            _filterThrottle = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60) // adjust (30–100ms)
            };
            _filterThrottle.Tick += (_, __) =>
            {
                _filterThrottle.Stop();
                ApplyPreviewFilter();
            };
        

            InitializeComponent();

            DrawCanvas.LayoutTransform = _zoom;
            if (RulerCanvas != null)
            {
                RulerCanvas.LayoutTransform = _zoom;
            }
            CanvasHost.LayoutTransform = _canvasRotation;

            // Apply initial zoom from slider value (don't hardcode 100%)
            SetZoom(ZoomSlider.Value);
            
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
                RenderAll,
                () => _paintWellController?.ActiveColorWell?.Id);

            _selectionController = new SelectionController(
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                () => _doc,
                RenderAll);

            _paintWellController = new PaintWellController(
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                () => _doc,
                RenderAll);

             RenderAll();
             UpdateConnStatus();
             InitializeSafeMarginBox();
             InitializeConsoleWindow();
             UpdatePaintWellsUI();
        }

        private void FilterControlSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {

            // restart timer each change -> only applies after user pauses for Interval
            _filterThrottle.Stop();
            _filterThrottle.Start();

        }

        private void ApplyPreviewFilter()
        {
            if (_imageService.OriginalImage == null) return;
            if (FindName("FilterControlSlider") is Slider slider)
            {
                int intensity = (int)Math.Round(slider.Value);
                _imageService.SetFilter(_imageService.CurrentFilter, intensity);
                RenderAll();
            }
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

            const double canvasPadding = 20.0; // Fixed padding around the canvas

            // With LayoutTransform, the canvas size is automatically scaled for layout purposes.
            // We just need to set the margin for padding around the canvas.
            var margin = new Thickness(canvasPadding, canvasPadding, canvasPadding, canvasPadding);
            DrawCanvas.Margin = margin;
            if (RulerCanvas != null)
                RulerCanvas.Margin = margin;

            // Clear any fixed size on CanvasHost - let it size to content
            CanvasHost.Width = double.NaN;
            CanvasHost.Height = double.NaN;
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

            const double rulerThickness = 18.0;
            var zx = viewportW / (_doc.WidthMm + rulerThickness);
            var zy = viewportH / (_doc.HeightMm + rulerThickness);
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
            var locked = sender is CheckBox { IsChecked: true };
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

        public void ImageFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs? e)
        {
            if (_suppressImageFilterChange) return;
            if (_imageService.OriginalImage == null) return;

            var filter = ParseImageFilter((sender as ComboBox)?.SelectedValue);
            if (filter == _imageService.CurrentFilter) return;

            _imageService.SetFilter(filter, 1);
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

            _zoom.ScaleX = z;
            _zoom.ScaleY = z;
            if (ZoomLabel != null) ZoomLabel.Text = $"{(int)Math.Round(z * 100)}%";

            UpdateZoomHost();
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
            RemoveSnapIndicator();

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
            if (_selectionController.IsActive)
            {
                _selectionController.Cancel();
            }
            if (_paintWellController.IsCreating || _paintWellController.IsDragging)
            {
                _paintWellController.Cancel();
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

            // Paint well tool handling
            if (_paintWellController.IsCreating || _paintWellController.IsDragging)
            {
                var paintPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                if (_paintWellController.HandleMouseMove(paintPoint))
                {
                    e.Handled = true;
                    return;
                }
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

            // Selection tool handling
            if (_selectionController.IsActive)
            {
                var selPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                if (_selectionController.HandleMouseMove(selPoint))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (_measurementOverlay.IsMeasuring)
            {
                var measurePoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _measurementOverlay.Update(measurePoint);
                e.Handled = true;
                return;
            }
 
            var rawMm = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            var mm = ApplySnapping(rawMm, out var snappedTo, out var snapType, out var gridSnapped);
            UpdateSnapIndicator(snappedTo, snapType, gridSnapped ? mm : null);

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
            var rawPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
            var point = ApplySnapping(rawPoint, out _, out _, out _);

            // Paint well tool
            if (tool == ToolMode.PaintWell)
            {
                if (_paintWellController.HandleMouseDown(point, Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
                {
                    e.Handled = true;
                    return;
                }
            }

            // Selection tool
            if (tool == ToolMode.Select)
            {
                var shiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                if (_selectionController.HandleMouseDown(point, shiftHeld))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (tool == ToolMode.Bezier)
            {
                _shapeController.HandleBezierClick(point);
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Polyline)
            {
                _shapeController.HandlePolylineClick(point, e.ClickCount >= 2);
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Measure)
            {
                _measurementOverlay.Begin(point);
                DrawCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Pan || _isPanning) return; // Pan tool

            _shapeController.BeginDraw(tool, point);
            e.Handled = true;
        }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            RemoveSnapIndicator();

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

            // Paint well tool handling
            if (_paintWellController.IsCreating || _paintWellController.IsDragging)
            {
                var paintPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                if (_paintWellController.HandleMouseUp(paintPoint))
                {
                    UpdatePaintWellsUI();
                    _lastGcode = ""; // Invalidate G-code cache
                    e.Handled = true;
                    return;
                }
            }

            // Selection tool handling
            if (_selectionController.IsActive)
            {
                var selPoint = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                _selectionController.HandleMouseUp(selPoint);
                _lastGcode = ""; // Invalidate G-code cache after transform
                e.Handled = true;
                return;
            }

            if (_measurementOverlay.IsMeasuring)
            {
                var rawEnd = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                var measureEnd = ApplySnapping(rawEnd, out _, out _, out _);
                _measurementOverlay.Complete(measureEnd, Utility.Distance);
                e.Handled = true;
                return;
            }

            if (_shapeController.IsDrawing)
            {
                var rawEnd = ClampToPage(MouseToMm(e.GetPosition(CanvasScroll)));
                var endMm = ApplySnapping(rawEnd, out _, out _, out _);
                _shapeController.CompleteDraw(endMm);
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

        // ===== ENDPOINT SNAPPING =====

        private enum SnapType { None, Start, End }

        private bool IsSnapEnabled => FindName("SnapEnabledCheck") is CheckBox cb && cb.IsChecked == true;

        private double GetSnapRadius()
        {
            if (FindName("SnapRadiusBox") is not TextBox tb) return 5.0;
            return ParseDouble(tb.Text, 5.0);
        }

        /// <summary>
        /// Snaps a point to the nearest stroke endpoint if within snap radius.
        /// Returns the snapped point (or original if no snap), and sets snapType to indicate
        /// whether it snapped to a line start (A) or end (B).
        /// </summary>
        private PointMm SnapToEndpoint(PointMm raw, out PointMm? snappedTo, out SnapType snapType)
        {
            snappedTo = null;
            snapType = SnapType.None;
            if (!IsSnapEnabled) return raw;

            var radius = GetSnapRadius();
            if (radius <= 0) return raw;

            double bestDist = double.MaxValue;
            PointMm? bestPoint = null;
            SnapType bestSnapType = SnapType.None;

            foreach (var stroke in _doc.Strokes)
            {
                var distA = Utility.Distance(raw, stroke.A);
                if (distA < bestDist && distA <= radius)
                {
                    bestDist = distA;
                    bestPoint = stroke.A;
                    bestSnapType = SnapType.Start;
                }

                var distB = Utility.Distance(raw, stroke.B);
                if (distB < bestDist && distB <= radius)
                {
                    bestDist = distB;
                    bestPoint = stroke.B;
                    bestSnapType = SnapType.End;
                }
            }

            if (bestPoint is PointMm pt)
            {
                snappedTo = pt;
                snapType = bestSnapType;
                return pt;
            }

            return raw;
        }

        /// <summary>
        /// Overload for cases where snap type isn't needed.
        /// </summary>
        private PointMm SnapToEndpoint(PointMm raw, out PointMm? snappedTo)
        {
            return SnapToEndpoint(raw, out snappedTo, out _);
        }

        private void UpdateSnapIndicator(PointMm? snapPoint, SnapType snapType, PointMm? gridSnapPoint = null)
        {
            if (DrawCanvas == null) return;

            // Determine what to show: endpoint snap takes priority, then grid snap
            PointMm? displayPoint = snapPoint ?? gridSnapPoint;

            if (displayPoint == null)
            {
                RemoveSnapIndicator();
                return;
            }

            // Colors: Green for start, Blue for end, Orange for grid
            Brush fillBrush;
            Brush strokeBrush;
            if (snapPoint != null)
            {
                if (snapType == SnapType.Start)
                {
                    fillBrush = new SolidColorBrush(Color.FromArgb(128, 0, 200, 0));
                    strokeBrush = Brushes.LimeGreen;
                }
                else
                {
                    fillBrush = new SolidColorBrush(Color.FromArgb(128, 30, 90, 180));
                    strokeBrush = new SolidColorBrush(Color.FromRgb(50, 120, 200)); // Mid blue
                }
            }
            else
            {
                // Grid snap - orange/yellow color
                fillBrush = new SolidColorBrush(Color.FromArgb(128, 255, 165, 0));
                strokeBrush = Brushes.Orange;
            }

            if (_snapIndicator == null)
            {
                _snapIndicator = new Ellipse
                {
                    Width = SNAP_INDICATOR_SIZE,
                    Height = SNAP_INDICATOR_SIZE,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true,
                    StrokeThickness = 2
                };
                DrawCanvas.Children.Add(_snapIndicator);
                Panel.SetZIndex(_snapIndicator, 20);
            }

            _snapIndicator.Fill = fillBrush;
            _snapIndicator.Stroke = strokeBrush;

            Canvas.SetLeft(_snapIndicator, displayPoint.Value.X - SNAP_INDICATOR_SIZE / 2.0);
            Canvas.SetTop(_snapIndicator, displayPoint.Value.Y - SNAP_INDICATOR_SIZE / 2.0);
        }

        private void RemoveSnapIndicator()
        {
            if (DrawCanvas != null && _snapIndicator != null)
            {
                DrawCanvas.Children.Remove(_snapIndicator);
                _snapIndicator = null;
            }
        }

        // ===== GRID / GUIDES =====

        private bool IsGridVisible => FindName("ShowGridCheck") is CheckBox cb && cb.IsChecked == true;
        private bool IsSnapToGridEnabled => FindName("SnapToGridCheck") is CheckBox cb && cb.IsChecked == true;

        private double GetGridSpacing()
        {
            if (FindName("GridSpacingBox") is not TextBox tb) return 10.0;
            var spacing = ParseDouble(tb.Text, 10.0);
            return Math.Clamp(spacing, 1.0, 500.0); // Clamp to reasonable range
        }

        private void ShowGridCheck_Click(object sender, RoutedEventArgs e)
        {
            RenderAll();
        }

        private void GridSpacingBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (IsGridVisible)
            {
                RenderAll();
            }
        }

        private void InitializeSafeMarginBox()
        {
            if (FindName("SafeMarginBox") is TextBox tb)
            {
                tb.Text = Settings.Default.safeMarginMm.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        private void InitializeConsoleWindow()
        {
            _consoleWindow = new ConsoleWindow();
            
            // Close console window when main window closes
            Closed += (s, e) => _consoleWindow?.ForceClose();
        }

        private void ShowConsoleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_consoleWindow == null)
            {
                InitializeConsoleWindow();
            }

            if (_consoleWindow!.IsVisible)
            {
                _consoleWindow.Hide();
            }
            else
            {
                // Set owner when showing (main window is now visible)
                if (_consoleWindow.Owner == null)
                {
                    _consoleWindow.Owner = this;
                }
                
                // Position to the right of the main window, but ensure it's on-screen
                var desiredLeft = Left + Width + 10;
                var desiredTop = Top;
                
                // Get the working area of the screen (excludes taskbar)
                var screen = System.Windows.SystemParameters.WorkArea;
                
                // If the console would be off the right edge, position it to the left of the main window
                if (desiredLeft + _consoleWindow.Width > screen.Right)
                {
                    desiredLeft = Left - _consoleWindow.Width - 10;
                }
                
                // If still off-screen (left edge), just put it at the left edge of the screen
                if (desiredLeft < screen.Left)
                {
                    desiredLeft = screen.Left;
                }
                
                // Ensure top is on-screen
                if (desiredTop < screen.Top)
                {
                    desiredTop = screen.Top;
                }
                
                // Ensure bottom is on-screen
                if (desiredTop + _consoleWindow.Height > screen.Bottom)
                {
                    desiredTop = screen.Bottom - _consoleWindow.Height;
                }
                
                _consoleWindow.Left = desiredLeft;
                _consoleWindow.Top = desiredTop;
                
                _consoleWindow.Show();
                _consoleWindow.Activate();
            }
        }

        private void SafeMarginBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;

            var value = ParseDouble(tb.Text, Settings.Default.safeMarginMm);
            value = Math.Clamp(value, 0, 500); // Reasonable range

            if (Math.Abs(value - Settings.Default.safeMarginMm) > 0.001)
            {
                Settings.Default.safeMarginMm = value;
                Settings.Default.Save();
                _lastGcode = ""; // Invalidate G-code cache
                RenderAll(); // Redraw to update margin overlay
            }
        }

        private void ShowMarginOverlayCheck_Click(object sender, RoutedEventArgs e)
        {
            RenderAll();
        }

        private bool IsMarginOverlayVisible => FindName("ShowMarginOverlayCheck") is CheckBox cb && cb.IsChecked == true;

        /// <summary>
        /// Draws a transparent overlay showing the safe margin zones around the edges.
        /// </summary>
        private void DrawSafeMarginOverlay()
        {
            if (DrawCanvas == null) return;
            if (!IsMarginOverlayVisible) return;

            var margin = SafeMarginMm;
            if (margin <= 0) return;

            const double rulerThickness = 18.0; // Must match ruler thickness

            var marginBrush = new SolidColorBrush(Color.FromArgb(40, 255, 100, 100)); // Semi-transparent red
            var marginStroke = new SolidColorBrush(Color.FromArgb(80, 200, 50, 50)); // Slightly more opaque border

            // Document area starts after ruler offset
            var docLeft = rulerThickness;
            var docTop = rulerThickness;
            var docWidth = _doc.WidthMm;
            var docHeight = _doc.HeightMm;

            // Top margin zone
            if (margin < docHeight)
            {
                var topRect = new Rectangle
                {
                    Width = docWidth,
                    Height = Math.Min(margin, docHeight),
                    Fill = marginBrush,
                    Stroke = marginStroke,
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(topRect, docLeft);
                Canvas.SetTop(topRect, docTop);
                Panel.SetZIndex(topRect, 1);
                DrawCanvas.Children.Add(topRect);
            }

            // Bottom margin zone
            if (margin < docHeight)
            {
                var bottomRect = new Rectangle
                {
                    Width = docWidth,
                    Height = Math.Min(margin, docHeight),
                    Fill = marginBrush,
                    Stroke = marginStroke,
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(bottomRect, docLeft);
                Canvas.SetTop(bottomRect, docTop + docHeight - margin);
                Panel.SetZIndex(bottomRect, 1);
                DrawCanvas.Children.Add(bottomRect);
            }

            // Left margin zone (between top and bottom margins)
            var verticalHeight = Math.Max(0, docHeight - 2 * margin);
            if (margin < docWidth && verticalHeight > 0)
            {
                var leftRect = new Rectangle
                {
                    Width = Math.Min(margin, docWidth),
                    Height = verticalHeight,
                    Fill = marginBrush,
                    Stroke = marginStroke,
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(leftRect, docLeft);
                Canvas.SetTop(leftRect, docTop + margin);
                Panel.SetZIndex(leftRect, 1);
                DrawCanvas.Children.Add(leftRect);
            }

            // Right margin zone (between top and bottom margins)
            if (margin < docWidth && verticalHeight > 0)
            {
                var rightRect = new Rectangle
                {
                    Width = Math.Min(margin, docWidth),
                    Height = verticalHeight,
                    Fill = marginBrush,
                    Stroke = marginStroke,
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(rightRect, docLeft + docWidth - margin);
                Canvas.SetTop(rightRect, docTop + margin);
                Panel.SetZIndex(rightRect, 1);
                DrawCanvas.Children.Add(rightRect);
            }

            // Draw a dashed inner border to clearly show the safe area boundary
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
            DrawCanvas.Children.Add(safeAreaBorder);
        }

        /// <summary>
        /// Draws the grid on the canvas if enabled.
        /// </summary>
        private void DrawGrid()
        {
            if (DrawCanvas == null || !IsGridVisible) return;

            var spacing = GetGridSpacing();
            if (spacing <= 0) return;

            var gridBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)); // Medium gray for visibility
            const double gridThickness = 1.0; // Thicker lines visible at all zoom levels
            const double rulerThickness = 18.0;

            // Draw vertical lines - offset by ruler thickness to align with ruler ticks
            for (double x = 0; x <= _doc.WidthMm; x += spacing)
            {
                var line = new Line
                {
                    X1 = rulerThickness + x,
                    Y1 = rulerThickness,
                    X2 = rulerThickness + x,
                    Y2 = rulerThickness + _doc.HeightMm,
                    Stroke = gridBrush,
                    StrokeThickness = gridThickness,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                Panel.SetZIndex(line, 2);
                DrawCanvas.Children.Add(line);
            }

            // Draw horizontal lines - offset by ruler thickness to align with ruler ticks
            for (double y = 0; y <= _doc.HeightMm; y += spacing)
            {
                var line = new Line
                {
                    X1 = rulerThickness,
                    Y1 = rulerThickness + y,
                    X2 = rulerThickness + _doc.WidthMm,
                    Y2 = rulerThickness + y,
                    Stroke = gridBrush,
                    StrokeThickness = gridThickness,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                Panel.SetZIndex(line, 2);
                DrawCanvas.Children.Add(line);
            }
        }

        /// <summary>
        /// Snaps a point to the nearest grid intersection if snap-to-grid is enabled.
        /// </summary>
        private PointMm SnapToGrid(PointMm raw)
        {
            if (!IsSnapToGridEnabled) return raw;

            var spacing = GetGridSpacing();
            if (spacing <= 0) return raw;

            var snappedX = Math.Round(raw.X / spacing) * spacing;
            var snappedY = Math.Round(raw.Y / spacing) * spacing;

            return new PointMm(snappedX, snappedY);
        }

        /// <summary>
        /// Combined snapping: endpoints first (higher priority), then grid.
        /// Returns the final snapped point and snap info for indicator display.
        /// </summary>
        private PointMm ApplySnapping(PointMm raw, out PointMm? endpointSnap, out SnapType snapType, out bool gridSnapped)
        {
            // First try endpoint snapping (higher priority)
            var result = SnapToEndpoint(raw, out endpointSnap, out snapType);

            // If no endpoint snap, try grid snap
            if (endpointSnap == null && IsSnapToGridEnabled)
            {
                var gridSnap = SnapToGrid(raw);
                gridSnapped = gridSnap.X != raw.X || gridSnap.Y != raw.Y;
                return gridSnap;
            }

            gridSnapped = false;
            return result;
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

        private void DrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_measurementOverlay.IsMeasuring || (GetCurrentTool() == ToolMode.Measure && _measurementOverlay.HasMeasurement))
            {
                _measurementOverlay.Reset();
                e.Handled = true;
                return;
            }

            if (_shapeController.TryFinishBezier())
            {
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

            if (tool != ToolMode.Bezier && _shapeController.IsBezierActive)
            {
                _shapeController.CancelBezier();
            }

            if (_shapeController.IsDrawing && tool != ToolMode.Polyline && tool != ToolMode.Bezier)
            {
                _shapeController.CancelAll();
            }

            // Clear selection when switching away from Select tool
            if (tool != ToolMode.Select && _selectionController.HasSelection)
            {
                _selectionController.ClearSelection();
            }

            // Cancel paint well operations when switching away
            if (tool != ToolMode.PaintWell && (_paintWellController.IsCreating || _paintWellController.IsDragging))
            {
                _paintWellController.Cancel();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Selection tool: Escape cancels, Delete removes selected strokes
            if (GetCurrentTool() == ToolMode.Select)
            {
                if (e.Key == Key.Escape)
                {
                    if (_selectionController.IsActive)
                    {
                        _selectionController.Cancel();
                    }
                    else if (_selectionController.HasSelection)
                    {
                        _selectionController.ClearSelection();
                    }
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Delete && _selectionController.HasSelection)
                {
                    _selectionController.DeleteSelection();
                    _lastGcode = "";
                    e.Handled = true;
                    return;
                }
            }

            if (_measurementOverlay.IsMeasuring && e.Key == Key.Escape)
            {
                _measurementOverlay.Reset();
                e.Handled = true;
                return;
            }

            if (_shapeController.IsBezierActive && (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Return))
            {
                if (e.Key == Key.Escape)
                {
                    _shapeController.CancelBezier();
                }
                else
                {
                    _shapeController.TryFinishBezier();
                }
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
            _consoleWindow?.AppendLog(message);
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
                    StrokeDashArray = [2, 2],
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
                    StrokeDashArray = [2, 2],
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

        private static Rectangle CreateWorkingAreaVisual()
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

        private void UpdateWorkingAreaStatus()
        {
            if (WorkingAreaStatus == null) return;
            WorkingAreaStatus.Text = _workingAreaManager.GetStatusText();
        }

        private void DrawRulers()
        {
            if (RulerCanvas == null) return;

            const double rulerThickness = 18.0;
            const double majorStep = 10.0;
            double labelStep = 50.0;

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

            // Corner block
            var corner = CreateBackground(rulerThickness, rulerThickness);
            Canvas.SetLeft(corner, 0);
            Canvas.SetTop(corner, 0);
            RulerCanvas.Children.Add(corner);

            // Top ruler background (starts at rulerThickness to not overlap corner)
            var topBackground = CreateBackground(_doc.WidthMm, rulerThickness);
            Canvas.SetLeft(topBackground, rulerThickness);
            Canvas.SetTop(topBackground, 0);
            RulerCanvas.Children.Add(topBackground);

            // Left ruler background (starts at rulerThickness to not overlap corner)
            var leftBackground = CreateBackground(rulerThickness, _doc.HeightMm);
            Canvas.SetLeft(leftBackground, 0);
            Canvas.SetTop(leftBackground, rulerThickness);
            RulerCanvas.Children.Add(leftBackground);

            // Vertical ticks on top ruler - position matches document coordinates
            var gridSpacing = GetGridSpacing();
            for (double x = 0; x <= _doc.WidthMm; x += gridSpacing)
            {
                bool isMajor = Math.Abs(x % majorStep) < 0.0001 || x == 0;
                bool showLabel = Math.Abs(x % labelStep) < 0.0001;
                double len = isMajor ? rulerThickness : rulerThickness / 2.5;

                // Draw tick at position that aligns with grid (offset by ruler corner)
                double cx = rulerThickness + x;

                var line = new Line
                {
                    X1 = cx,
                    X2 = cx,
                    Y1 = rulerThickness - len,
                    Y2 = rulerThickness,
                    Stroke = tickBrush,
                    StrokeThickness = isMajor ? 1.0 : 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                RulerCanvas.Children.Add(line);

                if (showLabel && x > 0)
                {
                    var label = CreateRulerLabel(x, rotate: false);
                    // Measure text width to center label on tick mark
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var textWidth = label.DesiredSize.Width;
                    Canvas.SetLeft(label, cx - textWidth / 2);
                    Canvas.SetTop(label, 1);
                    RulerCanvas.Children.Add(label);
                }
            }

            // Horizontal ticks on left ruler - position matches document coordinates
            for (double y = 0; y <= _doc.HeightMm; y += gridSpacing)
            {
                bool isMajor = Math.Abs(y % majorStep) < 0.0001 || y == 0;
                bool showLabel = Math.Abs(y % labelStep) < 0.0001;
                double len = isMajor ? rulerThickness : rulerThickness / 2.5;

                // Draw tick at position that aligns with grid (offset by ruler corner)
                double cy = rulerThickness + y;

                var line = new Line
                {
                    X1 = rulerThickness - len,
                    X2 = rulerThickness,
                    Y1 = cy,
                    Y2 = cy,
                    Stroke = tickBrush,
                    StrokeThickness = isMajor ? 1.0 : 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                RulerCanvas.Children.Add(line);

                if (showLabel && y > 0)
                {
                    var label = CreateRulerLabel(y, rotate: true);
                    // Measure text to center label on tick mark (after rotation, width becomes height)
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var textHeight = label.DesiredSize.Height; // After -90° rotation, this is the visual width
                    Canvas.SetLeft(label, 1);
                    Canvas.SetTop(label, cy - textHeight / 2);
                    RulerCanvas.Children.Add(label);
                }
            }
        }

        private static TextBlock CreateRulerLabel(double value, bool rotate)
        {
            var tb = new TextBlock
            {
                Text = value.ToString("0"),
                FontSize = 9,
                FontWeight= FontWeights.Bold,
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

            const double rulerThickness = 18.0;

            // Size canvas to page + ruler thickness
            DrawCanvas.Width = _doc.WidthMm + rulerThickness;
            DrawCanvas.Height = _doc.HeightMm + rulerThickness;
            RulerCanvas.Width = _doc.WidthMm + rulerThickness;
            RulerCanvas.Height = _doc.HeightMm + rulerThickness;

            // Page border (offset by ruler thickness)
            var pageRect = new Rectangle
            {
                Width = _doc.WidthMm,
                Height = _doc.HeightMm,
                Stroke = Brushes.Black,
                StrokeThickness = 1.0,
                Fill = Brushes.White
            };
            DrawCanvas.Children.Add(pageRect);
            Canvas.SetLeft(pageRect, rulerThickness);
            Canvas.SetTop(pageRect, rulerThickness);
            Panel.SetZIndex(pageRect, 0);

            DrawGrid();
            DrawRulers();
            DrawSafeMarginOverlay();
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


            // Paint wells (render before strokes so strokes appear on top)
            // Pass canvas rotation angle so labels can counter-rotate to stay upright
            _paintWellController.RenderPaintWells(_canvasRotation.Angle);

            // Existing strokes - with paint well colors if painting mode is enabled
            var paintModeEnabled = IsPaintModeEnabled;
            for (int i = 0; i < _doc.Strokes.Count; i++)
            {
                var s = _doc.Strokes[i];
                var isSelected = _selectionController.IsSelected(i);
                
                // Determine stroke color
                Brush strokeBrush;
                if (isSelected)
                {
                    strokeBrush = Brushes.DodgerBlue;
                }
                else if (paintModeEnabled && s.PaintWellId.HasValue)
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
                    StrokeThickness = isSelected ? 2.0 : 1.2,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(ln, EdgeMode.Aliased);
                Panel.SetZIndex(ln, 4);
                DrawCanvas.Children.Add(ln);
            }

            // Selection visuals (bounding box, handles)
            _selectionController.RenderSelectionVisuals();

            AppendLog($"Doc: {_doc.WidthMm:0} x {_doc.HeightMm:0} mm, strokes={_doc.Strokes.Count}, paintWells={_doc.PaintWells.Count}");
            AppendLog($"Bed: X={_bedX:0.###} Y={_bedY:0.###} {(_bedFromGrbl ? "(from $$)" : "(default)")}, margin={SafeMarginMm:0.###}mm");

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
                StrokeDashArray = [4, 2],
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
            var rotatedVector = Utility.RotateVector(new Vector(0, -ROTATE_HANDLE_OFFSET), _imageService.Angle);
            var handleCenter = new Point(center.X + rotatedVector.X, center.Y + rotatedVector.Y);

            var connector = new Line
            {
                X1 = center.X,
                Y1 = center.Y,
                X2 = handleCenter.X,
                Y2 = handleCenter.Y,
                Stroke = Brushes.DodgerBlue,
                StrokeThickness = 1,
                StrokeDashArray = [2, 2],
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
                    // Use generous timeout - long moves and dwell commands can take a while
                    // Some firmware doesn't send 'ok' until the move is complete
                    await _grbl!.SendLineWaitOkAsync(line, TimeSpan.FromSeconds(120), token);
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
            var paintModeEnabled = IsPaintModeEnabled;

            // Hardware Z is inverted: flip the commanded values
            var zUpCmd = -zUp;
            var zDownCmd = -zDown;

            var strokes = _doc.Strokes.ToList();
            if (optimize) strokes = StrokeOptimizer.OptimizeNearest(strokes);

            // Fit doc into bed (with margin), possibly rotating CW to better fit.
            var fit = ComputeFit(_doc.WidthMm, _doc.HeightMm, _bedX, _bedY, SafeMarginMm);

            AppendLog($"G-code fit={fit.Mode}, scale={fit.Scale:0.###}, usableBed=({_bedX - 2 * SafeMarginMm:0.###} x {_bedY - 2 * SafeMarginMm:0.###})");
            AppendLog("G-code convention: X>=0, Y<=0");
            if (paintModeEnabled && _doc.PaintWells.Count > 0)
            {
                AppendLog($"Paint mode: {_doc.PaintWells.Count} paint well(s) defined");
            }

            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("; NVSPlotter");
            sb.AppendLine("; Units: mm");
            sb.AppendLine("; Work convention: X positive, Y negative");
            if (paintModeEnabled && _doc.PaintWells.Count > 0)
            {
                sb.AppendLine("; PAINTING MODE ENABLED");
                foreach (var well in _doc.PaintWells)
                {
                    sb.AppendLine($"; Paint Well: {well.Name} at ({well.Center.X:0.#}, {well.Center.Y:0.#}), dip={well.DipDepth:0.#}mm, dwell={well.DwellTimeMs}ms");
                }
            }
            sb.AppendLine("G21");           // mm
            sb.AppendLine("G90");           // absolute
            sb.AppendLine("G54");
            sb.AppendLine("G92.1");         // clear G92 offsets
            sb.AppendLine("G10 L20 P1 X0 Y0"); // set G54 so current position (home) is work 0,0
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");

            const double joinTol = 0.01; // mm

            if (paintModeEnabled && _doc.PaintWells.Count > 0)
            {
                // PAINTING MODE: Group strokes by paint well, insert paint refresh sequences
                BuildPaintingModeGcode(sb, strokes, fit, zUpCmd, zDownCmd, feedXY, joinTol);
            }
            else
            {
                // NORMAL MODE: Build paths without paint considerations
                var paths = BuildPaths(strokes, joinTol);
                BuildNormalGcode(sb, paths, fit, zUpCmd, zDownCmd, feedXY);
            }

            sb.AppendLine("G0 X0 Y0"); // back to home (work origin)
            sb.AppendLine("M2");

            _lastGcode = sb.ToString();
            AppendLog($"Built G-code: lines={_lastGcode.Split('\n').Length}");
            return _lastGcode;
        }

        private void BuildNormalGcode(StringBuilder sb, List<List<LineStroke>> paths, FitSpec fit, double zUpCmd, double zDownCmd, double feedXY)
        {
            foreach (var path in paths)
            {
                if (path.Count == 0) continue;

                var first = path[0];
                var startWork = BedToWork(DocToBed(first.A, fit));

                sb.AppendLine($"G0 X{Fmt(startWork.X)} Y{Fmt(startWork.Y)}");
                sb.AppendLine($"G0 Z{Fmt(zDownCmd)}");

                bool firstMove = true;
                foreach (var seg in path)
                {
                    var endWork = BedToWork(DocToBed(seg.B, fit));
                    if (firstMove)
                    {
                        sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)} F{Fmt(feedXY)}");
                        firstMove = false;
                    }
                    else
                    {
                        sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)}");
                    }
                }

                sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
            }
        }

        private void BuildPaintingModeGcode(StringBuilder sb, List<LineStroke> strokes, FitSpec fit, double zUpCmd, double zDownCmd, double feedXY, double joinTol)
        {
            // Process strokes in DRAWING ORDER (not grouped by color)
            // This allows wash/wipe between color changes
            
            Guid? currentWellId = null;
            PaintWell? currentWell = null;
            double distanceTraveled = 0;
            double currentRefreshTarget = 0; // Randomized target for next refresh
            var random = new Random(); // For natural-looking refresh intervals
            
            // Helper to get a random refresh distance within the well's range
            double GetRandomRefreshDistance(PaintWell well)
            {
                var min = well.RefreshDistanceMinMm;
                var max = well.RefreshDistanceMaxMm;
                if (max <= min || max <= 0) return max; // No range, use max
                return min + random.NextDouble() * (max - min);
            }
            
            // Check if auto wash/wipe is enabled
            var autoWashWipeEnabled = FindName("AutoWashWipeCheck") is CheckBox cb && cb.IsChecked == true;
            
            // Find wash and wipe wells by name (case-insensitive)
            var washWell = _doc.PaintWells.FirstOrDefault(w => 
                w.Name.Equals("Wash", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("wash", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("rinse", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("clean", StringComparison.OrdinalIgnoreCase));
            
            var wipeWell = _doc.PaintWells.FirstOrDefault(w => 
                w.Name.Equals("Wipe", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("wipe", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("dry", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("towel", StringComparison.OrdinalIgnoreCase));

            sb.AppendLine("; Processing strokes in drawing order with color change sequences");
            sb.AppendLine($"; Auto wash/wipe: {(autoWashWipeEnabled ? "ENABLED" : "DISABLED")}");
            if (washWell != null) sb.AppendLine($"; Wash well: {washWell.Name} (swirl pattern)");
            if (wipeWell != null) sb.AppendLine($"; Wipe well: {wipeWell.Name} (zig-zag pattern)");

            // Build continuous paths from strokes while respecting color boundaries
            var paths = BuildPathsWithColorBoundaries(strokes, joinTol);
            
            foreach (var path in paths)
            {
                if (path.Count == 0) continue;

                var pathWellId = path[0].PaintWellId;
                var pathWell = pathWellId.HasValue ? _doc.PaintWells.FirstOrDefault(w => w.Id == pathWellId) : null;
                
                // Check if color changed - need to do wash/wipe/dip sequence
                if (pathWellId != currentWellId)
                {
                    // Color is changing!
                    if (currentWell != null && autoWashWipeEnabled)
                    {
                        sb.AppendLine($"; === Color change: {currentWell.Name} -> {pathWell?.Name ?? "Black"} ===");
                        
                        // Wash sequence with swirl pattern (if wash well exists and we had a previous color)
                        if (washWell != null)
                        {
                            sb.AppendLine("; Wash brush with swirl pattern");
                            GenerateWashSwirlPattern(sb, washWell, fit, zUpCmd, feedXY);
                        }
                        
                        // Wipe sequence with zig-zag pattern (if wipe well exists)
                        if (wipeWell != null)
                        {
                            sb.AppendLine("; Wipe brush with zig-zag pattern");
                            GenerateWipeZigZagPattern(sb, wipeWell, fit, zUpCmd, zDownCmd, feedXY);
                        }
                    }
                    else if (currentWell != null)
                    {
                        sb.AppendLine($"; === Color change: {currentWell.Name} -> {pathWell?.Name ?? "Black"} (auto wash/wipe disabled) ===");
                    }
                    
                    // Dip in new color (if it's a paint well, not black)
                    if (pathWell != null)
                    {
                        sb.AppendLine($"; === Paint Well: {pathWell.Name} ===");
                        GeneratePaintDipSequence(sb, pathWell, fit, zUpCmd, feedXY);
                        // Set randomized refresh target for this color
                        currentRefreshTarget = GetRandomRefreshDistance(pathWell);
                        sb.AppendLine($"; Next refresh at ~{currentRefreshTarget:F0}mm (range: {pathWell.RefreshDistanceMinMm:F0}-{pathWell.RefreshDistanceMaxMm:F0}mm)");
                    }
                    else
                    {
                        sb.AppendLine("; === No Paint Well (black) ===");
                        currentRefreshTarget = 0;
                    }
                    
                    currentWellId = pathWellId;
                    currentWell = pathWell;
                    distanceTraveled = 0;
                }

                // Draw this path
                var first = path[0];
                var startWork = BedToWork(DocToBed(first.A, fit));

                sb.AppendLine($"G0 X{Fmt(startWork.X)} Y{Fmt(startWork.Y)}");
                sb.AppendLine($"G0 Z{Fmt(zDownCmd)}");

                bool firstMove = true;
                PointMm lastPosition = startWork;

                foreach (var seg in path)
                {
                    var endWork = BedToWork(DocToBed(seg.B, fit));
                    
                    // Calculate stroke length
                    var strokeLength = Math.Sqrt(
                        Math.Pow(endWork.X - lastPosition.X, 2) + 
                        Math.Pow(endWork.Y - lastPosition.Y, 2));

                    // Check for paint refresh (within same color) - use randomized target
                    if (currentWell != null && currentRefreshTarget > 0 && strokeLength > 0.1)
                    {
                        var segmentStart = lastPosition;
                        var segmentEnd = endWork;
                        var remainingLength = strokeLength;
                        
                        while (remainingLength > 0.1)
                        {
                            var distanceUntilRefresh = currentRefreshTarget - distanceTraveled;
                            
                            if (remainingLength <= distanceUntilRefresh)
                            {
                                // Complete this segment without refresh
                                if (firstMove)
                                {
                                    sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)} F{Fmt(feedXY)}");
                                    firstMove = false;
                                }
                                else
                                {
                                    sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)}");
                                }
                                distanceTraveled += remainingLength;
                                lastPosition = segmentEnd;
                                remainingLength = 0;
                            }
                            else
                            {
                                // Need to stop partway for refresh
                                // But if remaining distance after this refresh would be less than half the max,
                                // skip the refresh and continue (avoids robotic appearance)
                                var remainingAfterRefresh = remainingLength - distanceUntilRefresh;
                                var skipThreshold = currentWell.RefreshDistanceMaxMm / 2.0;
                                
                                if (remainingAfterRefresh < skipThreshold && remainingAfterRefresh > 0.1)
                                {
                                    // Skip this refresh, just complete the segment
                                    if (firstMove)
                                    {
                                        sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)} F{Fmt(feedXY)} ; Skip refresh, only {remainingAfterRefresh:F0}mm left");
                                        firstMove = false;
                                    }
                                    else
                                    {
                                        sb.AppendLine($"G1 X{Fmt(segmentEnd.X)} Y{Fmt(segmentEnd.Y)} ; Skip refresh, only {remainingAfterRefresh:F0}mm left");
                                    }
                                    distanceTraveled += remainingLength;
                                    lastPosition = segmentEnd;
                                    remainingLength = 0;
                                    continue;
                                }
                                
                                var ratio = distanceUntilRefresh / remainingLength;
                                var breakPoint = new PointMm(
                                    segmentStart.X + (segmentEnd.X - segmentStart.X) * ratio,
                                    segmentStart.Y + (segmentEnd.Y - segmentStart.Y) * ratio);
                                
                                if (firstMove)
                                {
                                    sb.AppendLine($"G1 X{Fmt(breakPoint.X)} Y{Fmt(breakPoint.Y)} F{Fmt(feedXY)}");
                                    firstMove = false;
                                }
                                else
                                {
                                    sb.AppendLine($"G1 X{Fmt(breakPoint.X)} Y{Fmt(breakPoint.Y)}");
                                }
                                
                                distanceTraveled += distanceUntilRefresh;
                                
                                // Refresh paint (same color, no wash/wipe needed)
                                sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift for paint refresh (traveled {distanceTraveled:F1}mm)");
                                GeneratePaintDipSequence(sb, currentWell, fit, zUpCmd, feedXY);
                                sb.AppendLine($"G0 X{Fmt(breakPoint.X)} Y{Fmt(breakPoint.Y)} ; Return to position");
                                sb.AppendLine($"G0 Z{Fmt(zDownCmd)} ; Lower to continue");
                                
                                // Reset distance and get NEW random target for natural variation
                                distanceTraveled = 0;
                                currentRefreshTarget = GetRandomRefreshDistance(currentWell);
                                sb.AppendLine($"; Next refresh at ~{currentRefreshTarget:F0}mm");
                                
                                remainingLength -= distanceUntilRefresh;
                                segmentStart = breakPoint;
                                lastPosition = breakPoint;
                            }
                        }
                    }
                    else
                    {
                        // No refresh tracking - just draw
                        if (firstMove)
                        {
                            sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)} F{Fmt(feedXY)}");
                            firstMove = false;
                        }
                        else
                        {
                            sb.AppendLine($"G1 X{Fmt(endWork.X)} Y{Fmt(endWork.Y)}");
                        }
                        
                        if (currentWell != null && currentRefreshTarget > 0)
                        {
                            distanceTraveled += strokeLength;
                        }
                        lastPosition = endWork;
                    }
                }

                sb.AppendLine($"G0 Z{Fmt(zUpCmd)}");
            }
        }

        /// <summary>
        /// Builds paths from strokes, breaking at color boundaries.
        /// Strokes with the same color that are connected are grouped together.
        /// When color changes, a new path starts.
        /// </summary>
        private static List<List<LineStroke>> BuildPathsWithColorBoundaries(List<LineStroke> input, double tol)
        {
            var result = new List<List<LineStroke>>();
            List<LineStroke>? current = null;
            Guid? currentColorId = null;

            foreach (var stroke in input)
            {
                if (current == null)
                {
                    // Start first path
                    current = new List<LineStroke> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Check if color changed
                if (stroke.PaintWellId != currentColorId)
                {
                    // Color changed - start new path
                    current = new List<LineStroke> { stroke };
                    currentColorId = stroke.PaintWellId;
                    result.Add(current);
                    continue;
                }

                // Same color - check if connected to previous stroke
                var prev = current[^1];
                if (Utility.Distance(prev.B, stroke.A) <= tol)
                {
                    // Connected - add to current path
                    current.Add(stroke);
                }
                else
                {
                    // Not connected but same color - start new path (no wash/wipe needed)
                    current = new List<LineStroke> { stroke };
                    result.Add(current);
                }
            }

            return result;
        }

        private void GeneratePaintDipSequence(StringBuilder sb, PaintWell well, FitSpec fit, double zUpCmd, double feedXY)
        {
            // Paint wells should NOT be clamped to safe margin - they're physical locations
            // that may be outside the drawing area
            var wellCenter = BedToWork(DocToBed(well.Center, fit, clamp: false));
            // DipDepth is how deep to go into paint (positive value like 5mm)
            // We negate it to get the Z command (negative Z = down into paint)
            var dipDepthCmd = -well.DipDepth;

            sb.AppendLine($"; Dip in paint well: {well.Name}");
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift before moving to paint well");
            sb.AppendLine($"G0 X{Fmt(wellCenter.X)} Y{Fmt(wellCenter.Y)} ; Move to paint well");
            sb.AppendLine($"G0 Z{Fmt(dipDepthCmd)} ; Dip into paint");
            
            if (well.DwellTimeMs > 0)
            {
                // GRBL 1.1 uses G4 P<seconds> (not milliseconds!)
                // Convert ms to seconds for the dwell command
                var dwellSeconds = well.DwellTimeMs / 1000.0;
                sb.AppendLine($"G4 P{Fmt(dwellSeconds)} ; Dwell in paint ({well.DwellTimeMs}ms)");
            }
            
            // Re-assert feed rate after dwell to avoid "Feed rate not yet set" errors
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} F{Fmt(feedXY)} ; Lift from paint well");
        }

        /// <summary>
        /// Generates a swirl/spiral pattern for washing the brush in a rinse well.
        /// The pattern stays within the well bounds and makes multiple quick circular motions.
        /// </summary>
        private void GenerateWashSwirlPattern(StringBuilder sb, PaintWell washWell, FitSpec fit, double zUpCmd, double feedXY)
        {
            var wellCenter = BedToWork(DocToBed(washWell.Center, fit, clamp: false));
            var dipDepthCmd = -washWell.DipDepth;
            
            // Wash wells are rendered as circles using the smaller dimension as diameter
            // We need to stay INSIDE that circle to avoid knocking over the cup
            var circleDiameter = Math.Min(washWell.Bounds.Width, washWell.Bounds.Height);
            var circleRadius = circleDiameter / 2.0;
            
            // Use 60% of the radius for safety margin (stay well inside the cup)
            var maxRadius = circleRadius * 0.6;
            
            // Number of swirl loops and segments per loop
            const int numLoops = 3;
            const int segmentsPerLoop = 12;
            const double swirlFeedRate = 5000; // Fast swirl motion
            
            sb.AppendLine($"; === Wash swirl pattern in: {washWell.Name} ===");
            sb.AppendLine($"; Circle diameter: {circleDiameter:F1}mm, safe swirl radius: {maxRadius:F1}mm");
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift before moving to wash well");
            sb.AppendLine($"G0 X{Fmt(wellCenter.X)} Y{Fmt(wellCenter.Y)} ; Move to wash well center");
            sb.AppendLine($"G0 Z{Fmt(dipDepthCmd)} ; Lower into wash");
            
            // Generate swirl pattern - spiral outward then inward
            for (int loop = 0; loop < numLoops; loop++)
            {
                // Spiral outward
                for (int seg = 0; seg <= segmentsPerLoop; seg++)
                {
                    var t = (double)seg / segmentsPerLoop;
                    var radius = maxRadius * t; // Grow radius
                    var angle = t * 2 * Math.PI; // One full rotation per spiral
                    
                    var x = wellCenter.X + radius * Math.Cos(angle);
                    var y = wellCenter.Y + radius * Math.Sin(angle);
                    
                    if (seg == 0)
                        sb.AppendLine($"G1 X{Fmt(x)} Y{Fmt(y)} F{Fmt(swirlFeedRate)}");
                    else
                        sb.AppendLine($"G1 X{Fmt(x)} Y{Fmt(y)}");
                }
                
                // Spiral inward
                for (int seg = segmentsPerLoop; seg >= 0; seg--)
                {
                    var t = (double)seg / segmentsPerLoop;
                    var radius = maxRadius * t;
                    var angle = (1 - t) * 2 * Math.PI + Math.PI; // Reverse direction
                    
                    var x = wellCenter.X + radius * Math.Cos(angle);
                    var y = wellCenter.Y + radius * Math.Sin(angle);
                    
                    sb.AppendLine($"G1 X{Fmt(x)} Y{Fmt(y)}");
                }
            }
            
            // Return to center and lift
            sb.AppendLine($"G0 X{Fmt(wellCenter.X)} Y{Fmt(wellCenter.Y)} ; Return to center");
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} F{Fmt(feedXY)} ; Lift from wash well");
        }

        /// <summary>
        /// Generates a zig-zag wiping pattern for drying the brush on a paper towel.
        /// The pattern stays within the well bounds and makes multiple back-and-forth passes.
        /// </summary>
        private void GenerateWipeZigZagPattern(StringBuilder sb, PaintWell wipeWell, FitSpec fit, double zUpCmd, double zDownCmd, double feedXY)
        {
            // Get the well bounds in work coordinates
            var topLeft = BedToWork(DocToBed(new PointMm(wipeWell.Bounds.Left, wipeWell.Bounds.Top), fit, clamp: false));
            var bottomRight = BedToWork(DocToBed(new PointMm(wipeWell.Bounds.Right, wipeWell.Bounds.Bottom), fit, clamp: false));
            
            // Calculate the actual bounds (work coords may be inverted)
            var minX = Math.Min(topLeft.X, bottomRight.X);
            var maxX = Math.Max(topLeft.X, bottomRight.X);
            var minY = Math.Min(topLeft.Y, bottomRight.Y);
            var maxY = Math.Max(topLeft.Y, bottomRight.Y);
            
            // Add margin to stay inside the well
            var margin = 10.0; // 10mm margin from edges
            minX += margin;
            maxX -= margin;
            minY += margin;
            maxY -= margin;
            
            // Ensure we have valid bounds
            if (maxX <= minX || maxY <= minY)
            {
                // Well too small, fall back to simple dip
                GeneratePaintDipSequence(sb, wipeWell, fit, zUpCmd, feedXY);
                return;
            }
            
            // Zig-zag parameters
            const int numPasses = 4; // Number of back-and-forth passes
            const double wipeFeedRate = 4000; // Moderate speed for wiping
            var dipDepthCmd = -wipeWell.DipDepth;
            var stepY = (maxY - minY) / (numPasses * 2 - 1); // Vertical step between zig-zag lines
            
            sb.AppendLine($"; === Wipe zig-zag pattern in: {wipeWell.Name} ===");
            
            // Move to starting position (top-left of wipe area)
            var startX = minX;
            var startY = maxY;
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} ; Lift before moving to wipe area");
            sb.AppendLine($"G0 X{Fmt(startX)} Y{Fmt(startY)} ; Move to wipe start");
            sb.AppendLine($"G0 Z{Fmt(dipDepthCmd)} ; Lower onto wipe surface");
            
            // Generate zig-zag pattern
            var currentY = startY;
            var goingRight = true;
            
            for (int pass = 0; pass < numPasses * 2; pass++)
            {
                if (goingRight)
                {
                    // Move right
                    sb.AppendLine(pass == 0 
                        ? $"G1 X{Fmt(maxX)} Y{Fmt(currentY)} F{Fmt(wipeFeedRate)}"
                        : $"G1 X{Fmt(maxX)} Y{Fmt(currentY)}");
                }
                else
                {
                    // Move left
                    sb.AppendLine($"G1 X{Fmt(minX)} Y{Fmt(currentY)}");
                }
                
                // Step down for next pass (except on last pass)
                if (pass < numPasses * 2 - 1)
                {
                    currentY -= stepY;
                    currentY = Math.Max(currentY, minY); // Don't go below minY
                    sb.AppendLine($"G1 X{Fmt(goingRight ? maxX : minX)} Y{Fmt(currentY)}");
                }
                
                goingRight = !goingRight;
            }
            
            // Lift from wipe surface
            sb.AppendLine($"G0 Z{Fmt(zUpCmd)} F{Fmt(feedXY)} ; Lift from wipe surface");
        }

        private static List<List<LineStroke>> BuildPaths(List<LineStroke> input, double tol)
        {
            var result = new List<List<LineStroke>>();
            List<LineStroke>? current = null;

            foreach (var stroke in input)
            {
                if (current == null)
                {
                    current = new List<LineStroke> { stroke };
                    result.Add(current);
                    continue;
                }

                var prev = current[^1];
                if (Utility.Distance(prev.B, stroke.A) <= tol)
                {
                    current.Add(stroke);
                }
                else
                {
                    current = new List<LineStroke> { stroke };
                    result.Add(current);
                }
            }

            return result;
        }

 

          private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture); private enum FitMode { None, RotateCW }
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
            return DocToBed(p, fit, clamp: true);
        }

        private PointMm DocToBed(PointMm p, FitSpec fit, bool clamp)
        {
            var m = fit.Margin;
            var s = fit.Scale;

            double x, y;

            switch (fit.Mode)
            {
                case FitMode.RotateCW:
                    // CW rotation about doc:
                    // x' = y
                    // y' = docW - x
                    x = m + p.Y * s;
                    y = m + (fit.DocW - p.X) * s;
                    break;

                default:
                    x = m + p.X * s;
                    y = m + p.Y * s;
                    break;
            }

            if (clamp)
            {
                x = ClampBedX(x);
                y = ClampBedY(y);
            }
            else
            {
                // Still clamp to physical bed limits (0 to bed size), just not the safe margin
                x = Math.Clamp(x, 0, _bedX);
                y = Math.Clamp(y, 0, _bedY);
            }

            return new PointMm(x, y);
        }

        private double ClampBedX(double x) => Math.Clamp(x, SafeMarginMm, _bedX - SafeMarginMm);
        private double ClampBedY(double y) => Math.Clamp(y, SafeMarginMm, _bedY - SafeMarginMm);

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

        private bool EnsureConnected()
        {
            if (_grbl?.IsOpen == true)
            {
                return true;
            }

            AppendLog("Not connected to GRBL.");
            return false;
        }

        // ===== PAINTING MODE / PAINT WELLS =====

        private bool IsPaintModeEnabled => FindName("PaintModeEnabledCheck") is CheckBox cb && cb.IsChecked == true;

        private bool _suppressPaintWellUIUpdate;

        private void PaintModeEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            _lastGcode = ""; // Invalidate G-code cache
            RenderAll();
        }

        private void UpdatePaintWellsUI()
        {
            if (_suppressPaintWellUIUpdate) return;

            _suppressPaintWellUIUpdate = true;
            try
            {
                // Update paint wells list
                if (FindName("PaintWellsList") is ListBox list)
                {
                    var selectedId = (_paintWellController.SelectedWell)?.Id;
                    list.ItemsSource = null;
                    list.ItemsSource = _doc.PaintWells;
                    
                    if (selectedId.HasValue)
                    {
                        var selected = _doc.PaintWells.FirstOrDefault(w => w.Id == selectedId);
                        if (selected != null) list.SelectedItem = selected;
                    }
                }

                // Update active paint well combo
                if (FindName("ActivePaintWellCombo") is ComboBox combo)
                {
                    var activeId = _paintWellController.ActiveColorWell?.Id;
                    combo.ItemsSource = null;
                    combo.ItemsSource = _doc.PaintWells;
                    
                    if (activeId.HasValue)
                    {
                        var active = _doc.PaintWells.FirstOrDefault(w => w.Id == activeId);
                        if (active != null) combo.SelectedItem = active;
                    }
                }

                // Update selected well properties
                UpdateSelectedWellPropertiesUI();
            }
            finally
            {
                _suppressPaintWellUIUpdate = false;
            }
        }

        private void UpdateSelectedWellPropertiesUI()
        {
            var well = _paintWellController.SelectedWell;

            if (FindName("PaintWellNameBox") is TextBox nameBox)
            {
                nameBox.Text = well?.Name ?? "";
                nameBox.IsEnabled = well != null;
            }

            if (FindName("PaintWellColorPreview") is Rectangle colorPreview)
            {
                colorPreview.Fill = well != null ? new SolidColorBrush(well.Color) : Brushes.Transparent;
            }

            if (FindName("PaintWellDipDepthBox") is TextBox dipBox)
            {
                dipBox.Text = well?.DipDepth.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
                dipBox.IsEnabled = well != null;
            }

            if (FindName("PaintWellDwellBox") is TextBox dwellBox)
            {
                dwellBox.Text = well?.DwellTimeMs.ToString() ?? "";
                dwellBox.IsEnabled = well != null;
            }

            if (FindName("PaintWellRefreshMinBox") is TextBox refreshMinBox)
            {
                refreshMinBox.Text = well?.RefreshDistanceMinMm.ToString("0") ?? "";
                refreshMinBox.IsEnabled = well != null;
            }

            if (FindName("PaintWellRefreshMaxBox") is TextBox refreshMaxBox)
            {
                refreshMaxBox.Text = well?.RefreshDistanceMaxMm.ToString("0") ?? "";
                refreshMaxBox.IsEnabled = well != null;
            }
        }

        private void ActivePaintWellCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not ComboBox combo) return;

            var well = combo.SelectedItem as PaintWell;
            _paintWellController.SetActiveColor(well);
        }

        private void ClearActivePaintWell_Click(object sender, RoutedEventArgs e)
        {
            _paintWellController.SetActiveColor(null);
            if (FindName("ActivePaintWellCombo") is ComboBox combo)
            {
                combo.SelectedItem = null;
            }
        }

        private void PaintWellsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not ListBox list) return;

            var well = list.SelectedItem as PaintWell;
            _paintWellController.SelectWell(well);
            UpdateSelectedWellPropertiesUI();
        }

        private void AddPaintWellBtn_Click(object sender, RoutedEventArgs e)
        {
            // Switch to PaintWell tool so user can draw the well on canvas
            if (FindName("ToolCombo") is ComboBox toolCombo)
            {
                foreach (ComboBoxItem item in toolCombo.Items)
                {
                    if (item.Tag?.ToString() == "PaintWell")
                    {
                        toolCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            AppendLog("Select PaintWell tool: Draw a rectangle on canvas to create a paint well.");
        }

        private void RemovePaintWellBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = _paintWellController.SelectedWell;
            if (selected == null)
            {
                AppendLog("No paint well selected.");
                return;
            }

            _paintWellController.RemoveWell(selected.Id);
            _lastGcode = "";
            UpdatePaintWellsUI();
            AppendLog($"Removed paint well: {selected.Name}");
        }

        private void ClearPaintWellsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_doc.PaintWells.Count == 0) return;

            var result = MessageBox.Show(
                "Clear all paint wells? Stroke color assignments will also be cleared.",
                "Clear Paint Wells",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _paintWellController.ClearAll();
                _lastGcode = "";
                UpdatePaintWellsUI();
                AppendLog("Cleared all paint wells.");
            }
        }

        private void QuickSetupPaintWellsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Clear existing wells if any
            if (_doc.PaintWells.Count > 0)
            {
                var result = MessageBox.Show(
                    "This will clear existing paint wells. Continue?",
                    "Quick Setup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                _paintWellController.ClearAll();
            }

            // Create paint wells - positioned at bottom of canvas, evenly distributed
            // 5 wells: Red, Green, Blue, Wash (black), Wipe (light gray)
            const double wellWidth = 140;
            const double wellHeight = 100;
            const double marginFromEdge = 20;
            const int numWells = 5;

            // Calculate spacing to evenly distribute wells across the bottom
            double availableWidth = _doc.WidthMm - (2 * marginFromEdge);
            double totalWellsWidth = numWells * wellWidth;
            double spacing = (availableWidth - totalWellsWidth) / (numWells - 1);
            
            // If spacing is negative, wells are too big - reduce spacing to minimum
            if (spacing < 10) spacing = 10;

            // Position at bottom of canvas
            double startY = _doc.HeightMm - wellHeight - marginFromEdge;

            // Red well
            _paintWellController.CreateWell(
                new System.Windows.Rect(marginFromEdge, startY, wellWidth, wellHeight),
                System.Windows.Media.Colors.Red,
                "Red");

            // Green well
            _paintWellController.CreateWell(
                new System.Windows.Rect(marginFromEdge + (wellWidth + spacing) * 1, startY, wellWidth, wellHeight),
                System.Windows.Media.Colors.Green,
                "Green");

            // Blue well
            _paintWellController.CreateWell(
                new System.Windows.Rect(marginFromEdge + (wellWidth + spacing) * 2, startY, wellWidth, wellHeight),
                System.Windows.Media.Colors.Blue,
                "Blue");

            // Wash well (black - for cleaning/washing brush)
            _paintWellController.CreateWell(
                new System.Windows.Rect(marginFromEdge + (wellWidth + spacing) * 3, startY, wellWidth, wellHeight),
                System.Windows.Media.Colors.Black,
                "Wash");

            // Wipe well (light gray - for wiping/drying brush)
            _paintWellController.CreateWell(
                new System.Windows.Rect(marginFromEdge + (wellWidth + spacing) * 4, startY, wellWidth, wellHeight),
                System.Windows.Media.Color.FromRgb(192, 192, 192),
                "Wipe");

            _lastGcode = "";
            UpdatePaintWellsUI();
            RenderAll();
            AppendLog($"Created 5 paint wells at bottom: Red, Green, Blue, Wash, Wipe ({wellWidth}x{wellHeight}mm each).");
        }

        private void PaintWellNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not TextBox tb) return;
            if (_paintWellController.SelectedWell == null) return;

            _paintWellController.UpdateSelectedWell(name: tb.Text);
            _lastGcode = "";

            // Refresh the list display
            _suppressPaintWellUIUpdate = true;
            if (FindName("PaintWellsList") is ListBox list)
            {
                list.Items.Refresh();
            }
            if (FindName("ActivePaintWellCombo") is ComboBox combo)
            {
                combo.Items.Refresh();
            }
            _suppressPaintWellUIUpdate = false;
        }

        private void PaintWellColorPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_paintWellController.SelectedWell == null)
            {
                AppendLog("No paint well selected.");
                return;
            }

            // Simple color picker using Windows color dialog
            var colorDialog = new System.Windows.Forms.ColorDialog
            {
                Color = System.Drawing.Color.FromArgb(
                    _paintWellController.SelectedWell.Color.A,
                    _paintWellController.SelectedWell.Color.R,
                    _paintWellController.SelectedWell.Color.G,
                    _paintWellController.SelectedWell.Color.B),
                FullOpen = true
            };

            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var newColor = Color.FromArgb(
                    colorDialog.Color.A,
                    colorDialog.Color.R,
                    colorDialog.Color.G,
                    colorDialog.Color.B);

                _paintWellController.UpdateSelectedWell(color: newColor);
                _lastGcode = "";
                UpdateSelectedWellPropertiesUI();

                // Refresh lists
                _suppressPaintWellUIUpdate = true;
                if (FindName("PaintWellsList") is ListBox list) list.Items.Refresh();
                if (FindName("ActivePaintWellCombo") is ComboBox combo) combo.Items.Refresh();
                _suppressPaintWellUIUpdate = false;
            }
        }

        private void PaintWellDipDepthBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not TextBox tb) return;
            if (_paintWellController.SelectedWell == null) return;

            var value = ParseDouble(tb.Text, _paintWellController.SelectedWell.DipDepth);
            if (value > 0)
            {
                _paintWellController.UpdateSelectedWell(dipDepth: value);
                _lastGcode = "";
            }
        }

        private void PaintWellDwellBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not TextBox tb) return;
            if (_paintWellController.SelectedWell == null) return;

            if (int.TryParse(tb.Text, out var value) && value >= 0)
            {
                _paintWellController.UpdateSelectedWell(dwellTimeMs: value);
                _lastGcode = "";
            }
        }

        private void PaintWellRefreshMinBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not TextBox tb) return;
            if (_paintWellController.SelectedWell == null) return;

            if (double.TryParse(tb.Text, out var value) && value >= 0)
            {
                _paintWellController.UpdateSelectedWell(refreshDistanceMinMm: value);
                _lastGcode = "";
            }
        }

        private void PaintWellRefreshMaxBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not TextBox tb) return;
            if (_paintWellController.SelectedWell == null) return;

            if (double.TryParse(tb.Text, out var value) && value >= 0)
            {
                _paintWellController.UpdateSelectedWell(refreshDistanceMaxMm: value);
                _lastGcode = "";
            }
        }

        private void ApplyPaintToSelection_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectionController.HasSelection)
            {
                AppendLog("No strokes selected. Use the Select tool to select strokes first.");
                return;
            }

            var well = _paintWellController.SelectedWell;
            if (well == null)
            {
                AppendLog("No paint well selected in the list.");
                return;
            }

            // Apply the selected well to all selected strokes
            foreach (var idx in _selectionController.SelectedIndices)
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    _doc.Strokes[idx].PaintWellId = well.Id;
                }
            }

            _lastGcode = "";
            RenderAll();
            AppendLog($"Applied '{well.Name}' color to {_selectionController.SelectedIndices.Count} stroke(s).");
        }

        // ===== PROJECT FILE OPERATIONS =====

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private void NewProjectBtn_Click(object sender, RoutedEventArgs e)
        {
            // Confirm if there are unsaved changes
            if (_doc.Strokes.Count > 0 || _doc.PaintWells.Count > 0)
            {
                var result = MessageBox.Show(
                    "Create a new project? Any unsaved changes will be lost.",
                    "New Project",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;
            }

            // Reset document
            _doc = new PlotDocument(Settings.Default.bedX, Settings.Default.bedY);
            _undo.Clear();
            _lastGcode = "";
            _currentProjectPath = null;
            _imageService.Clear();
            _workingAreaManager.Clear();
            _selectionController.ClearSelection();
            _paintWellController.ClearAll();

            UpdatePaintWellsUI();
            UpdateReferenceUiState();
            UpdateWorkingAreaStatus();
            UpdateWindowTitle();
            RenderAll();

            AppendLog("New project created.");
        }

        private void OpenProjectBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "NVS Plotter Project (*.nvsp)|*.nvsp|All files (*.*)|*.*",
                Title = "Open Project"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    LoadProjectFromFile(dlg.FileName);
                }
                catch (Exception ex)
                {
                    AppendLog($"Failed to open project: {ex.Message}");
                    MessageBox.Show($"Failed to open project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveProjectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentProjectPath))
            {
                // No existing path, use Save As
                SaveAsProjectBtn_Click(sender, e);
            }
            else
            {
                try
                {
                    SaveProjectToFile(_currentProjectPath);
                }
                catch (Exception ex)
                {
                    AppendLog($"Failed to save project: {ex.Message}");
                    MessageBox.Show($"Failed to save project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveAsProjectBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "NVS Plotter Project (*.nvsp)|*.nvsp|All files (*.*)|*.*",
                Title = "Save Project As",
                FileName = string.IsNullOrEmpty(_currentProjectPath) 
                    ? "project.nvsp" 
                    : System.IO.Path.GetFileName(_currentProjectPath)
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    SaveProjectToFile(dlg.FileName);
                    _currentProjectPath = dlg.FileName;
                    UpdateWindowTitle();
                }
                catch (Exception ex)
                {
                    AppendLog($"Failed to save project: {ex.Message}");
                    MessageBox.Show($"Failed to save project:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SaveProjectToFile(string filePath)
        {
            var projectFile = new ProjectFile
            {
                Version = 1,
                WidthMm = _doc.WidthMm,
                HeightMm = _doc.HeightMm
            };

            // Convert strokes to serializable format
            foreach (var stroke in _doc.Strokes)
            {
                projectFile.Strokes.Add(new StrokeData
                {
                    Ax = stroke.A.X,
                    Ay = stroke.A.Y,
                    Bx = stroke.B.X,
                    By = stroke.B.Y,
                    PaintWellId = stroke.PaintWellId
                });
            }

            // Convert paint wells to serializable format
            foreach (var well in _doc.PaintWells)
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

            var json = JsonSerializer.Serialize(projectFile, _jsonOptions);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            AppendLog($"Project saved: {filePath} ({_doc.Strokes.Count} strokes, {_doc.PaintWells.Count} paint wells)");
        }

        private void LoadProjectFromFile(string filePath)
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var projectFile = JsonSerializer.Deserialize<ProjectFile>(json, _jsonOptions);

            if (projectFile == null)
            {
                throw new InvalidOperationException("Failed to parse project file.");
            }

            // Create new document with loaded dimensions
            _doc = new PlotDocument(projectFile.WidthMm, projectFile.HeightMm);

            // Load paint wells first (strokes reference them by ID)
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
                _doc.PaintWells.Add(well);
            }

            // Load strokes
            foreach (var strokeData in projectFile.Strokes)
            {
                var stroke = new LineStroke(
                    new PointMm(strokeData.Ax, strokeData.Ay),
                    new PointMm(strokeData.Bx, strokeData.By),
                    strokeData.PaintWellId);
                _doc.Strokes.Add(stroke);
            }

            // Reset state
            _undo.Clear();
            _lastGcode = "";
            _currentProjectPath = filePath;
            _imageService.Clear();
            _workingAreaManager.Clear();
            _selectionController.ClearSelection();

            // Update UI
            UpdatePaintWellsUI();
            UpdateReferenceUiState();
            UpdateWorkingAreaStatus();
            UpdateWindowTitle();
            RenderAll();

            AppendLog($"Project loaded: {filePath} ({_doc.Strokes.Count} strokes, {_doc.PaintWells.Count} paint wells)");
        }

        private void UpdateWindowTitle()
        {
            var projectName = string.IsNullOrEmpty(_currentProjectPath)
                ? "Untitled"
                : System.IO.Path.GetFileName(_currentProjectPath);
            Title = $"NVS Plotter - {projectName}";
        }

    }

 }
