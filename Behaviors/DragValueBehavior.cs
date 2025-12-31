using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// Avoid ambiguity with System.Windows.Forms types
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace NVSPlotter.Behaviors
{
    /// <summary>
    /// Attached behavior that enables drag-to-adjust functionality on TextBox controls.
    /// Click and drag vertically to increment/decrement the numeric value.
    /// Features:
    /// - North-South resize cursor during drag
    /// - Horizontal axis lock (cursor stays at same X position)
    /// - Screen edge wrapping (cursor wraps from top to bottom and vice versa)
    /// - Configurable sensitivity, min/max values, and decimal places
    /// </summary>
    public static class DragValueBehavior
    {
        #region P/Invoke

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        #endregion

        #region Attached Properties

        /// <summary>
        /// Enables or disables the drag-to-adjust behavior on a TextBox.
        /// </summary>
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DragValueBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

        /// <summary>
        /// Minimum allowed value.
        /// </summary>
        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.RegisterAttached(
                "Minimum",
                typeof(double),
                typeof(DragValueBehavior),
                new PropertyMetadata(0.0));

        public static double GetMinimum(DependencyObject obj) => (double)obj.GetValue(MinimumProperty);
        public static void SetMinimum(DependencyObject obj, double value) => obj.SetValue(MinimumProperty, value);

        /// <summary>
        /// Maximum allowed value.
        /// </summary>
        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.RegisterAttached(
                "Maximum",
                typeof(double),
                typeof(DragValueBehavior),
                new PropertyMetadata(500.0));

        public static double GetMaximum(DependencyObject obj) => (double)obj.GetValue(MaximumProperty);
        public static void SetMaximum(DependencyObject obj, double value) => obj.SetValue(MaximumProperty, value);

        /// <summary>
        /// Sensitivity - pixels of mouse movement per unit change.
        /// Higher values = less sensitive (more movement required).
        /// </summary>
        public static readonly DependencyProperty SensitivityProperty =
            DependencyProperty.RegisterAttached(
                "Sensitivity",
                typeof(double),
                typeof(DragValueBehavior),
                new PropertyMetadata(5.0));

        public static double GetSensitivity(DependencyObject obj) => (double)obj.GetValue(SensitivityProperty);
        public static void SetSensitivity(DependencyObject obj, double value) => obj.SetValue(SensitivityProperty, value);

        /// <summary>
        /// Number of decimal places to round to.
        /// </summary>
        public static readonly DependencyProperty DecimalPlacesProperty =
            DependencyProperty.RegisterAttached(
                "DecimalPlaces",
                typeof(int),
                typeof(DragValueBehavior),
                new PropertyMetadata(0));

        public static int GetDecimalPlaces(DependencyObject obj) => (int)obj.GetValue(DecimalPlacesProperty);
        public static void SetDecimalPlaces(DependencyObject obj, int value) => obj.SetValue(DecimalPlacesProperty, value);

        /// <summary>
        /// Step size for value changes. If 0, uses continuous adjustment.
        /// </summary>
        public static readonly DependencyProperty StepProperty =
            DependencyProperty.RegisterAttached(
                "Step",
                typeof(double),
                typeof(DragValueBehavior),
                new PropertyMetadata(1.0));

        public static double GetStep(DependencyObject obj) => (double)obj.GetValue(StepProperty);
        public static void SetStep(DependencyObject obj, double value) => obj.SetValue(StepProperty, value);

        #endregion

        #region Private State (per-TextBox via attached property)

        private static readonly DependencyProperty DragStateProperty =
            DependencyProperty.RegisterAttached(
                "DragState",
                typeof(DragState),
                typeof(DragValueBehavior),
                new PropertyMetadata(null));

        /// <summary>
        /// Threshold in pixels - if mouse moves less than this, treat as click for editing.
        /// </summary>
        private const double DragThreshold = 3.0;

        private class DragState
        {
            public bool IsMouseDown;
            public bool IsDragging;
            public bool IsEditing;
            public double StartValue;
            public double AccumulatedDelta;
            public int LockedScreenX;
            public Point StartMousePosition;
            public Point LastMousePosition;
            public Cursor? OriginalCursor;
        }

        private static DragState GetOrCreateDragState(DependencyObject obj)
        {
            var state = (DragState?)obj.GetValue(DragStateProperty);
            if (state == null)
            {
                state = new DragState();
                obj.SetValue(DragStateProperty, state);
            }
            return state;
        }

        #endregion

        #region Event Handlers

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox textBox) return;

            if ((bool)e.NewValue)
            {
                textBox.PreviewMouseLeftButtonDown += TextBox_PreviewMouseLeftButtonDown;
                textBox.PreviewMouseMove += TextBox_PreviewMouseMove;
                textBox.PreviewMouseLeftButtonUp += TextBox_PreviewMouseLeftButtonUp;
                textBox.LostMouseCapture += TextBox_LostMouseCapture;
                textBox.LostFocus += TextBox_LostFocus;
                textBox.KeyDown += TextBox_KeyDown;
                textBox.Cursor = Cursors.SizeNS;
            }
            else
            {
                textBox.PreviewMouseLeftButtonDown -= TextBox_PreviewMouseLeftButtonDown;
                textBox.PreviewMouseMove -= TextBox_PreviewMouseMove;
                textBox.PreviewMouseLeftButtonUp -= TextBox_PreviewMouseLeftButtonUp;
                textBox.LostMouseCapture -= TextBox_LostMouseCapture;
                textBox.LostFocus -= TextBox_LostFocus;
                textBox.KeyDown -= TextBox_KeyDown;
                textBox.Cursor = null;
            }
        }

        private static void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            var state = GetOrCreateDragState(textBox);
            
            // If already in edit mode, allow normal text editing behavior
            if (state.IsEditing)
            {
                return; // Don't handle - let TextBox handle normally
            }

            // Parse current value
            if (!double.TryParse(textBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double currentValue))
            {
                currentValue = GetMinimum(textBox);
            }

            state.IsMouseDown = true;
            state.IsDragging = false; // Not dragging yet - wait for movement
            state.StartValue = currentValue;
            state.AccumulatedDelta = 0;
            state.StartMousePosition = e.GetPosition(textBox);
            state.LastMousePosition = state.StartMousePosition;
            state.OriginalCursor = textBox.Cursor;

            // Lock the X position in screen coordinates
            if (GetCursorPos(out POINT cursorPos))
            {
                state.LockedScreenX = cursorPos.X;
            }

            textBox.CaptureMouse();
            e.Handled = true;
        }

        private static void TextBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            var state = GetOrCreateDragState(textBox);
            
            // If in edit mode, allow normal behavior
            if (state.IsEditing) return;
            
            // If mouse isn't down, nothing to do
            if (!state.IsMouseDown) return;

            var currentMouse = e.GetPosition(textBox);
            
            // Check if we should start dragging (exceeded threshold)
            if (!state.IsDragging)
            {
                var distance = Math.Sqrt(
                    Math.Pow(currentMouse.X - state.StartMousePosition.X, 2) +
                    Math.Pow(currentMouse.Y - state.StartMousePosition.Y, 2));
                
                if (distance < DragThreshold)
                {
                    return; // Not enough movement yet
                }
                
                // Start dragging
                state.IsDragging = true;
                textBox.Cursor = Cursors.SizeNS;
            }

            // Get current screen cursor position
            if (!GetCursorPos(out POINT screenPos)) return;

            // Get screen bounds for wrapping
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(screenPos.X, screenPos.Y));
            var screenTop = screen.Bounds.Top + 50;
            var screenBottom = screen.Bounds.Bottom - 50;

            // Calculate vertical delta (upward = positive = increase value)
            var dy = state.LastMousePosition.Y - currentMouse.Y;
            state.AccumulatedDelta += dy;

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
            bool needsXCorrection = Math.Abs(screenPos.X - state.LockedScreenX) > 1;
            if (needsXCorrection || wrapped)
            {
                SetCursorPos(state.LockedScreenX, wrapped ? newY : screenPos.Y);
            }

            // Update last position (accounting for wrap)
            if (wrapped)
            {
                // After wrapping, reset the tracked position
                var textBoxScreenPos = textBox.PointToScreen(new Point(0, 0));
                state.LastMousePosition = new Point(
                    state.LockedScreenX - textBoxScreenPos.X,
                    newY - textBoxScreenPos.Y);
            }
            else
            {
                state.LastMousePosition = currentMouse;
            }

            // Calculate new value
            var sensitivity = GetSensitivity(textBox);
            var step = GetStep(textBox);
            var minimum = GetMinimum(textBox);
            var maximum = GetMaximum(textBox);
            var decimalPlaces = GetDecimalPlaces(textBox);

            double delta;
            if (step > 0)
            {
                // Step-based: accumulate until threshold reached
                var steps = (int)(state.AccumulatedDelta / sensitivity);
                delta = steps * step;
            }
            else
            {
                // Continuous: direct mapping
                delta = state.AccumulatedDelta / sensitivity;
            }

            var newValue = state.StartValue + delta;
            newValue = Math.Clamp(newValue, minimum, maximum);
            newValue = Math.Round(newValue, decimalPlaces);

            // Update TextBox
            var format = decimalPlaces > 0 ? $"F{decimalPlaces}" : "F0";
            textBox.Text = newValue.ToString(format, CultureInfo.InvariantCulture);

            e.Handled = true;
        }

        private static void TextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            var state = GetOrCreateDragState(textBox);
            
            // If in edit mode, allow normal behavior
            if (state.IsEditing) return;
            
            if (!state.IsMouseDown) return;

            textBox.ReleaseMouseCapture();
            
            // If we never started dragging (click without enough movement), enter edit mode
            if (!state.IsDragging)
            {
                EnterEditMode(textBox, state);
                e.Handled = true;
                return;
            }

            // Complete the drag operation
            CompleteDrag(textBox, state);
            e.Handled = true;
        }

        private static void TextBox_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            var state = GetOrCreateDragState(textBox);
            
            // Don't interfere if in edit mode
            if (state.IsEditing) return;
            
            if (state.IsMouseDown || state.IsDragging)
            {
                CompleteDrag(textBox, state);
            }
        }

        private static void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            var state = GetOrCreateDragState(textBox);
            
            if (state.IsEditing)
            {
                ExitEditMode(textBox, state);
            }
        }

        private static void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox) return;

            var state = GetOrCreateDragState(textBox);
            
            if (!state.IsEditing) return;

            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                if (e.Key == Key.Enter)
                {
                    // Validate and clamp value on Enter
                    ValidateAndClampValue(textBox);
                }
                
                ExitEditMode(textBox, state);
                
                // Move focus away
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private static void EnterEditMode(TextBox textBox, DragState state)
        {
            state.IsMouseDown = false;
            state.IsDragging = false;
            state.IsEditing = true;
            
            // Change cursor to text cursor
            textBox.Cursor = Cursors.IBeam;
            
            // Select all text for easy replacement
            textBox.Focus();
            textBox.SelectAll();
        }

        private static void ExitEditMode(TextBox textBox, DragState state)
        {
            state.IsEditing = false;
            
            // Validate and clamp the value
            ValidateAndClampValue(textBox);
            
            // Restore drag cursor
            textBox.Cursor = Cursors.SizeNS;
        }

        private static void ValidateAndClampValue(TextBox textBox)
        {
            var minimum = GetMinimum(textBox);
            var maximum = GetMaximum(textBox);
            var decimalPlaces = GetDecimalPlaces(textBox);

            if (double.TryParse(textBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
            {
                value = Math.Clamp(value, minimum, maximum);
                value = Math.Round(value, decimalPlaces);
                
                var format = decimalPlaces > 0 ? $"F{decimalPlaces}" : "F0";
                textBox.Text = value.ToString(format, CultureInfo.InvariantCulture);
            }
            else
            {
                // Invalid input - reset to minimum
                var format = decimalPlaces > 0 ? $"F{decimalPlaces}" : "F0";
                textBox.Text = minimum.ToString(format, CultureInfo.InvariantCulture);
            }
        }

        private static void CompleteDrag(TextBox textBox, DragState state)
        {
            state.IsMouseDown = false;
            state.IsDragging = false;
            textBox.Cursor = Cursors.SizeNS; // Keep the drag cursor
            textBox.ReleaseMouseCapture();
        }

        #endregion
    }
}
