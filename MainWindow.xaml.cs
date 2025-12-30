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
        private TextBlock? _workingAreaSizeLabel;

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
        private readonly ClipboardService _clipboardService = new();

        // Undo - stores group sizes for multi-stroke operations (e.g., rectangles, circles, freehand)
        private readonly Stack<int> _undoGroupSizes = new();
        private int _strokeCountBeforeOperation;

        // Pan state (middle mouse)
        private bool _isPanning;
        private Point _panStartMouse;
        private double _panStartH;
        private double _panStartV;

        // Zoom tool drag state
        private bool _isZoomDragging;
        private Point _zoomDragStart;
        private double _zoomDragStartZoom;
        private Point _zoomDragCanvasPoint; // Point on canvas to zoom around
        private Point _zoomDragViewportOffset; // Where in viewport the click occurred

        // Subdivide operation state
        private bool _isSubdividing;
        private Point _subdivideStartMouse;
        private int _subdivideCurrentCount = 2; // Current number of points (2 = original line with start/end only)
        private List<LineStroke>? _subdivideOriginalStrokes; // Original strokes before subdivision
        private List<int>? _subdivideStrokeIndices; // Indices of strokes being subdivided
        private Cursor? _subdivideOriginalCursor; // Cursor to restore after subdivision
        private double _subdivideAccumulatedDelta; // Accumulated vertical movement for smooth subdivision
        private int _subdivideScreenX; // Fixed X position for cursor (screen coordinates)
        private HashSet<Guid>? _subdivideOriginalGroupIds; // Original group IDs (for reference)
        private HashSet<Guid>? _subdivideNewlyAddedGroupIds; // Group IDs we added to intermediate display during THIS operation
        
        // P/Invoke for cursor manipulation during subdivision
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

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
        private Line? _snapIndicatorLineH; // Horizontal dashed line through indicator
        private Line? _snapIndicatorLineV; // Vertical dashed line through indicator
        private const double SNAP_INDICATOR_SIZE = 24.0; // Large indicator for better visibility
        private const double SNAP_INDICATOR_LINE_EXTEND = 12.0; // How far the dashed lines extend beyond the circle
        
        // Grid snap indicator extras (full-length crosshair and center dot)
        private Line? _gridSnapCrosshairH; // Full-width horizontal line for grid snap
        private Line? _gridSnapCrosshairV; // Full-height vertical line for grid snap
        private Ellipse? _gridSnapCenterDot; // Tiny center dot for grid snap

        private readonly DispatcherTimer _filterThrottle;
        private ConsoleWindow? _consoleWindow;
        private ToolsWindow? _toolsWindow;

        // Project file state
        private string? _currentProjectPath;

        // Track which GroupIds should show intermediate points (toggle per selection)
        private readonly HashSet<Guid> _showIntermediatePointsForGroups = new();

        /// <summary>
        /// Recursively finds all descendant group IDs for the given parent group IDs.
        /// This traverses the parent-child hierarchy to any depth (parent -> child -> grandchild -> etc.)
        /// </summary>
        /// <param name="parentGroupIds">The parent group IDs to find descendants for</param>
        /// <returns>A set containing all parent IDs plus all their descendants at any depth</returns>
        private HashSet<Guid> GetAllDescendantGroupIds(IEnumerable<Guid> parentGroupIds)
        {
            var result = new HashSet<Guid>(parentGroupIds);
            var toProcess = new Queue<Guid>(parentGroupIds);
            
            while (toProcess.Count > 0)
            {
                var currentParent = toProcess.Dequeue();
                
                // Find all groups whose ParentGroupId matches the current parent
                foreach (var stroke in _doc.Strokes)
                {
                    if (stroke.ParentGroupId == currentParent && stroke.GroupId.HasValue)
                    {
                        var childGroupId = stroke.GroupId.Value;
                        if (result.Add(childGroupId)) // Only process if not already seen
                        {
                            toProcess.Enqueue(childGroupId); // This child might have its own children
                        }
                    }
                }
            }
            
            return result;
        }

        /// <summary>
        /// Checks if the given group ID or any of its ancestors have intermediate points enabled.
        /// This walks up the parent chain to check for inherited intermediate point display.
        /// </summary>
        /// <param name="groupId">The group ID to check</param>
        /// <returns>True if this group or any ancestor has intermediate points enabled</returns>
        private bool HasAncestorWithIntermediatePointsEnabled(Guid groupId)
        {
            // First check if this group itself has it enabled
            if (_showIntermediatePointsForGroups.Contains(groupId))
                return true;
            
            // Find the ParentGroupId for this group (any stroke with this GroupId will have the same parent)
            var parentGroupId = _doc.Strokes
                .FirstOrDefault(s => s.GroupId == groupId)?.ParentGroupId;
            
            // Walk up the ancestor chain
            var visited = new HashSet<Guid> { groupId }; // Prevent infinite loops
            while (parentGroupId.HasValue && !visited.Contains(parentGroupId.Value))
            {
                if (_showIntermediatePointsForGroups.Contains(parentGroupId.Value))
                    return true;
                
                visited.Add(parentGroupId.Value);
                
                // Find the next parent up the chain
                parentGroupId = _doc.Strokes
                    .FirstOrDefault(s => s.GroupId == parentGroupId.Value)?.ParentGroupId;
            }
            
            return false;
        }

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

            // Subscribe to shape completion to auto-select the drawn shape
            _shapeController.ShapeCompleted += OnShapeCompleted;

            // Track stroke count for grouped undo
            _strokeCountBeforeOperation = 0;

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
             InitializeToolsWindow();
             UpdatePaintWellsUI();
             InitializeThemeToggle();
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

            // Respect ScrollViewer padding so content never appears beneath the padded area.
            var padLeft = CanvasScroll?.Padding.Left ?? 0.0;
            var padTop = CanvasScroll?.Padding.Top ?? 0.0;
            var padRight = CanvasScroll?.Padding.Right ?? 0.0;
            var padBottom = CanvasScroll?.Padding.Bottom ?? 0.0;

            // With LayoutTransform, the canvas size is automatically scaled for layout purposes.
            // We set the margin to include both a fixed internal canvas padding and the ScrollViewer padding.
            var margin = new Thickness(canvasPadding + padLeft, canvasPadding + padTop, canvasPadding + padRight, canvasPadding + padBottom);
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
            _undoGroupSizes.Clear();
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
            // Wheel zooms towards the mouse position (like the zoom tool drag)
            var delta = e.Delta > 0 ? 0.1 : -0.1;
            var newZoom = Math.Clamp(ZoomSlider.Value + delta, ZoomSlider.Minimum, ZoomSlider.Maximum);
            
            // Capture mouse position for zoom-around-point calculation
            // Store the canvas point we're zooming around (mouse position on canvas)
            _zoomDragCanvasPoint = e.GetPosition(DrawCanvas);
            // Store where in the viewport the wheel occurred (relative to viewport, not content)
            _zoomDragViewportOffset = e.GetPosition(CanvasScroll);
            
            // Apply zoom centered on the mouse position
            ZoomAroundPoint(newZoom, _zoomDragCanvasPoint);
            
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
            _undoGroupSizes.Clear();
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

            // Show the console window when connecting
            ShowConsoleWindow();

            try
            {
                _grbl?.Dispose();
                _grbl = new GrblConnection(port, baud, AppendLog);

                UpdateConnStatus();
                await _grbl.OpenAsync();

                AppendLog("Connected to GRBL.");
                await LoadGrblSettingsAsync();

                // Auto-home if enabled
                if (FindName("AutoHomeOnConnectCheck") is CheckBox autoHomeCheck && autoHomeCheck.IsChecked == true)
                {
                    AppendLog("Auto-homing...");
                    try
                    {
                        await _grbl.SendLineWaitOkAsync("$H", TimeSpan.FromSeconds(30), _sendCts?.Token ?? CancellationToken.None);
                        _isHomed = true;
                        AppendLog("Auto-home completed.");
                    }
                    catch (Exception homeEx)
                    {
                        AppendLog("Auto-home failed: " + homeEx.Message);
                    }
                }
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

        /// <summary>
        /// Shows the console window at the bottom-right of the current screen.
        /// </summary>
        private void ShowConsoleWindow()
        {
            if (_consoleWindow == null)
            {
                InitializeConsoleWindow();
            }

            if (!_consoleWindow!.IsVisible)
            {
                // Set owner when showing (main window is now visible)
                if (_consoleWindow.Owner == null)
                {
                    _consoleWindow.Owner = this;
                }
                
                // Find the screen that contains the main window
                var mainWindowCenter = new System.Drawing.Point(
                    (int)(Left + Width / 2),
                    (int)(Top + Height / 2));
                var currentScreen = System.Windows.Forms.Screen.FromPoint(mainWindowCenter);
                var workArea = currentScreen.WorkingArea;
                
                // Position at bottom-right corner of the same screen as the main window
                var desiredLeft = workArea.Right - _consoleWindow.Width;
                var desiredTop = workArea.Bottom - _consoleWindow.Height;
                
                _consoleWindow.Left = desiredLeft;
                _consoleWindow.Top = desiredTop;
                
                _consoleWindow.Show();
            }
            
            _consoleWindow.Activate();
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

            // Determine how many strokes to undo
            int countToUndo = 1;
            if (_undoGroupSizes.Count > 0)
            {
                countToUndo = _undoGroupSizes.Pop();
            }

            // Remove the strokes (up to the group size or remaining strokes)
            countToUndo = Math.Min(countToUndo, _doc.Strokes.Count);
            
            // Collect GroupIds of strokes being removed
            var removedGroupIds = new HashSet<Guid>();
            for (int i = 0; i < countToUndo; i++)
            {
                var stroke = _doc.Strokes[_doc.Strokes.Count - 1];
                if (stroke.GroupId.HasValue)
                {
                    removedGroupIds.Add(stroke.GroupId.Value);
                }
                _doc.Strokes.RemoveAt(_doc.Strokes.Count - 1);
            }
            
            // Clean up _showIntermediatePointsForGroups - remove any GroupIds that no longer have strokes
            var existingGroupIds = new HashSet<Guid>(_doc.Strokes.Where(s => s.GroupId.HasValue).Select(s => s.GroupId!.Value));
            _showIntermediatePointsForGroups.RemoveWhere(id => !existingGroupIds.Contains(id));

            _lastGcode = "";
            RenderAll();
        }

        /// <summary>
        /// Called when a shape (rectangle, circle, polyline, bezier, poly-bezier, freedraw) is completed.
        /// Automatically selects the newly created shape and switches to the Select tool.
        /// </summary>
        private void OnShapeCompleted(int strokeCount)
        {
            if (strokeCount <= 0) return;

            // Select the newly created strokes (the last N strokes in the document)
            var startIndex = _doc.Strokes.Count - strokeCount;
            if (startIndex < 0) startIndex = 0;

            var indices = Enumerable.Range(startIndex, strokeCount).ToList();
            
            // Clear any existing selection and select the new shape
            _selectionController.ClearSelection();
            _selectionController.SelectStrokes(indices);

            // Switch to Select tool so user can immediately manipulate the shape
            if (_toolsWindow != null)
            {
                _toolsWindow.SelectTool(ToolMode.Select);
            }

            RenderAll();
            AppendLog($"Shape created with {strokeCount} stroke(s) - auto-selected.");
        }

        // ===== CLIPBOARD OPERATIONS =====

        private void PerformCopy()
        {
            if (!_selectionController.HasSelection)
            {
                AppendLog("Nothing selected to copy.");
                return;
            }

            var strokes = _selectionController.GetSelectedStrokes();
            var wells = _selectionController.GetSelectedPaintWells();
            var bounds = _selectionController.SelectionBounds;

            _clipboardService.CopyToClipboard(strokes, wells, bounds);
            AppendLog($"Copied {strokes.Count} stroke(s) and {wells.Count} paint well(s) to clipboard.");
        }

        private void PerformCut()
        {
            if (!_selectionController.HasSelection)
            {
                AppendLog("Nothing selected to cut.");
                return;
            }

            // First copy
            var strokes = _selectionController.GetSelectedStrokes();
            var wells = _selectionController.GetSelectedPaintWells();
            var bounds = _selectionController.SelectionBounds;
            _clipboardService.CopyToClipboard(strokes, wells, bounds);

            // Then delete
            _selectionController.DeleteSelection();
            _lastGcode = "";

            AppendLog($"Cut {strokes.Count} stroke(s) and {wells.Count} paint well(s) to clipboard.");
        }

        private void PerformPaste()
        {
            if (!_clipboardService.HasClipboardData())
            {
                AppendLog("Clipboard is empty or contains incompatible data.");
                return;
            }

            var result = _clipboardService.PasteFromClipboard();
            if (result == null)
            {
                AppendLog("Failed to paste from clipboard.");
                return;
            }

            var (strokes, wells) = result.Value;

            // Add strokes to document
            var startIndex = _doc.Strokes.Count;
            foreach (var stroke in strokes)
            {
                _doc.Strokes.Add(stroke);
            }

            // Add paint wells to document
            foreach (var well in wells)
            {
                _doc.PaintWells.Add(well);
            }

            // Select the pasted items
            _selectionController.ClearSelection();
            
            // Select strokes
            var indices = Enumerable.Range(startIndex, strokes.Count).ToList();
            _selectionController.SelectStrokes(indices);

            // Select paint wells
            _selectionController.SelectPaintWells(wells.Select(w => w.Id));

            // Switch to Select tool to show the pasted items
            if (_toolsWindow != null)
            {
                _toolsWindow.SelectTool(ToolMode.Select);
            }

            _lastGcode = "";
            UpdatePaintWellsUI();
            RenderAll();

            AppendLog($"Pasted {strokes.Count} stroke(s) and {wells.Count} paint well(s).");
        }

        private void PerformSelectAll()
        {
            _selectionController.ClearSelection();

            // Select all strokes
            var allIndices = Enumerable.Range(0, _doc.Strokes.Count);
            _selectionController.SelectStrokes(allIndices);

            // Select all paint wells
            _selectionController.SelectPaintWells(_doc.PaintWells.Select(w => w.Id));

            // Switch to Select tool to show the selection
            if (_toolsWindow != null)
            {
                _toolsWindow.SelectTool(ToolMode.Select);
            }

            RenderAll();
            AppendLog($"Selected all: {_doc.Strokes.Count} stroke(s) and {_doc.PaintWells.Count} paint well(s).");
        }

        // ===== CONTEXT MENU HANDLERS =====

        private void CanvasContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Update menu item enabled states based on current selection
            var hasSelection = _selectionController.HasSelection;
            var hasClipboard = _clipboardService.HasClipboardData();
            var hasContent = _doc.Strokes.Count > 0 || _doc.PaintWells.Count > 0;

            // Check if selection contains grouped strokes (for intermediate points toggle and separate)
            var hasGroupedStrokes = false;
            var anyGroupShowingIntermediate = false;
            var hasMultipleStrokes = _selectionController.SelectedIndices.Count > 1;
            var hasMultipleGroups = false;
            
            if (hasSelection)
            {
                var selectedGroupIds = new HashSet<Guid>();
                foreach (var idx in _selectionController.SelectedIndices)
                {
                    if (idx >= 0 && idx < _doc.Strokes.Count)
                    {
                        var stroke = _doc.Strokes[idx];
                        if (stroke.GroupId.HasValue)
                        {
                            selectedGroupIds.Add(stroke.GroupId.Value);
                            hasGroupedStrokes = true;
                        }
                    }
                }
                
                // Find ALL descendant groups recursively for intermediate points display check
                var allGroupIds = GetAllDescendantGroupIds(selectedGroupIds);
                anyGroupShowingIntermediate = allGroupIds.Any(id => _showIntermediatePointsForGroups.Contains(id));
                hasMultipleGroups = selectedGroupIds.Count > 1;
            }

            if (sender is not ContextMenu menu) return;

            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                switch (item.Name)
                {
                    case "CutMenuItem":
                    case "CopyMenuItem":
                    case "DeleteMenuItem":
                    case "DeselectMenuItem":
                        item.IsEnabled = hasSelection;
                        break;
                    case "PasteMenuItem":
                        item.IsEnabled = hasClipboard;
                        break;
                    case "SelectAllMenuItem":
                        item.IsEnabled = hasContent;
                        break;
                    case "GroupMenuItem":
                        // Enable Group when multiple strokes are selected (can group ungrouped strokes or merge groups)
                        item.IsEnabled = hasMultipleStrokes;
                        break;
                    case "UngroupMenuItem":
                        // Enable Ungroup when grouped strokes are selected
                        item.IsEnabled = hasGroupedStrokes;
                        break;
                    case "SeparateFromGroupMenuItem":
                        item.IsEnabled = hasGroupedStrokes;
                        break;
                    case "SubdivideMenuItem":
                        // Enable Subdivide when strokes are selected (can subdivide any stroke)
                        item.IsEnabled = hasSelection && _selectionController.SelectedIndices.Count > 0;
                        break;
                    case "ToggleIntermediatePointsMenuItem":
                        item.IsEnabled = hasGroupedStrokes;
                        item.IsChecked = anyGroupShowingIntermediate;
                        break;
                }
            }
        }

        private void CutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PerformCut();
        }

        private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PerformCopy();
        }

        private void PasteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PerformPaste();
        }

        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_selectionController.HasSelection)
            {
                _selectionController.DeleteSelection();
                _lastGcode = "";
                AppendLog("Deleted selected items.");
            }
        }

        private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PerformSelectAll();
        }

        private void DeselectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _selectionController.ClearSelection();
            AppendLog("Selection cleared.");
        }

        private void GroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectionController.HasSelection || _selectionController.SelectedIndices.Count < 2)
            {
                AppendLog("Select at least 2 strokes to group.");
                return;
            }

            var selectedIndices = _selectionController.SelectedIndices.OrderBy(i => i).ToList();
            var newGroupId = Guid.NewGuid();
            var count = 0;

            for (int i = 0; i < selectedIndices.Count; i++)
            {
                var idx = selectedIndices[i];
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    var stroke = _doc.Strokes[idx];
                    _doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                    {
                        PaintWellId = stroke.PaintWellId,
                        GroupId = newGroupId,
                        IsGroupStart = i == 0,
                        IsGroupEnd = i == selectedIndices.Count - 1
                    };
                    count++;
                }
            }

            _lastGcode = ""; // Invalidate G-code cache
            RenderAll();
            AppendLog($"Grouped {count} stroke(s) into a single object.");
        }

        private void UngroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectionController.HasSelection)
            {
                AppendLog("No strokes selected to ungroup.");
                return;
            }

            var selectedIndices = _selectionController.SelectedIndices.ToList();
            var ungroupedCount = 0;

            foreach (var idx in selectedIndices)
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    var stroke = _doc.Strokes[idx];
                    if (stroke.GroupId.HasValue)
                    {
                        // Remove from group - make it a standalone stroke
                        _doc.Strokes[idx] = new LineStroke(stroke.A, stroke.B)
                        {
                            PaintWellId = stroke.PaintWellId,
                            GroupId = null,
                            IsGroupStart = false,
                            IsGroupEnd = false
                        };
                        ungroupedCount++;
                    }
                }
            }

            if (ungroupedCount > 0)
            {
                _lastGcode = ""; // Invalidate G-code cache
                RenderAll();
                AppendLog($"Ungrouped {ungroupedCount} stroke(s) into individual objects.");
            }
            else
            {
                AppendLog("Selected strokes are not part of any group.");
            }
        }

        private void SeparateFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectionController.HasSelection)
            {
                AppendLog("No strokes selected to separate.");
                return;
            }

            if (_selectionController.SeparateFromGroup())
            {
                _lastGcode = ""; // Invalidate G-code cache
                var count = _selectionController.SelectedIndices.Count;
                AppendLog($"Separated {count} stroke(s) into a standalone object.");
            }
            else
            {
                AppendLog("Selected strokes are not part of a group (already standalone).");
            }
        }

        private void ToggleIntermediatePointsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectionController.HasSelection) return;

            // Get all unique GroupIds from selected strokes
            var selectedGroupIds = new HashSet<Guid>();
            foreach (var idx in _selectionController.SelectedIndices)
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    var stroke = _doc.Strokes[idx];
                    if (stroke.GroupId.HasValue)
                    {
                        selectedGroupIds.Add(stroke.GroupId.Value);
                    }
                }
            }

            if (selectedGroupIds.Count == 0)
            {
                AppendLog("No grouped strokes in selection.");
                return;
            }

            // Find ALL descendant groups recursively (children, grandchildren, great-grandchildren, etc.)
            var allGroupIds = GetAllDescendantGroupIds(selectedGroupIds);

            // Check if any of the selected groups (or descendants) are currently showing intermediate points
            var anyShowing = allGroupIds.Any(id => _showIntermediatePointsForGroups.Contains(id));

            if (anyShowing)
            {
                // Hide intermediate points for all groups (parent and all descendants)
                foreach (var groupId in allGroupIds)
                {
                    _showIntermediatePointsForGroups.Remove(groupId);
                }
                AppendLog($"Hidden intermediate points for {allGroupIds.Count} group(s).");
            }
            else
            {
                // Show intermediate points for all groups (parent and all descendants)
                foreach (var groupId in allGroupIds)
                {
                    _showIntermediatePointsForGroups.Add(groupId);
                }
                AppendLog($"Showing intermediate points for {allGroupIds.Count} group(s).");
            }

            RenderAll();
        }

        private void SubdivideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectionController.HasSelection)
            {
                AppendLog("No strokes selected to subdivide.");
                return;
            }

            // Get selected stroke indices
            var indices = _selectionController.SelectedIndices.OrderBy(i => i).ToList();
            if (indices.Count == 0)
            {
                AppendLog("No strokes selected to subdivide.");
                return;
            }

            // Store original strokes for subdivision preview
            _subdivideOriginalStrokes = new List<LineStroke>();
            _subdivideStrokeIndices = indices;
            
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    var stroke = _doc.Strokes[idx];
                    _subdivideOriginalStrokes.Add(stroke);
                }
            }

            if (_subdivideOriginalStrokes.Count == 0)
            {
                AppendLog("No valid strokes to subdivide.");
                return;
            }

            // Enable intermediate points display for selected groups during subdivision
            // Track which group IDs we're newly adding so we can remove them on cancel
            _subdivideOriginalGroupIds = new HashSet<Guid>();
            _subdivideNewlyAddedGroupIds = new HashSet<Guid>();
            foreach (var idx in indices)
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    var stroke = _doc.Strokes[idx];
                    if (stroke.GroupId.HasValue)
                    {
                        _subdivideOriginalGroupIds.Add(stroke.GroupId.Value);
                        // Only track as "newly added" if it wasn't already showing
                        if (!_showIntermediatePointsForGroups.Contains(stroke.GroupId.Value))
                        {
                            _subdivideNewlyAddedGroupIds.Add(stroke.GroupId.Value);
                        }
                        _showIntermediatePointsForGroups.Add(stroke.GroupId.Value);
                    }
                }
            }

            // Start subdivision mode
            _isSubdividing = true;
            _subdivideStartMouse = Mouse.GetPosition(CanvasScroll);
            _subdivideCurrentCount = 2; // Start with original (2 points = 1 segment)
            _subdivideAccumulatedDelta = 0; // Reset accumulated movement
            _subdivideOriginalCursor = DrawCanvas.Cursor;
            DrawCanvas.Cursor = Cursors.SizeNS;
            DrawCanvas.CaptureMouse();
            
            // Store the screen X position to lock horizontal movement
            // Get the current cursor position in screen coordinates
            if (GetCursorPos(out POINT cursorPos))
            {
                _subdivideScreenX = cursorPos.X;
            }
            else
            {
                // Fallback: calculate from WPF coordinates if P/Invoke fails
                var screenPoint = CanvasScroll.PointToScreen(_subdivideStartMouse);
                _subdivideScreenX = (int)screenPoint.X;
            }

            AppendLog("Subdivide mode: Drag up/down to adjust points. Click or press Escape to finish.");
            RenderAll();
        }

        private void UpdateSubdivision(Point currentMouse)
        {
            if (!_isSubdividing || _subdivideOriginalStrokes == null || _subdivideStrokeIndices == null) return;
            
            // Safety check: if _subdivideScreenX is 0 or invalid, recalculate it
            if (_subdivideScreenX <= 0)
            {
                var screenPoint = CanvasScroll.PointToScreen(currentMouse);
                _subdivideScreenX = (int)screenPoint.X;
            }

            // Get current screen cursor position
            GetCursorPos(out POINT screenPos);
            
            // Get screen bounds for wrapping
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(screenPos.X, screenPos.Y));
            var screenTop = screen.Bounds.Top + 50; // Leave some margin
            var screenBottom = screen.Bounds.Bottom - 50;
            
            // Calculate vertical delta from last position
            var dy = _subdivideStartMouse.Y - currentMouse.Y;
            
            // Accumulate the delta for smooth subdivision
            _subdivideAccumulatedDelta += dy;
            
            // Check for cursor wrapping at screen edges
            bool wrapped = false;
            int newY = screenPos.Y;
            
            if (screenPos.Y <= screenTop)
            {
                // Wrap to bottom
                newY = screenBottom - 10;
                wrapped = true;
            }
            else if (screenPos.Y >= screenBottom)
            {
                // Wrap to top
                newY = screenTop + 10;
                wrapped = true;
            }
            
            // Lock cursor to fixed X position and apply wrapping
            // Only move cursor if X position drifted or we need to wrap
            bool needsXCorrection = Math.Abs(screenPos.X - _subdivideScreenX) > 1;
            if (needsXCorrection || wrapped)
            {
                SetCursorPos(_subdivideScreenX, wrapped ? newY : screenPos.Y);
            }
            
            // Update start position for next delta calculation
            if (wrapped)
            {
                // After wrapping, reset the start mouse to the new wrapped position
                // Convert screen position to canvas scroll position
                var canvasScrollScreenPos = CanvasScroll.PointToScreen(new Point(0, 0));
                _subdivideStartMouse = new Point(
                    _subdivideScreenX - canvasScrollScreenPos.X,
                    newY - canvasScrollScreenPos.Y
                );
            }
            else
            {
                _subdivideStartMouse = currentMouse;
            }
            
            // Calculate new point count based on accumulated movement
            const double sensitivity = 20.0; // Pixels per point change (higher = less sensitive)
            var newCount = 2 + (int)(_subdivideAccumulatedDelta / sensitivity);
            newCount = Math.Clamp(newCount, 2, 100); // Cap at 100 points max, minimum 2

            // Only update if count changed
            if (newCount == _subdivideCurrentCount) return;
            
            _subdivideCurrentCount = newCount;

            // Apply subdivision to document
            ApplySubdivision(newCount);
            
            AppendLog($"Subdivide: {newCount} points ({newCount - 1} segments)");
            RenderAll();
        }

        private int GetCurrentSubdivisionCount()
        {
            // Count current segments in subdivided strokes
            if (_subdivideStrokeIndices == null || _subdivideStrokeIndices.Count == 0) return 2;
            
            // For grouped strokes, count all strokes in the group
            var firstIdx = _subdivideStrokeIndices[0];
            if (firstIdx >= 0 && firstIdx < _doc.Strokes.Count)
            {
                var stroke = _doc.Strokes[firstIdx];
                if (stroke.GroupId.HasValue)
                {
                    var groupCount = _doc.Strokes.Count(s => s.GroupId == stroke.GroupId);
                    return groupCount + 1; // segments + 1 = points
                }
            }
            return 2;
        }

        private void ApplySubdivision(int pointCount)
        {
            if (_subdivideOriginalStrokes == null || _subdivideStrokeIndices == null) return;

            // For each original stroke, subdivide it into (pointCount - 1) segments
            // We need to replace the original strokes with new subdivided ones

            // First, remove old strokes that were created during subdivision (keep track of original group structure)
            // The original strokes are stored in _subdivideOriginalStrokes

            // Get the first original stroke to determine the base properties
            var firstOriginal = _subdivideOriginalStrokes[0];
            
            // Calculate total path from all original strokes
            var pathPoints = new List<PointMm>();
            pathPoints.Add(_subdivideOriginalStrokes[0].A);
            foreach (var stroke in _subdivideOriginalStrokes)
            {
                pathPoints.Add(stroke.B);
            }

            // Calculate total path length
            double totalLength = 0;
            for (int i = 0; i < pathPoints.Count - 1; i++)
            {
                totalLength += Utility.Distance(pathPoints[i], pathPoints[i + 1]);
            }

            // Create evenly spaced points along the path
            var newPoints = new List<PointMm>();
            newPoints.Add(pathPoints[0]); // Always start at first point

            if (pointCount > 2 && totalLength > 0)
            {
                double segmentLength = totalLength / (pointCount - 1);
                double accumulatedLength = 0;
                int currentSegment = 0;

                for (int i = 1; i < pointCount - 1; i++)
                {
                    double targetLength = i * segmentLength;

                    // Find the segment containing this target length
                    while (currentSegment < pathPoints.Count - 1)
                    {
                        var segLen = Utility.Distance(pathPoints[currentSegment], pathPoints[currentSegment + 1]);
                        if (accumulatedLength + segLen >= targetLength)
                        {
                            // Interpolate within this segment
                            var t = (targetLength - accumulatedLength) / segLen;
                            var newPoint = new PointMm(
                                pathPoints[currentSegment].X + t * (pathPoints[currentSegment + 1].X - pathPoints[currentSegment].X),
                                pathPoints[currentSegment].Y + t * (pathPoints[currentSegment + 1].Y - pathPoints[currentSegment].Y)
                            );
                            newPoints.Add(newPoint);
                            break;
                        }
                        accumulatedLength += segLen;
                        currentSegment++;
                    }
                }
            }

            newPoints.Add(pathPoints[^1]); // Always end at last point

            // Remove existing strokes at the indices (in reverse order to maintain indices)
            foreach (var idx in _subdivideStrokeIndices.OrderByDescending(i => i))
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    _doc.Strokes.RemoveAt(idx);
                }
            }

            // Create new strokes from the subdivided points
            var newGroupId = newPoints.Count > 2 ? Guid.NewGuid() : (Guid?)null;
            var insertIndex = _subdivideStrokeIndices.Min();
            var newIndices = new List<int>();
            
            // Determine the parent group ID for hierarchical grouping
            // If original strokes had a GroupId, that becomes the parent
            // This allows "Show Intermediate Points" on the parent to show child group points too
            Guid? parentGroupId = firstOriginal.GroupId ?? firstOriginal.ParentGroupId;

            for (int i = 0; i < newPoints.Count - 1; i++)
            {
                var newStroke = new LineStroke(newPoints[i], newPoints[i + 1])
                {
                    PaintWellId = firstOriginal.PaintWellId,
                    GroupId = newGroupId,
                    ParentGroupId = parentGroupId,
                    IsGroupStart = i == 0,
                    IsGroupEnd = i == newPoints.Count - 2
                };
                
                _doc.Strokes.Insert(insertIndex + i, newStroke);
                newIndices.Add(insertIndex + i);
            }

            // Update the tracked indices
            _subdivideStrokeIndices = newIndices;

            // Update selection to include new strokes
            _selectionController.ClearSelection();
            _selectionController.SelectStrokes(newIndices);

            // Enable intermediate points for the new group
            if (newGroupId.HasValue)
            {
                _showIntermediatePointsForGroups.Add(newGroupId.Value);
            }
        }

        private void CompleteSubdivision()
        {
            if (!_isSubdividing) return;

            // Keep intermediate points visible for ALL groups involved in this operation:
            // - Original groups (if they still have strokes in the document)
            // - New group created during subdivision
            // The user enabled intermediate points display during this operation,
            // they can toggle off manually if desired using the context menu

            // Restore cursor
            DrawCanvas.Cursor = _subdivideOriginalCursor ?? Cursors.Arrow;
            DrawCanvas.ReleaseMouseCapture();

            var finalCount = _subdivideCurrentCount;
            
            // Clean up state
            _isSubdividing = false;
            _subdivideOriginalStrokes = null;
            _subdivideStrokeIndices = null;
            _subdivideOriginalCursor = null;
            _subdivideCurrentCount = 2;
            _subdivideAccumulatedDelta = 0;
            _subdivideOriginalGroupIds = null;
            _subdivideNewlyAddedGroupIds = null;

            _lastGcode = ""; // Invalidate G-code cache
            RenderAll();
            AppendLog($"Subdivision complete: {finalCount} points.");
        }

        private void CancelSubdivision()
        {
            if (!_isSubdividing || _subdivideOriginalStrokes == null || _subdivideStrokeIndices == null) return;

            // Remove current subdivided strokes
            foreach (var idx in _subdivideStrokeIndices.OrderByDescending(i => i))
            {
                if (idx >= 0 && idx < _doc.Strokes.Count)
                {
                    _doc.Strokes.RemoveAt(idx);
                }
            }

            // Restore original strokes
            var insertIndex = _subdivideStrokeIndices.Min();
            for (int i = 0; i < _subdivideOriginalStrokes.Count; i++)
            {
                _doc.Strokes.Insert(insertIndex + i, _subdivideOriginalStrokes[i]);
            }

            // Only remove intermediate points display for groups we NEWLY added during this operation
            // Don't remove groups that were already showing before subdivision started
            if (_subdivideNewlyAddedGroupIds != null)
            {
                foreach (var groupId in _subdivideNewlyAddedGroupIds)
                {
                    _showIntermediatePointsForGroups.Remove(groupId);
                }
            }

            // Restore cursor
            DrawCanvas.Cursor = _subdivideOriginalCursor ?? Cursors.Arrow;
            DrawCanvas.ReleaseMouseCapture();

            // Clean up state
            _isSubdividing = false;
            _subdivideOriginalStrokes = null;
            _subdivideStrokeIndices = null;
            _subdivideOriginalCursor = null;
            _subdivideCurrentCount = 2;
            _subdivideAccumulatedDelta = 0;
            _subdivideOriginalGroupIds = null;
            _subdivideNewlyAddedGroupIds = null;

            RenderAll();
            AppendLog("Subdivision cancelled.");
        }

        private void SetZoom(double z)
        {
            if (z <= 0) z = 1;

            _zoom.ScaleX = z;
            _zoom.ScaleY = z;
            if (ZoomLabel != null) ZoomLabel.Text = $"{(int)Math.Round(z * 100)}%";

            UpdateZoomHost();
        }

        /// <summary>
        /// Zooms the canvas while keeping a specific point (in canvas coordinates) visually stationary
        /// at the same viewport location where the user clicked.
        /// </summary>
        private void ZoomAroundPoint(double newZoom, Point canvasPoint)
        {
            if (newZoom <= 0) newZoom = ZoomSlider.Minimum;
            newZoom = Math.Clamp(newZoom, ZoomSlider.Minimum, ZoomSlider.Maximum);

            var oldZoom = _zoom.ScaleX;
            
            // Skip tiny changes to reduce jitter
            if (Math.Abs(oldZoom - newZoom) < 0.005) return;

            // The goal: keep the canvas point under the cursor at the same viewport position
            // 
            // Before zoom: The canvas point appears at viewport position _zoomDragViewportOffset
            // After zoom: We want the same canvas point to still appear at _zoomDragViewportOffset
            //
            // Canvas margin (20px) is part of the scaled content
            const double canvasPadding = 20.0;

            // Calculate the position of the canvas point in the scrollable content at the NEW zoom level
            var scaledContentX = (canvasPoint.X + canvasPadding) * newZoom;
            var scaledContentY = (canvasPoint.Y + canvasPadding) * newZoom;

            // To keep the point at the same viewport position, we need:
            // scrollOffset + viewportPosition = scaledContentPosition
            // Therefore: scrollOffset = scaledContentPosition - viewportPosition
            var targetScrollX = scaledContentX - _zoomDragViewportOffset.X;
            var targetScrollY = scaledContentY - _zoomDragViewportOffset.Y;

            // Apply the new zoom first (this changes the content size)
            _zoom.ScaleX = newZoom;
            _zoom.ScaleY = newZoom;
            ZoomSlider.Value = newZoom;
            if (ZoomLabel != null) ZoomLabel.Text = $"{(int)Math.Round(newZoom * 100)}%";

            UpdateZoomHost();

            // Calculate valid scroll range
            var maxScrollX = Math.Max(0, CanvasScroll.ExtentWidth - CanvasScroll.ViewportWidth);
            var maxScrollY = Math.Max(0, CanvasScroll.ExtentHeight - CanvasScroll.ViewportHeight);
            
            // Use soft clamping to reduce jumpiness at edges
            // If we're close to the edge, smoothly transition rather than hard clamping
            var newScrollX = SoftClamp(targetScrollX, 0, maxScrollX);
            var newScrollY = SoftClamp(targetScrollY, 0, maxScrollY);

            // Apply scroll to keep the point fixed
            CanvasScroll.ScrollToHorizontalOffset(newScrollX);
            CanvasScroll.ScrollToVerticalOffset(newScrollY);
        }

        /// <summary>
        /// Soft clamps a value to a range, applying smooth easing near the boundaries
        /// to reduce sudden jumps when hitting scroll limits.
        /// </summary>
        private static double SoftClamp(double value, double min, double max)
        {
            if (max <= min) return min;
            
            // Simple hard clamp for now - the smoothing is handled by the eased progress
            // in the zoom drag handler
            return Math.Clamp(value, min, max);
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

            if (_isSubdividing)
            {
                // Don't cancel on mouse leave - subdivision has mouse capture
                // Just let it continue - user can escape or click to finish
                return;
            }
            if (_isPanning)
            {
                _isPanning = false;
                DrawCanvas.ReleaseMouseCapture();
            }
            if (_isZoomDragging)
            {
                _isZoomDragging = false;
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
            if (_shapeController.IsFreeDrawing)
            {
                _shapeController.EndFreeDraw(); // Complete the stroke on leave instead of canceling
                _lastGcode = "";
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
            // Subdivision mode - drag to adjust point count
            if (_isSubdividing)
            {
                UpdateSubdivision(e.GetPosition(CanvasScroll));
                e.Handled = true;
                return;
            }

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

            if (_isPanning && (e.MiddleButton == MouseButtonState.Pressed || 
                (e.LeftButton == MouseButtonState.Pressed && GetCurrentTool() == ToolMode.Pan)))
            {
                var cur = e.GetPosition(CanvasScroll);
                var dx = cur.X - _panStartMouse.X;
                var dy = cur.Y - _panStartMouse.Y;
                CanvasScroll.ScrollToHorizontalOffset(_panStartH - dx);
                CanvasScroll.ScrollToVerticalOffset(_panStartV - dy);
                e.Handled = true;
                return;
            }

            // Zoom tool dragging - drag up to zoom in, down to zoom out
            if (_isZoomDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var cur = e.GetPosition(CanvasScroll);
                var dy = _zoomDragStart.Y - cur.Y; // Inverted: drag up = positive = zoom in
                
                // Sensitivity: 200 pixels of drag = double/half zoom
                const double sensitivity = 200.0;
                var zoomFactor = 1.0 + (dy / sensitivity);
                var newZoom = Math.Clamp(_zoomDragStartZoom * zoomFactor, ZoomSlider.Minimum, ZoomSlider.Maximum);
                
                // Calculate how much zoom has changed (as a ratio from start)
                var zoomProgress = Math.Abs(newZoom - _zoomDragStartZoom) / Math.Max(_zoomDragStartZoom * 0.5, 0.1);
                zoomProgress = Math.Clamp(zoomProgress, 0, 1);
                
                // Use eased progress for smoother transitions
                var easedProgress = zoomProgress * zoomProgress * (3.0 - 2.0 * zoomProgress); // Smoothstep
                
                // Gradually move the viewport target toward the center as zoom changes
                // This makes the clicked point gravitate toward the center of the viewport
                var viewportCenterX = CanvasScroll.ViewportWidth / 2.0;
                var viewportCenterY = CanvasScroll.ViewportHeight / 2.0;
                
                // Interpolate between original click position and viewport center based on zoom progress
                // Use a gentler interpolation factor (0.6) for smoother edge behavior
                _zoomDragViewportOffset = new Point(
                    _zoomDragStart.X + (viewportCenterX - _zoomDragStart.X) * easedProgress * 0.6,
                    _zoomDragStart.Y + (viewportCenterY - _zoomDragStart.Y) * easedProgress * 0.6
                );
                
                // Apply zoom centered on the original mouse position (which will move toward center)
                ZoomAroundPoint(newZoom, _zoomDragCanvasPoint);
                
                e.Handled = true;
                return;
            }

            // Selection tool handling - use unclamped point so handles outside document area work
            if (_selectionController.IsActive)
            {
                var selPoint = MouseToMm(e.GetPosition(CanvasScroll));
                if (_selectionController.HandleMouseMove(selPoint))
                {
                    e.Handled = true;
                    return;
                }
            }
            // Also handle hover detection when Select tool is active but not dragging
            else if (GetCurrentTool() == ToolMode.Select && _selectionController.HasSelection)
            {
                var selPoint = MouseToMm(e.GetPosition(CanvasScroll));
                _selectionController.HandleMouseMove(selPoint);
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

                // FreeDraw tool - update while mouse is held
                if (_shapeController.IsFreeDrawing)
                {
                    // Pass zoom level for pixel-based distance calculation
                    if (_shapeController.UpdateFreeDraw(rawMm, _zoom.ScaleX))
                    {
                        e.Handled = true;
                        return;
                    }
                }

                if (_shapeController.Update(mm, Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)))
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
            // Use unclamped point for selection handle detection (handles may be outside document area after rotation)
            var unclampedPoint = MouseToMm(e.GetPosition(CanvasScroll));
            var rawPoint = ClampToPage(unclampedPoint);
            var point = ApplySnapping(rawPoint, out _, out _, out _);

            // Check if clicking on a paint well - set it as active color regardless of current tool
            var hitWell = _paintWellController.TryHitTestPaintWell(point);
            if (hitWell != null)
            {
                _paintWellController.SetActiveColor(hitWell);
                UpdatePaintWellChipHighlights(); // Update visual highlight on color chips
                AppendLog($"Active color set to: {hitWell.Name}");
                RenderAll(); // Update visuals to show active well
                
                // For PaintWell tool and Select tool, continue to allow further processing
                // For other tools, just set the color and return
                if (tool != ToolMode.PaintWell && tool != ToolMode.Select)
                {
                    e.Handled = true;
                    return;
                }
            }

            // Paint well tool
            if (tool == ToolMode.PaintWell)
            {
                if (_paintWellController.HandleMouseDown(point, Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
                {
                    e.Handled = true;
                    return;
                }
            }

            // Selection tool - use unclamped point so handles outside document area are clickable
            if (tool == ToolMode.Select)
            {
                var shiftHeld = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                if (_selectionController.HandleMouseDown(unclampedPoint, shiftHeld))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (tool == ToolMode.Bezier)
            {
                // Track stroke count when starting a new bezier
                if (!_shapeController.IsBezierActive)
                {
                    _strokeCountBeforeOperation = _doc.Strokes.Count;
                }
                _shapeController.HandleBezierClick(point);
                // If bezier just finished (by third click), record undo group
                if (!_shapeController.IsBezierActive)
                {
                    var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                    if (strokesAdded > 0)
                    {
                        _undoGroupSizes.Push(strokesAdded);
                    }
                }
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.PolyBezier)
            {
                // Track stroke count when starting a new poly-bezier
                if (!_shapeController.IsPolyBezierActive)
                {
                    _strokeCountBeforeOperation = _doc.Strokes.Count;
                }
                _shapeController.HandlePolyBezierClick(point);
                e.Handled = true;
                return;
            }

            if (tool == ToolMode.Polyline)
            {
                // Track stroke count for the entire polyline operation
                if (!_shapeController.IsPolylineActive)
                {
                    _strokeCountBeforeOperation = _doc.Strokes.Count;
                }
                _shapeController.HandlePolylineClick(point, e.ClickCount >= 2);
                // If polyline just finished (by double-click or auto-close), record undo group
                if (!_shapeController.IsPolylineActive)
                {
                    var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                    if (strokesAdded > 0)
                    {
                        _undoGroupSizes.Push(strokesAdded);
                    }
                }
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

            if (tool == ToolMode.Pan)
            {
                // Pan tool - start panning with left mouse button (same as middle mouse)
                _isPanning = true;
                _panStartMouse = e.GetPosition(CanvasScroll);
                _panStartH = CanvasScroll.HorizontalOffset;
                _panStartV = CanvasScroll.VerticalOffset;
                DrawCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            
            if (_isPanning) return; // Already panning via middle mouse

            // Zoom tool - begin zoom drag
            if (tool == ToolMode.Zoom)
            {
                _isZoomDragging = true;
                _zoomDragStart = e.GetPosition(CanvasScroll);
                _zoomDragStartZoom = ZoomSlider.Value;
                // Store the canvas point we're zooming around (mouse position on canvas)
                _zoomDragCanvasPoint = e.GetPosition(DrawCanvas);
                // Store where in the viewport the click occurred (relative to viewport, not content)
                _zoomDragViewportOffset = _zoomDragStart;
                DrawCanvas.CaptureMouse();
                        e.Handled = true;
                        return;
                    }

                    // FreeDraw tool - continuous freehand drawing
                    if (tool == ToolMode.FreeDraw)
                    {
                        _strokeCountBeforeOperation = _doc.Strokes.Count;
                        _shapeController.BeginFreeDraw(point);
                        e.Handled = true;
                        return;
                    }

                    _strokeCountBeforeOperation = _doc.Strokes.Count;
                    _shapeController.BeginDraw(tool, point);
                    e.Handled = true;
                }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            RemoveSnapIndicator();

            // Subdivision mode complete
            if (_isSubdividing)
            {
                CompleteSubdivision();
                e.Handled = true;
                return;
            }

            // Zoom tool drag complete
            if (_isZoomDragging)
            {
                _isZoomDragging = false;
                DrawCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            // Pan tool drag complete (left mouse button pan)
            if (_isPanning && GetCurrentTool() == ToolMode.Pan)
            {
                _isPanning = false;
                DrawCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

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

            // FreeDraw tool completion
            if (_shapeController.IsFreeDrawing)
            {
                _shapeController.EndFreeDraw();
                // Record undo group for all strokes added by this freehand drawing
                var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                if (strokesAdded > 0)
                {
                    _undoGroupSizes.Push(strokesAdded);
                }
                _lastGcode = ""; // Invalidate G-code cache
                e.Handled = true;
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

            // Selection tool handling - use unclamped point so handles outside document area work
            if (_selectionController.IsActive)
            {
                var selPoint = MouseToMm(e.GetPosition(CanvasScroll));
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
                var constrainAspectRatio = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                _shapeController.CompleteDraw(endMm, constrainAspectRatio);
                // Record undo group for all strokes added by this shape
                var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                if (strokesAdded > 0)
                {
                    _undoGroupSizes.Push(strokesAdded);
                }
                _lastGcode = ""; // Invalidate G-code cache
                RenderAll(); // Ensure visual update after shape completion
                e.Handled = true;
                return;
            }
        }

        private ToolMode GetCurrentTool()
        {
            // Use the floating tools window if available
            if (_toolsWindow != null)
            {
                return _toolsWindow.CurrentTool;
            }

            // Fallback to combo box (for backward compatibility)
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

            // Colors: Use heat map for endpoint snap based on position along stroke group, Orange for grid
            Brush fillBrush;
            Brush strokeBrush;
            double progress = 0.5; // Default to middle
            if (snapPoint != null)
            {
                // Calculate heat map color based on position along the stroke group
                progress = CalculateSnapPointProgress(snapPoint.Value);
                var (fillColor, borderColor) = GetHeatMapColor(progress);
                fillBrush = new SolidColorBrush(fillColor);
                strokeBrush = new SolidColorBrush(borderColor);
            }
            else
            {
                // Grid snap - orange/yellow color
                fillBrush = new SolidColorBrush(Color.FromArgb(150, 255, 165, 0));
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
                    StrokeThickness = 3 // Thicker border for better visibility
                };
                DrawCanvas.Children.Add(_snapIndicator);
                Panel.SetZIndex(_snapIndicator, 20);
            }

            _snapIndicator.Fill = fillBrush;
            _snapIndicator.Stroke = strokeBrush;

            var centerX = displayPoint.Value.X;
            var centerY = displayPoint.Value.Y;

            Canvas.SetLeft(_snapIndicator, centerX - SNAP_INDICATOR_SIZE / 2.0);
            Canvas.SetTop(_snapIndicator, centerY - SNAP_INDICATOR_SIZE / 2.0);

            // Add dashed crosshair lines ONLY for start (t ≈ 0) and end (t ≈ 1) points
            // Not for intermediate points or grid snaps
            const double endpointThreshold = 0.01; // Tolerance for determining if it's a start/end point
            bool isStartOrEnd = snapPoint != null && (progress <= endpointThreshold || progress >= 1.0 - endpointThreshold);

            if (isStartOrEnd)
            {
                var lineExtend = SNAP_INDICATOR_SIZE / 2.0 + SNAP_INDICATOR_LINE_EXTEND;

                // Horizontal dashed line
                if (_snapIndicatorLineH == null)
                {
                    _snapIndicatorLineH = new Line
                    {
                        StrokeThickness = 3,
                        StrokeDashArray = [3, 2],
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    DrawCanvas.Children.Add(_snapIndicatorLineH);
                    Panel.SetZIndex(_snapIndicatorLineH, 19); // Below the circle
                }

                _snapIndicatorLineH.X1 = centerX - lineExtend;
                _snapIndicatorLineH.Y1 = centerY;
                _snapIndicatorLineH.X2 = centerX + lineExtend;
                _snapIndicatorLineH.Y2 = centerY;
                _snapIndicatorLineH.Stroke = strokeBrush;

                // Vertical dashed line
                if (_snapIndicatorLineV == null)
                {
                    _snapIndicatorLineV = new Line
                    {
                        StrokeThickness = 3,
                        StrokeDashArray = [3, 2],
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    DrawCanvas.Children.Add(_snapIndicatorLineV);
                    Panel.SetZIndex(_snapIndicatorLineV, 19); // Below the circle
                }

                _snapIndicatorLineV.X1 = centerX;
                _snapIndicatorLineV.Y1 = centerY - lineExtend;
                _snapIndicatorLineV.X2 = centerX;
                _snapIndicatorLineV.Y2 = centerY + lineExtend;
                _snapIndicatorLineV.Stroke = strokeBrush;
                
                // Remove grid snap crosshairs when showing endpoint crosshairs
                RemoveGridSnapCrosshairs();
            }
            else if (snapPoint == null && gridSnapPoint != null)
            {
                // Grid snap - show full-length dashed crosshair and center dot
                RemoveSnapIndicatorLines(); // Remove endpoint-style short crosshairs
                
                const double rulerThickness = 18.0;
                
                // Full-width horizontal dashed line
                if (_gridSnapCrosshairH == null)
                {
                    _gridSnapCrosshairH = new Line
                    {
                        StrokeThickness = 1,
                        StrokeDashArray = [4, 4],
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true,
                        Opacity = 0.7
                    };
                    DrawCanvas.Children.Add(_gridSnapCrosshairH);
                    Panel.SetZIndex(_gridSnapCrosshairH, 18); // Below the circle
                }
                
                _gridSnapCrosshairH.X1 = rulerThickness;
                _gridSnapCrosshairH.Y1 = centerY;
                _gridSnapCrosshairH.X2 = rulerThickness + _doc.WidthMm;
                _gridSnapCrosshairH.Y2 = centerY;
                _gridSnapCrosshairH.Stroke = Brushes.Orange;
                
                // Full-height vertical dashed line
                if (_gridSnapCrosshairV == null)
                {
                    _gridSnapCrosshairV = new Line
                    {
                        StrokeThickness = 1,
                        StrokeDashArray = [4, 4],
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true,
                        Opacity = 0.7
                    };
                    DrawCanvas.Children.Add(_gridSnapCrosshairV);
                    Panel.SetZIndex(_gridSnapCrosshairV, 18); // Below the circle
                }
                
                _gridSnapCrosshairV.X1 = centerX;
                _gridSnapCrosshairV.Y1 = rulerThickness;
                _gridSnapCrosshairV.X2 = centerX;
                _gridSnapCrosshairV.Y2 = rulerThickness + _doc.HeightMm;
                _gridSnapCrosshairV.Stroke = Brushes.Orange;
                
                // Tiny center dot
                const double dotSize = 4.0;
                if (_gridSnapCenterDot == null)
                {
                    _gridSnapCenterDot = new Ellipse
                    {
                        Width = dotSize,
                        Height = dotSize,
                        Fill = Brushes.Orange,
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    DrawCanvas.Children.Add(_gridSnapCenterDot);
                    Panel.SetZIndex(_gridSnapCenterDot, 21); // Above the main indicator
                }
                
                Canvas.SetLeft(_gridSnapCenterDot, centerX - dotSize / 2.0);
                Canvas.SetTop(_gridSnapCenterDot, centerY - dotSize / 2.0);
            }
            else
            {
                // Not a start/end point and not grid snap - remove all crosshair lines
                RemoveSnapIndicatorLines();
                RemoveGridSnapCrosshairs();
            }
        }

        /// <summary>
        /// Calculates the progress (0.0 to 1.0) of a snap point along its stroke group.
        /// 0.0 = group start, 1.0 = group end.
        /// For ungrouped strokes, returns 0.0 for point A and 1.0 for point B.
        /// </summary>
        private double CalculateSnapPointProgress(PointMm snapPoint)
        {
            const double tolerance = 0.5; // mm tolerance for point matching

            // Find which stroke(s) contain this point
            LineStroke? matchedStroke = null;
            bool isPointA = false;

            foreach (var stroke in _doc.Strokes)
            {
                if (Math.Abs(stroke.A.X - snapPoint.X) < tolerance && Math.Abs(stroke.A.Y - snapPoint.Y) < tolerance)
                {
                    matchedStroke = stroke;
                    isPointA = true;
                    break;
                }
                if (Math.Abs(stroke.B.X - snapPoint.X) < tolerance && Math.Abs(stroke.B.Y - snapPoint.Y) < tolerance)
                {
                    matchedStroke = stroke;
                    isPointA = false;
                    break;
                }
            }

            if (matchedStroke == null)
                return 0.5; // Default to middle if not found

            // If stroke has no group, it's a single stroke: A = 0.0, B = 1.0
            if (matchedStroke.GroupId == null)
            {
                return isPointA ? 0.0 : 1.0;
            }

            // Find all strokes in the same group
            var groupId = matchedStroke.GroupId.Value;
            var groupStrokes = _doc.Strokes.Where(s => s.GroupId == groupId).ToList();

            if (groupStrokes.Count == 0)
                return isPointA ? 0.0 : 1.0;

            // Build ordered list of all points in the group
            // Start from the stroke marked as IsGroupStart
            var points = new List<PointMm>();
            var startStroke = groupStrokes.FirstOrDefault(s => s.IsGroupStart) ?? groupStrokes[0];
            points.Add(startStroke.A);
            foreach (var stroke in groupStrokes)
            {
                points.Add(stroke.B);
            }

            // Find which point index matches the snap point
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (Math.Abs(pt.X - snapPoint.X) < tolerance && Math.Abs(pt.Y - snapPoint.Y) < tolerance)
                {
                    // Return progress: 0.0 for first point, 1.0 for last point
                    return points.Count > 1 ? (double)i / (points.Count - 1) : 0.0;
                }
            }

            // Fallback: use the matched stroke's position
            return isPointA ? 0.0 : 1.0;
        }

        private void RemoveSnapIndicator()
        {
            if (DrawCanvas != null && _snapIndicator != null)
            {
                DrawCanvas.Children.Remove(_snapIndicator);
                _snapIndicator = null;
            }
            RemoveSnapIndicatorLines();
            RemoveGridSnapCrosshairs();
        }

        private void RemoveSnapIndicatorLines()
        {
            if (DrawCanvas != null)
            {
                if (_snapIndicatorLineH != null)
                {
                    DrawCanvas.Children.Remove(_snapIndicatorLineH);
                    _snapIndicatorLineH = null;
                }
                if (_snapIndicatorLineV != null)
                {
                    DrawCanvas.Children.Remove(_snapIndicatorLineV);
                    _snapIndicatorLineV = null;
                }
            }
        }

        private void RemoveGridSnapCrosshairs()
        {
            if (DrawCanvas != null)
            {
                if (_gridSnapCrosshairH != null)
                {
                    DrawCanvas.Children.Remove(_gridSnapCrosshairH);
                    _gridSnapCrosshairH = null;
                }
                if (_gridSnapCrosshairV != null)
                {
                    DrawCanvas.Children.Remove(_gridSnapCrosshairV);
                    _gridSnapCrosshairV = null;
                }
                if (_gridSnapCenterDot != null)
                {
                    DrawCanvas.Children.Remove(_gridSnapCenterDot);
                    _gridSnapCenterDot = null;
                }
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

        private void InitializeToolsWindow()
        {
            _toolsWindow = new ToolsWindow(OnToolSelected);
            
            // Close tools window when main window closes
            Closed += (s, e) => _toolsWindow?.ForceClose();
            
            // Show the tools window when main window is loaded
            Loaded += (s, e) =>
            {
                if (_toolsWindow != null)
                {
                    _toolsWindow.Owner = this;
                    // Set the constraint element so the tools window stays within the canvas area
                    _toolsWindow.SetConstraintElement(CanvasScroll, this);
                    _toolsWindow.Show();
                }
            };
        }

        /// <summary>
        /// Called when a tool is selected from the floating tools panel.
        /// </summary>
        private void OnToolSelected(ToolMode tool)
        {
            // Handle tool change logic (same as ToolCombo_SelectionChanged)
            if (_measurementOverlay != null)
            {
                if (tool != ToolMode.Measure && (_measurementOverlay.IsMeasuring || _measurementOverlay.HasMeasurement))
                {
                    _measurementOverlay.Reset();
                }
            }

            if (tool != ToolMode.Polyline)
            {
                _shapeController.FinishPolyline();
            }

            if (tool != ToolMode.Bezier && _shapeController.IsBezierActive)
            {
                _shapeController.CancelBezier();
            }

            if (tool != ToolMode.PolyBezier && _shapeController.IsPolyBezierActive)
            {
                _shapeController.CancelPolyBezier();
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

            // Sync the combo box for backward compatibility
            if (ToolCombo != null)
            {
                foreach (ComboBoxItem item in ToolCombo.Items)
                {
                    if (item.Tag?.ToString()?.Equals(tool.ToString(), StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ToolCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            AppendLog($"Tool: {tool}");
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
                ShowConsoleWindow();
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
        /// Grid opacity and thickness adapt to zoom level to ensure drawing strokes remain visible.
        /// </summary>
        private void DrawGrid()
        {
            if (DrawCanvas == null || !IsGridVisible) return;

            var spacing = GetGridSpacing();
            if (spacing <= 0) return;

            // Adaptive grid appearance based on zoom level
            // At low zoom, grid becomes more transparent and thinner to avoid obscuring strokes
            var currentZoom = _zoom.ScaleX;
            
            // Grid opacity: 100% at zoom >= 1.0, fades to 40% at zoom <= 0.2
            var gridOpacity = Math.Clamp(0.4 + 0.6 * Math.Min(1.0, currentZoom), 0.4, 1.0);
            
            // Grid thickness: 1.0 at zoom >= 1.0, thins to 0.5 at low zoom
            var gridThickness = Math.Clamp(0.5 + 0.5 * Math.Min(1.0, currentZoom), 0.5, 1.0);
            
            var gridColor = Color.FromArgb((byte)(200 * gridOpacity), 200, 200, 200);
            var gridBrush = new SolidColorBrush(gridColor);
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

            // Grid lines are drawn at rulerThickness + (n * spacing) positions
            // To snap to grid intersections, we need to account for this offset
            const double rulerThickness = 18.0;
            
            // Convert to document coordinates (relative to ruler), snap, then convert back
            var docX = raw.X - rulerThickness;
            var docY = raw.Y - rulerThickness;
            
            var snappedDocX = Math.Round(docX / spacing) * spacing;
            var snappedDocY = Math.Round(docY / spacing) * spacing;

            // Convert back to canvas coordinates
            return new PointMm(snappedDocX + rulerThickness, snappedDocY + rulerThickness);
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
                // Record undo group for the bezier that just finished
                var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                if (strokesAdded > 0)
                {
                    _undoGroupSizes.Push(strokesAdded);
                }
                e.Handled = true;
                return;
            }

            if (_shapeController.TryFinishPolyBezier())
            {
                // Record undo group for the poly-bezier that just finished
                var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                if (strokesAdded > 0)
                {
                    _undoGroupSizes.Push(strokesAdded);
                }
                e.Handled = true;
                return;
            }

            if (_shapeController.TryFinishPolyline())
            {
                // Record undo group for the polyline that just finished
                var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                if (strokesAdded > 0)
                {
                    _undoGroupSizes.Push(strokesAdded);
                }
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

            if (tool != ToolMode.PolyBezier && _shapeController.IsPolyBezierActive)
            {
                _shapeController.CancelPolyBezier();
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
            // Subdivision mode - Escape to cancel
            if (_isSubdividing && e.Key == Key.Escape)
            {
                CancelSubdivision();
                e.Handled = true;
                return;
            }

            // Tool keyboard shortcuts (when not in a text box)
            if (e.OriginalSource is not TextBox && _toolsWindow?.HandleKeyboardShortcut(e.Key, Keyboard.Modifiers) == true)
            {
                e.Handled = true;
                return;
            }

            // Clipboard shortcuts (Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A) - work in any tool when there's a selection
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.OriginalSource is not TextBox)
            {
                switch (e.Key)
                {
                    case Key.X:
                        PerformCut();
                        e.Handled = true;
                        return;
                    case Key.C:
                        PerformCopy();
                        e.Handled = true;
                        return;
                    case Key.V:
                        PerformPaste();
                        e.Handled = true;
                        return;
                    case Key.A:
                        PerformSelectAll();
                        e.Handled = true;
                        return;
                    case Key.G:
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        {
                            // Ctrl+Shift+G = Ungroup
                            UngroupMenuItem_Click(sender, e);
                        }
                        else
                        {
                            // Ctrl+G = Group
                            GroupMenuItem_Click(sender, e);
                        }
                        e.Handled = true;
                        return;
                }
            }

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
                    // Record undo group for the bezier that just finished
                    var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                    if (strokesAdded > 0)
                    {
                        _undoGroupSizes.Push(strokesAdded);
                    }
                }
                e.Handled = true;
                return;
            }

            if (_shapeController.IsPolyBezierActive && (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Return))
            {
                if (e.Key == Key.Escape)
                {
                    _shapeController.CancelPolyBezier();
                }
                else
                {
                    _shapeController.TryFinishPolyBezier();
                    // Record undo group for the poly-bezier that just finished
                    var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                    if (strokesAdded > 0)
                    {
                        _undoGroupSizes.Push(strokesAdded);
                    }
                }
                e.Handled = true;
                return;
            }

            if (_shapeController.IsPolylineActive && (e.Key == Key.Escape || e.Key == Key.Enter || e.Key == Key.Return))
            {
                _shapeController.FinishPolyline();
                // Record undo group for the polyline that just finished (only on Enter, not Escape)
                if (e.Key != Key.Escape)
                {
                    var strokesAdded = _doc.Strokes.Count - _strokeCountBeforeOperation;
                    if (strokesAdded > 0)
                    {
                        _undoGroupSizes.Push(strokesAdded);
                    }
                }
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
            UpdateWorkingAreaSizeLabel(current, rect);
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

            RemoveWorkingAreaSizeLabel();
        }

        private void UpdateWorkingAreaSizeLabel(PointMm cursorPos, Rect rect)
        {
            if (DrawCanvas == null) return;

            // Create the label if it doesn't exist
            if (_workingAreaSizeLabel == null)
            {
                _workingAreaSizeLabel = new TextBlock
                {
                    Foreground = Brushes.DeepSkyBlue,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                    Padding = new Thickness(6, 3, 6, 3),
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                DrawCanvas.Children.Add(_workingAreaSizeLabel);
                Panel.SetZIndex(_workingAreaSizeLabel, 20);
            }

            // Update the size text
            _workingAreaSizeLabel.Text = $"{rect.Width:0.#} mm × {rect.Height:0.#} mm";

            // Measure the label to position it correctly
            _workingAreaSizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelWidth = _workingAreaSizeLabel.DesiredSize.Width;
            var labelHeight = _workingAreaSizeLabel.DesiredSize.Height;

            // Position centered below the cursor with a small offset
            const double verticalOffset = 15;
            var labelX = cursorPos.X - (labelWidth / 2);
            var labelY = cursorPos.Y + verticalOffset;

            // Clamp to keep within canvas bounds
            labelX = Math.Max(0, Math.Min(labelX, _doc.WidthMm - labelWidth));
            labelY = Math.Max(0, Math.Min(labelY, _doc.HeightMm - labelHeight));

            Canvas.SetLeft(_workingAreaSizeLabel, labelX);
            Canvas.SetTop(_workingAreaSizeLabel, labelY);
        }

        private void RemoveWorkingAreaSizeLabel()
        {
            if (DrawCanvas != null && _workingAreaSizeLabel != null)
            {
                DrawCanvas.Children.Remove(_workingAreaSizeLabel);
                _workingAreaSizeLabel = null;
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
            // Extra margin around document to allow selection handles outside document bounds to be clickable
            // This accommodates rotation handles (ROTATE_HANDLE_OFFSET = 45) plus selection padding (20) plus handle size (12)
            const double handleMargin = 100.0;

            // Size canvas to page + ruler thickness + handle margin on all sides
            DrawCanvas.Width = _doc.WidthMm + rulerThickness + handleMargin * 2;
            DrawCanvas.Height = _doc.HeightMm + rulerThickness + handleMargin * 2;
            RulerCanvas.Width = _doc.WidthMm + rulerThickness + handleMargin * 2;
            RulerCanvas.Height = _doc.HeightMm + rulerThickness + handleMargin * 2;

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
            
            // Calculate zoom-adaptive stroke thickness to ensure strokes are always visible
            // At low zoom, we increase the logical thickness so strokes remain visible
            // Minimum desired screen thickness is ~2 pixels
            var currentZoom = _zoom.ScaleX;
            const double minScreenThickness = 2.0; // Minimum pixels on screen
            const double baseThickness = 1.2; // Base logical thickness in mm
            // Ensure strokes are at least minScreenThickness pixels when rendered
            var adaptiveThickness = Math.Max(baseThickness, minScreenThickness / currentZoom);
            
            for (int i = 0; i < _doc.Strokes.Count; i++)
            {
                var s = _doc.Strokes[i];
                
                // Determine stroke color - keep original color even when selected
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
                // Note: Removed SnapsToDevicePixels and EdgeMode.Aliased as they cause
                // perfectly vertical/horizontal lines to vanish at certain zoom levels
                // (60%, 40%) when the line falls exactly between device pixels.
                Panel.SetZIndex(ln, 4);
                DrawCanvas.Children.Add(ln);
            }

            // Note: Selection indicators show individual group start/end points within the selection
            // This allows circle curve segments and other multi-group shapes to display their markers
            DrawSelectionIndicators();

            // Selection visuals (bounding box, handles)
            _selectionController.RenderSelectionVisuals();

            AppendLog($"Doc: {_doc.WidthMm:0} x {_doc.HeightMm:0} mm, strokes={_doc.Strokes.Count}, paintWells={_doc.PaintWells.Count}");
            AppendLog($"Bed: X={_bedX:0.###} Y={_bedY:0.###} {(_bedFromGrbl ? "(from $$)" : "(default)")}, margin={SafeMarginMm:0.###}mm");

            UpdateZoomHost();
        }

        /// <summary>
        /// Draws start/end indicators for selected strokes using IsGroupStart/IsGroupEnd markers.
        /// For grouped objects: shows start at stroke with IsGroupStart=true, end at stroke with IsGroupEnd=true.
        /// For closed loops (start == end across ALL groups): shows a single purple indicator.
        /// For individual strokes (no group): shows start/end on that stroke.
        /// </summary>
        private void DrawSelectionIndicators()
        {
            if (!_selectionController.HasSelection) return;

            const double indicatorSize = 18.0; // Larger indicator size for better visibility
            const double closedLoopTolerance = 0.5; // mm tolerance for considering points equal

            // Get selected strokes in sorted order for consistency
            var selectedIndices = _selectionController.SelectedIndices.OrderBy(i => i).ToList();
            if (selectedIndices.Count == 0) return;

            // Group selected strokes by GroupId
            var groupedStrokes = new Dictionary<Guid, List<LineStroke>>();
            var ungroupedStrokes = new List<LineStroke>();

            foreach (var idx in selectedIndices)
            {
                if (idx < 0 || idx >= _doc.Strokes.Count) continue;
                var stroke = _doc.Strokes[idx];
                
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

            // During subdivision, also include ANCESTOR groups (parent, grandparent, etc.)
            // This ensures parent indicators remain visible while subdividing a child
            if (_isSubdividing)
            {
                // Find all ancestor group IDs for the selected groups
                var ancestorGroupIds = new HashSet<Guid>();
                foreach (var groupId in groupedStrokes.Keys.ToList())
                {
                    // Walk up the ancestor chain
                    var currentGroupId = groupId;
                    var visited = new HashSet<Guid> { currentGroupId };
                    
                    while (true)
                    {
                        var parentGroupId = _doc.Strokes
                            .FirstOrDefault(s => s.GroupId == currentGroupId)?.ParentGroupId;
                        
                        if (!parentGroupId.HasValue || visited.Contains(parentGroupId.Value))
                            break;
                        
                        ancestorGroupIds.Add(parentGroupId.Value);
                        visited.Add(parentGroupId.Value);
                        currentGroupId = parentGroupId.Value;
                    }
                }
                
                // Add ancestor group strokes if they have intermediate points enabled
                foreach (var ancestorId in ancestorGroupIds)
                {
                    if (_showIntermediatePointsForGroups.Contains(ancestorId))
                    {
                        if (!groupedStrokes.ContainsKey(ancestorId))
                        {
                            var ancestorStrokes = _doc.Strokes.Where(s => s.GroupId == ancestorId).ToList();
                            if (ancestorStrokes.Count > 0)
                            {
                                groupedStrokes[ancestorId] = ancestorStrokes;
                            }
                        }
                    }
                }
            }

            // Find and add child groups if parent group has intermediate points enabled
            // This allows child group indicators to be shown when the parent is selected
            // Note: We iterate over a copy of the keys to avoid modifying the dictionary while iterating
            // Use recursive lookup to find ALL descendants (children, grandchildren, etc.)
            var parentGroupIds = groupedStrokes.Keys.ToList();
            var allDescendantGroupIds = GetAllDescendantGroupIds(
                parentGroupIds.Where(id => _showIntermediatePointsForGroups.Contains(id)));
            
            // Add all descendant group strokes to the rendering
            foreach (var stroke in _doc.Strokes)
            {
                if (stroke.GroupId.HasValue && allDescendantGroupIds.Contains(stroke.GroupId.Value))
                {
                    // Add this descendant group's strokes (if not already added)
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

            // Collect ALL start and end points from all groups to detect cross-group closed loops
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

            // Find points that are both a start AND an end (closed loop junctions)
            var closedLoopPoints = new HashSet<(double X, double Y)>();
            foreach (var startPt in allStartPoints)
            {
                foreach (var endPt in allEndPoints)
                {
                    if (Math.Abs(startPt.X - endPt.X) < closedLoopTolerance &&
                        Math.Abs(startPt.Y - endPt.Y) < closedLoopTolerance)
                    {
                        // Use rounded coordinates as key to handle tolerance
                        closedLoopPoints.Add((Math.Round(startPt.X, 1), Math.Round(startPt.Y, 1)));
                    }
                }
            }

            // Draw indicators for each group, passing the closed loop info
            foreach (var (_, strokes) in groupedStrokes)
            {
                DrawIndicatorsUsingMarkers(strokes, indicatorSize, closedLoopTolerance, closedLoopPoints);
            }

            // Draw indicators for ungrouped strokes (each is its own group)
            foreach (var stroke in ungroupedStrokes)
            {
                DrawIndicatorsUsingMarkers(new List<LineStroke> { stroke }, indicatorSize, closedLoopTolerance, closedLoopPoints);
            }
        }

        /// <summary>
        /// Draws indicators for a group of strokes using their IsGroupStart/IsGroupEnd markers.
        /// Shows only start (green) and end (red) indicators, or purple if they overlap (closed loop).
        /// If intermediate points are enabled for this group, also shows all connection points with heat map colors.
        /// The closedLoopPoints set contains points that are both a start AND an end across all selected groups.
        /// </summary>
        private void DrawIndicatorsUsingMarkers(List<LineStroke> strokes, double indicatorSize, double closedLoopTolerance, HashSet<(double X, double Y)> closedLoopPoints)
        {
            if (strokes.Count == 0) return;

            // Check if any stroke in this group has intermediate points enabled
            // Also check if any ANCESTOR group has intermediate points enabled (hierarchical - any depth)
            var groupId = strokes.FirstOrDefault(s => s.GroupId.HasValue)?.GroupId;
            
            // Use the helper method to check the entire ancestor chain
            var showIntermediatePoints = groupId.HasValue && HasAncestorWithIntermediatePointsEnabled(groupId.Value);

            // Find the start point (stroke with IsGroupStart=true)
            var startStroke = strokes.FirstOrDefault(s => s.IsGroupStart);
            var startPoint = startStroke?.A ?? strokes[0].A;

            // Find the end point (stroke with IsGroupEnd=true)
            var endStroke = strokes.FirstOrDefault(s => s.IsGroupEnd);
            var endPoint = endStroke?.B ?? strokes[^1].B;

            // Check if start point is part of a closed loop (overlaps with any end point across all groups)
            var startRounded = (Math.Round(startPoint.X, 1), Math.Round(startPoint.Y, 1));
            var endRounded = (Math.Round(endPoint.X, 1), Math.Round(endPoint.Y, 1));
            
            bool startIsClosed = closedLoopPoints.Contains(startRounded);
            bool endIsClosed = closedLoopPoints.Contains(endRounded);

            // Check if start and end of THIS group are at the same position
            bool isSelfClosed = Math.Abs(startPoint.X - endPoint.X) < closedLoopTolerance &&
                               Math.Abs(startPoint.Y - endPoint.Y) < closedLoopTolerance;

            // If showing intermediate points, draw all connection points with heat map colors
            if (showIntermediatePoints && strokes.Count > 1)
            {
                // Collect all unique points in order
                var allPoints = new List<PointMm>();
                var seenPoints = new HashSet<(double, double)>();
                
                // Add start point
                allPoints.Add(strokes[0].A);
                seenPoints.Add((Math.Round(strokes[0].A.X, 2), Math.Round(strokes[0].A.Y, 2)));
                
                // Add all B points (and A points if not already added, for non-contiguous paths)
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

                // Draw intermediate points (skip first and last which get special treatment)
                const double intermediateSize = 10.0; // Smaller size for intermediate points
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
                    Panel.SetZIndex(indicator, 4); // Below main start/end indicators
                    DrawCanvas.Children.Add(indicator);
                }
            }

            if (isSelfClosed)
            {
                // This group forms a complete closed loop by itself - single purple indicator
                var closedIndicator = new Ellipse
                {
                    Width = indicatorSize,
                    Height = indicatorSize,
                    Fill = new SolidColorBrush(Color.FromArgb(180, 128, 0, 128)), // Purple fill
                    Stroke = new SolidColorBrush(Color.FromRgb(100, 0, 100)), // Dark purple border
                    StrokeThickness = 2,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(closedIndicator, startPoint.X - indicatorSize / 2.0);
                Canvas.SetTop(closedIndicator, startPoint.Y - indicatorSize / 2.0);
                Panel.SetZIndex(closedIndicator, 5);
                DrawCanvas.Children.Add(closedIndicator);
            }
            else
            {
                // Open path within this group, but check if endpoints connect to other groups
                
                // Start indicator - purple if it's part of a cross-group closed loop, green otherwise
                if (startIsClosed)
                {
                    // This start point coincides with an end point from another group - purple
                    var closedIndicator = new Ellipse
                    {
                        Width = indicatorSize,
                        Height = indicatorSize,
                        Fill = new SolidColorBrush(Color.FromArgb(180, 128, 0, 128)), // Purple fill
                        Stroke = new SolidColorBrush(Color.FromRgb(100, 0, 100)), // Dark purple border
                        StrokeThickness = 2,
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    Canvas.SetLeft(closedIndicator, startPoint.X - indicatorSize / 2.0);
                    Canvas.SetTop(closedIndicator, startPoint.Y - indicatorSize / 2.0);
                    Panel.SetZIndex(closedIndicator, 5);
                    DrawCanvas.Children.Add(closedIndicator);
                }
                else
                {
                    // Normal start indicator (green)
                    var startIndicator = new Ellipse
                    {
                        Width = indicatorSize,
                        Height = indicatorSize,
                        Fill = new SolidColorBrush(Color.FromArgb(180, 0, 180, 0)), // Green fill
                        Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 0)), // Dark green border
                        StrokeThickness = 2,
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true
                    };
                    Canvas.SetLeft(startIndicator, startPoint.X - indicatorSize / 2.0);
                    Canvas.SetTop(startIndicator, startPoint.Y - indicatorSize / 2.0);
                    Panel.SetZIndex(startIndicator, 5);
                    DrawCanvas.Children.Add(startIndicator);
                }

                // End indicator - purple if it's part of a cross-group closed loop, red otherwise
                // But skip if end point is at the same location as start (already drawn)
                bool endSameAsStart = Math.Abs(startPoint.X - endPoint.X) < closedLoopTolerance &&
                                     Math.Abs(startPoint.Y - endPoint.Y) < closedLoopTolerance;
                
                if (!endSameAsStart)
                {
                    if (endIsClosed)
                    {
                        // This end point coincides with a start point from another group - purple
                        var closedIndicator = new Ellipse
                        {
                            Width = indicatorSize,
                            Height = indicatorSize,
                            Fill = new SolidColorBrush(Color.FromArgb(180, 128, 0, 128)), // Purple fill
                            Stroke = new SolidColorBrush(Color.FromRgb(100, 0, 100)), // Dark purple border
                            StrokeThickness = 2,
                            IsHitTestVisible = false,
                            SnapsToDevicePixels = true
                        };
                        Canvas.SetLeft(closedIndicator, endPoint.X - indicatorSize / 2.0);
                        Canvas.SetTop(closedIndicator, endPoint.Y - indicatorSize / 2.0);
                        Panel.SetZIndex(closedIndicator, 5);
                        DrawCanvas.Children.Add(closedIndicator);
                    }
                    else
                    {
                        // Normal end indicator (red)
                        var endIndicator = new Ellipse
                        {
                            Width = indicatorSize,
                            Height = indicatorSize,
                            Fill = new SolidColorBrush(Color.FromArgb(180, 220, 50, 50)), // Red fill
                            Stroke = new SolidColorBrush(Color.FromRgb(160, 30, 30)), // Dark red border
                            StrokeThickness = 2,
                            IsHitTestVisible = false,
                            SnapsToDevicePixels = true
                        };
                        Canvas.SetLeft(endIndicator, endPoint.X - indicatorSize / 2.0);
                        Canvas.SetTop(endIndicator, endPoint.Y - indicatorSize / 2.0);
                        Panel.SetZIndex(endIndicator, 5);
                        DrawCanvas.Children.Add(endIndicator);
                    }
                }
            }
        }

        /// <summary>
        /// Calculates a heat map color gradient from green (t=0) through yellow (t=0.5) to red (t=1).
        /// Returns both fill color (semi-transparent) and stroke color (darker border).
        /// </summary>
        private static (Color fill, Color stroke) GetHeatMapColor(double t)
        {
            // Clamp t to [0, 1]
            t = Math.Clamp(t, 0.0, 1.0);
            
            byte r, g, b;
            
            if (t < 0.5)
            {
                // Green to Yellow: increase red from 0 to 220
                var localT = t * 2.0; // 0 to 1 within first half
                r = (byte)(localT * 220);
                g = 180;
                b = 0;
            }
            else
            {
                // Yellow to Red: decrease green from 180 to 50
                var localT = (t - 0.5) * 2.0; // 0 to 1 within second half
                r = 220;
                g = (byte)(180 - localT * 130); // 180 -> 50
                b = (byte)(localT * 50); // Add slight blue tint to red
            }
            
            // Fill is semi-transparent
            var fill = Color.FromArgb(180, r, g, b);
            
            // Stroke is darker version
            var stroke = Color.FromRgb(
                (byte)(r * 0.7),
                (byte)(g * 0.5),
                (byte)(b * 0.5));
            
            return (fill, stroke);
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
                // Update paint wells grid (new compact view)
                if (FindName("PaintWellsGrid") is ItemsControl grid)
                {
                    grid.ItemsSource = null;
                    grid.ItemsSource = _doc.PaintWells;
                }

                // Update hidden paint wells list (for backward compatibility with selection logic)
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

                // Update selected well properties
                    UpdateSelectedWellPropertiesUI();
                
                    // Update chip highlights after the grid is populated
                    Dispatcher.BeginInvoke(new Action(UpdatePaintWellChipHighlights), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                finally
                {
                    _suppressPaintWellUIUpdate = false;
                }
        }

        /// <summary>
        /// Updates the visual highlight on paint well chips to show which is active.
        /// Uses a bright cyan/blue glow effect for the selected chip that's visible in both light and dark modes.
        /// </summary>
        private void UpdatePaintWellChipHighlights()
        {
            if (FindName("PaintWellsGrid") is not ItemsControl grid) return;

            var activeWellId = _paintWellController.ActiveColorWell?.Id;

            // Bright cyan-blue glow color that's visible in both light and dark modes
            var glowColor = Color.FromRgb(0, 200, 255); // Bright cyan (#00C8FF)

            // Iterate through all items in the grid
            for (int i = 0; i < grid.Items.Count; i++)
            {
                var container = grid.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                if (container == null) continue;

                // Find the SelectionHighlight border in the visual tree
                var highlightBorder = FindVisualChild<Border>(container, "SelectionHighlight");
                if (highlightBorder == null) continue;

                // Find the inner WellChip to get the paint well data
                var wellChip = FindVisualChild<Border>(container, "WellChip");
                if (wellChip?.Tag is not PaintWell well) continue;

                // Apply glow effect if this is the active well
                if (activeWellId.HasValue && well.Id == activeWellId.Value)
                {
                    // Active well: show bright glow effect with large blur radius for prominence
                    wellChip.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = glowColor,
                        BlurRadius = 20,
                        ShadowDepth = 0,
                        Opacity = 1.0
                    };
                }
                else
                {
                    // Not active: remove glow effect
                    wellChip.Effect = null;
                }
            }
        }

        /// <summary>
        /// Finds a child element of the specified type and name in the visual tree.
        /// </summary>
        private static T? FindVisualChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild && child is FrameworkElement fe && fe.Name == childName)
                {
                    return typedChild;
                }

                var found = FindVisualChild<T>(child, childName);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
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

        private void ClearActivePaintWell_Click(object sender, RoutedEventArgs e)
        {
            _paintWellController.SetActiveColor(null);
            _paintWellController.SelectWell(null); // Clear selection in drawing area (removes resize handles)
            
            // Also clear the hidden ListBox selection (controls property panel visibility)
            _suppressPaintWellUIUpdate = true;
            if (FindName("PaintWellsList") is ListBox list)
            {
                list.SelectedItem = null;
            }
            _suppressPaintWellUIUpdate = false;
            
            UpdatePaintWellChipHighlights(); // Clear the glow effect from all chips
            UpdateSelectedWellPropertiesUI(); // Update the properties panel
            RenderAll();
            AppendLog("Active color cleared (using black).");
        }

        private void PaintWellsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not ListBox list) return;

            var well = list.SelectedItem as PaintWell;
            _paintWellController.SelectWell(well);
            UpdateSelectedWellPropertiesUI();
        }

        /// <summary>
        /// Handles click on a paint well color chip in the grid view.
        /// If strokes are selected, automatically applies the color to them.
        /// </summary>
        private void PaintWellChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_suppressPaintWellUIUpdate) return;
            if (sender is not FrameworkElement element) return;
            if (element.Tag is not PaintWell well) return;

            // Select the well for editing
            _paintWellController.SelectWell(well);
            
            // Also set as active color for painting new strokes
            _paintWellController.SetActiveColor(well);
            
            // AUTO-APPLY: If strokes are selected, apply this color to them immediately
            if (_selectionController.HasSelection && _selectionController.SelectedIndices.Count > 0)
            {
                foreach (var idx in _selectionController.SelectedIndices)
                {
                    if (idx >= 0 && idx < _doc.Strokes.Count)
                    {
                        _doc.Strokes[idx].PaintWellId = well.Id;
                    }
                }
                _lastGcode = ""; // Invalidate G-code cache
                AppendLog($"Applied '{well.Name}' color to {_selectionController.SelectedIndices.Count} stroke(s).");
            }
            else
            {
                AppendLog($"Active color set to: {well.Name}");
            }
            
            // Also sync the hidden ListBox selection for backward compatibility
            if (FindName("PaintWellsList") is ListBox list)
            {
                _suppressPaintWellUIUpdate = true;
                list.SelectedItem = well;
                _suppressPaintWellUIUpdate = false;
            }
            
            UpdateSelectedWellPropertiesUI();
            UpdatePaintWellChipHighlights(); // Update visual highlight
            RenderAll(); // Update visuals to show active well and applied colors
            e.Handled = true;
        }

        private void AddPaintWellBtn_Click(object sender, RoutedEventArgs e)
        {
            // Create a new default-sized paint well near the center of the drawing area
            const double defaultWidth = 100;
            const double defaultHeight = 80;
            
            // Calculate center position, offset by number of existing wells to avoid overlap
            var existingCount = _doc.PaintWells.Count;
            var offsetX = (existingCount % 5) * 20; // Stagger horizontally
            var offsetY = (existingCount / 5) * 20; // Stagger vertically after 5
            
            var centerX = (_doc.WidthMm / 2.0) - (defaultWidth / 2.0) + offsetX;
            var centerY = (_doc.HeightMm / 2.0) - (defaultHeight / 2.0) + offsetY;
            
            // Ensure the well stays within document bounds
            centerX = Math.Clamp(centerX, 0, _doc.WidthMm - defaultWidth);
            centerY = Math.Clamp(centerY, 0, _doc.HeightMm - defaultHeight);
            
            var bounds = new Rect(centerX, centerY, defaultWidth, defaultHeight);
            var index = existingCount + 1;
            var color = GetDefaultPaintWellColor(index);
            var name = $"Paint {index}";
            
            _paintWellController.CreateWell(bounds, color, name);
            _lastGcode = "";
            UpdatePaintWellsUI();
            
            AppendLog($"Created paint well '{name}' at ({centerX:0}, {centerY:0})");
        }

        /// <summary>
        /// Gets a default color for a new paint well based on its index.
        /// </summary>
        private static Color GetDefaultPaintWellColor(int index)
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
            _undoGroupSizes.Clear();
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
                    PaintWellId = stroke.PaintWellId,
                    GroupId = stroke.GroupId
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

            // Save settings
            projectFile.Settings = new ProjectSettings
            {
                // Document settings
                PagePresetIndex = PagePresetCombo?.SelectedIndex ?? 0,
                HomeCornerIndex = HomeCornerCombo?.SelectedIndex ?? 0,

                // Snap settings
                SnapEnabled = (FindName("SnapEnabledCheck") as CheckBox)?.IsChecked ?? true,
                SnapRadius = ParseDouble((FindName("SnapRadiusBox") as TextBox)?.Text, 5.0),

                // Grid settings
                ShowGrid = (FindName("ShowGridCheck") as CheckBox)?.IsChecked ?? true,
                SnapToGrid = (FindName("SnapToGridCheck") as CheckBox)?.IsChecked ?? true,
                GridSpacing = ParseDouble((FindName("GridSpacingBox") as TextBox)?.Text, 5.0),

                // View settings
                Zoom = ZoomSlider?.Value ?? 0.6,
                CanvasRotation = _canvasRotation.Angle,

                // Painting mode settings
                PaintModeEnabled = (FindName("PaintModeEnabledCheck") as CheckBox)?.IsChecked ?? false,
                AutoWashWipe = (FindName("AutoWashWipeCheck") as CheckBox)?.IsChecked ?? true,

                // G-code settings
                FeedXY = ParseDouble(FeedXYBox?.Text, 3000),
                ZUp = ParseDouble(ZUpBox?.Text, 10),
                ZDown = ParseDouble(ZDownBox?.Text, 2),
                SafeMargin = ParseDouble((FindName("SafeMarginBox") as TextBox)?.Text, 50),
                ShowMarginOverlay = (FindName("ShowMarginOverlayCheck") as CheckBox)?.IsChecked ?? true,
                OptimizeStrokes = OptimizeCheck?.IsChecked ?? false,
                StartGcode = (FindName("StartGcodeBox") as TextBox)?.Text ?? "",
                EndGcode = (FindName("EndGcodeBox") as TextBox)?.Text ?? ""
            };

            // Save working area if defined
            if (_workingAreaManager.DefinedArea is Rect area)
            {
                projectFile.Settings.WorkingArea = new WorkingAreaData
                {
                    Left = area.Left,
                    Top = area.Top,
                    Width = area.Width,
                    Height = area.Height
                };
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
                    strokeData.PaintWellId,
                    strokeData.GroupId);
                _doc.Strokes.Add(stroke);
            }

            // Load settings (with null checks for backward compatibility)
            var settings = projectFile.Settings ?? new ProjectSettings();

            // Document settings
            if (PagePresetCombo != null) PagePresetCombo.SelectedIndex = settings.PagePresetIndex;
            if (HomeCornerCombo != null) HomeCornerCombo.SelectedIndex = settings.HomeCornerIndex;

            // Snap settings
            if (FindName("SnapEnabledCheck") is CheckBox snapCheck) snapCheck.IsChecked = settings.SnapEnabled;
            if (FindName("SnapRadiusBox") is TextBox snapRadiusBox) snapRadiusBox.Text = settings.SnapRadius.ToString(CultureInfo.InvariantCulture);

            // Grid settings
            if (FindName("ShowGridCheck") is CheckBox showGridCheck) showGridCheck.IsChecked = settings.ShowGrid;
            if (FindName("SnapToGridCheck") is CheckBox snapToGridCheck) snapToGridCheck.IsChecked = settings.SnapToGrid;
            if (FindName("GridSpacingBox") is TextBox gridSpacingBox) gridSpacingBox.Text = settings.GridSpacing.ToString(CultureInfo.InvariantCulture);

            // View settings
            if (ZoomSlider != null) ZoomSlider.Value = settings.Zoom;
            _canvasRotation.Angle = settings.CanvasRotation;

            // Painting mode settings
            if (FindName("PaintModeEnabledCheck") is CheckBox paintModeCheck) paintModeCheck.IsChecked = settings.PaintModeEnabled;
            if (FindName("AutoWashWipeCheck") is CheckBox autoWashCheck) autoWashCheck.IsChecked = settings.AutoWashWipe;

            // G-code settings
            if (FeedXYBox != null) FeedXYBox.Text = settings.FeedXY.ToString(CultureInfo.InvariantCulture);
            if (ZUpBox != null) ZUpBox.Text = settings.ZUp.ToString(CultureInfo.InvariantCulture);
            if (ZDownBox != null) ZDownBox.Text = settings.ZDown.ToString(CultureInfo.InvariantCulture);
            if (FindName("SafeMarginBox") is TextBox safeMarginBox) safeMarginBox.Text = settings.SafeMargin.ToString(CultureInfo.InvariantCulture);
            if (FindName("ShowMarginOverlayCheck") is CheckBox showMarginCheck) showMarginCheck.IsChecked = settings.ShowMarginOverlay;
            if (OptimizeCheck != null) OptimizeCheck.IsChecked = settings.OptimizeStrokes;
            if (FindName("StartGcodeBox") is TextBox startGcodeBox) startGcodeBox.Text = settings.StartGcode;
            if (FindName("EndGcodeBox") is TextBox endGcodeBox) endGcodeBox.Text = settings.EndGcode;

            // Working area
            _workingAreaManager.Clear();
            if (settings.WorkingArea != null)
            {
                var area = new Rect(
                    settings.WorkingArea.Left,
                    settings.WorkingArea.Top,
                    settings.WorkingArea.Width,
                    settings.WorkingArea.Height);
                _workingAreaManager.SetArea(area);
            }

            // Reset state
            _undoGroupSizes.Clear();
            _lastGcode = "";
            _currentProjectPath = filePath;
            _imageService.Clear();
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

        // ===== THEME TOGGLE =====

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
            {
                ThemeManager.Instance.ToggleTheme();
                toggle.IsChecked = ThemeManager.Instance.IsDarkMode;
            }
        }

        /// <summary>
        /// Initializes the theme toggle button state based on current theme.
        /// </summary>
        private void InitializeThemeToggle()
        {
            if (FindName("ThemeToggle") is System.Windows.Controls.Primitives.ToggleButton toggle)
            {
                toggle.IsChecked = ThemeManager.Instance.IsDarkMode;
            }

            // Subscribe to theme changes to update toggle state
            ThemeManager.Instance.ThemeChanged += (_, isDark) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (FindName("ThemeToggle") is System.Windows.Controls.Primitives.ToggleButton t)
                    {
                        t.IsChecked = isDark;
                    }
                });
            };
        }

    }

 }
