using NVSPlotter.Properties;
using NVSPlotter.Services;
using System;
using System.Globalization;
using System.Windows;

// Avoid ambiguity with System.Windows.Forms types
using MessageBox = System.Windows.MessageBox;

namespace NVSPlotter.Windows;

/// <summary>
/// Machine settings dialog for configuring plotter bed size and home position.
/// </summary>
public partial class MachineSettingsWindow : Window
{
    private readonly GrblManagerService? _grblManager;
    private readonly Action? _onSettingsChanged;

    /// <summary>
    /// Bed size configuration mode.
    /// </summary>
    public enum BedSizeMode
    {
        /// <summary>Auto-detect from GRBL $130/$131 settings when connected.</summary>
        Auto,
        /// <summary>Use manually configured bed dimensions.</summary>
        Manual
    }

    public MachineSettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    /// <summary>
    /// Creates the dialog with optional GRBL manager reference for status display.
    /// </summary>
    /// <param name="grblManager">GRBL manager service for connection status</param>
    /// <param name="onSettingsChanged">Callback when settings are applied</param>
    public MachineSettingsWindow(GrblManagerService? grblManager, Action? onSettingsChanged = null)
    {
        _grblManager = grblManager;
        _onSettingsChanged = onSettingsChanged;
        
        InitializeComponent();
        LoadSettings();
        UpdateStatus();
    }

    private void LoadSettings()
    {
        // Load bed size mode
        var modeStr = Settings.Default.bedSizeMode ?? "Auto";
        var isManual = modeStr.Equals("Manual", StringComparison.OrdinalIgnoreCase);
        
        AutoModeRadio.IsChecked = !isManual;
        ManualModeRadio.IsChecked = isManual;

        // Load bed dimensions
        BedXBox.Text = Settings.Default.bedX.ToString(CultureInfo.InvariantCulture);
        BedYBox.Text = Settings.Default.bedY.ToString(CultureInfo.InvariantCulture);

        // Load home position
        HomeAtMaxXCheck.IsChecked = Settings.Default.manualHomeAtMaxX;
        HomeAtMaxYCheck.IsChecked = Settings.Default.manualHomeAtMaxY;

        UpdateControlStates();
    }

    private void SaveSettings()
    {
        // Save bed size mode
        Settings.Default.bedSizeMode = ManualModeRadio.IsChecked == true ? "Manual" : "Auto";

        // Save bed dimensions
        if (TryParseDouble(BedXBox.Text, out var bedX) && bedX >= 100 && bedX <= 10000)
        {
            Settings.Default.bedX = (int)bedX;
        }
        
        if (TryParseDouble(BedYBox.Text, out var bedY) && bedY >= 100 && bedY <= 10000)
        {
            Settings.Default.bedY = (int)bedY;
        }

        // Save home position
        Settings.Default.manualHomeAtMaxX = HomeAtMaxXCheck.IsChecked == true;
        Settings.Default.manualHomeAtMaxY = HomeAtMaxYCheck.IsChecked == true;

        Settings.Default.Save();
    }

    private void UpdateControlStates()
    {
        var isManual = ManualModeRadio.IsChecked == true;
        
        // Enable/disable manual controls based on mode
        BedDimensionsGroup.IsEnabled = isManual;
        HomePositionGroup.IsEnabled = isManual;

        // Dim the groups visually when disabled
        BedDimensionsGroup.Opacity = isManual ? 1.0 : 0.5;
        HomePositionGroup.Opacity = isManual ? 1.0 : 0.5;
    }

    private void UpdateStatus()
    {
        if (_grblManager == null)
        {
            StatusText.Text = "GRBL manager not available.";
            return;
        }

        var isConnected = _grblManager.IsConnected;
        var bedFromGrbl = _grblManager.BedFromGrbl;
        var bedX = _grblManager.BedX;
        var bedY = _grblManager.BedY;
        var homeMaxX = _grblManager.HomeAtMaxX;
        var homeMaxY = _grblManager.HomeAtMaxY;

        if (isConnected)
        {
            if (bedFromGrbl)
            {
                StatusText.Text = $"Connected to {_grblManager.PortName}\n" +
                                  $"GRBL reported: {bedX:0} × {bedY:0} mm\n" +
                                  $"Home: X={( homeMaxX ? "Max" : "Min")}, Y={(homeMaxY ? "Max" : "Min")}";
            }
            else
            {
                StatusText.Text = $"Connected to {_grblManager.PortName}\n" +
                                  $"GRBL did not report bed size ($130/$131 not set).\n" +
                                  $"Using configured: {bedX:0} × {bedY:0} mm";
            }
        }
        else
        {
            var mode = ManualModeRadio.IsChecked == true ? "Manual" : "Auto";
            StatusText.Text = $"Not connected to GRBL.\n" +
                              $"Mode: {mode}\n" +
                              $"Configured size: {Settings.Default.bedX} × {Settings.Default.bedY} mm";
        }
    }

    private void BedSizeMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateControlStates();
        UpdateStatus();
    }

    // ===== PRESET BUTTONS =====

    private void SetPreset(int width, int height)
    {
        BedXBox.Text = width.ToString(CultureInfo.InvariantCulture);
        BedYBox.Text = height.ToString(CultureInfo.InvariantCulture);
        
        // Auto-select manual mode when using presets
        ManualModeRadio.IsChecked = true;
        UpdateControlStates();
    }

    private void PresetA0_Click(object sender, RoutedEventArgs e) => SetPreset(841, 1189);
    private void PresetA1_Click(object sender, RoutedEventArgs e) => SetPreset(594, 841);
    private void PresetA2_Click(object sender, RoutedEventArgs e) => SetPreset(420, 594);
    private void PresetA3_Click(object sender, RoutedEventArgs e) => SetPreset(297, 420);
    private void PresetA4_Click(object sender, RoutedEventArgs e) => SetPreset(210, 297);
    private void Preset24x36_Click(object sender, RoutedEventArgs e) => SetPreset(610, 914); // 24×36 inches rounded

    // ===== DIALOG BUTTONS =====

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        
        SaveSettings();
        _onSettingsChanged?.Invoke();
        UpdateStatus();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        
        SaveSettings();
        _onSettingsChanged?.Invoke();
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private bool ValidateInputs()
    {
        if (!TryParseDouble(BedXBox.Text, out var bedX) || bedX < 100 || bedX > 10000)
        {
            MessageBox.Show("Bed Width must be between 100 and 10,000 mm.", "Validation Error", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
            BedXBox.Focus();
            BedXBox.SelectAll();
            return false;
        }

        if (!TryParseDouble(BedYBox.Text, out var bedY) || bedY < 100 || bedY > 10000)
        {
            MessageBox.Show("Bed Height must be between 100 and 10,000 mm.", "Validation Error", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
            BedYBox.Focus();
            BedYBox.SelectAll();
            return false;
        }

        return true;
    }

    private static bool TryParseDouble(string? text, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
    }
}
