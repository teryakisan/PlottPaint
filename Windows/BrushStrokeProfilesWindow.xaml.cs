using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NVSPlotter.Controls;

namespace NVSPlotter.Windows
{
    public partial class BrushStrokeProfilesWindow : Window
    {
        private readonly List<CurveEditor> _editors = new();

        public BrushStrokeProfilesWindow()
        {
            InitializeComponent();
            Loaded += BrushStrokeProfilesWindow_Loaded;

            AddProfileBtn.Click += AddProfileBtn_Click;
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
                }
                else if (child is Controls.CurveEditor ce2)
                {
                    _editors.Add(ce2);
                }
            }

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

        private void AddProfileBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Add a new profile cell with a CurveEditor
            var border = CreateProfileCell();
            ProfilesGrid.Children.Add(border);
            if (border.Child is CurveEditor ce)
            {
                _editors.Add(ce);
            }
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
            grid.Children.Add(rect);
            var ce = new CurveEditor { Width = 220, Height = 220 };
            grid.Children.Add(ce);
            border.Child = grid;
            ce.CurveUpdated += Editor_CurveUpdated;
            return border;
        }

        private void SaveProfileBtn_Click(object? sender, RoutedEventArgs e)
        {
            // For demonstration, collect current profiles' normalized points
            var profiles = new List<object>();
            foreach (var ce in _editors)
            {
                profiles.Add(ce.GetNormalizedPoints());
            }

            // Here you could show a save dialog or persist into settings
            System.Windows.MessageBox.Show($"Saved {profiles.Count} profiles.", "Save Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
