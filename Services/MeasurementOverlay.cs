using NVSPlotter.Models;
using NVSPlotter.Util;
using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;

namespace NVSPlotter.Services;

public sealed class MeasurementOverlay
{
    private const double MEASUREMENT_MARKER_RADIUS = 4.0;

    private readonly Canvas _canvas;
    private readonly TextBlock? _status;

    private bool _isMeasuring;
    private bool _hasMeasurement;
    private PointMm _start;
    private PointMm _end;
    private Line? _line;
    private Ellipse? _startMarker;
    private Ellipse? _endMarker;
    private readonly Utility _util = new();

    public bool IsMeasuring => _isMeasuring;
    public bool HasMeasurement => _hasMeasurement;

    public MeasurementOverlay(Canvas canvas, TextBlock? status)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _status = status;
        UpdateStatusText("—");
    }

    public void Begin(PointMm start)
    {
        _isMeasuring = true;
        _hasMeasurement = false;
        _start = start;
        _end = start;

        ClearVisuals();

        _line = new Line
        {
            Stroke = Brushes.MediumPurple,
            StrokeThickness = 1.5,
            StrokeDashArray = [2, 2],
            SnapsToDevicePixels = true
        };
        _canvas.Children.Add(_line);
        Panel.SetZIndex(_line, 18);

        _startMarker = CreateMarker();
        _endMarker = CreateMarker();
        _canvas.Children.Add(_startMarker);
        _canvas.Children.Add(_endMarker);
        Panel.SetZIndex(_startMarker, 19);
        Panel.SetZIndex(_endMarker, 19);

        Update(start);
    }

    public void Update(PointMm current)
    {
        if (!_isMeasuring && !_hasMeasurement)
        {
            return;
        }

        _end = current;

        if (_line != null)
        {
            _line.X1 = _start.X;
            _line.Y1 = _start.Y;
            _line.X2 = _end.X;
            _line.Y2 = _end.Y;
        }

        SetMarkerPosition(_startMarker, _start);
        SetMarkerPosition(_endMarker, _end);

        UpdateStatus(_start, _end);
    }

    public void Complete(PointMm end, Func<PointMm, PointMm, double>? distanceEvaluator, double minimumDistance = 0.25)
    {
        if (!_isMeasuring)
            return;

        _end = end;
        var distance = distanceEvaluator?.Invoke(_start, _end) ?? Utility.Distance(_start, _end);
        if (distance < minimumDistance)
        {
            Reset();
            return;
        }

        Update(_end);
        _isMeasuring = false;
        _hasMeasurement = true;
        ReleaseMouseCapture();
    }

    public void Reset()
    {
        _isMeasuring = false;
        _hasMeasurement = false;
        ClearVisuals();
        UpdateStatusText("—");
        ReleaseMouseCapture();
    }

    private void ClearVisuals()
    {
        if (_line != null)
        {
            _canvas.Children.Remove(_line);
            _line = null;
        }
        if (_startMarker != null)
        {
            _canvas.Children.Remove(_startMarker);
            _startMarker = null;
        }
        if (_endMarker != null)
        {
            _canvas.Children.Remove(_endMarker);
            _endMarker = null;
        }
    }

    private static Ellipse CreateMarker()
    {
        return new Ellipse
        {
            Width = MEASUREMENT_MARKER_RADIUS * 2,
            Height = MEASUREMENT_MARKER_RADIUS * 2,
            Stroke = Brushes.MediumPurple,
            StrokeThickness = 1,
            Fill = Brushes.White,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
    }

    private static void SetMarkerPosition(Ellipse? marker, PointMm position)
    {
        if (marker == null) return;
        Canvas.SetLeft(marker, position.X - MEASUREMENT_MARKER_RADIUS);
        Canvas.SetTop(marker, position.Y - MEASUREMENT_MARKER_RADIUS);
    }

    private void UpdateStatus(PointMm start, PointMm end)
    {
        var dist = Utility.Distance(start, end);
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180.0 / Math.PI;
        if (angle < 0) angle += 360.0;
        UpdateStatusText($"{dist:0.##} mm @ {angle:0.#}°");
    }

    private void UpdateStatusText(string text)
    {
        if (_status != null)
        {
            _status.Text = text;
        }
    }

    private void ReleaseMouseCapture()
    {
        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

  
}
