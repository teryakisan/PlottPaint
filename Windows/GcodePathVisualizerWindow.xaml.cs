using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NVSPlotter.Services;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Clipboard = System.Windows.Clipboard;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace NVSPlotter.Windows;

/// <summary>
/// G-code path visualizer for debugging and analyzing generated tool paths.
/// Allows marking segments as "bad" and outputs them to console for analysis.
/// </summary>
public partial class GcodePathVisualizerWindow : Window
{
    private readonly Action<string> _log;
    private readonly ObservableCollection<GcodeSegment> _segments = new();
    private readonly List<UIElement> _pathElements = new();
    private readonly List<UIElement> _pointElements = new();
    private readonly List<UIElement> _labelElements = new();
    
    // Paint well information parsed from G-code
    private readonly Dictionary<string, ParsedPaintWell> _paintWells = new();
    private bool _isPaintingModeGcode;
    
    private double _zoom = 1.0;
    private double _canvasRotationAngle = 0;
    private Point _panStart;
    private Point _panOrigin;
    private bool _isPanning;
    private GcodeSegment? _hoveredSegment;
    private GcodeSegment? _selectedSegment;
    
    // Path bounds for fitting
    private double _minX, _maxX, _minY, _maxY;
    
    // Colors for different path types
    private static readonly Brush RapidBrush = new SolidColorBrush(Color.FromRgb(100, 100, 255));
    private static readonly Brush FeedBrush = new SolidColorBrush(Color.FromRgb(50, 200, 50));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(255, 50, 50));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(255, 200, 0));
    private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0, 200, 255));
    private static readonly Brush PointBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
    
    public GcodePathVisualizerWindow(Action<string> log)
    {
        _log = log ?? (s => { });
        InitializeComponent();
        
        SegmentList.ItemsSource = _segments;
        
        // Set up keyboard shortcuts
        KeyDown += GcodePathVisualizerWindow_KeyDown;
    }
    
    /// <summary>
    /// Sets the canvas rotation angle (0, 90, 180, 270 degrees).
    /// This syncs the visualizer view with the main canvas rotation.
    /// </summary>
    public void SetCanvasRotation(double angleDegrees)
    {
        _canvasRotationAngle = angleDegrees % 360;
        CanvasRotation.Angle = _canvasRotationAngle;
        
        // Update status to show rotation
        if (_canvasRotationAngle != 0)
        {
            StatusLabel.Text = $"Canvas rotated {_canvasRotationAngle}°";
        }
    }
    
    /// <summary>
    /// Gets the current canvas rotation angle.
    /// </summary>
    public double CanvasRotationAngle => _canvasRotationAngle;
    
    /// <summary>
    /// Loads and parses G-code for visualization.
    /// </summary>
    public void LoadGcode(string gcode)
    {
        _segments.Clear();
        ClearCanvas();
        
        if (string.IsNullOrWhiteSpace(gcode))
        {
            StatusLabel.Text = "No G-code to display";
            return;
        }
        
        StatusLabel.Text = "Parsing G-code...";
        
        // Parse G-code into segments
        ParseGcode(gcode);
        
        // Calculate bounds
        CalculateBounds();
        
        // Render paths
        RenderPaths();
        
        // Fit to view
        FitToView();
        
        UpdateStatusBar();
        
        // Log painting mode info for debugging
        if (_isPaintingModeGcode)
        {
            _log($"[VISUALIZER] Paint Mode G-code detected. Paint wells: {_paintWells.Count}");
            foreach (var well in _paintWells)
            {
                _log($"  - {well.Key}: {well.Value.Name}");
            }
            var strokeCount = _segments.Count(s => s.PaintingStrokeNumber > 0);
            var maxStroke = _segments.Max(s => s.PaintingStrokeNumber);
            _log($"[VISUALIZER] Segments with stroke numbers: {strokeCount}, max stroke: {maxStroke}");
        }
        
        StatusLabel.Text = $"Loaded {_segments.Count} segments";
    }
    
    private void ParseGcode(string gcode)
    {
        var lines = gcode.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        double currentX = 0, currentY = 0, currentZ = 0;
        bool isRapid = true; // G0 is rapid, G1 is feed
        int lineNumber = 0;
        
        // Paint well tracking
        _paintWells.Clear();
        _isPaintingModeGcode = false;
        string? currentPaintWellName = null;
        int paintingStrokeNumber = 0;
        bool penIsDown = false; // Track pen state based on Z movement patterns
        double zUpValue = 0;    // Will be determined from G-code
        double zDownValue = 0;  // Will be determined from G-code
        bool zValuesDetected = false;
        
        // Regex patterns for parsing
        var xPattern = new Regex(@"X(-?\d+\.?\d*)", RegexOptions.IgnoreCase);
        var yPattern = new Regex(@"Y(-?\d+\.?\d*)", RegexOptions.IgnoreCase);
        var zPattern = new Regex(@"Z(-?\d+\.?\d*)", RegexOptions.IgnoreCase);
        
        // Regex for parsing paint well comments: "; Paint Well: Red at (x, y), dip=5mm, dwell=500ms"
        var paintWellPattern = new Regex(@";\s*Paint\s*Well:\s*(\w+)\s+at\s*\((-?\d+\.?\d*),\s*(-?\d+\.?\d*)\)", RegexOptions.IgnoreCase);
        // Regex for paint well switch comments: 
        // "; === Paint Well: Red ===" or "; === Paint Well: Red (Order: 1) ===" or "; === Paint Well: Red (Order: 1, continuing) ==="
        var paintWellSwitchPattern = new Regex(@";\s*===\s*Paint\s*Well:\s*(\w+)(?:\s*\([^)]*\))?\s*===", RegexOptions.IgnoreCase);
        // Regex for color change: "; === Color change: Red -> Blue ===" or with order info
        var colorChangePattern = new Regex(@";\s*===\s*Color\s*change:.*->\s*(\w+)(?:\s*\([^)]*\))?\s*===", RegexOptions.IgnoreCase);
        // Regex for "No Paint Well" comments: "; === No Paint Well (Order: 1) ==="
        var noPaintWellPattern = new Regex(@";\s*===\s*No\s*Paint\s*Well", RegexOptions.IgnoreCase);
        
        // First pass: scan for paint wells, painting mode, and detect Z values
        var zValues = new List<double>();
        string? pendingWellName = null; // Track when we're about to get paint well coordinates
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Check for painting mode header
            if (line.Contains("PAINTING MODE ENABLED", StringComparison.OrdinalIgnoreCase))
            {
                _isPaintingModeGcode = true;
            }
            
            // Parse paint well definitions - just register the name and color
            var wellMatch = paintWellPattern.Match(line);
            if (wellMatch.Success)
            {
                var wellName = wellMatch.Groups[1].Value;
                if (!_paintWells.ContainsKey(wellName))
                {
                    _paintWells[wellName] = new ParsedPaintWell
                    {
                        Name = wellName,
                        Color = GetColorForPaintWellName(wellName)
                    };
                }
            }
            
            // Check for "Dip in paint well: Name" comment - this tells us which well the next move goes to
            if (line.Contains("; Dip in paint well:", StringComparison.OrdinalIgnoreCase))
            {
                var dipMatch = Regex.Match(line, @";\s*Dip\s+in\s+paint\s+well:\s*(\w+)", RegexOptions.IgnoreCase);
                if (dipMatch.Success)
                {
                    pendingWellName = dipMatch.Groups[1].Value;
                }
            }
            
            // Check for "Move to paint well" comment followed by G0 X Y - capture work coordinates
            if (line.Contains("; Move to paint well", StringComparison.OrdinalIgnoreCase) && pendingWellName != null)
            {
                // Look at the command part before the comment
                var commandPart = line.Split(';')[0].Trim();
                if (!string.IsNullOrEmpty(commandPart))
                {
                    var xMatch = xPattern.Match(commandPart);
                    var yMatch = yPattern.Match(commandPart);
                    if (xMatch.Success && yMatch.Success)
                    {
                        if (double.TryParse(xMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wx) &&
                            double.TryParse(yMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wy))
                        {
                            // Update the paint well with actual work coordinates
                            if (_paintWells.TryGetValue(pendingWellName, out var well))
                            {
                                well.X = wx;
                                well.Y = wy;
                            }
                        }
                    }
                }
                pendingWellName = null;
            }
            
            // Collect Z values to determine up/down thresholds
            if (!line.StartsWith(";"))
            {
                var zMatch = zPattern.Match(line);
                if (zMatch.Success && double.TryParse(zMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                {
                    if (!zValues.Contains(z))
                        zValues.Add(z);
                }
            }
        }
        
        // Determine Z up/down values (typically two main values used)
        // In this G-code: Z-10 is pen UP (most negative), Z-2 is pen DOWN (less negative)
        // So pen is DOWN when Z is GREATER than the minimum (closer to 0)
        if (zValues.Count >= 2)
        {
            zValues.Sort();
            // Most negative value is pen UP (travel height), e.g., -10
            // The drawing happens at values closer to 0, e.g., -2
            zUpValue = zValues.First();    // Most negative = pen up/travel (e.g., -10)
            zDownValue = zValues.Last();   // Least negative = pen down/draw (e.g., -2)
            zValuesDetected = true;
        }
        
        // Second pass: parse segments with paint well associations
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            
            // Check for paint well switch comments (even in comment-only lines)
            var wellSwitchMatch = paintWellSwitchPattern.Match(line);
            if (wellSwitchMatch.Success)
            {
                currentPaintWellName = wellSwitchMatch.Groups[1].Value;
                // Ensure this paint well is registered
                if (!_paintWells.ContainsKey(currentPaintWellName))
                {
                    _paintWells[currentPaintWellName] = new ParsedPaintWell
                    {
                        Name = currentPaintWellName,
                        Color = GetColorForPaintWellName(currentPaintWellName)
                    };
                }
                continue;
            }
            
            var colorChangeMatch = colorChangePattern.Match(line);
            if (colorChangeMatch.Success)
            {
                currentPaintWellName = colorChangeMatch.Groups[1].Value;
                if (!_paintWells.ContainsKey(currentPaintWellName))
                {
                    _paintWells[currentPaintWellName] = new ParsedPaintWell
                    {
                        Name = currentPaintWellName,
                        Color = GetColorForPaintWellName(currentPaintWellName)
                    };
                }
                continue;
            }
            
            // Check for "No Paint Well" which means strokes without a color assigned
            if (noPaintWellPattern.IsMatch(line))
            {
                currentPaintWellName = null;
                continue;
            }
            
            // Skip empty lines and pure comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";"))
                continue;
            
            // Remove inline comments
            var commentIndex = line.IndexOf(';');
            var command = commentIndex >= 0 ? line.Substring(0, commentIndex).Trim() : line;
            
            if (string.IsNullOrWhiteSpace(command))
                continue;
            
            // Check for G0/G1 commands
            if (command.StartsWith("G0", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("G00", StringComparison.OrdinalIgnoreCase))
            {
                isRapid = true;
            }
            else if (command.StartsWith("G1", StringComparison.OrdinalIgnoreCase) ||
                     command.StartsWith("G01", StringComparison.OrdinalIgnoreCase))
            {
                isRapid = false;
            }
            
            // Parse coordinates
            double? newX = null, newY = null, newZ = null;
            
            var xMatch = xPattern.Match(command);
            if (xMatch.Success && double.TryParse(xMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x))
                newX = x;
            
            var yMatch = yPattern.Match(command);
            if (yMatch.Success && double.TryParse(yMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                newY = y;
            
            var zMatch = zPattern.Match(command);
            if (zMatch.Success && double.TryParse(zMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                newZ = z;
            
            // Track pen up/down state for stroke numbering
            // Pen is DOWN when Z is greater than zUpValue (closer to 0)
            // e.g., Z=-2 (drawing) > Z=-10 (travel), so -2 > -10 means pen is down
            if (newZ.HasValue && zValuesDetected)
            {
                var wasPenDown = penIsDown;
                // Pen is down when Z is greater than the up value (closer to 0 than travel height)
                penIsDown = newZ.Value > zUpValue + 0.5;
                
                // Starting a new painting stroke (pen going down)
                if (!wasPenDown && penIsDown)
                {
                    paintingStrokeNumber++;
                }
            }
            
            // If we have X or Y movement, create a segment
            if (newX.HasValue || newY.HasValue)
            {
                var fromX = currentX;
                var fromY = currentY;
                var fromZ = currentZ;
                
                if (newX.HasValue) currentX = newX.Value;
                if (newY.HasValue) currentY = newY.Value;
                if (newZ.HasValue) currentZ = newZ.Value;
                
                // Determine paint well color
                Color? paintWellColor = null;
                if (currentPaintWellName != null && _paintWells.TryGetValue(currentPaintWellName, out var well))
                {
                    paintWellColor = well.Color;
                }
                
                // For painting mode, assign stroke number to feed movements when pen is down
                var strokeNum = 0;
                if (_isPaintingModeGcode && !isRapid && penIsDown)
                {
                    strokeNum = paintingStrokeNumber;
                }
                
                var segment = new GcodeSegment
                {
                    LineNumber = lineNumber,
                    GcodeCommand = line,
                    FromX = fromX,
                    FromY = fromY,
                    FromZ = fromZ,
                    ToX = currentX,
                    ToY = currentY,
                    ToZ = currentZ,
                    IsRapid = isRapid,
                    PaintWellName = currentPaintWellName,
                    PaintWellColor = paintWellColor,
                    PaintingStrokeNumber = strokeNum
                };
                
                _segments.Add(segment);
            }
            else if (newZ.HasValue)
            {
                // Z-only movement (pen up/down)
                currentZ = newZ.Value;
            }
        }
    }
    
    /// <summary>
    /// Gets a color for a paint well based on its name.
    /// </summary>
    private static Color GetColorForPaintWellName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "red" => Colors.Red,
            "blue" => Colors.Blue,
            "green" => Colors.Green,
            "orange" => Colors.Orange,
            "purple" => Colors.Purple,
            "cyan" => Colors.Cyan,
            "magenta" => Colors.Magenta,
            "yellow" => Colors.Yellow,
            "brown" => Colors.Brown,
            "pink" => Colors.HotPink,
            "black" => Colors.Black,
            "white" => Colors.White,
            "gray" or "grey" => Colors.Gray,
            "wash" => Colors.DarkBlue,
            "wipe" => Colors.DarkGray,
            _ => Colors.Black
        };
    }
    
    /// <summary>
    /// Lightens a color for better visibility in dark mode.
    /// </summary>
    private static Color LightenColorForDarkMode(Color color)
    {
        // Lighten the color by blending with white
        const double lightenFactor = 0.4; // 40% towards white
        return Color.FromArgb(
            color.A,
            (byte)Math.Min(255, color.R + (255 - color.R) * lightenFactor),
            (byte)Math.Min(255, color.G + (255 - color.G) * lightenFactor),
            (byte)Math.Min(255, color.B + (255 - color.B) * lightenFactor));
    }
    
    /// <summary>
    /// Gets the display color for a paint well, adjusted for dark mode if needed.
    /// </summary>
    private static Color GetDisplayColor(Color baseColor)
    {
        if (ThemeManager.Instance.IsDarkMode)
        {
            return LightenColorForDarkMode(baseColor);
        }
        return baseColor;
    }
    
    private void CalculateBounds()
    {
        if (_segments.Count == 0)
        {
            _minX = _minY = 0;
            _maxX = _maxY = 100;
            return;
        }
        
        _minX = double.MaxValue;
        _minY = double.MaxValue;
        _maxX = double.MinValue;
        _maxY = double.MinValue;
        
        foreach (var seg in _segments)
        {
            _minX = Math.Min(_minX, Math.Min(seg.FromX, seg.ToX));
            _maxX = Math.Max(_maxX, Math.Max(seg.FromX, seg.ToX));
            _minY = Math.Min(_minY, Math.Min(seg.FromY, seg.ToY));
            _maxY = Math.Max(_maxY, Math.Max(seg.FromY, seg.ToY));
        }
        
        // Add some padding
        var padX = (_maxX - _minX) * 0.1;
        var padY = (_maxY - _minY) * 0.1;
        _minX -= padX;
        _maxX += padX;
        _minY -= padY;
        _maxY += padY;
    }
    
    private void ClearCanvas()
    {
        PathCanvas.Children.Clear();
        _pathElements.Clear();
        _pointElements.Clear();
        _labelElements.Clear();
    }
    
    /// <summary>
    /// Renders paint wells as transparent circles with wide colored borders.
    /// </summary>
    private void RenderPaintWells()
    {
        const double wellRadius = 40; // Visual radius in mm
        const double borderThickness = 8;
        
        foreach (var well in _paintWells.Values)
        {
            if (!well.HasPosition) continue;
            
            // Get display color (lightened for dark mode)
            var wellColor = GetDisplayColor(well.Color);
            var transparentFill = Color.FromArgb(40, wellColor.R, wellColor.G, wellColor.B);
            var borderColor = Color.FromArgb(150, wellColor.R, wellColor.G, wellColor.B);
            
            var ellipse = new Ellipse
            {
                Width = wellRadius * 2,
                Height = wellRadius * 2,
                Fill = new SolidColorBrush(transparentFill),
                Stroke = new SolidColorBrush(borderColor),
                StrokeThickness = borderThickness,
                IsHitTestVisible = false
            };
            
            // Position the ellipse (Y is inverted in visualization)
            Canvas.SetLeft(ellipse, well.X - wellRadius);
            Canvas.SetTop(ellipse, -well.Y - wellRadius);
            
            PathCanvas.Children.Add(ellipse);
            
            // Add label with paint well name
            var label = new TextBlock
            {
                Text = well.Name,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(wellColor),
                IsHitTestVisible = false
            };
            
            // Apply counter-rotation so text stays readable when canvas is rotated
            if (_canvasRotationAngle != 0)
            {
                label.RenderTransformOrigin = new Point(0.5, 0.5);
                label.RenderTransform = new RotateTransform(-_canvasRotationAngle);
            }
            
            // Center the label in the well
            Canvas.SetLeft(label, well.X - 15);
            Canvas.SetTop(label, -well.Y - 6);
            
            PathCanvas.Children.Add(label);
        }
    }
    
    private void RenderPaths()
    {
        ClearCanvas();
        
        // Render paint wells first (behind everything else)
        if (_isPaintingModeGcode)
        {
            RenderPaintWells();
        }
        
        var showPaintingOnly = ShowPaintingOnlyToggle.IsChecked == true;
        
        // If painting only mode is selected, use special rendering
        if (showPaintingOnly)
        {
            RenderPaintingStrokesOnly();
            return;
        }
        
        var showRapids = ShowRapidsToggle.IsChecked == true;
        var showFeeds = ShowFeedsToggle.IsChecked == true;
        var showPoints = ShowPointsToggle.IsChecked == true;
        var showDirection = ShowDirectionToggle.IsChecked == true;
        var showLineNumbers = ShowLineNumbersToggle.IsChecked == true;
        
        // First pass: draw all path lines
        foreach (var segment in _segments)
        {
            if (segment.IsRapid && !showRapids) continue;
            if (!segment.IsRapid && !showFeeds) continue;
            
            var line = CreateSegmentLine(segment);
            PathCanvas.Children.Add(line);
            _pathElements.Add(line);
            segment.Visual = line;
            
            // Direction arrow
            if (showDirection)
            {
                var arrow = CreateDirectionArrow(segment);
                if (arrow != null)
                {
                    PathCanvas.Children.Add(arrow);
                    _pathElements.Add(arrow);
                }
            }
            
            // Line number label
            if (showLineNumbers)
            {
                var label = CreateLineNumberLabel(segment);
                PathCanvas.Children.Add(label);
                _labelElements.Add(label);
            }
        }
        
        // Second pass: draw waypoints on top
        if (showPoints)
        {
            var drawnPoints = new HashSet<(double, double)>();
            
            foreach (var segment in _segments)
            {
                if (segment.IsRapid && !showRapids) continue;
                if (!segment.IsRapid && !showFeeds) continue;
                
                // Start point
                var fromKey = (Math.Round(segment.FromX, 3), Math.Round(segment.FromY, 3));
                if (!drawnPoints.Contains(fromKey))
                {
                    var point = CreateWaypoint(segment.FromX, segment.FromY);
                    PathCanvas.Children.Add(point);
                    _pointElements.Add(point);
                    drawnPoints.Add(fromKey);
                }
                
                // End point
                var toKey = (Math.Round(segment.ToX, 3), Math.Round(segment.ToY, 3));
                if (!drawnPoints.Contains(toKey))
                {
                    var point = CreateWaypoint(segment.ToX, segment.ToY);
                    PathCanvas.Children.Add(point);
                    _pointElements.Add(point);
                    drawnPoints.Add(toKey);
                }
            }
        }
    }
    
    /// <summary>
    /// Renders only the painting strokes with stroke numbers and paint well colors.
    /// A "painting stroke" is a continuous sequence of G1 feed moves between pen-down and pen-up.
    /// Other toggles (Rapids, Points, Direction, Line #) still apply.
    /// </summary>
    private void RenderPaintingStrokesOnly()
    {
        if (!_isPaintingModeGcode)
        {
            StatusLabel.Text = "Not a Paint Mode G-code file - no painting strokes to display";
            return;
        }
        
        // Render paint wells first (behind everything else)
        RenderPaintWells();
        
        // Read other toggle states
        var showRapids = ShowRapidsToggle.IsChecked == true;
        var showPoints = ShowPointsToggle.IsChecked == true;
        var showDirection = ShowDirectionToggle.IsChecked == true;
        var showLineNumbers = ShowLineNumbersToggle.IsChecked == true;
        
        // Draw rapids if enabled (these are the travel moves between strokes)
        // Color them based on the paint well they're associated with
        if (showRapids)
        {
            foreach (var segment in _segments.Where(s => s.IsRapid))
            {
                var fromY = -segment.FromY;
                var toY = -segment.ToY;
                
                // Use paint well color (lighter/transparent) or default rapid color
                Brush rapidBrush;
                if (segment.PaintWellColor.HasValue)
                {
                    var color = GetDisplayColor(segment.PaintWellColor.Value);
                    rapidBrush = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
                }
                else
                {
                    rapidBrush = new SolidColorBrush(Color.FromArgb(80, 100, 100, 255));
                }
                
                var line = new Line
                {
                    X1 = segment.FromX,
                    Y1 = fromY,
                    X2 = segment.ToX,
                    Y2 = toY,
                    StrokeThickness = 1.5,
                    Stroke = rapidBrush,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Tag = segment,
                    Cursor = Cursors.Hand
                };
                
                line.MouseEnter += SegmentLine_MouseEnter;
                line.MouseLeave += SegmentLine_MouseLeave;
                line.MouseLeftButtonDown += SegmentLine_MouseLeftButtonDown;
                
                PathCanvas.Children.Add(line);
                _pathElements.Add(line);
                segment.Visual = line;
            }
        }
        
        // Group consecutive feed segments into strokes based on stroke number
        // A stroke is all segments with the same PaintingStrokeNumber > 0
        var strokeNumbers = _segments
            .Where(s => s.PaintingStrokeNumber > 0)
            .Select(s => s.PaintingStrokeNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        
        if (strokeNumbers.Count == 0)
        {
            StatusLabel.Text = "No painting strokes found in G-code";
            return;
        }
        
        var strokeStartPoints = new List<(double X, double Y, int StrokeNum, string? WellName, Color Color)>();
        var drawnPoints = new HashSet<(double, double)>();
        
        foreach (var strokeNum in strokeNumbers)
        {
            var segments = _segments
                .Where(s => s.PaintingStrokeNumber == strokeNum)
                .OrderBy(s => s.LineNumber)
                .ToList();
            
            if (segments.Count == 0) continue;
            
            // Get color for this stroke - use paint well color or default, adjusted for dark mode
            var firstSeg = segments[0];
            var strokeColor = firstSeg.PaintWellColor.HasValue 
                ? GetDisplayColor(firstSeg.PaintWellColor.Value) 
                : Colors.DarkGray;
            var strokeBrush = new SolidColorBrush(strokeColor);
            
            // Draw all segments in this stroke
            foreach (var segment in segments)
            {
                var fromY = -segment.FromY;
                var toY = -segment.ToY;
                
                var line = new Line
                {
                    X1 = segment.FromX,
                    Y1 = fromY,
                    X2 = segment.ToX,
                    Y2 = toY,
                    StrokeThickness = 3,
                    Stroke = strokeBrush,
                    Tag = segment,
                    Cursor = Cursors.Hand
                };
                
                line.MouseEnter += SegmentLine_MouseEnter;
                line.MouseLeave += SegmentLine_MouseLeave;
                line.MouseLeftButtonDown += SegmentLine_MouseLeftButtonDown;
                
                PathCanvas.Children.Add(line);
                _pathElements.Add(line);
                segment.Visual = line;
                
                // Direction arrow
                if (showDirection)
                {
                    var arrow = CreateDirectionArrowWithColor(segment, strokeBrush);
                    if (arrow != null)
                    {
                        PathCanvas.Children.Add(arrow);
                        _pathElements.Add(arrow);
                    }
                }
                
                // Line number label
                if (showLineNumbers)
                {
                    var label = CreateLineNumberLabel(segment);
                    PathCanvas.Children.Add(label);
                    _labelElements.Add(label);
                }
                
                // Collect points for later rendering
                if (showPoints)
                {
                    var fromKey = (Math.Round(segment.FromX, 3), Math.Round(segment.FromY, 3));
                    if (!drawnPoints.Contains(fromKey))
                        drawnPoints.Add(fromKey);
                    
                    var toKey = (Math.Round(segment.ToX, 3), Math.Round(segment.ToY, 3));
                    if (!drawnPoints.Contains(toKey))
                        drawnPoints.Add(toKey);
                }
            }
            
            // Record stroke start point for label (use adjusted color)
            strokeStartPoints.Add((
                firstSeg.FromX, 
                firstSeg.FromY, 
                strokeNum, 
                firstSeg.PaintWellName,
                strokeColor));
        }
        
        // Draw waypoints on top
        if (showPoints)
        {
            foreach (var (x, y) in drawnPoints)
            {
                var point = CreateWaypoint(x, y);
                PathCanvas.Children.Add(point);
                _pointElements.Add(point);
            }
        }
        
        // Draw stroke number labels at start of each stroke
        foreach (var (x, y, strokeNum, wellName, color) in strokeStartPoints)
        {
            var label = CreatePaintingStrokeLabel(x, y, strokeNum, wellName, color);
            PathCanvas.Children.Add(label);
            _labelElements.Add(label);
        }
        
        // Update status
        var totalStrokes = strokeStartPoints.Count;
        var wellCounts = strokeStartPoints
            .GroupBy(s => s.WellName ?? "No Color")
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();
        
        StatusLabel.Text = $"Showing {totalStrokes} painting strokes ({string.Join(", ", wellCounts)})";
    }
    
    /// <summary>
    /// Creates a direction arrow with a specific brush color.
    /// </summary>
    private Polygon? CreateDirectionArrowWithColor(GcodeSegment segment, Brush brush)
    {
        var dx = segment.ToX - segment.FromX;
        var dy = segment.ToY - segment.FromY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        
        if (length < 1) return null; // Too short for arrow
        
        // Arrow at midpoint
        var midX = (segment.FromX + segment.ToX) / 2;
        var midY = -(segment.FromY + segment.ToY) / 2; // Inverted Y
        
        // Normalize direction
        var dirX = dx / length;
        var dirY = -dy / length; // Inverted Y
        
        // Perpendicular
        var perpX = -dirY;
        var perpY = dirX;
        
        var arrowSize = 4.0;
        
        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(midX + dirX * arrowSize, midY + dirY * arrowSize),
                new Point(midX - dirX * arrowSize + perpX * arrowSize * 0.5, midY - dirY * arrowSize + perpY * arrowSize * 0.5),
                new Point(midX - dirX * arrowSize - perpX * arrowSize * 0.5, midY - dirY * arrowSize - perpY * arrowSize * 0.5)
            },
            Fill = brush,
            IsHitTestVisible = false
        };
        
        return arrow;
    }
    
    /// <summary>
    /// Creates a label for a painting stroke showing its number and paint well name.
    /// </summary>
    private Border CreatePaintingStrokeLabel(double x, double y, int strokeNumber, string? wellName, Color color)
    {
        var displayText = wellName != null 
            ? $"{strokeNumber} ({wellName})"
            : strokeNumber.ToString();
        
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
        if (_canvasRotationAngle != 0)
        {
            border.RenderTransformOrigin = new Point(0, 0.5);
            border.RenderTransform = new RotateTransform(-_canvasRotationAngle);
        }
        
        // Position at start point with small offset
        Canvas.SetLeft(border, x + 5);
        Canvas.SetTop(border, -y - 15); // Inverted Y with offset above the point
        
        return border;
    }
    
    private Line CreateSegmentLine(GcodeSegment segment)
    {
        // Transform Y coordinate (G-code Y is typically inverted for display)
        var fromY = -segment.FromY;
        var toY = -segment.ToY;
        
        var line = new Line
        {
            X1 = segment.FromX,
            Y1 = fromY,
            X2 = segment.ToX,
            Y2 = toY,
            StrokeThickness = segment.IsBad ? 3 : 2,
            Stroke = GetSegmentBrush(segment),
            StrokeDashArray = segment.IsRapid ? new DoubleCollection { 4, 2 } : null,
            Tag = segment,
            Cursor = Cursors.Hand
        };
        
        line.MouseEnter += SegmentLine_MouseEnter;
        line.MouseLeave += SegmentLine_MouseLeave;
        line.MouseLeftButtonDown += SegmentLine_MouseLeftButtonDown;
        
        return line;
    }
    
    private Brush GetSegmentBrush(GcodeSegment segment)
    {
        if (segment == _selectedSegment) return SelectedBrush;
        if (segment == _hoveredSegment) return HoverBrush;
        if (segment.IsBad) return BadBrush;
        
        // For rapid moves in paint mode, use a lighter/transparent version of the paint well color
        if (segment.IsRapid && segment.PaintWellColor.HasValue)
        {
            var color = GetDisplayColor(segment.PaintWellColor.Value);
            // Create a lighter, more transparent color for rapids
            return new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B));
        }
        
        return segment.IsRapid ? RapidBrush : FeedBrush;
    }
    
    private Polygon? CreateDirectionArrow(GcodeSegment segment)
    {
        var dx = segment.ToX - segment.FromX;
        var dy = segment.ToY - segment.FromY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        
        if (length < 1) return null; // Too short for arrow
        
        // Arrow at midpoint
        var midX = (segment.FromX + segment.ToX) / 2;
        var midY = -(segment.FromY + segment.ToY) / 2; // Inverted Y
        
        // Normalize direction
        var dirX = dx / length;
        var dirY = -dy / length; // Inverted Y
        
        // Perpendicular
        var perpX = -dirY;
        var perpY = dirX;
        
        var arrowSize = 4.0;
        
        var arrow = new Polygon
        {
            Points = new PointCollection
            {
                new Point(midX + dirX * arrowSize, midY + dirY * arrowSize),
                new Point(midX - dirX * arrowSize + perpX * arrowSize * 0.5, midY - dirY * arrowSize + perpY * arrowSize * 0.5),
                new Point(midX - dirX * arrowSize - perpX * arrowSize * 0.5, midY - dirY * arrowSize - perpY * arrowSize * 0.5)
            },
            Fill = GetSegmentBrush(segment),
            IsHitTestVisible = false
        };
        
        return arrow;
    }
    
    private TextBlock CreateLineNumberLabel(GcodeSegment segment)
    {
        var midX = (segment.FromX + segment.ToX) / 2;
        var midY = -(segment.FromY + segment.ToY) / 2;
        
        var label = new TextBlock
        {
            Text = segment.LineNumber.ToString(),
            FontSize = 8,
            Foreground = Brushes.Gray,
            IsHitTestVisible = false
        };
        
        // Apply counter-rotation so text stays readable when canvas is rotated
        if (_canvasRotationAngle != 0)
        {
            label.RenderTransformOrigin = new Point(0, 0.5);
            label.RenderTransform = new RotateTransform(-_canvasRotationAngle);
        }
        
        Canvas.SetLeft(label, midX + 3);
        Canvas.SetTop(label, midY - 10);
        
        return label;
    }
    
    private Ellipse CreateWaypoint(double x, double y)
    {
        var size = 4.0;
        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = PointBrush,
            IsHitTestVisible = false
        };
        
        Canvas.SetLeft(ellipse, x - size / 2);
        Canvas.SetTop(ellipse, -y - size / 2); // Inverted Y
        
        return ellipse;
    }
    
    private void FitToView()
    {
        if (_segments.Count == 0) return;
        
        var viewWidth = CanvasContainer.ActualWidth - 40;
        var viewHeight = CanvasContainer.ActualHeight - 40;
        
        if (viewWidth <= 0 || viewHeight <= 0) return;
        
        var dataWidth = _maxX - _minX;
        var dataHeight = _maxY - _minY;
        
        if (dataWidth <= 0 || dataHeight <= 0) return;
        
        var scaleX = viewWidth / dataWidth;
        var scaleY = viewHeight / dataHeight;
        
        _zoom = Math.Min(scaleX, scaleY);
        
        CanvasScale.ScaleX = _zoom;
        CanvasScale.ScaleY = _zoom;
        
        // The paths are drawn with Y inverted (-Y), so the visual bounds are:
        // Visual minY = -_maxY, Visual maxY = -_minY
        // Center in visual space
        var visualCenterX = (_minX + _maxX) / 2;
        var visualCenterY = (-_maxY + -_minY) / 2; // Center of inverted Y range
        
        // Calculate translation to center the content
        // We want: visualCenter * scale + translate = viewCenter
        // So: translate = viewCenter - visualCenter * scale
        CanvasTranslate.X = (viewWidth / 2 + 20) - visualCenterX * _zoom;
        CanvasTranslate.Y = (viewHeight / 2 + 20) - visualCenterY * _zoom;
        
        UpdateZoomLabel();
    }
    
    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = $"{_zoom * 100:F0}%";
    }
    
    private void UpdateStatusBar()
    {
        SegmentCountLabel.Text = $"Segments: {_segments.Count}";
        BadCountLabel.Text = $"Bad: {_segments.Count(s => s.IsBad)}";
    }
    
    private void SelectSegment(GcodeSegment? segment)
    {
        var previousSelected = _selectedSegment;
        _selectedSegment = segment;
        
        // Update visuals
        if (previousSelected?.Visual is Line prevLine)
        {
            prevLine.Stroke = GetSegmentBrush(previousSelected);
            prevLine.StrokeThickness = previousSelected.IsBad ? 3 : 2;
        }
        
        if (segment?.Visual is Line newLine)
        {
            newLine.Stroke = SelectedBrush;
            newLine.StrokeThickness = 4;
        }
        
        // Update list selection
        if (segment != null)
        {
            SegmentList.SelectedItem = segment;
            SegmentList.ScrollIntoView(segment);
        }
        
        // Update details panel
        UpdateSegmentDetails(segment);
    }
    
    private void UpdateSegmentDetails(GcodeSegment? segment)
    {
        if (segment == null)
        {
            SegmentTypeLabel.Text = "Type: -";
            SegmentFromLabel.Text = "From: -";
            SegmentToLabel.Text = "To: -";
            SegmentLengthLabel.Text = "Length: -";
            SegmentLineLabel.Text = "Line: -";
            SegmentGcodeBox.Text = "";
            return;
        }
        
        SegmentTypeLabel.Text = $"Type: {(segment.IsRapid ? "Rapid (G0)" : "Feed (G1)")}";
        SegmentFromLabel.Text = $"From: X{segment.FromX:F3} Y{segment.FromY:F3}";
        SegmentToLabel.Text = $"To: X{segment.ToX:F3} Y{segment.ToY:F3}";
        SegmentLengthLabel.Text = $"Length: {segment.Length:F3} mm";
        SegmentLineLabel.Text = $"Line: {segment.LineNumber}";
        SegmentGcodeBox.Text = segment.GcodeCommand;
    }
    
    private void MarkSegmentBad(GcodeSegment segment, bool isBad)
    {
        segment.IsBad = isBad;
        
        if (segment.Visual is Line line)
        {
            line.Stroke = GetSegmentBrush(segment);
            line.StrokeThickness = isBad ? 3 : 2;
        }
        
        UpdateStatusBar();
        
        // Log to console
        if (isBad)
        {
            _log($"[BAD SEGMENT] Line {segment.LineNumber}: {segment.GcodeCommand}");
            _log($"  From: X{segment.FromX:F3} Y{segment.FromY:F3} To: X{segment.ToX:F3} Y{segment.ToY:F3}");
            _log($"  Length: {segment.Length:F3}mm, Type: {(segment.IsRapid ? "Rapid" : "Feed")}");
        }
        else
        {
            _log($"[CLEARED] Line {segment.LineNumber}: {segment.GcodeCommand}");
        }
        
        // Force list refresh
        var index = _segments.IndexOf(segment);
        if (index >= 0)
        {
            _segments.RemoveAt(index);
            _segments.Insert(index, segment);
            SegmentList.SelectedItem = segment;
        }
    }
    
    #region Event Handlers
    
    private void SegmentLine_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Line line && line.Tag is GcodeSegment segment)
        {
            _hoveredSegment = segment;
            if (segment != _selectedSegment)
            {
                line.Stroke = HoverBrush;
                line.StrokeThickness = 3;
            }
        }
    }
    
    private void SegmentLine_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Line line && line.Tag is GcodeSegment segment)
        {
            _hoveredSegment = null;
            if (segment != _selectedSegment)
            {
                line.Stroke = GetSegmentBrush(segment);
                line.StrokeThickness = segment.IsBad ? 3 : 2;
            }
        }
    }
    
    private void SegmentLine_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Line line && line.Tag is GcodeSegment segment)
        {
            SelectSegment(segment);
            e.Handled = true;
        }
    }
    
    private void PathCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == PathCanvas)
        {
            // Clicking on empty canvas - deselect
            SelectSegment(null);
        }
    }
    
    // Container-level mouse handlers for pan and zoom (work without selection)
    private Border? _canvasContainerBorder;
    private Border CanvasContainer => _canvasContainerBorder ??= (Border)this.FindName("CanvasContainerBorder")!;
    
    private void CanvasContainer_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Start panning with right mouse button
        StartPanning(e.GetPosition(CanvasContainer));
        e.Handled = true;
    }
    
    private void CanvasContainer_MouseMiddleButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Start panning with middle mouse button
        if (e.ChangedButton == MouseButton.Middle)
        {
            StartPanning(e.GetPosition(CanvasContainer));
            e.Handled = true;
        }
    }
    
    private void CanvasContainer_MouseButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle))
        {
            _isPanning = false;
            CanvasContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }
    
    private void StartPanning(Point startPosition)
    {
        _isPanning = true;
        _panStart = startPosition;
        _panOrigin = new Point(CanvasTranslate.X, CanvasTranslate.Y);
        CanvasContainer.CaptureMouse();
    }
    
    private void CanvasContainer_MouseMove(object sender, MouseEventArgs e)
    {
        // Update mouse position display
        var pos = e.GetPosition(PathCanvas);
        var worldX = pos.X;
        var worldY = -pos.Y; // Inverted Y
        MousePosLabel.Text = $"X: {worldX:F1} Y: {worldY:F1}";
        
        // Handle panning (right button or middle button)
        if (_isPanning && (e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed))
        {
            var currentPos = e.GetPosition(CanvasContainer);
            var delta = currentPos - _panStart;
            
            CanvasTranslate.X = _panOrigin.X + delta.X;
            CanvasTranslate.Y = _panOrigin.Y + delta.Y;
        }
        else if (_isPanning)
        {
            // Mouse button was released without triggering the up event
            _isPanning = false;
            CanvasContainer.ReleaseMouseCapture();
        }
    }
    
    private void CanvasContainer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Get mouse position relative to the container (screen space)
        var mousePos = e.GetPosition(CanvasContainer);
        
        // Calculate the zoom factor
        var zoomFactor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = _zoom * zoomFactor;
        newZoom = Math.Max(0.1, Math.Min(50, newZoom));
        
        // Calculate what point in canvas space is currently under the mouse
        // screenPos = canvasPos * scale + translate
        // canvasPos = (screenPos - translate) / scale
        var canvasX = (mousePos.X - CanvasTranslate.X) / _zoom;
        var canvasY = (mousePos.Y - CanvasTranslate.Y) / _zoom;
        
        // Apply new zoom
        _zoom = newZoom;
        CanvasScale.ScaleX = _zoom;
        CanvasScale.ScaleY = _zoom;
        
        // Adjust translation so the same canvas point stays under the mouse
        // newScreenPos = canvasPos * newScale + newTranslate = mousePos
        // newTranslate = mousePos - canvasPos * newScale
        CanvasTranslate.X = mousePos.X - canvasX * _zoom;
        CanvasTranslate.Y = mousePos.Y - canvasY * _zoom;
        
        UpdateZoomLabel();
        e.Handled = true;
    }
    
    private void SegmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SegmentList.SelectedItem is GcodeSegment segment)
        {
            SelectSegment(segment);
        }
    }
    
    private void GcodePathVisualizerWindow_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.B when _selectedSegment != null:
                MarkSegmentBad(_selectedSegment, !_selectedSegment.IsBad);
                e.Handled = true;
                break;
            case Key.Delete when _selectedSegment != null:
                MarkSegmentBad(_selectedSegment, false);
                e.Handled = true;
                break;
            case Key.Escape:
                SelectSegment(null);
                e.Handled = true;
                break;
            case Key.F:
                FitToView();
                e.Handled = true;
                break;
        }
    }
    
    private void FitToViewBtn_Click(object sender, RoutedEventArgs e) => FitToView();
    
    private void ZoomInBtn_Click(object sender, RoutedEventArgs e)
    {
        _zoom *= 1.2;
        CanvasScale.ScaleX = _zoom;
        CanvasScale.ScaleY = _zoom;
        UpdateZoomLabel();
    }
    
    private void ZoomOutBtn_Click(object sender, RoutedEventArgs e)
    {
        _zoom *= 0.8;
        CanvasScale.ScaleX = _zoom;
        CanvasScale.ScaleY = _zoom;
        UpdateZoomLabel();
    }
    
    private void DisplayToggle_Click(object sender, RoutedEventArgs e) => RenderPaths();
    
    private void PaintingOnlyToggle_Click(object sender, RoutedEventArgs e)
    {
        // When Painting Only is toggled ON, set default toggle states for painting view
        if (ShowPaintingOnlyToggle.IsChecked == true)
        {
            // Turn off Points, Feeds, Direction, Line # by default in painting mode
            // Keep Rapids on so user can see travel moves
            ShowPointsToggle.IsChecked = false;
            ShowFeedsToggle.IsChecked = false;  // Feeds toggle doesn't apply in painting mode
            ShowDirectionToggle.IsChecked = false;
            ShowLineNumbersToggle.IsChecked = false;
            ShowRapidsToggle.IsChecked = true;
        }
        else
        {
            // When turning off Painting Only, restore normal defaults
            ShowRapidsToggle.IsChecked = true;
            ShowFeedsToggle.IsChecked = true;
            ShowPointsToggle.IsChecked = true;
            ShowDirectionToggle.IsChecked = false;
            ShowLineNumbersToggle.IsChecked = false;
        }
        
        RenderPaths();
    }
    
    private void MarkBadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSegment != null)
        {
            MarkSegmentBad(_selectedSegment, true);
        }
    }
    
    private void ClearBadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSegment != null)
        {
            MarkSegmentBad(_selectedSegment, false);
        }
    }
    
    private void ClearAllBadBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var segment in _segments.Where(s => s.IsBad).ToList())
        {
            MarkSegmentBad(segment, false);
        }
    }
    
    private void FindBacktracksBtn_Click(object sender, RoutedEventArgs e)
    {
        // Find segments that reverse direction compared to previous segment
        _log("[ANALYSIS] Finding backtrack segments...");
        
        // Materialize the list first to avoid collection modification during iteration
        var feedSegments = _segments.Where(s => !s.IsRapid).ToList();
        var backtrackSegments = new List<GcodeSegment>();
        
        GcodeSegment? prev = null;
        
        // First pass: identify backtrack segments without modifying collection
        foreach (var segment in feedSegments)
        {
            if (prev != null)
            {
                var prevDx = prev.ToX - prev.FromX;
                var prevDy = prev.ToY - prev.FromY;
                var dx = segment.ToX - segment.FromX;
                var dy = segment.ToY - segment.FromY;
                
                // Check if direction reversed (dot product negative)
                var dot = prevDx * dx + prevDy * dy;
                if (dot < -0.01) // Some tolerance for perpendicular
                {
                    backtrackSegments.Add(segment);
                }
            }
            prev = segment;
        }
        
        // Second pass: mark backtrack segments as bad
        int found = 0;
        foreach (var segment in backtrackSegments)
        {
            MarkSegmentBad(segment, true);
            found++;
        }
        
        _log($"[ANALYSIS] Found {found} backtrack segments");
        StatusLabel.Text = $"Found {found} backtrack segments";
    }
    
    private void FindLongRapidsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Find unusually long rapid movements
        var rapids = _segments.Where(s => s.IsRapid).ToList();
        if (rapids.Count == 0)
        {
            StatusLabel.Text = "No rapid movements found";
            return;
        }
        
        var avgLength = rapids.Average(s => s.Length);
        var threshold = avgLength * 3; // 3x average is "long"
        
        _log($"[ANALYSIS] Finding long rapids (threshold: {threshold:F1}mm, avg: {avgLength:F1}mm)...");
        
        // Find long rapids first, then mark them (to avoid collection modification during iteration)
        var longRapids = rapids.Where(s => s.Length > threshold).ToList();
        
        int found = 0;
        foreach (var segment in longRapids)
        {
            MarkSegmentBad(segment, true);
            found++;
        }
        
        _log($"[ANALYSIS] Found {found} long rapid segments");
        StatusLabel.Text = $"Found {found} long rapid segments (>{threshold:F1}mm)";
    }
    
    private void FindDisconnectsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Find feed segments that don't start where the previous feed segment ended
        // This helps diagnose paint mode issues where segments don't connect properly
        _log("[ANALYSIS] Finding disconnected feed segments...");
        
        const double tolerance = 0.1; // 0.1mm tolerance for connection
        int found = 0;
        GcodeSegment? prevFeed = null;
        
        // Materialize the list first to avoid collection modification during iteration
        var feedSegments = _segments.Where(s => !s.IsRapid).ToList();
        var disconnectedSegments = new List<(GcodeSegment segment, double gap, GcodeSegment prev)>();
        
        // First pass: identify disconnected segments without modifying collection
        foreach (var segment in feedSegments)
        {
            if (prevFeed != null)
            {
                // Check if this segment starts where the previous one ended
                var gapX = Math.Abs(segment.FromX - prevFeed.ToX);
                var gapY = Math.Abs(segment.FromY - prevFeed.ToY);
                var gap = Math.Sqrt(gapX * gapX + gapY * gapY);
                
                if (gap > tolerance)
                {
                    disconnectedSegments.Add((segment, gap, prevFeed));
                }
            }
            prevFeed = segment;
        }
        
        // Second pass: mark disconnected segments as bad
        foreach (var (segment, gap, prev) in disconnectedSegments)
        {
            MarkSegmentBad(segment, true);
            _log($"[DISCONNECT] Line {segment.LineNumber}: Gap of {gap:F3}mm from previous endpoint");
            _log($"  Previous ended at: ({prev.ToX:F3}, {prev.ToY:F3})");
            _log($"  This starts at: ({segment.FromX:F3}, {segment.FromY:F3})");
            found++;
        }
        
        _log($"[ANALYSIS] Found {found} disconnected feed segments");
        StatusLabel.Text = $"Found {found} disconnected segments (gap > {tolerance}mm)";
    }
    
    private void ExportBadBtn_Click(object sender, RoutedEventArgs e)
    {
        var badSegments = _segments.Where(s => s.IsBad).ToList();
        if (badSegments.Count == 0)
        {
            StatusLabel.Text = "No bad segments to export";
            return;
        }
        
        var sb = new StringBuilder();
        sb.AppendLine("; BAD SEGMENTS EXPORT");
        sb.AppendLine($"; Total: {badSegments.Count} segments");
        sb.AppendLine();
        
        foreach (var segment in badSegments)
        {
            sb.AppendLine($"; Line {segment.LineNumber}: From ({segment.FromX:F3}, {segment.FromY:F3}) To ({segment.ToX:F3}, {segment.ToY:F3})");
            sb.AppendLine(segment.GcodeCommand);
            sb.AppendLine();
        }
        
        Clipboard.SetText(sb.ToString());
        _log($"[EXPORT] Copied {badSegments.Count} bad segments to clipboard");
        StatusLabel.Text = $"Copied {badSegments.Count} bad segments to clipboard";
    }
    
    #endregion
}

/// <summary>
/// Represents a single G-code movement segment for visualization.
/// </summary>
public class GcodeSegment : INotifyPropertyChanged
{
    private bool _isBad;
    
    public int LineNumber { get; set; }
    public string GcodeCommand { get; set; } = "";
    
    public double FromX { get; set; }
    public double FromY { get; set; }
    public double FromZ { get; set; }
    
    public double ToX { get; set; }
    public double ToY { get; set; }
    public double ToZ { get; set; }
    
    public bool IsRapid { get; set; }
    
    public bool IsBad
    {
        get => _isBad;
        set
        {
            if (_isBad != value)
            {
                _isBad = value;
                OnPropertyChanged();
            }
        }
    }
    
    /// <summary>Paint well name this segment belongs to (null if none)</summary>
    public string? PaintWellName { get; set; }
    
    /// <summary>Paint well color (parsed from G-code comments)</summary>
    public Color? PaintWellColor { get; set; }
    
    /// <summary>Painting stroke number within the paint well sequence (1-based)</summary>
    public int PaintingStrokeNumber { get; set; }
    
    /// <summary>Visual element on the canvas (for updating appearance)</summary>
    public UIElement? Visual { get; set; }
    
    /// <summary>Length of the segment in mm</summary>
    public double Length => Math.Sqrt(Math.Pow(ToX - FromX, 2) + Math.Pow(ToY - FromY, 2));
    
    /// <summary>Short preview of the command for display in list</summary>
    public string CommandPreview
    {
        get
        {
            var cmd = GcodeCommand.Length > 40 ? GcodeCommand[..37] + "..." : GcodeCommand;
            return cmd;
        }
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Represents a paint well parsed from G-code comments.
/// </summary>
public class ParsedPaintWell
{
    public string Name { get; set; } = "";
    public Color Color { get; set; } = Colors.Black;
    public double X { get; set; }
    public double Y { get; set; }
    public bool HasPosition => X != 0 || Y != 0;
}
