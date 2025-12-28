using Microsoft.Win32;
using NVSPlotter.Models;
using NVSPlotter.Properties;
using NVSPlotter.Services;
using NVSPlotter.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;
using Application = System.Windows.Application;

namespace NVSPlotter.Services
{
    internal class RulerFactory
    {
        private MainWindow mw = (MainWindow)Application.Current.MainWindow;
        public void DrawRulers()
        {
            if (mw.RulerCanvas == null) return;

            const double rulerThickness = 18.0;
            const double minorStep = 1.0;
            const double majorStep = 10.0;
            double labelStep = 50.0;

            Brush rulerFill = new SolidColorBrush(Color.FromRgb(248, 248, 248));
            Brush borderBrush = Brushes.LightGray;
            Brush tickBrush = Brushes.Gray;

            double ox = rulerThickness; // document X offset (top ruler starts after corner)
            double oy = rulerThickness; // document Y offset (left ruler starts after corner)

            Rectangle CreateBackground(double width, double height)
            {
                return new Rectangle
                {
                    Width = width,
                    Height = height,
                    Fill = rulerFill,
                    Stroke = borderBrush,
                    StrokeThickness = 0.5,
                    IsHitTestVisible = false
                };
            }

            // Corner block so the rulers don't overlap each other
            var corner = CreateBackground(rulerThickness, rulerThickness);
            Canvas.SetLeft(corner, 0);
            Canvas.SetTop(corner, 0);
            mw.RulerCanvas.Children.Add(corner);

            // Top ruler (shifted right by corner width)
            var topBackground = CreateBackground(mw._doc.WidthMm - rulerThickness, rulerThickness);
            Canvas.SetLeft(topBackground, ox);
            Canvas.SetTop(topBackground, 0);
            mw.RulerCanvas.Children.Add(topBackground);

            // Left ruler (shifted down by corner height)
            var leftBackground = CreateBackground(rulerThickness, mw._doc.HeightMm - rulerThickness);
            Canvas.SetLeft(leftBackground, 0);
            Canvas.SetTop(leftBackground, oy);
            mw.RulerCanvas.Children.Add(leftBackground);

            void AddVerticalTick(double x)
            {
                bool isMajor = Math.Abs(x % majorStep) < 0.0001;
                bool showLabel = isMajor && Math.Abs(x % labelStep) < 0.0001;
                double len = isMajor ? rulerThickness : rulerThickness / 2.5;

                double cx = ox + x;

                var line = new Line
                {
                    X1 = cx,
                    X2 = cx,
                    Y1 = 0,
                    Y2 = len,
                    Stroke = tickBrush,
                    StrokeThickness = 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                mw.RulerCanvas.Children.Add(line);

                if (showLabel)
                {
                    var label = CreateRulerLabel(x, rotate: false);
                    Canvas.SetLeft(label, cx + 2);
                    Canvas.SetTop(label, 1);
                    mw.RulerCanvas.Children.Add(label);
                }
            }

            void AddHorizontalTick(double y)
            {
                bool isMajor = Math.Abs(y % majorStep) < 0.0001;
                bool showLabel = isMajor && Math.Abs(y % labelStep) < 0.0001;
                double len = isMajor ? rulerThickness : rulerThickness / 2.5;

                double cy = oy + y;

                var line = new Line
                {
                    X1 = 0,
                    X2 = len,
                    Y1 = cy,
                    Y2 = cy,
                    Stroke = tickBrush,
                    StrokeThickness = 0.6,
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                mw.RulerCanvas.Children.Add(line);

                if (showLabel)
                {
                    var label = CreateRulerLabel(y, rotate: true);
                    Canvas.SetLeft(label, 1);
                    Canvas.SetTop(label, cy + 2);
                    mw.RulerCanvas.Children.Add(label);
                }
            }

            for (double x = 0; x <= mw._doc.WidthMm; x += minorStep)
                AddVerticalTick(x);

            for (double y = 0; y <= mw._doc.HeightMm; y += minorStep)
                AddHorizontalTick(y);
        }

        public static TextBlock CreateRulerLabel(double value, bool rotate)
        {
            var tb = new TextBlock
            {
                Text = value.ToString("0"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray,
                IsHitTestVisible = false
            };
            if (rotate)
            {
                tb.LayoutTransform = new RotateTransform(-90);
            }
            return tb;
        }
    }
}
