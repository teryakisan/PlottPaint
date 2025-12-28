using NVSPlotter.Models;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NVSPlotter.Services;

public sealed class ReferenceImageService
{
    public BitmapSource? OriginalImage { get; private set; }
    public BitmapSource? ProcessedImage { get; set; }
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
            ApplyCurrentFilter(1);
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

    public void SetFilter(ImageFilter filter, int strength)
    {
        CurrentFilter = filter;
        ApplyCurrentFilter(strength);
        ImageChanged?.Invoke();
    }

    public void ApplyCurrentFilter(int strength)
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

        ProcessedImage = ApplyFilter(OriginalImage, CurrentFilter, strength);
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

    public static BitmapSource ApplyFilter(BitmapSource source, ImageFilter filter, int strength)
    {
        ArgumentNullException.ThrowIfNull(source);


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

        if (filter == ImageFilter.None)
            return formatted;

        // Convolution filters need a second buffer + width/height context
        if (IsConvolutionFilter(filter))
        {
            var output = new byte[pixels.Length];

            switch (filter)
            {
                case ImageFilter.BoxBlur3:
                  
                    strength = Math.Clamp(strength, 1, 100);

                    var k = new int[]
                    {
                        1,1,1,
                        1,1,1,
                        1,1,1
                    };

                    byte[] a = pixels;
                    byte[] b = output;

                    for (int pass = 0; pass < strength; pass++)
                    {
                        Convolve3x3Bgra(a, b, width, height, stride, kernel: k, divisor: 9, offset: 0);
                        (a, b) = (b, a); // ping-pong
                    }

                    if (!ReferenceEquals(a, output))
                        Buffer.BlockCopy(a, 0, output, 0, output.Length);

                    break;

                case ImageFilter.GaussianBlur5:
                    Convolve5x5Bgra(pixels, output, width, height, stride,
                        kernel:
                        [
                        1,  4,  6,  4, 1,
                        4, 16, 24, 16, 4,
                        6, 24, 36, 24, 6,
                        4, 16, 24, 16, 4,
                        1,  4,  6,  4, 1
                        ],
                        divisor: 256,
                        offset: 0);
                    break;

                case ImageFilter.Sharpen:
                    Convolve3x3Bgra(pixels, output, width, height, stride,
                        kernel:
                        [
                         0,-1, 0,
                        -1, 5,-1,
                         0,-1, 0
                        ],
                        divisor: 1,
                        offset: 0);
                    break;

                case ImageFilter.Emboss:
                    Convolve3x3Bgra(pixels, output, width, height, stride,
                        kernel:
                        [
                        -2,-1, 0,
                        -1, 1, 1,
                         0, 1, 2
                        ],
                        divisor: 1,
                        offset: 128);
                    break;

                case ImageFilter.SobelEdge:
                    SobelEdgeBgra(pixels, output, width, height, stride);
                    break;

                case ImageFilter.ReverseSobelEdge:
                    ReverseSobelEdgeBgra(pixels, output, width, height, stride);
                    break;
            }

            var wbConv = new WriteableBitmap(width, height, formatted.DpiX, formatted.DpiY, PixelFormats.Bgra32, null);
            wbConv.WritePixels(new Int32Rect(0, 0, width, height), output, stride, 0);
            wbConv.Freeze();
            return wbConv;
        }

        // Per-pixel filters
        byte[]? gammaLut = null;

        if (filter == ImageFilter.Gamma22) gammaLut = BuildGammaLut(2.2);
        if (filter == ImageFilter.Gamma08) gammaLut = BuildGammaLut(0.8);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            // byte a = pixels[i + 3]; // preserve

            switch (filter)
            {
                // Existing
                case ImageFilter.Grayscale:
                    {
                        byte gray = (byte)Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0, strength);
                        pixels[i] = gray;
                        pixels[i + 1] = gray;
                        pixels[i + 2] = gray;
                        break;
                    }
                case ImageFilter.Sepia:
                    {
                        pixels[i + 2] = (byte)Math.Clamp(0.393 * r + 0.769 * g + 0.189 * b, 0, strength);
                        pixels[i + 1] = (byte)Math.Clamp(0.349 * r + 0.686 * g + 0.168 * b, 0, strength);
                        pixels[i] = (byte)Math.Clamp(0.272 * r + 0.534 * g + 0.131 * b, 0, strength);
                        break;
                    }
                case ImageFilter.Invert:
                    {
                        pixels[i] = (byte)(strength - b);
                        pixels[i + 1] = (byte)(strength - g);
                        pixels[i + 2] = (byte)(strength - r);
                        break;
                    }
                case ImageFilter.HighContrast:
                    {
                        byte avg = (byte)((r + g + b) / 3);
                        byte value = avg < strength ? (byte)0 : (byte)255;
                        pixels[i] = value;
                        pixels[i + 1] = value;
                        pixels[i + 2] = value;
                        break;
                    }

                // Added
                case ImageFilter.BrightnessUp:
                    {
                        const int d = 30;
                        pixels[i] = (byte)Math.Clamp(b + strength, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(g + d, 0, 255);
                        pixels[i + 2] = (byte)Math.Clamp(r + d, 0, 255);
                        break;
                    }
                case ImageFilter.BrightnessDown:
                    {
                        const int d = -30;
                        pixels[i] = (byte)Math.Clamp(b + strength, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(g + d, 0, 255);
                        pixels[i + 2] = (byte)Math.Clamp(r + d, 0, 255);
                        break;
                    }
                case ImageFilter.ContrastBoost:
                    {
                        const double f = 1.6;
                        pixels[i] = (byte)Math.Clamp(((b - 128) * f) + 128, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(((g - 128) * f) + 128, 0, 255);
                        pixels[i + 2] = (byte)Math.Clamp(((r - 128) * f) + 128, 0, 255);
                        break;
                    }
                case ImageFilter.ContrastReduce:
                    {
                        const double f = 0.7;
                        pixels[i] = (byte)Math.Clamp(((b - 128) * f) + 128, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(((g - 128) * f) + 128, 0, 255);
                        pixels[i + 2] = (byte)Math.Clamp(((r - 128) * f) + 128, 0, 255);
                        break;
                    }
                case ImageFilter.Gamma22:
                case ImageFilter.Gamma08:
                    {
                        // LUT already built above
                        pixels[i] = gammaLut![b];
                        pixels[i + 1] = gammaLut![g];
                        pixels[i + 2] = gammaLut![r];
                        break;
                    }
                case ImageFilter.Threshold128:
                    {
                        byte avg = (byte)((r + g + b) / 3);
                        byte v = avg < strength ? (byte)0 : (byte)255;
                        pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v;
                        break;
                    }
                case ImageFilter.Solarize:
                    {
                        byte sb = b > 128 ? (byte)(255 - b) : b;
                        byte sg = g > 128 ? (byte)(255 - g) : g;
                        byte sr = r > 128 ? (byte)(255 - r) : r;
                        pixels[i] = sb; pixels[i + 1] = sg; pixels[i + 2] = sr;
                        break;
                    }
                case ImageFilter.Posterize6:
                    {
                        
                        double step = 255.0 / (strength - 1);

                        byte pb = (byte)Math.Clamp(Math.Round(b / step) * step, 0, 255);
                        byte pg = (byte)Math.Clamp(Math.Round(g / step) * step, 0, 255);
                        byte pr = (byte)Math.Clamp(Math.Round(r / step) * step, 0, 255);

                        pixels[i] = pb; pixels[i + 1] = pg; pixels[i + 2] = pr;
                        break;
                    }
                case ImageFilter.Warm:
                    {
                        pixels[i + 2] = (byte)Math.Clamp(r + strength / 10, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(g + strength / 5, 0, 255);
                        pixels[i] = (byte)Math.Clamp(b - strength / 20, 0, 255);
                        break;
                    }
                case ImageFilter.Cool:
                    {
                        pixels[i + 2] = (byte)Math.Clamp(r - strength / 10, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(g + strength / 5, 0, 255);
                        pixels[i] = (byte)Math.Clamp(b + strength / 20, 0, 255);
                        break;
                    }
                case ImageFilter.NightVision:
                    {
                        byte avg = (byte)((r + g + b) / 3);
                        pixels[i + 1] = (byte)Math.Clamp(strength - avg * 1.4, 0, 255);  // G
                        pixels[i + 2] = (byte)Math.Clamp(strength - avg * 0.35, 0, 255); // R
                        pixels[i] = (byte)Math.Clamp(strength - avg * 0.35, 0, 255); // B
                        break;
                    }
                case ImageFilter.RedOnly:
                    pixels[i] = 0; pixels[i + 1] = 0; /* keep R */ break;

                case ImageFilter.GreenOnly:
                    pixels[i] = 0; /* keep G */ pixels[i + 2] = 0; break;

                case ImageFilter.BlueOnly:
                    /* keep B */
                    pixels[i + 1] = 0; pixels[i + 2] = 0; break;

                case ImageFilter.SwapRB:
                    {
                        (pixels[i + 2], pixels[i]) = (pixels[i], pixels[i + 2]);      // B
                        break;
                    }
                case ImageFilter.TintRed:
                    {
                        pixels[i + 2] = (byte)Math.Clamp(r * 1.25, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(g * 0.95, 0, 255);
                        pixels[i] = (byte)Math.Clamp(b * 0.95, 0, 255);
                        break;
                    }
                case ImageFilter.TintCyan:
                    {
                        pixels[i + 2] = (byte)Math.Clamp(r * 0.85, 0, 255);
                        pixels[i + 1] = (byte)Math.Clamp(g * 1.15, 0, 255);
                        pixels[i] = (byte)Math.Clamp(b * 1.15, 0, 255);
                        break;
                    }
            }
        }

        var wb = new WriteableBitmap(width, height, formatted.DpiX, formatted.DpiY, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }
    // ----------------- Helpers -----------------

    private static bool IsConvolutionFilter(ImageFilter filter) =>
        filter == ImageFilter.BoxBlur3 ||
        filter == ImageFilter.GaussianBlur5 ||
        filter == ImageFilter.Sharpen ||
        filter == ImageFilter.Emboss ||
        filter == ImageFilter.SobelEdge ||
        filter == ImageFilter.ReverseSobelEdge;

    private static byte[] BuildGammaLut(double gamma)
    {
        // gamma>1 brightens midtones when using inverse, but we treat this as "apply gamma" directly:
        // output = 255 * pow(input/255, 1/gamma) gives the common "display gamma correction" feel.
        double inv = 1.0 / gamma;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = 255.0 * Math.Pow(i / 255.0, inv);
            lut[i] = (byte)Math.Clamp(v, 0, 255);
        }
        return lut;
    }

    private static int ClampCoord(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    private static void Convolve3x3Bgra(byte[] src, byte[] dst, int width, int height, int stride, int[] kernel, int divisor, int offset)
    {
        // kernel length must be 9
        if (kernel == null || kernel.Length != 9) throw new ArgumentException("3x3 kernel must have 9 elements.", nameof(kernel));
        if (divisor == 0) divisor = 1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0;
                int k = 0;

                for (int ky = -1; ky <= 1; ky++)
                {
                    int sy = ClampCoord(y + ky, 0, height - 1);
                    int row = sy * stride;

                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int sx = ClampCoord(x + kx, 0, width - 1);
                        int si = row + (sx * 4);

                        int w = kernel[k++];
                        sumB += src[si] * w;
                        sumG += src[si + 1] * w;
                        sumR += src[si + 2] * w;
                    }
                }

                int di = (y * stride) + (x * 4);
                dst[di] = (byte)Math.Clamp((sumB / (double)divisor) + offset, 0, 255);
                dst[di + 1] = (byte)Math.Clamp((sumG / (double)divisor) + offset, 0, 255);
                dst[di + 2] = (byte)Math.Clamp((sumR / (double)divisor) + offset, 0, 255);
                dst[di + 3] = src[di + 3]; // preserve alpha
            }
        }
    }

    private static void Convolve5x5Bgra(byte[] src, byte[] dst, int width, int height, int stride, int[] kernel, int divisor, int offset)
    {
        // kernel length must be 25
        if (kernel == null || kernel.Length != 25) throw new ArgumentException("5x5 kernel must have 25 elements.", nameof(kernel));
        if (divisor == 0) divisor = 1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0;
                int k = 0;

                for (int ky = -2; ky <= 2; ky++)
                {
                    int sy = ClampCoord(y + ky, 0, height - 1);
                    int row = sy * stride;

                    for (int kx = -2; kx <= 2; kx++)
                    {
                        int sx = ClampCoord(x + kx, 0, width - 1);
                        int si = row + (sx * 4);

                        int w = kernel[k++];
                        sumB += src[si] * w;
                        sumG += src[si + 1] * w;
                        sumR += src[si + 2] * w;
                    }
                }

                int di = (y * stride) + (x * 4);
                dst[di] = (byte)Math.Clamp((sumB / (double)divisor) + offset, 0, 255);
                dst[di + 1] = (byte)Math.Clamp((sumG / (double)divisor) + offset, 0, 255);
                dst[di + 2] = (byte)Math.Clamp((sumR / (double)divisor) + offset, 0, 255);
                dst[di + 3] = src[di + 3];
            }
        }
    }

    private static void SobelEdgeBgra(byte[] src, byte[] dst, int width, int height, int stride)
    {
        // Sobel on luminance, output grayscale edge magnitude
        // Gx:
        // -1 0 1
        // -2 0 2
        // -1 0 1
        // Gy:
        // -1 -2 -1
        //  0  0  0
        //  1  2  1

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double gx = 0, gy = 0;

                for (int ky = -1; ky <= 1; ky++)
                {
                    int sy = ClampCoord(y + ky, 0, height - 1);
                    int row = sy * stride;

                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int sx = ClampCoord(x + kx, 0, width - 1);
                        int si = row + (sx * 4);

                        byte b = src[si];
                        byte g = src[si + 1];
                        byte r = src[si + 2];

                        // luminance
                        double lum = (0.299 * r) + (0.587 * g) + (0.114 * b);

                        int wx = (kx, ky) switch
                        {
                            (-1, -1) => -1,
                            (0, -1) => 0,
                            (1, -1) => 1,
                            (-1, 0) => -2,
                            (0, 0) => 0,
                            (1, 0) => 2,
                            (-1, 1) => -1,
                            (0, 1) => 0,
                            (1, 1) => 1,
                            _ => 0
                        };

                        int wy = (kx, ky) switch
                        {
                            (-1, -1) => -1,
                            (0, -1) => -2,
                            (1, -1) => -1,
                            (-1, 0) => 0,
                            (0, 0) => 0,
                            (1, 0) => 0,
                            (-1, 1) => 1,
                            (0, 1) => 2,
                            (1, 1) => 1,
                            _ => 0
                        };

                        gx += lum * wx;
                        gy += lum * wy;
                    }
                }

                double mag = Math.Sqrt((gx * gx) + (gy * gy));
                byte v = (byte)Math.Clamp(mag, 0, 255);

                int di = (y * stride) + (x * 4);
                dst[di] = v;
                dst[di + 1] = v;
                dst[di + 2] = v;
                dst[di + 3] = src[di + 3];
            }
        }
    }

    private static void ReverseSobelEdgeBgra(byte[] src, byte[] dst, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double gx = 0, gy = 0;

                for (int ky = -1; ky <= 1; ky++)
                {
                    int sy = ClampCoord(y + ky, 0, height - 1);
                    int row = sy * stride;

                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int sx = ClampCoord(x + kx, 0, width - 1);
                        int si = row + (sx * 4);

                        byte b = src[si];
                        byte g = src[si + 1];
                        byte r = src[si + 2];

                        double lum = (0.299 * r) + (0.587 * g) + (0.114 * b);

                        int wx = (kx, ky) switch
                        {
                            (-1, -1) => -1,
                            (0, -1) => 0,
                            (1, -1) => 1,
                            (-1, 0) => -2,
                            (0, 0) => 0,
                            (1, 0) => 2,
                            (-1, 1) => -1,
                            (0, 1) => 0,
                            (1, 1) => 1,
                            _ => 0
                        };

                        int wy = (kx, ky) switch
                        {
                            (-1, -1) => -1,
                            (0, -1) => -2,
                            (1, -1) => -1,
                            (-1, 0) => 0,
                            (0, 0) => 0,
                            (1, 0) => 0,
                            (-1, 1) => 1,
                            (0, 1) => 2,
                            (1, 1) => 1,
                            _ => 0
                        };

                        gx += lum * wx;
                        gy += lum * wy;
                    }
                }

                // edge magnitude -> [0..255], then invert so edges become black on white
                double mag = Math.Sqrt((gx * gx) + (gy * gy));
                byte edge = (byte)Math.Clamp(mag, 0, 255);
                byte v = (byte)(255 - edge);

                int di = (y * stride) + (x * 4);
                dst[di] = v;
                dst[di + 1] = v;
                dst[di + 2] = v;
                dst[di + 3] = src[di + 3]; // preserve alpha
            }
        }
    }
}
