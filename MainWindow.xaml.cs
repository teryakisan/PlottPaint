using Microsoft.Win32;
using NVSPlotter.Models;
using NVSPlotter.Properties;
using NVSPlotter.Services;
using NVSPlotter.Util;
using NVSPlotter.Windows;
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
        // Coordinate transformer Service
        private readonly CoordinateTransformService _coordTransform;

        // Snapping Service
        private readonly SnappingService _snappingService;

        // G-code Generator Service
        private readonly GcodeGeneratorService _gcodeGenerator;

        // Canvas Renderer Service
        private readonly CanvasRendererService _canvasRenderer;

        // GRBL Manager Service
        private readonly GrblManagerService _grblManager;

        // Project File Service
        private readonly ProjectFileService _projectFileService;

        // Floating reference image window
        private ReferenceImageWindow? _referenceImageWindow;
        private UIElement? _referenceExpanderContentsParented;

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
        
        // P/Invoke for window title bar dark mode
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private void BrushProfilesBtn_Click(object sender, RoutedEventArgs e)
        {
            var w = new Windows.BrushStrokeProfilesWindow { Owner = this };
            w.Show();
            w.Activate();
        }

        // G-code cache
        private string _lastGcode = "";

        private const double SAFE_MARGIN_MM = 50.0; // Default fallback, actual value from Settings
        private double SafeMarginMm => Settings.Default.safeMarginMm;
        
        // Individual margin properties
        private bool UseIndividualMargins => Settings.Default.useIndividualMargins;
        private double MarginLeftMm => Settings.Default.marginLeftMm;
        private double MarginTopMm => Settings.Default.marginTopMm;
        private double MarginRightMm => Settings.Default.marginRightMm;
        private double MarginBottomMm => Settings.Default.marginBottomMm;
        
        /// <summary>
        /// Gets the effective margins (left, top, right, bottom).
        /// Uses individual margins if enabled, otherwise uses uniform safe margin.
        /// </summary>
        private (double Left, double Top, double Right, double Bottom) GetEffectiveMargins()
        {
            if (UseIndividualMargins)
            {
                return (MarginLeftMm, MarginTopMm, MarginRightMm, MarginBottomMm);
            }
            var margin = SafeMarginMm;
            return (margin, margin, margin, margin);
        }

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

            // Initialize GRBL manager service first (needed by coordinate transform)
            _grblManager = new GrblManagerService(AppendLog);

            // Initialize coordinate transform service
            _coordTransform = new CoordinateTransformService(
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                CanvasScroll ?? throw new InvalidOperationException("CanvasScroll control is missing."),
                () => _doc,
                () => _grblManager.BedX,
                () => _grblManager.BedY,
                () => _grblManager.HomeAtMaxX,
                () => _grblManager.HomeAtMaxY,
                () => SafeMarginMm);

            // Initialize snapping service
            _snappingService = new SnappingService(
                () => _doc,
                () => IsSnapEnabled,
                () => GetSnapRadius(),
                () => IsSnapToGridEnabled,
                () => GetGridSpacing(),
                () => SafeMarginMm,
                () => GetEffectiveMargins(),
                () => Settings.Default.LockMarginsToCanvas,
                () => _workingAreaManager.DefinedArea);

            // Initialize G-code generator service
            _gcodeGenerator = new GcodeGeneratorService(
                _coordTransform,
                () => _doc,
                AppendLog);

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

            // Subscribe to paint well events for automatic UI updates
            _paintWellController.PaintWellsChanged += OnPaintWellsChanged;

            // Initialize canvas renderer service
            _canvasRenderer = new CanvasRendererService(
                DrawCanvas ?? throw new InvalidOperationException("DrawCanvas control is missing."),
                RulerCanvas ?? throw new InvalidOperationException("RulerCanvas control is missing."),
                () => _doc,
                _selectionController,
                _paintWellController,
                _imageService,
                _workingAreaManager,
                AppendLog,
                () => _showIntermediatePointsForGroups);

            // Initialize project file service
            _projectFileService = new ProjectFileService(AppendLog);

            // Subscribe to GRBL manager events
            _grblManager.ConnectionStateChanged += (s, e) => UpdateConnStatus();
            _grblManager.MachineStateChanged += (s, e) => RenderAll();

             RenderAll();
             UpdateConnStatus();
             InitializeSafeMarginBox();
             InitializeConsoleWindow();
             InitializeToolsWindow();
             UpdatePaintWellsUI();
             InitializeThemeToggle();
             
             // Apply window chrome theme after window is loaded
             Loaded += (s, e) => ApplyWindowChromeTheme();
             
             // Fit the drawing area to the available height on initial load
             // Use ContentRendered event because viewport dimensions are not accurate until layout is complete
             ContentRendered += (s, e) => FitToHeight();
             
             // Subscribe to theme changes to update title bar
             ThemeManager.Instance.ThemeChanged += (s, isDark) => ApplyWindowChromeTheme();

            // Wire up ReferenceView events when present
            if (FindName("ReferenceView") is ReferenceImageView rv)
            {
                rv.ImportClicked += ImportImageBtn_Click;
                rv.ClearClicked += ClearImageBtn_Click;
                rv.ImageLockCheckBox.Click += ImageLockCheck_Click;
                rv.RotationChanged += ImageRotateSlider_ValueChanged;
                rv.RotationResetClicked += ImageRotateResetBtn_Click;
                rv.FilterChanged += ImageFilterCombo_SelectionChanged;
                rv.FilterSliderChanged += FilterControlSlider_ValueChanged;
            }
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
        // NOTE: Page/Bed size is now configured via Machine Settings dialog (MachineSettingsWindow)
        // The PagePresetCombo has been removed from the UI

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

        /// <summary>
        /// Fits the drawing area to the available viewport height, accounting for the plotter size setting.
        /// This is called on application startup to show the full drawing area height.
        /// </summary>
        private void FitToHeight()
        {
            var viewportH = CanvasScroll.ViewportHeight;
            if (viewportH <= 0) return;

            const double rulerThickness = 18.0;
            const double padding = 40.0; // Account for canvas margin/padding
            
            // Calculate zoom level to fit the document height in the viewport
            var zy = viewportH / (_doc.HeightMm + rulerThickness + padding);
            var z = Math.Clamp(zy * 0.95, ZoomSlider.Minimum, ZoomSlider.Maximum);
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

        private void MachineSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MachineSettingsWindow(_grblManager, OnMachineSettingsChanged)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        private void OnMachineSettingsChanged()
        {
            // Update the GRBL manager with new manual settings if in manual mode
            var isManualMode = Settings.Default.bedSizeMode?.Equals("Manual", StringComparison.OrdinalIgnoreCase) == true;
            
            if (isManualMode || !_grblManager.IsConnected)
            {
                // Apply manual bed size settings
                _grblManager.SetManualBedSize(
                    Settings.Default.bedX,
                    Settings.Default.bedY,
                    Settings.Default.manualHomeAtMaxX,
                    Settings.Default.manualHomeAtMaxY);
            }
            
            // Update the document size to match the new bed dimensions
            // This will update the drawing area, rulers, and all measurements
            var newWidth = Settings.Default.bedX;
            var newHeight = Settings.Default.bedY;
            
            if (Math.Abs(_doc.WidthMm - newWidth) > 0.001 || Math.Abs(_doc.HeightMm - newHeight) > 0.001)
            {
                // Resize the document - preserve existing strokes and paint wells
                _doc.ResizePreserveContent(newWidth, newHeight);
                
                // Clear working area if it's now outside the document bounds
                if (_workingAreaManager.DefinedArea is Rect area)
                {
                    if (area.Right > newWidth || area.Bottom > newHeight)
                    {
                        _workingAreaManager.Clear();
                        UpdateWorkingAreaStatus();
                        AppendLog("Working area cleared (exceeded new document bounds).");
                    }
                }
            }
            
            // Invalidate G-code cache and re-render
            _lastGcode = "";
            RenderAll();
            AppendLog($"Machine settings updated: {newWidth} × {newHeight} mm");
        }

        private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_grblManager.IsConnected)
            {
                await _grblManager.DisconnectAsync();
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

            var autoHome = FindName("AutoHomeOnConnectCheck") is CheckBox autoHomeCheck && autoHomeCheck.IsChecked == true;
            
            var success = await _grblManager.ConnectAsync(port, baud, autoHome);
            
            if (success)
            {
                AppendLog("Connected to GRBL.");
            }
            
            UpdateConnStatus();
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

            //old
            //var pCanvas = CanvasScroll.TranslatePoint(pViewportSpace, DrawCanvas);
            //return new PointMm(pCanvas.X, pCanvas.Y);

            //new
            //var canvasPoint = _canvasScroll.TranslatePoint(viewportPoint, _drawCanvas);
            //return new PointMm(canvasPoint.X, canvasPoint.Y);

            return _coordTransform.MouseToMm(pViewportSpace);
        }

        private PointMm ClampToPage(PointMm p)
        {
            //var x = Math.Clamp(p.X, 0, _doc.WidthMm);
            //var y = Math.Clamp(p.Y, 0, _doc.HeightMm);
            //return new PointMm(x, y);

            return _coordTransform.ClampToPage(p);
        }

        // ===== ENDPOINT SNAPPING =====

        private enum SnapType { None, Start, End }

        private bool IsSnapEnabled => FindName("SnapEnabledCheck") is CheckBox cb && cb.IsChecked == true;

        private double GetSnapRadius()
        {
            if (FindName("SnapRadiusBox") is not TextBox tb) return 5.0;
            return ParseDouble(tb.Text, 5.0);
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

            // Add dashed crosshair lines ONLY for start (t ˜ 0) and end (t ˜ 1) points
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
            return _snappingService.CalculateSnapPointProgress(snapPoint);
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
            
            // Initialize individual margin controls
            if (FindName("UseIndividualMarginsCheck") is CheckBox useIndividualCheck)
            {
                useIndividualCheck.IsChecked = Settings.Default.useIndividualMargins;
            }
            
            if (FindName("IndividualMarginsPanel") is StackPanel panel)
            {
                panel.Visibility = Settings.Default.useIndividualMargins ? Visibility.Visible : Visibility.Collapsed;
            }
            
            if (FindName("MarginLeftBox") is TextBox leftBox)
            {
                leftBox.Text = Settings.Default.marginLeftMm.ToString("0.##", CultureInfo.InvariantCulture);
            }
            
            if (FindName("MarginTopBox") is TextBox topBox)
            {
                topBox.Text = Settings.Default.marginTopMm.ToString("0.##", CultureInfo.InvariantCulture);
            }
            
            if (FindName("MarginRightBox") is TextBox rightBox)
            {
                rightBox.Text = Settings.Default.marginRightMm.ToString("0.##", CultureInfo.InvariantCulture);
            }
            
            if (FindName("MarginBottomBox") is TextBox bottomBox)
            {
                bottomBox.Text = Settings.Default.marginBottomMm.ToString("0.##", CultureInfo.InvariantCulture);
            }
            
            // Initialize lock margins to canvas checkbox
            if (FindName("LockMarginsToCanvasCheck") is CheckBox lockMarginsCheck)
            {
                lockMarginsCheck.IsChecked = Settings.Default.LockMarginsToCanvas;
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

        private void ReferenceImageBtn_Click(object sender, RoutedEventArgs e)
        {
            // If window already open, activate
            if (_referenceImageWindow != null)
            {
                if (_referenceImageWindow.IsVisible)
                {
                    _referenceImageWindow.Activate();
                    return;
                }
            }

            // Use the ReferenceView control instance and detach it from Expander to host in floating window
            if (FindName("ReferenceView") is ReferenceImageView rv)
            {
                // Remove from current parent
                var parent = VisualTreeHelper.GetParent(rv) as Panel;
                if (parent != null)
                {
                    parent.Children.Remove(rv);
                }

                _referenceImageWindow = new ReferenceImageWindow
                {
                    Owner = this
                };
                _referenceImageWindow.SetContent(rv);
                _referenceImageWindow.Closed += ReferenceImageWindow_Closed;
                _referenceExpanderContentsParented = rv;
                _referenceImageWindow.Show();
            }
        }

        private void ReferenceImageWindow_Closed(object? sender, System.EventArgs e)
        {
            if (_referenceImageWindow == null) return;
            // Restore contents back into the expander
            if (_referenceExpanderContentsParented is ReferenceImageView rv && FindName("ReferenceExpander") is Expander exp)
            {
                exp.Content = null;
                exp.Content = rv;
            }

            _referenceImageWindow.Closed -= ReferenceImageWindow_Closed;
            _referenceImageWindow = null;
            _referenceExpanderContentsParented = null;
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

        private void UseIndividualMarginsCheck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb) return;
            
            Settings.Default.useIndividualMargins = cb.IsChecked == true;
            Settings.Default.Save();
            
            // Show/hide individual margin controls
            if (FindName("IndividualMarginsPanel") is StackPanel panel)
            {
                panel.Visibility = cb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
            
            _lastGcode = ""; // Invalidate G-code cache
            RenderAll();
        }

        private void MarginLeftBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;

            var value = ParseDouble(tb.Text, Settings.Default.marginLeftMm);
            value = Math.Clamp(value, 0, 500);

            if (Math.Abs(value - Settings.Default.marginLeftMm) > 0.001)
            {
                Settings.Default.marginLeftMm = value;
                Settings.Default.Save();
                _lastGcode = "";
                RenderAll();
            }
        }

        private void MarginTopBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;

            var value = ParseDouble(tb.Text, Settings.Default.marginTopMm);
            value = Math.Clamp(value, 0, 500);

            if (Math.Abs(value - Settings.Default.marginTopMm) > 0.001)
            {
                Settings.Default.marginTopMm = value;
                Settings.Default.Save();
                _lastGcode = "";
                RenderAll();
            }
        }

        private void MarginRightBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;

            var value = ParseDouble(tb.Text, Settings.Default.marginRightMm);
            value = Math.Clamp(value, 0, 500);

            if (Math.Abs(value - Settings.Default.marginRightMm) > 0.001)
            {
                Settings.Default.marginRightMm = value;
                Settings.Default.Save();
                _lastGcode = "";
                RenderAll();
            }
        }

        private void MarginBottomBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;

            var value = ParseDouble(tb.Text, Settings.Default.marginBottomMm);
            value = Math.Clamp(value, 0, 500);

            if (Math.Abs(value - Settings.Default.marginBottomMm) > 0.001)
            {
                Settings.Default.marginBottomMm = value;
                Settings.Default.Save();
                _lastGcode = "";
                RenderAll();
            }
        }

        private bool IsMarginOverlayVisible => FindName("ShowMarginOverlayCheck") is CheckBox cb && cb.IsChecked == true;


        /// <summary>
        /// Combined snapping: endpoints first (higher priority), then grid.
        /// Returns the final snapped point and snap info for indicator display.
        /// </summary>
        private PointMm ApplySnapping(PointMm raw, out PointMm? endpointSnap, out SnapType snapType, out bool gridSnapped)
        {
            var result = _snappingService.ApplySnapping(raw, out endpointSnap, out var serviceSnapType, out gridSnapped);
            snapType = (SnapType)serviceSnapType; // Cast from service enum to local enum
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
            var isConnected = _grblManager.IsConnected;

            if (ConnStatusLabel != null)
            {
                ConnStatusLabel.Text = isConnected
                    ? $"Connected: {_grblManager.PortName} @ {_grblManager.BaudRate}"
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
            if (PaintCanvasStatus == null) return;
            PaintCanvasStatus.Text = _workingAreaManager.GetStatusText();
            
            // Also update the width/height text boxes
            UpdatePaintCanvasDimensionBoxes();
        }
        
        /// <summary>
        /// Updates the paint canvas width/height text boxes from the current working area.
        /// </summary>
        private void UpdatePaintCanvasDimensionBoxes()
        {
            if (FindName("PaintCanvasWidthBox") is TextBox widthBox &&
                FindName("PaintCanvasHeightBox") is TextBox heightBox)
            {
                if (_workingAreaManager.DefinedArea is Rect area)
                {
                    widthBox.Text = area.Width.ToString("0", CultureInfo.InvariantCulture);
                    heightBox.Text = area.Height.ToString("0", CultureInfo.InvariantCulture);
                }
                else
                {
                    widthBox.Text = "0";
                    heightBox.Text = "0";
                }
            }
        }
        
        private void PaintCanvasWidthBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;
            
            var width = ParseDouble(tb.Text, 0);
            if (width <= 0) return;
            
            // Update the working area width while preserving position
            if (_workingAreaManager.DefinedArea is Rect area)
            {
                var newArea = new Rect(area.X, area.Y, width, area.Height);
                _workingAreaManager.SetArea(newArea);
                RenderAll();
            }
            else if (width > 0)
            {
                // Create a new area starting from the home corner, inside the safe margins
                var height = ParseDouble((FindName("PaintCanvasHeightBox") as TextBox)?.Text, 100);
                if (height <= 0) height = 100;
                
                // Ruler thickness offset (working area is stored in canvas coordinates)
                const double rulerThickness = 18.0;
                
                // Get the effective margins
                var margins = GetEffectiveMargins();
                
                // Calculate the safe area bounds in document coordinates
                var safeLeft = margins.Left;
                var safeTop = margins.Top;
                var safeRight = _doc.WidthMm - margins.Right;
                var safeBottom = _doc.HeightMm - margins.Bottom;
                
                // Position based on home corner, but inside the safe margins
                // Home at MaxX means home is on the right, so canvas starts from left margin
                // Home at MaxY means home is at the bottom, so canvas starts from top margin
                var docX = _grblManager.HomeAtMaxX ? safeLeft : (safeRight - width);
                var docY = _grblManager.HomeAtMaxY ? safeTop : (safeBottom - height);
                
                // Clamp to stay within safe area (in document coordinates)
                docX = Math.Max(safeLeft, Math.Min(docX, safeRight - width));
                docY = Math.Max(safeTop, Math.Min(docY, safeBottom - height));
                
                // Convert to canvas coordinates by adding ruler thickness
                var canvasX = rulerThickness + docX;
                var canvasY = rulerThickness + docY;
                
                var newArea = new Rect(canvasX, canvasY, width, height);
                _workingAreaManager.SetArea(newArea);
                RenderAll();
            }
        }
        
        private void PaintCanvasHeightBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not TextBox tb) return;
            
            var height = ParseDouble(tb.Text, 0);
            if (height <= 0) return;
            
            // Update the working area height while preserving position
            if (_workingAreaManager.DefinedArea is Rect area)
            {
                var newArea = new Rect(area.X, area.Y, area.Width, height);
                _workingAreaManager.SetArea(newArea);
                RenderAll();
            }
            else if (height > 0)
            {
                // Create a new area starting from the home corner, inside the safe margins
                var width = ParseDouble((FindName("PaintCanvasWidthBox") as TextBox)?.Text, 100);
                if (width <= 0) width = 100;
                
                // Ruler thickness offset (working area is stored in canvas coordinates)
                const double rulerThickness = 18.0;
                
                // Get the effective margins
                var margins = GetEffectiveMargins();
                
                // Calculate the safe area bounds in document coordinates
                var safeLeft = margins.Left;
                var safeTop = margins.Top;
                var safeRight = _doc.WidthMm - margins.Right;
                var safeBottom = _doc.HeightMm - margins.Bottom;
                
                // Position based on home corner, but inside the safe margins
                // Home at MaxX means home is on the right, so canvas starts from left margin
                // Home at MaxY means home is at the bottom, so canvas starts from top margin
                var docX = _grblManager.HomeAtMaxX ? safeLeft : (safeRight - width);
                var docY = _grblManager.HomeAtMaxY ? safeTop : (safeBottom - height);
                
                // Clamp to stay within safe area (in document coordinates)
                docX = Math.Max(safeLeft, Math.Min(docX, safeRight - width));
                docY = Math.Max(safeTop, Math.Min(docY, safeBottom - height));
                
                // Convert to canvas coordinates by adding ruler thickness
                var canvasX = rulerThickness + docX;
                var canvasY = rulerThickness + docY;
                
                var newArea = new Rect(canvasX, canvasY, width, height);
                _workingAreaManager.SetArea(newArea);
                RenderAll();
            }
        }
        
        private void LockMarginsToCanvasCheck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb) return;
            
            Settings.Default.LockMarginsToCanvas = cb.IsChecked == true;
            Settings.Default.Save();
            
            _lastGcode = ""; // Invalidate G-code cache
            RenderAll();
            
            AppendLog(cb.IsChecked == true 
                ? "Safe margins now relative to paint canvas area." 
                : "Safe margins now relative to plotter bed.");
        }

       

        

        private void RenderAll()
        {
            if (DrawCanvas == null || RulerCanvas == null) return;

            // Create render settings from current UI state
            var settings = new RenderSettings
            {
                IsGridVisible = IsGridVisible,
                IsMarginOverlayVisible = IsMarginOverlayVisible,
                IsPaintModeEnabled = IsPaintModeEnabled,
                GridSpacing = GetGridSpacing(),
                SafeMarginMm = SafeMarginMm,
                UseIndividualMargins = UseIndividualMargins,
                MarginLeftMm = MarginLeftMm,
                MarginTopMm = MarginTopMm,
                MarginRightMm = MarginRightMm,
                MarginBottomMm = MarginBottomMm,
                LockMarginsToCanvas = Settings.Default.LockMarginsToCanvas,
                PaintCanvasArea = _workingAreaManager.DefinedArea,
                ZoomScale = _zoom.ScaleX,
                CanvasRotationAngle = _canvasRotation.Angle,
                BedX = _grblManager.BedX,
                BedY = _grblManager.BedY,
                BedFromGrbl = _grblManager.BedFromGrbl
            };

            // Delegate to canvas renderer service
            _canvasRenderer.RenderAll(settings);

            // Render reference image manipulation handles (requires event handlers in MainWindow)
            RenderReferenceImageHandles();

            UpdateZoomHost();
        }

        /// <summary>
        /// Renders the reference image manipulation handles that require event handlers.
        /// The image itself is rendered by the CanvasRendererService.
        /// </summary>
        private void RenderReferenceImageHandles()
        {
            if (_imageService.ProcessedImage == null || _imageService.ImageRect is not Rect rect) return;
            if (_imageService.IsLocked) return;

            // Hit box for moving
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

        

        private void ReferenceHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_imageService.ImageRect is null || _imageService.IsLocked) return;
            if (sender is not FrameworkElement fe || fe.Tag is not ImageHandle handle) return;

            _imageManipulator.BeginHandle(e, handle);
        }

        private async void HomeBtn_Click(object sender, RoutedEventArgs e)
        {
            await _grblManager.HomeAsync();
        }

        private async void UnlockBtn_Click(object sender, RoutedEventArgs e)
        {
            await _grblManager.UnlockAsync();
        }

        private async void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            await _grblManager.SoftResetAsync();
        }

        private async void ManualSendBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_grblManager.EnsureConnected()) return;
            var cmd = (ManualCmdBox.Text ?? "").Trim();
            if (cmd.Length == 0) return;

            ManualCmdBox.Text = "";
            try
            {
                // Use direct connection for manual commands
                // This requires exposing the connection or adding a method to the service
                await _grblManager.SendGcodeAsync(cmd);
            }
            catch (Exception ex)
            {
                AppendLog("Manual send failed: " + ex.Message);
            }
        }

        private async void SendBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_grblManager.EnsureConnected()) return;

            if (!_grblManager.IsHomed)
            {
                AppendLog("Refusing to send: not homed. Click Home first.");
                return;
            }

            var g = string.IsNullOrWhiteSpace(_lastGcode) ? BuildGcode() : _lastGcode;

            if (string.IsNullOrWhiteSpace(g))
            {
                AppendLog("No G-code to send.");
                return;
            }

            AppendLog("Sending G-code...");
            await _grblManager.SendGcodeAsync(g, (current, total) =>
            {
                if (current % 25 == 0)
                {
                    AppendLog($"Progress: {current}/{total}");
                }
            });
        }

        private async void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            await _grblManager.StopAsync();
            RenderAll();
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
            var settings = new GcodeSettings
            {
                FeedXY = ParseDouble(FeedXYBox.Text, 3000),
                ZUp = ParseDouble(ZUpBox.Text, 10),
                ZDown = ParseDouble(ZDownBox.Text, 2),
                SafeMarginMm = SafeMarginMm,
                BedX = _grblManager.BedX,
                BedY = _grblManager.BedY,
                Optimize = OptimizeCheck.IsChecked == true,
                PaintModeEnabled = IsPaintModeEnabled,
                AutoWashWipeEnabled = FindName("AutoWashWipeCheck") is CheckBox cb && cb.IsChecked == true
            };

            _lastGcode = _gcodeGenerator.BuildGcode(settings);
            return _lastGcode;
        }

       

        private static double ParseDouble(string? s, double fallback)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return v;
            return fallback;
        }

        // ===== PAINTING MODE / PAINT WELLS =====

        private bool IsPaintModeEnabled => FindName("PaintModeEnabledCheck") is CheckBox cb && cb.IsChecked == true;

        private bool _suppressPaintWellUIUpdate;

        /// <summary>
        /// Handles changes from PaintWellController and updates UI accordingly.
        /// </summary>
        private void OnPaintWellsChanged(object? sender, PaintWellsChangedEventArgs e)
        {
            // Skip if we're already in a UI update (prevents recursion)
            if (_suppressPaintWellUIUpdate) return;

            switch (e.ChangeType)
            {
                case PaintWellChangeType.WellCreated:
                case PaintWellChangeType.WellRemoved:
                case PaintWellChangeType.WellsCleared:
                    // Collection changed - full UI refresh
                    UpdatePaintWellsUI();
                    break;

                case PaintWellChangeType.SelectionChanged:
                    // Selection changed - update property panel and sync ListBox
                    _suppressPaintWellUIUpdate = true;
                    try
                    {
                        if (FindName("PaintWellsList") is ListBox list)
                        {
                            list.SelectedItem = e.AffectedWell;
                        }
                        UpdateSelectedWellPropertiesUI();
                    }
                    finally
                    {
                        _suppressPaintWellUIUpdate = false;
                    }
                    break;

                case PaintWellChangeType.ActiveColorChanged:
                    // Active color changed - update chip highlights
                    UpdatePaintWellChipHighlights();
                    break;

                case PaintWellChangeType.WellUpdated:
                    // Well properties changed - refresh display
                    _suppressPaintWellUIUpdate = true;
                    try
                    {
                        if (FindName("PaintWellsList") is ListBox list)
                        {
                            list.Items.Refresh();
                        }
                        if (FindName("PaintWellsGrid") is ItemsControl grid)
                        {
                            grid.Items.Refresh();
                        }
                    }
                    finally
                    {
                        _suppressPaintWellUIUpdate = false;
                    }
                    break;
            }
        }

        private void PaintModeEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            var isEnabled = IsPaintModeEnabled;
            
            // Enable/disable the paint mode controls panel
            if (FindName("PaintModeControlsPanel") is StackPanel controlsPanel)
            {
                controlsPanel.IsEnabled = isEnabled;
                controlsPanel.Opacity = isEnabled ? 1.0 : 0.5;
            }
            
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
            
            // UI updates (property panel, chip highlights, ListBox selection) are handled 
            // by OnPaintWellsChanged event for SelectionChanged and ActiveColorChanged
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
            var color = PaintWellController.GetDefaultPaintWellColor(index);
            var name = $"Paint {index}";
            
            _paintWellController.CreateWell(bounds, color, name);
            _lastGcode = "";
            // UI update handled by OnPaintWellsChanged event
            
            AppendLog($"Created paint well '{name}' at ({centerX:0}, {centerY:0})");
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
            // UI update handled by OnPaintWellsChanged event
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
                // UI update handled by OnPaintWellsChanged event
                AppendLog("Cleared all paint wells.");
            }
        }

        private void QuickSetupPaintWellsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Confirm if there are existing wells
            if (_doc.PaintWells.Count > 0)
            {
                var result = MessageBox.Show(
                    "This will clear existing paint wells. Continue?",
                    "Quick Setup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;
            }

            // Delegate to controller
            var count = _paintWellController.QuickSetupWells(_doc.WidthMm, _doc.HeightMm);

            _lastGcode = "";
            // UI update handled by OnPaintWellsChanged event (multiple WellCreated events)
            RenderAll();
            AppendLog($"Created {count} paint wells at bottom: Red, Green, Blue, Wash, Wipe.");
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
            // Collect current UI settings
            var settings = new ProjectSettings
            {
                // Document settings - bed size now comes from Settings
                PagePresetIndex = 0, // Deprecated - bed size is now in Settings.Default.bedX/bedY
                HomeCornerIndex = 0, // Deprecated - home position is now in Settings.Default.manualHomeAtMaxX/Y

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

            // Delegate to service
            _projectFileService.SaveProject(filePath, _doc, settings, _workingAreaManager.DefinedArea);
        }

        private void LoadProjectFromFile(string filePath)
        {
            // Load project using service
            var result = _projectFileService.LoadProject(filePath);

            // Update document
            _doc = result.Document;

            // Apply settings to UI
            var settings = result.Settings;

            // Document settings - Page/Home combos have been removed
            // Bed size is now controlled via Machine Settings dialog
            // Legacy project files may have these indices but they are ignored

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
            if (result.WorkingArea is Rect area)
            {
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
        }

        private void UpdateWindowTitle()
        {
            var projectName = ProjectFileService.GetProjectName(_currentProjectPath);
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
        
        /// <summary>
        /// Applies dark/light mode theme to the window's title bar (Windows 10 build 18985+).
        /// Uses DWM API to set immersive dark mode for the window chrome.
        /// </summary>
        private void ApplyWindowChromeTheme()
        {
            try
            {
                // Get window handle
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) return;

                // Determine if we should use dark mode
                var useDarkMode = ThemeManager.Instance.IsDarkMode ? 1 : 0;

                // Try the newer attribute first (Windows 10 build 19041+)
                var result = DwmSetWindowAttribute(
                    handle,
                    DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref useDarkMode,
                    sizeof(int));

                // If that fails, try the older attribute (Windows 10 build 18985-19040)
                if (result != 0)
                {
                    DwmSetWindowAttribute(
                        handle,
                        DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1,
                        ref useDarkMode,
                        sizeof(int));
                }
            }
            catch
            {
                // Silently fail if DWM API is not available (Windows 7/8)
            }
        }

    }

 }
