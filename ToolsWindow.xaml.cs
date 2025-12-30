using NVSPlotter.Models;
using NVSPlotter.Properties;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// Avoid ambiguity with System.Windows.Forms types
using Button = System.Windows.Controls.Button;
using Screen = System.Windows.Forms.Screen;
using Point = System.Windows.Point;

namespace NVSPlotter;

/// <summary>
/// Floating tools panel window with Photoshop-style tool selection.
/// </summary>
public partial class ToolsWindow : Window
{
    private readonly Dictionary<string, Button> _toolButtons = new();
    private readonly Action<ToolMode> _onToolSelected;
    private ToolMode _currentTool = ToolMode.Select;
    private bool _isClosingForReal;
    private bool _positionRestored;
    
    // Reference to the CanvasScroll for constraining position
    private FrameworkElement? _constraintElement;
    private Window? _ownerWindow;
    
    // Track relative position within constraint element (0.0 to 1.0)
    private double _relativeX;
    private double _relativeY;
    private Rect _lastConstraintBounds;
    private bool _isDragging;

    public ToolMode CurrentTool => _currentTool;

    public ToolsWindow(Action<ToolMode> onToolSelected)
    {
        _onToolSelected = onToolSelected ?? throw new ArgumentNullException(nameof(onToolSelected));
        
        InitializeComponent();
        
        // Build button lookup dictionary
        foreach (var child in ToolsPanel.Children)
        {
            if (child is Button btn && btn.Tag is string tag)
            {
                _toolButtons[tag] = btn;
            }
        }
    }

    /// <summary>
    /// Sets the element that the tools window should be constrained within.
    /// </summary>
    public void SetConstraintElement(FrameworkElement element, Window ownerWindow)
    {
        // Unsubscribe from previous element
        if (_constraintElement != null)
        {
            _constraintElement.SizeChanged -= ConstraintElement_SizeChanged;
        }
        if (_ownerWindow != null)
        {
            _ownerWindow.LocationChanged -= OwnerWindow_LocationChanged;
            _ownerWindow.StateChanged -= OwnerWindow_StateChanged;
        }
        
        _constraintElement = element;
        _ownerWindow = ownerWindow;
        
        // Subscribe to size and position changes
        if (_constraintElement != null)
        {
            _constraintElement.SizeChanged += ConstraintElement_SizeChanged;
        }
        if (_ownerWindow != null)
        {
            _ownerWindow.LocationChanged += OwnerWindow_LocationChanged;
            _ownerWindow.StateChanged += OwnerWindow_StateChanged;
        }
        
        // Store initial bounds
        var bounds = GetConstraintBounds();
        if (bounds != null)
        {
            _lastConstraintBounds = bounds.Value;
        }
    }

    private void ConstraintElement_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // When the constraint element resizes, reposition the tools window
        // to maintain its relative position
        RepositionToRelativeLocation();
    }

    private void OwnerWindow_LocationChanged(object? sender, EventArgs e)
    {
        // When the owner window moves, reposition the tools window
        RepositionToRelativeLocation();
    }

    private void OwnerWindow_StateChanged(object? sender, EventArgs e)
    {
        // When the window state changes (maximize, restore, etc.), reposition
        // Use dispatcher to ensure layout has updated
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RepositionToRelativeLocation();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Repositions the tools window based on its stored relative position.
    /// </summary>
    private void RepositionToRelativeLocation()
    {
        if (!IsLoaded || !_positionRestored || _isDragging) return;
        
        var bounds = GetConstraintBounds();
        if (bounds == null) return;
        
        var rect = bounds.Value;
        
        // Calculate new position based on relative coordinates
        var newLeft = rect.Left + (_relativeX * (rect.Width - ActualWidth));
        var newTop = rect.Top + (_relativeY * (rect.Height - ActualHeight));
        
        // Ensure window stays within bounds
        const double margin = 5;
        newLeft = Math.Max(rect.Left + margin, Math.Min(newLeft, rect.Right - ActualWidth - margin));
        newTop = Math.Max(rect.Top + margin, Math.Min(newTop, rect.Bottom - ActualHeight - margin));
        
        // Apply position
        Left = newLeft;
        Top = newTop;
        
        // Update last known bounds
        _lastConstraintBounds = rect;
    }

    /// <summary>
    /// Updates the stored relative position based on current window position.
    /// </summary>
    private void UpdateRelativePosition()
    {
        var bounds = GetConstraintBounds();
        if (bounds == null) return;
        
        var rect = bounds.Value;
        
        // Calculate relative position (0.0 to 1.0)
        var availableWidth = rect.Width - ActualWidth;
        var availableHeight = rect.Height - ActualHeight;
        
        if (availableWidth > 0)
            _relativeX = Math.Clamp((Left - rect.Left) / availableWidth, 0, 1);
        else
            _relativeX = 0;
            
        if (availableHeight > 0)
            _relativeY = Math.Clamp((Top - rect.Top) / availableHeight, 0, 1);
        else
            _relativeY = 0;
        
        _lastConstraintBounds = rect;
    }

    /// <summary>
    /// Gets the screen bounds of the constraint element.
    /// </summary>
    private Rect? GetConstraintBounds()
    {
        if (_constraintElement == null || _ownerWindow == null) return null;
        
        try
        {
            // Get the position of the constraint element in screen coordinates
            var topLeft = _constraintElement.PointToScreen(new Point(0, 0));
            var bottomRight = _constraintElement.PointToScreen(
                new Point(_constraintElement.ActualWidth, _constraintElement.ActualHeight));
            
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Constrains the window position to stay within the constraint element bounds.
    /// </summary>
    private void ConstrainToElement()
    {
        var bounds = GetConstraintBounds();
        if (bounds == null) return;
        
        var rect = bounds.Value;
        
        // Ensure the window stays within bounds
        // Allow a small margin so the window isn't exactly at the edge
        const double margin = 5;
        
        var newLeft = Left;
        var newTop = Top;
        
        // Constrain left edge (window can't go past left of constraint area)
        if (newLeft < rect.Left + margin)
            newLeft = rect.Left + margin;
        
        // Constrain right edge (window can't go past right of constraint area)
        if (newLeft + ActualWidth > rect.Right - margin)
            newLeft = rect.Right - ActualWidth - margin;
        
        // Constrain top edge
        if (newTop < rect.Top + margin)
            newTop = rect.Top + margin;
        
        // Constrain bottom edge
        if (newTop + ActualHeight > rect.Bottom - margin)
            newTop = rect.Bottom - ActualHeight - margin;
        
        // Apply constrained position if different
        if (Math.Abs(Left - newLeft) > 0.5 || Math.Abs(Top - newTop) > 0.5)
        {
            Left = newLeft;
            Top = newTop;
        }
        
        // Update relative position after constraining
        UpdateRelativePosition();
    }

    /// <summary>
    /// Checks if a position is visible on any monitor.
    /// </summary>
    private static bool IsPositionOnAnyScreen(double left, double top, double minVisible = 50)
    {
        // Check against all screens using Windows Forms Screen class
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.WorkingArea;
            // Check if at least minVisible pixels would be visible on this screen
            if (left >= bounds.Left - minVisible && 
                left < bounds.Right - minVisible &&
                top >= bounds.Top - minVisible && 
                top < bounds.Bottom - minVisible)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gets a default position within the constraint element or near the main window.
    /// </summary>
    private (double Left, double Top) GetDefaultPosition()
    {
        // Try to use constraint bounds if available
        var bounds = GetConstraintBounds();
        if (bounds != null)
        {
            return (bounds.Value.Left + 10, bounds.Value.Top + 50);
        }
        
        // Fallback to primary screen
        var primaryScreen = Screen.PrimaryScreen;
        if (primaryScreen != null)
        {
            return (primaryScreen.WorkingArea.Left + 100, primaryScreen.WorkingArea.Top + 150);
        }
        return (100, 150);
    }

    private void RestoreSavedPosition()
    {
        if (_positionRestored) return;
        
        var savedLeft = Settings.Default.ToolsWindowLeft;
        var savedTop = Settings.Default.ToolsWindowTop;

        // First, check if the saved position is within the constraint element
        var bounds = GetConstraintBounds();
        if (bounds != null)
        {
            var rect = bounds.Value;
            if (savedLeft >= rect.Left && savedLeft + ActualWidth <= rect.Right &&
                savedTop >= rect.Top && savedTop + ActualHeight <= rect.Bottom)
            {
                Left = savedLeft;
                Top = savedTop;
                _positionRestored = true;
                UpdateRelativePosition();
                return;
            }
        }

        // Check if saved position is valid on any screen (for backward compatibility)
        if (bounds == null && IsPositionOnAnyScreen(savedLeft, savedTop))
        {
            Left = savedLeft;
            Top = savedTop;
            _positionRestored = true;
        }
        else
        {
            // Use default position
            var (defaultLeft, defaultTop) = GetDefaultPosition();
            Left = defaultLeft;
            Top = defaultTop;
            _positionRestored = true;
        }
        
        UpdateRelativePosition();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Restore saved position after load
        RestoreSavedPosition();

        // Select initial tool (Select is the default)
        SelectTool(ToolMode.Select, notify: false);
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        // Constrain position to stay within the canvas scroll area
        if (IsLoaded && _positionRestored && _constraintElement != null && !_isDragging)
        {
            ConstrainToElement();
        }
        
        // Save position whenever the window is moved (but not during close or before loaded)
        if (!_isClosingForReal && IsLoaded && _positionRestored)
        {
            Settings.Default.ToolsWindowLeft = Left;
            Settings.Default.ToolsWindowTop = Top;
            // Save immediately to ensure position is persisted even if app crashes
            Settings.Default.Save();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosingForReal)
        {
            // Don't actually close, just hide
            e.Cancel = true;
            Hide();
            return;
        }

        // Unsubscribe from events
        if (_constraintElement != null)
        {
            _constraintElement.SizeChanged -= ConstraintElement_SizeChanged;
        }
        if (_ownerWindow != null)
        {
            _ownerWindow.LocationChanged -= OwnerWindow_LocationChanged;
            _ownerWindow.StateChanged -= OwnerWindow_StateChanged;
        }

        // Save position on actual close
        Settings.Default.ToolsWindowLeft = Left;
        Settings.Default.ToolsWindowTop = Top;
        Settings.Default.Save();
    }

    /// <summary>
    /// Actually close the window (called when app is shutting down)
    /// </summary>
    public void ForceClose()
    {
        _isClosingForReal = true;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Allow dragging the window from anywhere
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            try
            {
                DragMove();
            }
            finally
            {
                _isDragging = false;
                
                // After drag completes, constrain to element bounds and update relative position
                if (_constraintElement != null)
                {
                    ConstrainToElement();
                    UpdateRelativePosition();
                }
            }
        }
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (Enum.TryParse<ToolMode>(tag, true, out var tool))
            {
                SelectTool(tool);
            }
        }
    }

    private void CircleSegments_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string tagStr && int.TryParse(tagStr, out var segments))
        {
            Settings.Default.circleCurveSegments = segments;
            Settings.Default.Save();
        }
    }

    /// <summary>
    /// Selects a tool and updates the visual state.
    /// </summary>
    public void SelectTool(ToolMode tool, bool notify = true)
    {
        _currentTool = tool;

        // Update button visual states
        foreach (var kvp in _toolButtons)
        {
            var isSelected = kvp.Key.Equals(tool.ToString(), StringComparison.OrdinalIgnoreCase);
            kvp.Value.Tag = isSelected ? "Selected" : kvp.Key;
            
            // Force style update
            if (isSelected)
            {
                kvp.Value.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 120, 212)); // #0078D4
                kvp.Value.BorderBrush = kvp.Value.Background;
            }
            else
            {
                kvp.Value.Background = System.Windows.Media.Brushes.Transparent;
                kvp.Value.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
        }

        if (notify)
        {
            _onToolSelected(tool);
        }
    }

    /// <summary>
    /// Handles keyboard shortcuts for tool selection.
    /// Call this from MainWindow's PreviewKeyDown.
    /// </summary>
    public bool HandleKeyboardShortcut(Key key, ModifierKeys modifiers)
    {
        // Handle Shift+B for PolyBezier
        if (key == Key.B && modifiers == ModifierKeys.Shift)
        {
            SelectTool(ToolMode.PolyBezier);
            return true;
        }

        // Only handle other shortcuts when no modifiers are pressed
        if (modifiers != ModifierKeys.None) return false;

        ToolMode? tool = key switch
        {
            Key.V => ToolMode.Select,
            Key.F => ToolMode.FreeDraw,
            Key.L => ToolMode.Line,
            Key.R => ToolMode.Rectangle,
            Key.C => ToolMode.Circle,
            Key.P => ToolMode.Polyline,
            Key.B => ToolMode.Bezier,
            Key.W => ToolMode.PaintWell,
            Key.H => ToolMode.Pan,
            Key.Z => ToolMode.Zoom,
            Key.M => ToolMode.Measure,
            Key.E => ToolMode.Erase,
            _ => null
        };

        if (tool.HasValue)
        {
            SelectTool(tool.Value);
            return true;
        }

        return false;
    }
}
