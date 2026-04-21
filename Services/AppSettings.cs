using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NVSPlotter.Services;

/// <summary>
/// Modern JSON-based application settings that work with single-file deployment.
/// Replaces the old ApplicationSettingsBase approach.
/// </summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlotPaint",
        "settings.json");

    private static AppSettings? _instance;
    private static readonly object _lock = new();

    public static AppSettings Default
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= Load();
                }
            }
            return _instance;
        }
    }

    // Machine Settings
    public int BedX { get; set; } = 841;
    public int BedY { get; set; } = 1189;
    public int CircleSegments { get; set; } = 128;
    public int CircleCurveSegments { get; set; } = 1;
    public double SafeMarginMm { get; set; } = 50;
    public bool UseIndividualMargins { get; set; } = false;
    public double MarginLeftMm { get; set; } = 50;
    public double MarginTopMm { get; set; } = 50;
    public double MarginRightMm { get; set; } = 50;
    public double MarginBottomMm { get; set; } = 50;
    public string BedSizeMode { get; set; } = "Auto";
    public bool ManualHomeAtMaxX { get; set; } = true;
    public bool ManualHomeAtMaxY { get; set; } = true;

    // Window Positions
    public double ToolsWindowLeft { get; set; } = 100;
    public double ToolsWindowTop { get; set; } = 150;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // If loading fails, return defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Silently fail if we can't save settings
        }
    }
}
