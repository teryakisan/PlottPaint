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
    /// Gets a default position near the main window or primary screen.
    /// </summary>
    private static (double Left, double Top) GetDefaultPosition()
    {
        // Try to position on primary screen
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

        // Check if saved position is valid (not default uninitialized values)
        // and is visible on any screen (supports multi-monitor)
        if (IsPositionOnAnyScreen(savedLeft, savedTop))
        {
            Left = savedLeft;
            Top = savedTop;
            _positionRestored = true;
        }
        else
        {
            // Use default position on primary screen
            var (defaultLeft, defaultTop) = GetDefaultPosition();
            Left = defaultLeft;
            Top = defaultTop;
            _positionRestored = true;
        }
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
            DragMove();
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
