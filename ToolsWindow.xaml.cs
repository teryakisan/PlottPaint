using NVSPlotter.Models;
using NVSPlotter.Properties;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// Avoid ambiguity with System.Windows.Forms types
using Button = System.Windows.Controls.Button;

namespace NVSPlotter;

/// <summary>
/// Floating tools panel window with Photoshop-style tool selection.
/// </summary>
public partial class ToolsWindow : Window
{
    private readonly Dictionary<string, Button> _toolButtons = new();
    private readonly Action<ToolMode> _onToolSelected;
    private ToolMode _currentTool = ToolMode.PaintWell;
    private bool _isClosingForReal;

    public ToolMode CurrentTool => _currentTool;

    public ToolsWindow(Action<ToolMode> onToolSelected)
    {
        _onToolSelected = onToolSelected ?? throw new ArgumentNullException(nameof(onToolSelected));
        
        // Restore saved position BEFORE InitializeComponent to ensure it takes effect
        RestoreSavedPosition();
        
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

    private void RestoreSavedPosition()
    {
        var savedLeft = Settings.Default.ToolsWindowLeft;
        var savedTop = Settings.Default.ToolsWindowTop;

        // Validate position is on screen
        var screen = SystemParameters.WorkArea;
        if (savedLeft >= screen.Left && savedLeft < screen.Right - 50 && 
            savedTop >= screen.Top && savedTop < screen.Bottom - 50)
        {
            Left = savedLeft;
            Top = savedTop;
        }
        else
        {
            // Default position: offset from edge
            Left = 100;
            Top = 150;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-apply saved position after load (in case Owner changed it)
        RestoreSavedPosition();

        // Select initial tool
        SelectTool(ToolMode.PaintWell, notify: false);
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        // Save position whenever the window is moved (but not during close)
        if (!_isClosingForReal && IsLoaded)
        {
            Settings.Default.ToolsWindowLeft = Left;
            Settings.Default.ToolsWindowTop = Top;
            // Don't save immediately on every move - will be saved on close
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
        // Only handle shortcuts when no modifiers are pressed (except for specific combos)
        if (modifiers != ModifierKeys.None) return false;

        ToolMode? tool = key switch
        {
            Key.W => ToolMode.PaintWell,
            Key.L => ToolMode.Line,
            Key.R => ToolMode.Rectangle,
            Key.C => ToolMode.Circle,
            Key.P => ToolMode.Polyline,
            Key.B => ToolMode.Bezier,
            Key.V => ToolMode.Select,
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
