using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NVSPlotter.Controls
{
    public partial class CurveEditor : System.Windows.Controls.UserControl
    {
        // Points in normalized (0..1) vertical coordinates, X is implicit: left=0,right=1
        private double p0 = 0.5; // left anchor Y (0 top, 1 bottom)
        private double p3 = 0.5; // right anchor Y
        private double? p1 = null; // inner control 1
        private double? p2 = null; // inner control 2

        // Visual elements
        private System.Windows.Shapes.Rectangle backgroundRect;
        private System.Windows.Shapes.Path curvePath;
        private System.Windows.Shapes.Ellipse handleP0, handleP1, handleP2, handleP3;
        private System.Windows.Shapes.Line baseline;

        private const double HANDLE_RADIUS = 6.0;

        // Mapping properties
        public static readonly DependencyProperty BedWidthMmProperty = DependencyProperty.Register(
            nameof(BedWidthMm), typeof(double), typeof(CurveEditor), new PropertyMetadata(200.0));

        public double BedWidthMm
        {
            get => (double)GetValue(BedWidthMmProperty);
            set => SetValue(BedWidthMmProperty, value);
        }

        public static readonly DependencyProperty MinZProperty = DependencyProperty.Register(
            nameof(MinZ), typeof(double), typeof(CurveEditor), new PropertyMetadata(0.0));
        public double MinZ { get => (double)GetValue(MinZProperty); set => SetValue(MinZProperty, value); }

        public static readonly DependencyProperty MaxZProperty = DependencyProperty.Register(
            nameof(MaxZ), typeof(double), typeof(CurveEditor), new PropertyMetadata(10.0));
        public double MaxZ { get => (double)GetValue(MaxZProperty); set => SetValue(MaxZProperty, value); }

        public static readonly DependencyProperty SampleCountProperty = DependencyProperty.Register(
            nameof(SampleCount), typeof(int), typeof(CurveEditor), new PropertyMetadata(200));
        public int SampleCount { get => (int)GetValue(SampleCountProperty); set => SetValue(SampleCountProperty, value); }

        public event EventHandler<IList<(double Xmm, double Zmm)>>? CurveUpdated;

        // Expose/get set normalized control points: p0,p1,p2,p3 (0..1, 0=top)
        public (double p0, double? p1, double? p2, double p3) GetNormalizedPoints()
        {
            return (p0, p1, p2, p3);
        }

        public void SetNormalizedPoints(double p0v, double? p1v, double? p2v, double p3v)
        {
            p0 = Clamp01(p0v);
            p1 = p1v.HasValue ? Clamp01(p1v.Value) : (double?)null;
            p2 = p2v.HasValue ? Clamp01(p2v.Value) : (double?)null;
            p3 = Clamp01(p3v);
            RenderAll();
            CurveChangedInternal();
        }

        public CurveEditor()
        {
            InitializeComponent();
            Loaded += CurveEditor_Loaded;
            SizeChanged += CurveEditor_SizeChanged;
        }

        private void CurveEditor_Loaded(object sender, RoutedEventArgs e)
        {
            SetupVisuals();
            RenderAll();
        }

        private void CurveEditor_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // force square: use the smaller dimension
            var s = Math.Min(ActualWidth, ActualHeight);
            Width = s;
            Height = s;
            RenderAll();
        }

        private void SetupVisuals()
        {
            var c = PART_Canvas;
            c.Children.Clear();

            // background grid (4 quadrants)
            backgroundRect = new System.Windows.Shapes.Rectangle();
            // use themed background brush
            backgroundRect.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "RefBackgroundBrush");
            c.Children.Add(backgroundRect);

            // baseline
            baseline = new System.Windows.Shapes.Line { StrokeThickness = 1 };
            baseline.SetResourceReference(System.Windows.Shapes.Line.StrokeProperty, "RefForegroundBrush");
            c.Children.Add(baseline);

            curvePath = new System.Windows.Shapes.Path { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            curvePath.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "RefAccentBrush");
            c.Children.Add(curvePath);

            handleP0 = CreateHandle(); c.Children.Add(handleP0);
            handleP1 = CreateHandle(); c.Children.Add(handleP1);
            handleP2 = CreateHandle(); c.Children.Add(handleP2);
            handleP3 = CreateHandle(); c.Children.Add(handleP3);

            PART_Canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
            PART_Canvas.MouseMove += PART_Canvas_MouseMove;
            PART_Canvas.MouseLeftButtonUp += PART_Canvas_MouseLeftButtonUp;
        }

        private System.Windows.Shapes.Ellipse CreateHandle()
        {
            var e = new System.Windows.Shapes.Ellipse
            {
                Width = HANDLE_RADIUS * 2,
                Height = HANDLE_RADIUS * 2,
                StrokeThickness = 2,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            // themed brushes (fill and stroke update with theme)
            e.SetResourceReference(System.Windows.Shapes.Ellipse.FillProperty, "ControlBackgroundBrush");
            e.SetResourceReference(System.Windows.Shapes.Ellipse.StrokeProperty, "RefAccentBrush");
            e.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            return e;
        }

        private System.Windows.UIElement? draggingHandle = null;
        private System.Windows.Point dragStart;

        private void Handle_MouseLeftButtonDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is UIElement el)
            {
                draggingHandle = el;
                dragStart = e.GetPosition(PART_Canvas);
                PART_Canvas.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(PART_Canvas);
            // if curve only has endpoints, insert inner controls
            if (!p1.HasValue && !p2.HasValue)
            {
                // create control points near the click X (normalized space)
                var nx = pos.X / PART_Canvas.ActualWidth;
                // place p1 at nx/3 between left and right, p2 at 2*nx/3
                p1 = 0.5; // default middle
                p2 = 0.5;
                // start dragging nearest new handle
                RenderAll();
                CurveChangedInternal();
            }
        }

        private void PART_Canvas_MouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            if (draggingHandle == null) return;
            var p = e.GetPosition(PART_Canvas);
            var h = PART_Canvas.ActualHeight;
            var normY = Clamp01(p.Y / h);

            if (draggingHandle == handleP0)
            {
                p0 = normY;
            }
            else if (draggingHandle == handleP3)
            {
                p3 = normY;
            }
            else if (draggingHandle == handleP1 && p1.HasValue)
            {
                p1 = normY;
            }
            else if (draggingHandle == handleP2 && p2.HasValue)
            {
                p2 = normY;
            }
            RenderAll();
            CurveChangedInternal();
        }

        private void PART_Canvas_MouseLeftButtonUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (draggingHandle != null)
            {
                draggingHandle = null;
                PART_Canvas.ReleaseMouseCapture();
            }
        }

        private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

        private void RenderAll()
        {
            if (PART_Canvas == null) return;
            // Ensure visuals initialized
            if (backgroundRect == null || curvePath == null || handleP0 == null) return;
            var w = PART_Canvas.ActualWidth;
            var h = PART_Canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            Canvas.SetLeft(backgroundRect, 0); Canvas.SetTop(backgroundRect, 0);
            backgroundRect.Width = w; backgroundRect.Height = h;

            // draw quadrant grid
            // (simple visual: draw center lines)
            // remove any previous helper lines (keep shapes added earlier)
            // for brevity we won't clear the canvas here

            // baseline across center horizontally
            baseline.X1 = 0; baseline.X2 = w; baseline.Y1 = h / 2; baseline.Y2 = h / 2;

            // positions
            var y0 = p0 * h;
            var y3 = p3 * h;
            var x0 = 0.0;
            var x3 = w;

            // compute control points in pixel
            var c1x = w * 0.33;
            var c2x = w * 0.66;
            var c1y = (p1 ?? 0.5) * h;
            var c2y = (p2 ?? 0.5) * h;

            // build bezier geometry
            var pg = new PathGeometry();
            var pf = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(x0, y0), IsClosed = false };
            pf.Segments.Add(new System.Windows.Media.BezierSegment(new System.Windows.Point(c1x, c1y), new System.Windows.Point(c2x, c2y), new System.Windows.Point(x3, y3), true));
            pg.Figures.Add(pf);
            curvePath.Data = pg;

            // place handles
            PlaceHandle(handleP0, x0, y0);
            PlaceHandle(handleP1, c1x, c1y);
            PlaceHandle(handleP2, c2x, c2y);
            PlaceHandle(handleP3, x3, y3);

            // ensure mouse events wired
            PART_Canvas.MouseMove -= PART_Canvas_MouseMove;
            PART_Canvas.MouseLeftButtonUp -= PART_Canvas_MouseLeftButtonUp;
            PART_Canvas.MouseMove += PART_Canvas_MouseMove;
            PART_Canvas.MouseLeftButtonUp += PART_Canvas_MouseLeftButtonUp;
        }

        private void PlaceHandle(System.Windows.Shapes.Ellipse h, double x, double y)
        {
            Canvas.SetLeft(h, x - HANDLE_RADIUS);
            Canvas.SetTop(h, y - HANDLE_RADIUS);
            h.Visibility = Visibility.Visible;
        }

        private void CurveChangedInternal()
        {
            var samples = SampleCurve(SampleCount);
            CurveUpdated?.Invoke(this, samples.Select(p => (Xmm: p.X * BedWidthMm, Zmm: MapYToZ(p.Y))).ToList());
        }

        // returns list of normalized points (x:0..1, y:0..1) where 0 top
        private IList<(double X, double Y)> SampleCurve(int count)
        {
            var list = new List<(double X, double Y)>();
            for (int i = 0; i < count; i++)
            {
                var t = (double)i / (count - 1);
                var pt = EvaluateCubic(t);
                list.Add((pt.X, pt.Y));
            }
            return list;
        }

        // Evaluate cubic bezier in normalized pixel space (X 0..1, Y 0..1 where 0 top)
        private (double X, double Y) EvaluateCubic(double t)
        {
            // control points as normalized x,y
            var x0 = 0.0; var y0 = p0;
            var x1 = 0.33; var y1 = p1 ?? 0.5;
            var x2 = 0.66; var y2 = p2 ?? 0.5;
            var x3 = 1.0; var y3 = p3;

            double cx = Math.Pow(1 - t, 3) * x0 + 3 * Math.Pow(1 - t, 2) * t * x1 + 3 * (1 - t) * Math.Pow(t, 2) * x2 + Math.Pow(t, 3) * x3;
            double cy = Math.Pow(1 - t, 3) * y0 + 3 * Math.Pow(1 - t, 2) * t * y1 + 3 * (1 - t) * Math.Pow(t, 2) * y2 + Math.Pow(t, 3) * y3;
            return (cx, cy);
        }

        private double MapYToZ(double normalizedY)
        {
            // normalizedY: 0 at top -> should map to MaxZ
            var inv = 1.0 - normalizedY; // top -> 1
            return MinZ + inv * (MaxZ - MinZ);
        }
    }
}
