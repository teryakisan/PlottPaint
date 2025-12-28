using NVSPlotter.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// Avoid ambiguity with System.Drawing and System.Windows.Forms types
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace NVSPlotter.Services;

public enum ImageHandle
{
    None,
    Move,
    Nw,
    Ne,
    Se,
    Sw,
    Rotate
}

public sealed class ReferenceImageManipulator
{
    private const double MIN_IMAGE_SIZE = 20.0;
    private const double ROTATE_SNAP_DEGREES = 15.0;

    private readonly ReferenceImageService _imageService;
    private readonly Canvas _canvas;
    private readonly ScrollViewer _scrollViewer;
    private readonly Func<PlotDocument> _docProvider;
    private readonly Action _renderAll;
    private readonly Action _updateUiState;

    private ImageHandle _activeHandle = ImageHandle.None;
    private PointMm _dragStart;
    private Rect _rectStart;
    private double _rotateStartAngle;
    private double _rotateStartVectorAngle;
    private PointMm _rotateCenter;

    public ReferenceImageManipulator(
        ReferenceImageService imageService,
        Canvas canvas,
        ScrollViewer scrollViewer,
        Func<PlotDocument> docProvider,
        Action renderAll,
        Action updateUiState)
    {
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _scrollViewer = scrollViewer ?? throw new ArgumentNullException(nameof(scrollViewer));
        _docProvider = docProvider ?? throw new ArgumentNullException(nameof(docProvider));
        _renderAll = renderAll ?? throw new ArgumentNullException(nameof(renderAll));
        _updateUiState = updateUiState ?? throw new ArgumentNullException(nameof(updateUiState));
    }

    public bool IsManipulating => _activeHandle != ImageHandle.None;

    public void BeginHandle(MouseButtonEventArgs e, ImageHandle handle)
    {
        if (_imageService.IsLocked) return;
        if (_imageService.ImageRect is null) return;
        if (handle == ImageHandle.None) return;

        _activeHandle = handle;
        _rectStart = _imageService.ImageRect.Value;

        if (handle == ImageHandle.Rotate)
        {
            _rotateCenter = new PointMm(
                _rectStart.Left + _rectStart.Width / 2.0,
                _rectStart.Top + _rectStart.Height / 2.0);

            var start = ClampToPage(MouseToMm(e.GetPosition(_scrollViewer)));
            _rotateStartVectorAngle = Math.Atan2(start.Y - _rotateCenter.Y, start.X - _rotateCenter.X) * 180.0 / Math.PI;
            _rotateStartAngle = _imageService.Angle;
        }
        else
        {
            _dragStart = ClampToPage(MouseToMm(e.GetPosition(_scrollViewer)));
        }

        _canvas.CaptureMouse();
        e.Handled = true;
    }

    public void Update(MouseEventArgs e)
    {
        if (_imageService.IsLocked) return;
        if (_activeHandle == ImageHandle.None) return;
        if (_imageService.ImageRect is null)
        {
            End();
            return;
        }

        if (_activeHandle == ImageHandle.Rotate)
        {
            var rotatePoint = ClampToPage(MouseToMm(e.GetPosition(_scrollViewer)));
            var currentAngle = Math.Atan2(rotatePoint.Y - _rotateCenter.Y, rotatePoint.X - _rotateCenter.X) * 180.0 / Math.PI;
            var delta = currentAngle - _rotateStartVectorAngle;
            var angle = NormalizeAngle(_rotateStartAngle + delta);

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                angle = Math.Round(angle / ROTATE_SNAP_DEGREES) * ROTATE_SNAP_DEGREES;
            }

            _imageService.Angle = angle;
            _updateUiState();
            _renderAll();
            return;
        }

        var current = ClampToPage(MouseToMm(e.GetPosition(_scrollViewer)));
        var dx = current.X - _dragStart.X;
        var dy = current.Y - _dragStart.Y;
        var keepAspect = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        Rect rect;
        if (_activeHandle == ImageHandle.Move)
        {
            rect = MoveRect(_rectStart, dx, dy);
        }
        else
        {
            rect = ResizeReferenceRect(_activeHandle, _rectStart, dx, dy, keepAspect);
        }

        _imageService.ImageRect = rect;
        _updateUiState();
        _renderAll();
    }

    public void End()
    {
        if (_activeHandle == ImageHandle.None) return;
        _activeHandle = ImageHandle.None;
        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

    private Rect MoveRect(Rect start, double dx, double dy)
    {
        var doc = _docProvider();
        var width = start.Width;
        var height = start.Height;

        var left = Math.Clamp(start.Left + dx, 0, Math.Max(0, doc.WidthMm - width));
        var top = Math.Clamp(start.Top + dy, 0, Math.Max(0, doc.HeightMm - height));
        return new Rect(left, top, width, height);
    }

    private Rect ResizeReferenceRect(ImageHandle handle, Rect start, double dx, double dy, bool keepAspect)
    {
        var doc = _docProvider();
        double left = start.Left;
        double right = start.Right;
        double top = start.Top;
        double bottom = start.Bottom;

        switch (handle)
        {
            case ImageHandle.Nw:
                left = Math.Clamp(left + dx, 0, right - MIN_IMAGE_SIZE);
                top = Math.Clamp(top + dy, 0, bottom - MIN_IMAGE_SIZE);
                break;
            case ImageHandle.Ne:
                right = Math.Clamp(right + dx, left + MIN_IMAGE_SIZE, doc.WidthMm);
                top = Math.Clamp(top + dy, 0, bottom - MIN_IMAGE_SIZE);
                break;
            case ImageHandle.Se:
                right = Math.Clamp(right + dx, left + MIN_IMAGE_SIZE, doc.WidthMm);
                bottom = Math.Clamp(bottom + dy, top + MIN_IMAGE_SIZE, doc.HeightMm);
                break;
            case ImageHandle.Sw:
                left = Math.Clamp(left + dx, 0, right - MIN_IMAGE_SIZE);
                bottom = Math.Clamp(bottom + dy, top + MIN_IMAGE_SIZE, doc.HeightMm);
                break;
        }

        var width = Math.Max(MIN_IMAGE_SIZE, right - left);
        var height = Math.Max(MIN_IMAGE_SIZE, bottom - top);

        if (keepAspect && start.Height > 0)
        {
            var aspect = start.Width / start.Height;
            switch (handle)
            {
                case ImageHandle.Nw:
                    width = Math.Max(MIN_IMAGE_SIZE, width);
                    height = width / aspect;
                    left = right - width;
                    top = bottom - height;
                    break;
                case ImageHandle.Ne:
                    width = Math.Max(MIN_IMAGE_SIZE, width);
                    height = width / aspect;
                    top = bottom - height;
                    break;
                case ImageHandle.Se:
                    width = Math.Max(MIN_IMAGE_SIZE, width);
                    height = width / aspect;
                    break;
                case ImageHandle.Sw:
                    width = Math.Max(MIN_IMAGE_SIZE, width);
                    height = width / aspect;
                    left = right - width;
                    break;
            }
        }

        width = Math.Max(MIN_IMAGE_SIZE, width);
        height = Math.Max(MIN_IMAGE_SIZE, height);

        left = Math.Clamp(left, 0, Math.Max(0, doc.WidthMm - width));
        top = Math.Clamp(top, 0, Math.Max(0, doc.HeightMm - height));

        return new Rect(left, top, width, height);
    }

    private PointMm MouseToMm(Point pViewportSpace)
    {
        var pCanvas = _scrollViewer.TranslatePoint(pViewportSpace, _canvas);
        return new PointMm(pCanvas.X, pCanvas.Y);
    }

    private PointMm ClampToPage(PointMm p)
    {
        var doc = _docProvider();
        var x = Math.Clamp(p.X, 0, doc.WidthMm);
        var y = Math.Clamp(p.Y, 0, doc.HeightMm);
        return new PointMm(x, y);
    }

    private static double NormalizeAngle(double angle)
    {
        var normalized = angle % 360.0;
        if (normalized <= -180.0) normalized += 360.0;
        if (normalized > 180.0) normalized -= 360.0;
        return normalized;
    }
}
