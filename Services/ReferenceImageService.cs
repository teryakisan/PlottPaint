using NVSPlotter.Models;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NVSPlotter.Services;

public sealed class ReferenceImageService
{
    public BitmapSource? OriginalImage { get; private set; }
    public BitmapSource? ProcessedImage { get; private set; }
    public Rect? ImageRect { get; set; }
    public double Angle { get; set; }
    public ImageFilter CurrentFilter { get; private set; } = ImageFilter.None;
    public bool IsLocked { get; private set; }

    public bool HasImage => ProcessedImage != null;

    public event Action? ImageChanged;

    public bool TryLoadFromFile(string filePath, double docWidth, double docHeight, out string? error)
    {
        error = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath);
            bitmap.EndInit();
            bitmap.Freeze();

            OriginalImage = bitmap;
            Angle = 0;
            CurrentFilter = ImageFilter.None;
            ImageRect = CalculateInitialImageRect(bitmap, docWidth, docHeight);
            ApplyCurrentFilter();
            ImageChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Clear();
            return false;
        }
    }

    public void Clear()
    {
        OriginalImage = null;
        ProcessedImage = null;
        ImageRect = null;
        Angle = 0;
        CurrentFilter = ImageFilter.None;
        ImageChanged?.Invoke();
    }

    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        if (locked)
        {
            // Nothing extra yet, but keep hook for future actions
        }
    }

    public void SetFilter(ImageFilter filter)
    {
        if (filter == CurrentFilter) return;
        CurrentFilter = filter;
        ApplyCurrentFilter();
        ImageChanged?.Invoke();
    }

    public void ApplyCurrentFilter()
    {
        if (OriginalImage == null)
        {
            ProcessedImage = null;
            return;
        }

        if (CurrentFilter == ImageFilter.None)
        {
            ProcessedImage = OriginalImage;
            return;
        }

        ProcessedImage = ApplyFilter(OriginalImage, CurrentFilter);
    }

    private static Rect CalculateInitialImageRect(BitmapSource img, double docWidth, double docHeight)
    {
        const double minImageSize = 20.0;
        double maxWidth = docWidth * 0.8;
        double maxHeight = docHeight * 0.8;

        double scaleX = maxWidth / Math.Max(1, img.PixelWidth);
        double scaleY = maxHeight / Math.Max(1, img.PixelHeight);
        double scale = Math.Min(1.0, Math.Min(scaleX, scaleY));
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        double width = Math.Max(minImageSize, img.PixelWidth * scale);
        double height = Math.Max(minImageSize, img.PixelHeight * scale);

        width = Math.Min(width, docWidth);
        height = Math.Min(height, docHeight);

        var left = (docWidth - width) / 2.0;
        var top = (docHeight - height) / 2.0;
        return new Rect(left, top, width, height);
    }

    private static BitmapSource ApplyFilter(BitmapSource source, ImageFilter filter)
    {
        BitmapSource formatted = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converter = new FormatConvertedBitmap();
            converter.BeginInit();
            converter.Source = source;
            converter.DestinationFormat = PixelFormats.Bgra32;
            converter.EndInit();
            converter.Freeze();
            formatted = converter;
        }

        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        formatted.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];

            switch (filter)
            {
                case ImageFilter.Grayscale:
                    byte gray = (byte)Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0, 255);
                    pixels[i] = gray;
                    pixels[i + 1] = gray;
                    pixels[i + 2] = gray;
                    break;
                case ImageFilter.Sepia:
                    pixels[i + 2] = (byte)Math.Clamp(0.393 * r + 0.769 * g + 0.189 * b, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(0.349 * r + 0.686 * g + 0.168 * b, 0, 255);
                    pixels[i] = (byte)Math.Clamp(0.272 * r + 0.534 * g + 0.131 * b, 0, 255);
                    break;
                case ImageFilter.Invert:
                    pixels[i] = (byte)(255 - b);
                    pixels[i + 1] = (byte)(255 - g);
                    pixels[i + 2] = (byte)(255 - r);
                    break;
                case ImageFilter.HighContrast:
                    byte avg = (byte)((r + g + b) / 3);
                    byte value = avg < 128 ? (byte)0 : (byte)255;
                    pixels[i] = value;
                    pixels[i + 1] = value;
                    pixels[i + 2] = value;
                    break;
            }
        }

        var wb = new WriteableBitmap(width, height, formatted.DpiX, formatted.DpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }
}
