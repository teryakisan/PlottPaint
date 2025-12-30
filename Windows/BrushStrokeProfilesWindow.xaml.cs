using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NVSPlotter.Controls;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace NVSPlotter.Windows
{
    public partial class BrushStrokeProfilesWindow : Window
    {
        private readonly List<CurveEditor> _editors = new();

        public BrushStrokeProfilesWindow()
        {
            InitializeComponent();
            Loaded += BrushStrokeProfilesWindow_Loaded;

            ClearAllBtn.Click += ClearAllBtn_Click;
            SaveProfileBtn.Click += SaveProfileBtn_Click;
            CloseBtn.Click += (_, __) => Close();
        }

        // Use generated InitializeComponent from XAML

        private void BrushStrokeProfilesWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // collect editors present
            _editors.Clear();
            foreach (var child in ProfilesGrid.Children)
            {
                if (child is Border b && b.Child is Grid g && g.Children.OfType<Controls.CurveEditor>().FirstOrDefault() is CurveEditor ce)
                {
                    _editors.Add(ce);
                    ce.ProfileSelected += Editor_ProfileSelected;
                    ce.ProfileRenamed += Editor_ProfileRenamed;
                    ce.ProfileDeleted += Editor_ProfileDeleted;
                }
                else if (child is Controls.CurveEditor ce2)
                {
                    _editors.Add(ce2);
                    ce2.ProfileSelected += Editor_ProfileSelected;
                    ce2.ProfileRenamed += Editor_ProfileRenamed;
                    ce2.ProfileDeleted += Editor_ProfileDeleted;
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

        private void Editor_ProfileSelected(object? sender, EventArgs e)
        {
            // ensure single selection: deselect others
            foreach (var ce in _editors)
            {
                if (!ReferenceEquals(ce, sender))
                {
                    ce.ProfileSelected -= Editor_ProfileSelected; // avoid reentrancy
                    // deselect by toggling select state via reflection: set background stroke thickness
                    // simplest method: force redraw of editor
                    ce.InvalidateVisual();
                    ce.ProfileSelected += Editor_ProfileSelected;
                }
            }
        }

        private void Editor_ProfileRenamed(object? sender, string e)
        {
            // optional: persist name mapping when saving
        }

        private void Editor_ProfileDeleted(object? sender, EventArgs e)
        {
            if (sender is CurveEditor ce)
            {
                // remove from grid
                foreach (var child in ProfilesGrid.Children)
                {
                    if (child is Border b && b.Child is Grid g && g.Children.Contains(ce))
                    {
                        g.Children.Remove(ce);
                        // show placeholder textblock if exists
                        var tb = g.Children.OfType<TextBlock>().FirstOrDefault();
                        if (tb != null) tb.Visibility = Visibility.Visible;
                        break;
                    }
                }
                _editors.Remove(ce);
            }
        }

        private void FirstEmptyCell_Click(object sender, MouseButtonEventArgs e)
        {
            AddNewProfile();
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
                Width = 220,
                Height = 220,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            initialGrid.Children.Add(initialCe);
            initialBorder.Child = initialGrid;

            ProfilesGrid.Children.Add(initialBorder);
            
            initialCe.CurveUpdated += Editor_CurveUpdated;
            initialCe.ProfileSelected += Editor_ProfileSelected;
            initialCe.ProfileRenamed += Editor_ProfileRenamed;
            initialCe.ProfileDeleted += Editor_ProfileDeleted;
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
            // Count empty cells
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

            // If this is the last empty cell, add a new empty cell first to maintain at least one empty
            if (emptyCount == 1)
            {
                var newEmptyCell = CreateEmptyCell();
                ProfilesGrid.Children.Add(newEmptyCell);
            }

            // Now add the CurveEditor to the first empty cell
            if (firstEmptyIndex >= 0 && ProfilesGrid.Children[firstEmptyIndex] is Border border && border.Child is Grid grid)
            {
                var ce = new CurveEditor { Width = 220, Height = 220 };
                grid.Children.Add(ce);
                ce.CurveUpdated += Editor_CurveUpdated;
                ce.ProfileSelected += Editor_ProfileSelected;
                ce.ProfileRenamed += Editor_ProfileRenamed;
                ce.ProfileDeleted += Editor_ProfileDeleted;
                _editors.Add(ce);
                
                // ensure newly created editor is visible
                EnsureElementFullyVisible(ce);
                
                // hide any TextBlock placeholder and plus icon in this cell
                foreach (var child in grid.Children.OfType<UIElement>().ToList())
                {
                    if (child is TextBlock || child is FontAwesome.WPF.ImageAwesome)
                    {
                        child.Visibility = Visibility.Collapsed;
                    }
                }

                // Find the next empty cell and convert it to the "add" cell with plus icon
                ConvertNextEmptyToAddCell();
            }
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
            var ce = new CurveEditor { Width = 220, Height = 220 };
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
                profiles.Add(new SerializableProfile { Points = list, Name = ce.ProfileName ?? string.Empty, IsSelected = ce.IsSelected });
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
                if (profiles == null) return;

                // apply to editors sequentially
                for (int i = 0; i < profiles.Count && i < ProfilesGrid.Children.Count; i++)
                {
                    if (ProfilesGrid.Children[i] is Border b && b.Child is Grid g)
                    {
                        // ensure there is a CurveEditor in this grid
                        var ce = g.Children.OfType<CurveEditor>().FirstOrDefault();
                        if (ce == null)
                        {
                            ce = new CurveEditor { Width = 220, Height = 220 };
                            g.Children.Add(ce);
                            ce.CurveUpdated += Editor_CurveUpdated;
                            _editors.Add(ce);
                            var tb = g.Children.OfType<TextBlock>().FirstOrDefault();
                            if (tb != null) tb.Visibility = Visibility.Collapsed;
                        }

                        // ensure background for this cell exists
                        EnsureCellHasBackground(g);

                        var pts = profiles[i].Points?.Select(p => new System.Windows.Point(p.X, p.Y)) ?? Enumerable.Empty<System.Windows.Point>();
                        ce.SetNormalizedPoints(pts);
                        if (!string.IsNullOrWhiteSpace(profiles[i].Name)) ce.ProfileName = profiles[i].Name;
                        ce.IsSelected = profiles[i].IsSelected;
                    }
                }
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
        }

        private class SerializablePoint
        {
            public double X { get; set; }
            public double Y { get; set; }
        }
    }
}
