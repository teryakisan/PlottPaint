using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NVSPlotter.Controls;
using NVSPlotter.Services;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Globalization;

namespace NVSPlotter.Windows
{
    /// <summary>
    /// Represents a brush stroke profile that can be used by the plotter.
    /// </summary>
    public class BrushProfile
    {
        public string Name { get; set; } = string.Empty;
        public IReadOnlyList<System.Windows.Point> NormalizedPoints { get; set; } = Array.Empty<System.Windows.Point>();
        public double StrokeSpeed { get; set; } = 100.0;
        public double PressureMultiplier { get; set; } = 1.0;
        public double MinZ { get; set; } = 0.0;
        public double MaxZ { get; set; } = 10.0;
        public int SampleCount { get; set; } = 200;
        public double BedWidthMm { get; set; } = 200.0;
        public string Description { get; set; } = string.Empty;
        
        /// <summary>
        /// Interpolates the Y value (pressure) at a given normalized X position (0..1).
        /// </summary>
        public double InterpolateY(double x)
        {
            if (NormalizedPoints.Count == 0) return 0.5;
            if (NormalizedPoints.Count == 1) return NormalizedPoints[0].Y;
            
            x = Math.Max(0, Math.Min(1, x));
            
            for (int i = 0; i < NormalizedPoints.Count - 1; i++)
            {
                var p1 = NormalizedPoints[i];
                var p2 = NormalizedPoints[i + 1];
                
                if (x >= p1.X && x <= p2.X)
                {
                    var segmentWidth = p2.X - p1.X;
                    if (segmentWidth < 0.0001) return p1.Y;
                    
                    var t = (x - p1.X) / segmentWidth;
                    return p1.Y + t * (p2.Y - p1.Y);
                }
            }
            
            return NormalizedPoints[^1].Y;
        }
        
        /// <summary>
        /// Maps a normalized Y value (0..1 where 0=top, 1=bottom) to a Z coordinate.
        /// </summary>
        public double MapYToZ(double normalizedY)
        {
            return MinZ + normalizedY * (MaxZ - MinZ);
        }
    }

    public partial class BrushStrokeProfilesWindow : Window
    {
        private readonly List<CurveEditor> _editors = new();
        private CurveEditor? _selectedEditor = null;
        private bool _isUpdatingProperties = false;
        private readonly GrblManagerService? _grblManager;
        private readonly Action<string>? _log;
        
        /// <summary>
        /// Event raised when the list of active profiles (those with IsInGroup checked) changes.
        /// </summary>
        public event EventHandler<IReadOnlyList<BrushProfile>>? ActiveProfilesChanged;
        
        /// <summary>
        /// Gets the list of brush profiles that have IsInGroup (Include in Use Group) enabled.
        /// These are the profiles available for the plotter to apply to strokes.
        /// </summary>
        public IReadOnlyList<BrushProfile> ActiveProfiles => GetActiveProfiles();
        
        private List<BrushProfile> GetActiveProfiles()
        {
            var profiles = new List<BrushProfile>();
            foreach (var ce in _editors)
            {
                if (ce.IsInGroup)
                {
                    profiles.Add(CreateBrushProfileFromEditor(ce));
                }
            }
            return profiles;
        }
        
        private static BrushProfile CreateBrushProfileFromEditor(CurveEditor ce)
        {
            return new BrushProfile
            {
                Name = ce.ProfileName ?? string.Empty,
                NormalizedPoints = ce.GetNormalizedPoints(),
                StrokeSpeed = ce.StrokeSpeed,
                PressureMultiplier = ce.PressureMultiplier,
                MinZ = ce.MinZ,
                MaxZ = ce.MaxZ,
                SampleCount = ce.SampleCount,
                BedWidthMm = ce.BedWidthMm,
                Description = ce.Description ?? string.Empty
            };
        }
        
        private void RaiseActiveProfilesChanged()
        {
            ActiveProfilesChanged?.Invoke(this, ActiveProfiles);
        }

        public BrushStrokeProfilesWindow() : this(null, null)
        {
        }

        public BrushStrokeProfilesWindow(GrblManagerService? grblManager, Action<string>? log)
        {
            _grblManager = grblManager;
            _log = log ?? (s => System.Diagnostics.Debug.WriteLine(s));
            
            InitializeComponent();
            Loaded += BrushStrokeProfilesWindow_Loaded;

            ClearAllBtn.Click += ClearAllBtn_Click;
            SaveProfileBtn.Click += SaveProfileBtn_Click;
            CloseBtn.Click += (_, __) => Close();
            
            UpdatePropertiesPanel();
        }

        private void PropertiesExpander_Expanded(object sender, RoutedEventArgs e)
        {
            // Animate from current width to expanded width (current + panel width)
            double currentWidth = this.ActualWidth;
            double targetWidth = Math.Max(875, currentWidth + 260); // Ensure we expand by at least 260px
            AnimateWindowWidth(currentWidth, targetWidth, 1.0);
        }

        private void PropertiesExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            // Animate from current width to collapsed width (MinWidth or current - panel width)
            double currentWidth = this.ActualWidth;
            double targetWidth = Math.Max(this.MinWidth, currentWidth - 260); // Respect MinWidth
            AnimateWindowWidth(currentWidth, targetWidth, 0.5);
        }

        private void AnimateWindowWidth(double from, double to, double durationSeconds)
        {
            // Ensure we respect MinWidth
            to = Math.Max(this.MinWidth, to);
            from = Math.Max(this.MinWidth, from);
            
            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                EasingFunction = new System.Windows.Media.Animation.PowerEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            
            this.BeginAnimation(Window.WidthProperty, animation);
        }

        // Use generated InitializeComponent from XAML

        private void BrushStrokeProfilesWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Set MinWidth to the current width (collapsed state)
            this.MinWidth = this.ActualWidth;
            
            // collect editors present
            _editors.Clear();
            foreach (var child in ProfilesGrid.Children)
            {
                if (child is Border b && b.Child is Grid g && g.Children.OfType<Controls.CurveEditor>().FirstOrDefault() is CurveEditor ce)
                {
                    _editors.Add(ce);
                    ce.ProfileRenamed += Editor_ProfileRenamed;
                    ce.ProfileDeleted += Editor_ProfileDeleted;
                    ce.SettingsChanged += Editor_SettingsChanged;
                }
                else if (child is Controls.CurveEditor ce2)
                {
                    _editors.Add(ce2);
                    ce2.ProfileRenamed += Editor_ProfileRenamed;
                    ce2.ProfileDeleted += Editor_ProfileDeleted;
                    ce2.SettingsChanged += Editor_SettingsChanged;
                }
            }

            // Autoload saved profiles (if any)
            LoadProfiles();

            // example: subscribe to first
            if (_editors.Count > 0)
            {
                _editors[0].CurveUpdated += Editor_CurveUpdated;
            }
        }

        private void Editor_CurveUpdated(object? sender, IList<(double Xmm, double Zmm)> samples)
        {
            // handle updates (e.g., preview or live send)
        }

        private void Editor_ProfileRenamed(object? sender, string e)
        {
            // optional: persist name mapping when saving
        }

        private void Editor_SettingsChanged(object? sender, EventArgs e)
        {
            // Auto-save when settings like ShowTangentHandles or IsInGroup change
            SaveProfiles();
            
            // Raise event if the active profiles list might have changed
            // (this happens when IsInGroup is toggled on any editor)
            RaiseActiveProfilesChanged();
        }

        private void Editor_ProfileDeleted(object? sender, EventArgs e)
        {
            if (sender is CurveEditor ce)
            {
                // Check if the deleted profile was in the active group
                bool wasInGroup = ce.IsInGroup;
                
                // The CurveEditor now removes its containing Border element itself,
                // so we just need to clean up our internal list
                _editors.Remove(ce);
                
                // If the deleted editor was selected, clear the selection
                if (_selectedEditor == ce)
                {
                    _selectedEditor = null;
                    UpdatePropertiesPanel();
                }
                
                // Raise event if the deleted profile was in the active group
                if (wasInGroup)
                {
                    RaiseActiveProfilesChanged();
                }
            }
        }

        private void FirstEmptyCell_Click(object sender, MouseButtonEventArgs e)
        {
            AddNewProfile();
        }

        private void CurveEditorCell_Click(object sender, MouseButtonEventArgs e)
        {
            // Find the CurveEditor within the clicked Border
            if (sender is Border border && border.Child is Grid grid)
            {
                var curveEditor = grid.Children.OfType<CurveEditor>().FirstOrDefault();
                if (curveEditor != null)
                {
                    SelectCurveEditor(curveEditor);
                    
                    // Handle double-click: open properties panel
                    if (e.ClickCount >= 2)
                    {
                        if (!PropertiesExpander.IsExpanded)
                        {
                            PropertiesExpander.IsExpanded = true;
                        }
                        e.Handled = true;
                    }
                }
            }
        }

        private void SelectCurveEditor(CurveEditor editor)
        {
            // Deselect all others
            foreach (var ce in _editors)
            {
                ce.IsSelected = false;
            }
            
            // Select the clicked one
            editor.IsSelected = true;
            _selectedEditor = editor;
            UpdatePropertiesPanel();
        }

        private void ClearAllBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Confirm with user before clearing
            var result = System.Windows.MessageBox.Show(
                "Are you sure you want to delete all profiles and start over? This action cannot be undone.",
                "Clear All Profiles",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // Clear all editors
            _editors.Clear();

            // Remove all children from ProfilesGrid
            ProfilesGrid.Children.Clear();

            // Add initial profile cell with CurveEditor
            var initialBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(6),
                Padding = new Thickness(6),
                SnapsToDevicePixels = true
            };
            initialBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            initialBorder.BorderThickness = new Thickness(1);
            initialBorder.SetResourceReference(Border.BackgroundProperty, "RefBackgroundBrush");
            initialBorder.MouseLeftButtonDown += CurveEditorCell_Click;

            var initialGrid = new Grid();
            var initialRect = new System.Windows.Shapes.Rectangle
            {
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4
            };
            initialRect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
            initialGrid.Children.Add(initialRect);

            var initialCe = new CurveEditor
            {
                Width = 194,
                Height = 194,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            initialGrid.Children.Add(initialCe);
            initialBorder.Child = initialGrid;

            ProfilesGrid.Children.Add(initialBorder);
            
            initialCe.CurveUpdated += Editor_CurveUpdated;
            initialCe.ProfileRenamed += Editor_ProfileRenamed;
            initialCe.ProfileDeleted += Editor_ProfileDeleted;
            initialCe.SettingsChanged += Editor_SettingsChanged;
            _editors.Add(initialCe);

            // Add first empty cell with plus icon
            var emptyBorder = new Border
            {
                Name = "FirstEmptyCell",
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(6),
                Padding = new Thickness(6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            emptyBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            emptyBorder.BorderThickness = new Thickness(1);
            emptyBorder.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            emptyBorder.MouseLeftButtonDown += FirstEmptyCell_Click;

            var emptyGrid = new Grid();
            var emptyRect = new System.Windows.Shapes.Rectangle
            {
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4
            };
            emptyRect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
            emptyGrid.Children.Add(emptyRect);

            var plusIcon = new FontAwesome.WPF.ImageAwesome
            {
                Icon = FontAwesome.WPF.FontAwesomeIcon.Plus,
                Width = 80,
                Height = 80,
                Opacity = 0.3,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            plusIcon.SetResourceReference(FontAwesome.WPF.ImageAwesome.ForegroundProperty, "RefForegroundBrush");
            emptyGrid.Children.Add(plusIcon);

            var helpText = new TextBlock
            {
                Text = "Click to add profile",
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 20),
                Opacity = 0.6
            };
            helpText.SetResourceReference(TextBlock.ForegroundProperty, "RefForegroundBrush");
            emptyGrid.Children.Add(helpText);

            emptyBorder.Child = emptyGrid;
            ProfilesGrid.Children.Add(emptyBorder);

            // Save the cleared state
            SaveProfiles();
        }

        private void AddNewProfile()
        {
            // Count empty cells (cells without CurveEditor)
            int emptyCount = 0;
            int firstEmptyIndex = -1;
            
            for (int i = 0; i < ProfilesGrid.Children.Count; i++)
            {
                if (ProfilesGrid.Children[i] is Border b && b.Child is Grid g)
                {
                    if (!g.Children.OfType<Controls.CurveEditor>().Any())
                    {
                        if (firstEmptyIndex == -1)
                        {
                            firstEmptyIndex = i;
                        }
                        emptyCount++;
                    }
                }
            }

            // If there's no empty cell found, something is wrong - create one
            if (firstEmptyIndex < 0)
            {
                var newEmptyCell = CreateAddCell();
                ProfilesGrid.Children.Add(newEmptyCell);
                firstEmptyIndex = ProfilesGrid.Children.Count - 1;
                emptyCount = 1;
            }

            // Now add the CurveEditor to the first empty cell
            if (ProfilesGrid.Children[firstEmptyIndex] is Border border && border.Child is Grid grid)
            {
                // Clear existing children (plus icon, help text, etc.)
                grid.Children.Clear();
                
                // Add the dashed rectangle background
                var rect = new System.Windows.Shapes.Rectangle
                {
                    StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                    StrokeThickness = 1,
                    RadiusX = 4,
                    RadiusY = 4
                };
                rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
                grid.Children.Add(rect);
                
                // Add the CurveEditor
                var ce = new CurveEditor { Width = 194, Height = 194 };
                grid.Children.Add(ce);
                ce.CurveUpdated += Editor_CurveUpdated;
                ce.ProfileRenamed += Editor_ProfileRenamed;
                ce.ProfileDeleted += Editor_ProfileDeleted;
                ce.SettingsChanged += Editor_SettingsChanged;
                _editors.Add(ce);
                
                // Update the border's click handler and cursor
                border.MouseLeftButtonDown -= FirstEmptyCell_Click; // Remove the "add" click handler
                border.MouseLeftButtonDown += CurveEditorCell_Click; // Add the selection click handler
                border.Cursor = System.Windows.Input.Cursors.Arrow; // Change cursor back to normal
                
                // Ensure newly created editor is visible
                EnsureElementFullyVisible(ce);
            }

            // Now ensure there's always an "add" cell available
            // Check if we need to create a new one
            bool hasAddCell = false;
            for (int i = 0; i < ProfilesGrid.Children.Count; i++)
            {
                if (ProfilesGrid.Children[i] is Border b && b.Child is Grid g)
                {
                    if (!g.Children.OfType<Controls.CurveEditor>().Any())
                    {
                        hasAddCell = true;
                        break;
                    }
                }
            }

            if (!hasAddCell)
            {
                // Create a new "add" cell
                var newAddCell = CreateAddCell();
                ProfilesGrid.Children.Add(newAddCell);
            }
        }

        /// <summary>
        /// Creates a new "add" cell with plus icon for adding new profiles.
        /// </summary>
        private Border CreateAddCell()
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(6),
                Padding = new Thickness(6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            border.BorderThickness = new Thickness(1);
            border.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            border.MouseLeftButtonDown += FirstEmptyCell_Click;

            var grid = new Grid();
            
            var rect = new System.Windows.Shapes.Rectangle
            {
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4
            };
            rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
            grid.Children.Add(rect);

            var plusIcon = new FontAwesome.WPF.ImageAwesome
            {
                Icon = FontAwesome.WPF.FontAwesomeIcon.Plus,
                Width = 80,
                Height = 80,
                Opacity = 0.3,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            plusIcon.SetResourceReference(FontAwesome.WPF.ImageAwesome.ForegroundProperty, "RefForegroundBrush");
            grid.Children.Add(plusIcon);

            var helpText = new TextBlock
            {
                Text = "Click to add profile",
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 20),
                Opacity = 0.6
            };
            helpText.SetResourceReference(TextBlock.ForegroundProperty, "RefForegroundBrush");
            grid.Children.Add(helpText);

            border.Child = grid;
            return border;
        }

        private void ConvertNextEmptyToAddCell()
        {
            // Find the first empty cell (without CurveEditor)
            for (int i = 0; i < ProfilesGrid.Children.Count; i++)
            {
                if (ProfilesGrid.Children[i] is Border border && border.Child is Grid grid)
                {
                    // Skip cells that already have a CurveEditor
                    if (grid.Children.OfType<Controls.CurveEditor>().Any())
                        continue;

                    // Check if this cell already has the plus icon
                    if (grid.Children.OfType<FontAwesome.WPF.ImageAwesome>().Any())
                        return; // Already has plus icon, nothing to do

                    // Clear existing children (the "Empty" text)
                    grid.Children.Clear();

                    // Add the dashed rectangle background
                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                        StrokeThickness = 1,
                        RadiusX = 4,
                        RadiusY = 4
                    };
                    rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
                    grid.Children.Add(rect);

                    // Add the plus icon
                    var plusIcon = new FontAwesome.WPF.ImageAwesome
                    {
                        Icon = FontAwesome.WPF.FontAwesomeIcon.Plus,
                        Width = 80,
                        Height = 80,
                        Opacity = 0.3,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    };
                    plusIcon.SetResourceReference(FontAwesome.WPF.ImageAwesome.ForegroundProperty, "RefForegroundBrush");
                    grid.Children.Add(plusIcon);

                    // Add the help text
                    var helpText = new TextBlock
                    {
                        Text = "Click to add profile",
                        FontSize = 11,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 20),
                        Opacity = 0.6
                    };
                    helpText.SetResourceReference(TextBlock.ForegroundProperty, "RefForegroundBrush");
                    grid.Children.Add(helpText);

                    // Make the border clickable
                    border.Cursor = System.Windows.Input.Cursors.Hand;
                    border.MouseLeftButtonDown -= FirstEmptyCell_Click; // Remove existing handler if any
                    border.MouseLeftButtonDown += FirstEmptyCell_Click; // Add click handler

                    return; // Only convert the first empty cell
                }
            }
        }

        private Border CreateEmptyCell()
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(6),
                Padding = new Thickness(6),
                Background = (System.Windows.Media.Brush)FindResource("ControlBackgroundBrush")
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            border.BorderThickness = new Thickness(1);

            var grid = new Grid();
            
            var rect = new System.Windows.Shapes.Rectangle
            {
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4
            };
            rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
            grid.Children.Add(rect);

            var textBlock = new TextBlock
            {
                Text = "Empty",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, "RefForegroundBrush");
            grid.Children.Add(textBlock);

            border.Child = grid;
            return border;
        }

        private Border CreateProfileCell()
        {
            var border = new Border
            {
                Margin = new Thickness(6),
                Padding = new Thickness(6)
            };
            var rect = new System.Windows.Shapes.Rectangle
            {
                Stroke = System.Windows.Media.Brushes.DarkGray,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                RadiusX = 4,
                RadiusY = 4
            };
            var grid = new Grid();
            rect.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "ControlBackgroundBrush");
            rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
            grid.Children.Add(rect);
            var ce = new CurveEditor { Width = 194, Height = 194 };
            grid.Children.Add(ce);
            border.Child = grid;
            ce.CurveUpdated += Editor_CurveUpdated;
            return border;
        }

        private void EnsureCellHasBackground(Grid g)
        {
            if (g == null) return;
            // if there is already a Rectangle background, ensure it has themed brushes
            var rect = g.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault();
            if (rect == null)
            {
                rect = new System.Windows.Shapes.Rectangle();
                rect.RadiusX = 4; rect.RadiusY = 4;
                rect.StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 };
                rect.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "ControlBackgroundBrush");
                rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
                rect.StrokeThickness = 1;
                g.Children.Insert(0, rect);
            }
            else
            {
                rect.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "ControlBackgroundBrush");
                rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
            }
        }

        private void SaveProfileBtn_Click(object? sender, RoutedEventArgs e)
        {
            SaveProfiles();
        }

        private string GetProfilesPath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVSPlotter");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "brushprofiles.json");
        }

        private void SaveProfiles()
        {
            var profiles = new List<SerializableProfile>();
            foreach (var ce in _editors)
            {
                var pts = ce.GetNormalizedPoints();
                var list = pts.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                profiles.Add(new SerializableProfile 
                { 
                    Points = list, 
                    Name = ce.ProfileName ?? string.Empty, 
                    IsSelected = ce.IsSelected,
                    IsInGroup = ce.IsInGroup,
                    ShowTangentHandles = ce.ShowTangentHandles,
                    StrokeSpeed = ce.StrokeSpeed,
                    PressureMultiplier = ce.PressureMultiplier,
                    Enabled = ce.Enabled,
                    Description = ce.Description ?? string.Empty,
                    BedWidthMm = ce.BedWidthMm,
                    MinZ = ce.MinZ,
                    MaxZ = ce.MaxZ,
                    SampleCount = ce.SampleCount
                });
            }

            var path = GetProfilesPath();
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(profiles, opts));
            //System.Windows.MessageBox.Show($"Saved {profiles.Count} profiles.", "Save Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LoadProfiles()
        {
            var path = GetProfilesPath();
            if (!File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var profiles = JsonSerializer.Deserialize<List<SerializableProfile>>(json);
                if (profiles == null || profiles.Count == 0) return;

                // We need to create cells for profiles beyond what exists in XAML
                // First profile goes to the existing CurveEditor0 cell (index 0)
                // Additional profiles need new cells created
                
                for (int i = 0; i < profiles.Count; i++)
                {
                    Border? border = null;
                    Grid? grid = null;
                    CurveEditor? ce = null;
                    
                    if (i < ProfilesGrid.Children.Count && ProfilesGrid.Children[i] is Border existingBorder)
                    {
                        border = existingBorder;
                        grid = border.Child as Grid;
                        ce = grid?.Children.OfType<CurveEditor>().FirstOrDefault();
                    }
                    
                    // If we need to use a cell that's currently an "add" cell (no CurveEditor), convert it
                    if (border != null && grid != null && ce == null)
                    {
                        // This is an empty/add cell - convert it to a profile cell
                        grid.Children.Clear();
                        
                        // Add rectangle background
                        var rect = new System.Windows.Shapes.Rectangle
                        {
                            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                            StrokeThickness = 1,
                            RadiusX = 4,
                            RadiusY = 4
                        };
                        rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
                        grid.Children.Add(rect);
                        
                        // Create and add CurveEditor
                        ce = new CurveEditor { Width = 194, Height = 194 };
                        grid.Children.Add(ce);
                        ce.CurveUpdated += Editor_CurveUpdated;
                        ce.ProfileRenamed += Editor_ProfileRenamed;
                        ce.ProfileDeleted += Editor_ProfileDeleted;
                        ce.SettingsChanged += Editor_SettingsChanged;
                        _editors.Add(ce);
                        
                        // Update border handlers - remove add handler, add selection handler
                        border.MouseLeftButtonDown -= FirstEmptyCell_Click;
                        border.MouseLeftButtonDown -= CurveEditorCell_Click; // Remove to avoid duplicate
                        border.MouseLeftButtonDown += CurveEditorCell_Click;
                        border.Cursor = System.Windows.Input.Cursors.Arrow;
                        border.SetResourceReference(Border.BackgroundProperty, "RefBackgroundBrush");
                    }
                    else if (border == null || grid == null)
                    {
                        // Need to create a new cell entirely
                        border = new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            Margin = new Thickness(6),
                            Padding = new Thickness(6),
                            SnapsToDevicePixels = true
                        };
                        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                        border.BorderThickness = new Thickness(1);
                        border.SetResourceReference(Border.BackgroundProperty, "RefBackgroundBrush");
                        border.MouseLeftButtonDown += CurveEditorCell_Click;

                        grid = new Grid();
                        var rect = new System.Windows.Shapes.Rectangle
                        {
                            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 },
                            StrokeThickness = 1,
                            RadiusX = 4,
                            RadiusY = 4
                        };
                        rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "RefForegroundBrush");
                        grid.Children.Add(rect);

                        ce = new CurveEditor { Width = 194, Height = 194 };
                        grid.Children.Add(ce);
                        border.Child = grid;
                        
                        // Insert before the last item (which should be the add cell)
                        var insertIndex = Math.Min(i, ProfilesGrid.Children.Count);
                        ProfilesGrid.Children.Insert(insertIndex, border);
                        
                        ce.CurveUpdated += Editor_CurveUpdated;
                        ce.ProfileRenamed += Editor_ProfileRenamed;
                        ce.ProfileDeleted += Editor_ProfileDeleted;
                        ce.SettingsChanged += Editor_SettingsChanged;
                        _editors.Add(ce);
                    }
                    
                    // Now load the profile data into the CurveEditor
                    if (ce != null)
                    {
                        var pts = profiles[i].Points?.Select(p => new System.Windows.Point(p.X, p.Y)) ?? Enumerable.Empty<System.Windows.Point>();
                        ce.SetNormalizedPoints(pts);
                        if (!string.IsNullOrWhiteSpace(profiles[i].Name)) ce.ProfileName = profiles[i].Name;
                        ce.IsSelected = profiles[i].IsSelected;
                        ce.IsInGroup = profiles[i].IsInGroup;
                        ce.ShowTangentHandles = profiles[i].ShowTangentHandles;
                        ce.StrokeSpeed = profiles[i].StrokeSpeed;
                        ce.PressureMultiplier = profiles[i].PressureMultiplier;
                        ce.Enabled = profiles[i].Enabled;
                        ce.Description = profiles[i].Description ?? string.Empty;
                        ce.BedWidthMm = profiles[i].BedWidthMm;
                        ce.MinZ = profiles[i].MinZ;
                        ce.MaxZ = profiles[i].MaxZ;
                        ce.SampleCount = profiles[i].SampleCount;
                        
                        // If this profile is selected, update the properties panel
                        if (ce.IsSelected)
                        {
                            _selectedEditor = ce;
                            UpdatePropertiesPanel();
                        }
                    }
                }
                
                // Ensure there's always an "add" cell at the end
                bool hasAddCell = false;
                for (int i = 0; i < ProfilesGrid.Children.Count; i++)
                {
                    if (ProfilesGrid.Children[i] is Border b && b.Child is Grid g)
                    {
                        if (!g.Children.OfType<CurveEditor>().Any())
                        {
                            hasAddCell = true;
                            break;
                        }
                    }
                }
                
                if (!hasAddCell)
                        {
                            var addCell = CreateAddCell();
                            ProfilesGrid.Children.Add(addCell);
                        }
                
                        // Raise event to notify listeners of loaded active profiles
                        RaiseActiveProfilesChanged();
                    }
                    catch
                    {
                        // ignore load errors
                    }
                }

        private void EnsureElementFullyVisible(UIElement element)
        {
            if (element == null) return;
            if (ProfilesScrollViewer == null) return;

            // Delay until layout updated
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var sv = ProfilesScrollViewer;
                    // transform to scrollviewer content coordinates
                    var transform = element.TransformToAncestor(sv);
                    var rect = transform.TransformBounds(new System.Windows.Rect(new System.Windows.Size(element.RenderSize.Width, element.RenderSize.Height)));
                    var viewportHeight = sv.ViewportHeight > 0 ? sv.ViewportHeight : sv.ActualHeight;

                    // compute how much of the element is outside the bottom
                    var outsideBelow = rect.Bottom - viewportHeight;
                    // if more than 1/10 of element height is outside, scroll down so it's fully visible
                    if (outsideBelow > (element.RenderSize.Height / 10.0))
                    {
                        var targetOffset = sv.VerticalOffset + outsideBelow + 8; // small padding
                        sv.ScrollToVerticalOffset(targetOffset);
                    }
                    // also handle if element is above the viewport (optional)
                    var outsideAbove = rect.Top;
                    if (outsideAbove < 0)
                    {
                        sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + outsideAbove - 8));
                    }
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private class SerializableProfile
        {
            public List<SerializablePoint> Points { get; set; } = new();
            public string Name { get; set; } = string.Empty;
            public bool IsSelected { get; set; } = false;
            public bool IsInGroup { get; set; } = false;
            public bool ShowTangentHandles { get; set; } = true;
            public double StrokeSpeed { get; set; } = 100.0;
            public double PressureMultiplier { get; set; } = 1.0;
            public bool Enabled { get; set; } = true;
            public string Description { get; set; } = string.Empty;
            public double BedWidthMm { get; set; } = 200.0;
            public double MinZ { get; set; } = 0.0;
            public double MaxZ { get; set; } = 10.0;
            public int SampleCount { get; set; } = 200;
        }

        private class SerializablePoint
        {
            public double X { get; set; }
            public double Y { get; set; }
        }

        // Properties Panel Methods
        private void UpdatePropertiesPanel()
        {
            _isUpdatingProperties = true;
            
            if (_selectedEditor == null)
            {
                // Hide all property controls, show "no selection" message
                PropProfileName.IsEnabled = false;
                PropStrokeSpeed.IsEnabled = false;
                PropStrokeSpeedSlider.IsEnabled = false;
                PropPressureMultiplier.IsEnabled = false;
                PropPressureMultiplierSlider.IsEnabled = false;
                PropMinZ.IsEnabled = false;
                PropMaxZ.IsEnabled = false;
                PropSampleCount.IsEnabled = false;
                
                PropProfileName.Text = "";
                PropStrokeSpeed.Text = "100";
                PropStrokeSpeedSlider.Value = 100;
                PropPressureMultiplier.Text = "1.0";
                PropPressureMultiplierSlider.Value = 1.0;
                PropMinZ.Text = "0";
                PropMaxZ.Text = "10";
                PropSampleCount.Text = "200";
                
                NoSelectionText.Visibility = Visibility.Visible;
            }
            else
            {
                // Enable all controls and populate with selected editor's values
                PropProfileName.IsEnabled = true;
                PropStrokeSpeed.IsEnabled = true;
                PropStrokeSpeedSlider.IsEnabled = true;
                PropPressureMultiplier.IsEnabled = true;
                PropPressureMultiplierSlider.IsEnabled = true;
                PropMinZ.IsEnabled = true;
                PropMaxZ.IsEnabled = true;
                PropSampleCount.IsEnabled = true;
                
                PropProfileName.Text = _selectedEditor.ProfileName ?? "";
                PropStrokeSpeed.Text = _selectedEditor.StrokeSpeed.ToString("F0");
                PropStrokeSpeedSlider.Value = _selectedEditor.StrokeSpeed;
                PropPressureMultiplier.Text = _selectedEditor.PressureMultiplier.ToString("F2");
                PropPressureMultiplierSlider.Value = _selectedEditor.PressureMultiplier;
                PropMinZ.Text = _selectedEditor.MinZ.ToString("F1");
                PropMaxZ.Text = _selectedEditor.MaxZ.ToString("F1");
                PropSampleCount.Text = _selectedEditor.SampleCount.ToString();
                
                NoSelectionText.Visibility = Visibility.Collapsed;
            }
            
            _isUpdatingProperties = false;
        }

        private void PropProfileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            _selectedEditor.ProfileName = PropProfileName.Text;
        }

        private void PropStrokeSpeed_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            if (double.TryParse(PropStrokeSpeed.Text, out double value))
            {
                _selectedEditor.StrokeSpeed = value;
                _isUpdatingProperties = true;
                PropStrokeSpeedSlider.Value = Math.Max(PropStrokeSpeedSlider.Minimum, Math.Min(PropStrokeSpeedSlider.Maximum, value));
                _isUpdatingProperties = false;
            }
        }

        private void PropStrokeSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            _selectedEditor.StrokeSpeed = PropStrokeSpeedSlider.Value;
            _isUpdatingProperties = true;
            PropStrokeSpeed.Text = PropStrokeSpeedSlider.Value.ToString("F0");
            _isUpdatingProperties = false;
        }

        private void PropPressureMultiplier_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            if (double.TryParse(PropPressureMultiplier.Text, out double value))
            {
                _selectedEditor.PressureMultiplier = value;
                _isUpdatingProperties = true;
                PropPressureMultiplierSlider.Value = Math.Max(PropPressureMultiplierSlider.Minimum, Math.Min(PropPressureMultiplierSlider.Maximum, value));
                _isUpdatingProperties = false;
            }
        }

        private void PropPressureMultiplierSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            _selectedEditor.PressureMultiplier = PropPressureMultiplierSlider.Value;
            _isUpdatingProperties = true;
            PropPressureMultiplier.Text = PropPressureMultiplierSlider.Value.ToString("F2");
            _isUpdatingProperties = false;
        }

        private void PropMinZ_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            if (double.TryParse(PropMinZ.Text, out double value))
            {
                _selectedEditor.MinZ = value;
            }
        }

        private void PropMaxZ_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            if (double.TryParse(PropMaxZ.Text, out double value))
            {
                _selectedEditor.MaxZ = value;
            }
        }

        private void PropSampleCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingProperties || _selectedEditor == null) return;
            if (int.TryParse(PropSampleCount.Text, out int value))
            {
                _selectedEditor.SampleCount = value;
            }
        }

        /// <summary>
        /// Tests the selected profile by sending Z-axis only G-code to the plotter.
        /// This allows testing the brush pressure curve without moving X/Y.
        /// </summary>
        private async void TestProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedEditor == null)
            {
                System.Windows.MessageBox.Show("Please select a profile to test.", "No Profile Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_grblManager == null)
            {
                System.Windows.MessageBox.Show("GRBL manager not available. Please open this window from the main application.", "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_grblManager.IsConnected)
            {
                System.Windows.MessageBox.Show("Please connect to the plotter first.", "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_grblManager.IsHomed)
            {
                var result = System.Windows.MessageBox.Show(
                    "The machine has not been homed. Testing brush profiles without homing could be dangerous.\n\nDo you want to continue anyway?",
                    "Machine Not Homed",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                TestProfileBtn.IsEnabled = false;
                TestProfileBtn.Content = "Testing...";

                var gcode = GenerateZTestGcode(_selectedEditor);
                _log?.Invoke($"[PROFILE TEST] Sending {gcode.Split('\n').Length} lines of Z-axis test G-code");
                _log?.Invoke($"[PROFILE TEST] Profile: {_selectedEditor.ProfileName ?? "Unnamed"}, MinZ={_selectedEditor.MinZ}, MaxZ={_selectedEditor.MaxZ}");

                var success = await _grblManager.SendGcodeAsync(gcode, (current, total) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TestProfileBtn.Content = $"Testing... {current}/{total}";
                    });
                });

                if (success)
                {
                    _log?.Invoke("[PROFILE TEST] Test complete.");
                }
                else
                {
                    _log?.Invoke("[PROFILE TEST] Test failed or was cancelled.");
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[PROFILE TEST] Error: {ex.Message}");
                System.Windows.MessageBox.Show($"Error during profile test: {ex.Message}", "Test Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TestProfileBtn.IsEnabled = true;
                TestProfileBtn.Content = "?? Test Z Profile";
            }
        }

        /// <summary>
        /// Generates G-code that only moves the Z axis to test a brush pressure profile.
        /// 
        /// Curve interpretation:
        /// - Handle 0 (X=0): Starting Z position of the stroke
        /// - Handle 4 (X=1): Ending Z position of the stroke
        /// - Y=0 (top of curve): Safe/retracted position (MinZ in GRBL terms - less negative)
        /// - Y=0.5 (center line): Canvas surface contact
        /// - Y=1 (bottom of curve): Maximum pressure into canvas (MaxZ in GRBL terms - more negative)
        /// 
        /// GRBL Z convention: More negative = physically lower (into the canvas)
        /// MinZ = safe height (e.g., -2 = 2mm above contact)
        /// MaxZ = max pressure depth (e.g., -5 = 5mm down, pressing into canvas)
        /// 
        /// The test ensures the brush starts and ends in the UP position for safety.
        /// </summary>
        private static string GenerateZTestGcode(CurveEditor editor)
        {
            var sb = new StringBuilder();
            
            // Get normalized points from the curve editor
            var normalizedPoints = editor.GetNormalizedPoints();
            
            // Profile parameters
            // In GRBL: more negative Z = physically lower (into canvas)
            // MinZ = safe/retracted (less negative, e.g., -2)
            // MaxZ = max pressure/depth (more negative, e.g., -5)
            var minZ = editor.MinZ;  // Safe position (less negative = higher)
            var maxZ = editor.MaxZ;  // Max depth (more negative = lower/into canvas)
            var strokeSpeed = editor.StrokeSpeed;
            var sampleCount = Math.Max(10, Math.Min(editor.SampleCount, 100)); // Limit samples for test
            
            // Safe Z position - use MinZ directly (it's already in GRBL coordinates)
            var safeZCmd = minZ;
            
            // Header
            sb.AppendLine("; NVSPlotter Brush Stroke Profile Test");
            sb.AppendLine($"; Profile: {editor.ProfileName ?? "Unnamed"}");
            sb.AppendLine($"; MinZ (safe/retracted): {minZ:F2}mm, MaxZ (max depth): {maxZ:F2}mm");
            sb.AppendLine($"; Stroke Speed: {strokeSpeed:F0} mm/min");
            sb.AppendLine($"; Sample Count: {sampleCount}");
            sb.AppendLine(";");
            sb.AppendLine("; Curve interpretation (GRBL Z: more negative = lower):");
            sb.AppendLine(";   Handle 0 (X=0): Starting Z position");
            sb.AppendLine(";   Handle 4 (X=1): Ending Z position");
            sb.AppendLine(";   Y=0 (top): MinZ = safe/retracted (brush UP)");
            sb.AppendLine(";   Y=0.5 (center): Canvas surface contact");
            sb.AppendLine(";   Y=1 (bottom): MaxZ = max depth/pressure (brush DOWN)");
            sb.AppendLine();
            
            // Setup
            sb.AppendLine("G21"); // mm mode
            sb.AppendLine("G90"); // absolute positioning
            sb.AppendLine();
            
            // === SAFETY: Ensure brush is UP before starting ===
            sb.AppendLine("; === SAFETY: Move to safe/retracted position first ===");
            sb.AppendLine($"G0 Z{Fmt(safeZCmd)} ; Rapid to safe Z (MinZ={minZ:F2}mm)");
            sb.AppendLine("G4 P0.5 ; Dwell to confirm position");
            sb.AppendLine();
            
            // === FULL STROKE: Sample the entire curve from X=0 to X=1 ===
            sb.AppendLine("; === STROKE PROFILE (curve X: 0.0 -> 1.0) ===");
            sb.AppendLine("; Following the complete brush stroke trajectory");
            
            for (int i = 0; i <= sampleCount; i++)
            {
                // Map i to X range 0.0 to 1.0
                var t = (double)i / sampleCount;
                
                // Get Y value from curve (0=top/retracted, 0.5=surface, 1=pressed down)
                var y = InterpolateCurveY(normalizedPoints, t);
                
                // Map Y to Z: 
                // Y=0 (top) -> MinZ (safe/retracted, less negative)
                // Y=1 (bottom) -> MaxZ (max depth, more negative)
                // Linear interpolation: Z = MinZ + Y * (MaxZ - MinZ)
                var zCmd = minZ + y * (maxZ - minZ);
                
                if (i == 0)
                {
                    // First move sets the feed rate
                    sb.AppendLine($"G1 Z{Fmt(zCmd)} F{Fmt(strokeSpeed)} ; t={t:F3}, y={y:F3} (START)");
                }
                else if (i == sampleCount)
                {
                    sb.AppendLine($"G1 Z{Fmt(zCmd)} ; t={t:F3}, y={y:F3} (END)");
                }
                else
                {
                    sb.AppendLine($"G1 Z{Fmt(zCmd)} ; t={t:F3}, y={y:F3}");
                }
            }
            
            sb.AppendLine();
            
            // === SAFETY: Ensure brush is UP after test ===
            sb.AppendLine("; === SAFETY: Return to safe/retracted position ===");
            sb.AppendLine($"G0 Z{Fmt(safeZCmd)} ; Rapid to safe Z (MinZ={minZ:F2}mm)");
            sb.AppendLine("G4 P0.5 ; Final dwell");
            sb.AppendLine();
            sb.AppendLine("; End of profile test");
            
            return sb.ToString();
        }

        /// <summary>
        /// Interpolates the Y value on the curve at a given normalized X position (0..1).
        /// </summary>
        private static double InterpolateCurveY(IReadOnlyList<System.Windows.Point> points, double x)
        {
            if (points.Count == 0) return 0.5;
            if (points.Count == 1) return points[0].Y;
            
            // Clamp x to valid range
            x = Math.Max(0, Math.Min(1, x));
            
            // Find the segment containing x
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];
                
                if (x >= p1.X && x <= p2.X)
                {
                    // Linear interpolation within segment
                    var segmentWidth = p2.X - p1.X;
                    if (segmentWidth < 0.0001) return p1.Y;
                    
                    var t = (x - p1.X) / segmentWidth;
                    return p1.Y + t * (p2.Y - p1.Y);
                }
            }
            
            // If x is beyond the last point, return last Y
            return points[^1].Y;
        }

        /// <summary>
        /// Formats a double value for G-code output.
        /// </summary>
        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
