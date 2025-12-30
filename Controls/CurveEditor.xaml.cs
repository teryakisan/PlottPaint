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
        // Control points in normalized coordinates (0..1). Index 0 = left endpoint, last = right endpoint.
        // The endpoints have X fixed at 0 and 1. Inner points can move freely but their X is clamped between neighbors.
        private readonly List<System.Windows.Point> controlPoints = new();

        // Visual elements
        private System.Windows.Shapes.Rectangle backgroundRect;
        private System.Windows.Shapes.Path curvePath;
        private System.Windows.Shapes.Line verticalLine;
        private System.Windows.Shapes.Ellipse handleP0, handleP1, handleP2, handleP3, handleP4;
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

        // Expose/get set normalized control points (list of points). Returns points with X,Y normalized (0..1, 0=top)
        public IReadOnlyList<System.Windows.Point> GetNormalizedPoints() => controlPoints.AsReadOnly();

        public void SetNormalizedPoints(IEnumerable<System.Windows.Point> points)
        {
            controlPoints.Clear();
            foreach (var p in points)
            {
                controlPoints.Add(new System.Windows.Point(Clamp01(p.X), Clamp01(p.Y)));
            }
            EnsureEndpointsAndOrdering();
            RenderAll();
            CurveChangedInternal();
        }

        private void EnsureEndpointsAndOrdering()
        {
            // ensure there are at least 5 points; if not, initialize defaults
            if (controlPoints.Count < 5)
            {
                controlPoints.Clear();
                controlPoints.Add(new System.Windows.Point(0.0, 0.0));
                controlPoints.Add(new System.Windows.Point(0.25, 0.5));
                controlPoints.Add(new System.Windows.Point(0.5, 0.5));
                controlPoints.Add(new System.Windows.Point(0.75, 0.5));
                controlPoints.Add(new System.Windows.Point(1.0, 0.0));
                return;
            }

            // clamp endpoints X and ensure endpoints Y default to top if NaN
            controlPoints[0] = new System.Windows.Point(0.0, Clamp01(controlPoints[0].Y));
            var last = controlPoints.Count - 1;
            controlPoints[last] = new System.Windows.Point(1.0, Clamp01(controlPoints[last].Y));

            // ensure X ordering
            for (int i = 1; i < last; i++)
            {
                var minX = controlPoints[i - 1].X + 0.001;
                var maxX = controlPoints[i + 1].X - 0.001;
                var x = Clamp01(controlPoints[i].X);
                x = Math.Max(minX, Math.Min(maxX, x));
                controlPoints[i] = new System.Windows.Point(x, Clamp01(controlPoints[i].Y));
            }
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

            // vertical mid line for quadrant grid
            verticalLine = new System.Windows.Shapes.Line { StrokeThickness = 1 };
            verticalLine.SetResourceReference(System.Windows.Shapes.Line.StrokeProperty, "RefForegroundBrush");
            c.Children.Add(verticalLine);

            curvePath = new System.Windows.Shapes.Path { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            curvePath.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "RefAccentBrush");
            c.Children.Add(curvePath);

            // create five handles
            // ensure control points exist
            EnsureEndpointsAndOrdering();

            handleP0 = CreateHandle(); c.Children.Add(handleP0);
            handleP1 = CreateHandle(); c.Children.Add(handleP1);
            handleP2 = CreateHandle(); c.Children.Add(handleP2);
            handleP3 = CreateHandle(); c.Children.Add(handleP3);
            handleP4 = CreateHandle(); c.Children.Add(handleP4);

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
            // No-op: controlPoints are initialized with five points by default.
            // Future: could insert additional points on click.
        }

        private void PART_Canvas_MouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            if (draggingHandle == null) return;
            var p = e.GetPosition(PART_Canvas);
            var w = PART_Canvas.ActualWidth;
            var h = PART_Canvas.ActualHeight;
            var nx = Clamp01(p.X / Math.Max(1, w));
            var ny = Clamp01(p.Y / Math.Max(1, h));

            // map dragging handle to index
            var handleList = new List<System.Windows.Shapes.Ellipse> { handleP0, handleP1, handleP2, handleP3, handleP4 };
            var idx = handleList.IndexOf(draggingHandle as System.Windows.Shapes.Ellipse);
            if (idx < 0) return;

            // endpoints (0 and last) constrained to X=0/1, move only vertically
            if (idx == 0)
            {
                controlPoints[0] = new System.Windows.Point(controlPoints[0].X, ny);
            }
            else if (idx == controlPoints.Count - 1)
            {
                var last = controlPoints.Count - 1;
                controlPoints[last] = new System.Windows.Point(controlPoints[last].X, ny);
            }
            else
            {
                // inner points can move freely but must remain between neighbors in X
                var minX = controlPoints[idx - 1].X + 0.001;
                var maxX = controlPoints[idx + 1].X - 0.001;
                var x = Math.Max(minX, Math.Min(maxX, nx));
                controlPoints[idx] = new System.Windows.Point(x, ny);
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
            // ensure at least 5 control points and endpoints ordering
            EnsureEndpointsAndOrdering();
            var w = PART_Canvas.ActualWidth;
            var h = PART_Canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            Canvas.SetLeft(backgroundRect, 0); Canvas.SetTop(backgroundRect, 0);
            backgroundRect.Width = w; backgroundRect.Height = h;

            // draw quadrant grid by setting baseline (horizontal center) and verticalLine (vertical center)
            baseline.X1 = 0; baseline.X2 = w; baseline.Y1 = h / 2; baseline.Y2 = h / 2;

            // positions from controlPoints
            EnsureEndpointsAndOrdering();
            var pts = controlPoints.ToArray();
            var pixelPts = pts.Select(pt => new System.Windows.Point(pt.X * w, pt.Y * h)).ToArray();
            var x0 = pixelPts[0].X; var y0 = pixelPts[0].Y;
            var x3 = pixelPts[pixelPts.Length - 1].X; var y3 = pixelPts[pixelPts.Length - 1].Y;

            // build bezier as poly-bezier through control points (approximate using cubic segments)
            var pg = new PathGeometry();
            var pf = new System.Windows.Media.PathFigure { StartPoint = new System.Windows.Point(pixelPts[0].X, pixelPts[0].Y), IsClosed = false };

            // Create cubic bezier segments between successive points using Hermite-style interpolation for smoothness
            for (int i = 1; i < pixelPts.Length; i++)
            {
                var pPrev = pixelPts[i - 1];
                var pCurr = pixelPts[i];
                // simple control handles: tangent-based
                var dx = (pCurr.X - pPrev.X);
                var c1 = new System.Windows.Point(pPrev.X + dx * 0.33, pPrev.Y + dx * 0.0);
                var c2 = new System.Windows.Point(pPrev.X + dx * 0.66, pCurr.Y + dx * 0.0);
                pf.Segments.Add(new System.Windows.Media.BezierSegment(c1, c2, pCurr, true));
            }
            pg.Figures.Add(pf);
            curvePath.Data = pg;

            // place handles for up to five control points (or fewer)
            var handles = new[] { handleP0, handleP1, handleP2, handleP3, handleP4 };
            for (int i = 0; i < handles.Length; i++)
            {
                if (i < pixelPts.Length)
                {
                    PlaceHandle(handles[i], pixelPts[i].X, pixelPts[i].Y);
                }
                else
                {
                    handles[i].Visibility = Visibility.Collapsed;
                }
            }

            // update quadrant lines
            verticalLine.X1 = w / 2; verticalLine.X2 = w / 2; verticalLine.Y1 = 0; verticalLine.Y2 = h;

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

        // Evaluate curve: given normalized X t (0..1), return (X,Y) on the piecewise bezier approximation
        private (double X, double Y) EvaluateCubic(double t)
        {
            EnsureEndpointsAndOrdering();
            var pts = controlPoints.ToArray();

            // clamp t
            var tx = Clamp01(t);

            // find segment containing tx
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                if (tx >= a.X && tx <= b.X)
                {
                    var u = (tx - a.X) / Math.Max(1e-6, (b.X - a.X));
                    var y = a.Y * (1 - u) + b.Y * u; // linear interpolation between control points
                    return (tx, y);
                }
            }

            var last = pts.Last();
            return (last.X, last.Y);
        }

        private double MapYToZ(double normalizedY)
        {
            // normalizedY: 0 at top -> should map to MaxZ
            var inv = 1.0 - normalizedY; // top -> 1
            return MinZ + inv * (MaxZ - MinZ);
        }
    }
}
