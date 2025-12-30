using System;
using System.Windows;
using Microsoft.Win32;

// Avoid ambiguity with System.Windows.Forms.Application
using Application = System.Windows.Application;

namespace NVSPlotter.Services;

/// <summary>
/// Manages application themes (Light/Dark mode) with system theme detection support.
/// </summary>
public sealed class ThemeManager
{
    private static ThemeManager? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the singleton instance of the ThemeManager.
    /// </summary>
    public static ThemeManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new ThemeManager();
                }
            }
            return _instance;
        }
    }

    private bool _isDarkMode;
    private bool _useSystemTheme = true;

    /// <summary>
    /// Gets or sets whether dark mode is currently active.
    /// </summary>
    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (_isDarkMode != value)
            {
                _isDarkMode = value;
                ApplyTheme();
                ThemeChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// Gets or sets whether to follow the system theme.
    /// </summary>
    public bool UseSystemTheme
    {
        get => _useSystemTheme;
        set
        {
            _useSystemTheme = value;
            if (value)
            {
                IsDarkMode = IsSystemDarkMode();
            }
        }
    }

    /// <summary>
    /// Event raised when the theme changes.
    /// </summary>
    public event EventHandler<bool>? ThemeChanged;

    private ThemeManager()
    {
        // Subscribe to system theme changes
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    /// <summary>
    /// Initializes the theme based on saved settings or system preference.
    /// </summary>
    public void Initialize()
    {
        _useSystemTheme = Properties.Settings.Default.UseSystemTheme;
        
        if (_useSystemTheme)
        {
            _isDarkMode = IsSystemDarkMode();
        }
        else
        {
            _isDarkMode = Properties.Settings.Default.IsDarkMode;
        }

        ApplyTheme();
    }

    /// <summary>
    /// Saves the current theme settings.
    /// </summary>
    public void SaveSettings()
    {
        Properties.Settings.Default.IsDarkMode = _isDarkMode;
        Properties.Settings.Default.UseSystemTheme = _useSystemTheme;
        Properties.Settings.Default.Save();
    }

    /// <summary>
    /// Toggles between light and dark mode.
    /// </summary>
    public void ToggleTheme()
    {
        _useSystemTheme = false;
        IsDarkMode = !IsDarkMode;
        SaveSettings();
    }

    /// <summary>
    /// Detects if the system is currently using dark mode.
    /// </summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0; // 0 = dark mode, 1 = light mode
            }
        }
        catch
        {
            // Ignore registry access errors
        }

        return false; // Default to light mode
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _useSystemTheme)
        {
            // Check if theme actually changed
            var systemDark = IsSystemDarkMode();
            if (systemDark != _isDarkMode)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsDarkMode = systemDark;
                });
            }
        }
    }

    private void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null) return;

        var themeName = _isDarkMode ? "DarkTheme" : "LightTheme";
        var themeUri = new Uri($"/Themes/{themeName}.xaml", UriKind.Relative);

        // Find and remove any existing theme dictionary
        ResourceDictionary? existingTheme = null;
        foreach (var dict in app.Resources.MergedDictionaries)
        {
            if (dict.Source?.OriginalString.Contains("Theme") == true)
            {
                existingTheme = dict;
                break;
            }
        }

        if (existingTheme != null)
        {
            app.Resources.MergedDictionaries.Remove(existingTheme);
        }

        // Add the new theme
        var newTheme = new ResourceDictionary { Source = themeUri };
        app.Resources.MergedDictionaries.Add(newTheme);
    }

    /// <summary>
    /// Cleans up resources when the application exits.
    /// </summary>
    public void Cleanup()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
    }
}
