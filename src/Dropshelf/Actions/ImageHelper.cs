using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dropshelf.Actions;

public enum ImageFormatKind { Jpeg, Png, Bmp, Tiff, Gif, JpegXr }

/// <summary>Image processing on Windows Imaging Component (no extra libraries). Call from an STA thread.</summary>
public static class ImageHelper
{
    public static ImageFormatKind FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormatKind.Png,
        ".bmp" or ".dib" => ImageFormatKind.Bmp,
        ".tif" or ".tiff" => ImageFormatKind.Tiff,
        ".gif" => ImageFormatKind.Gif,
        ".jxr" or ".wdp" or ".hdp" => ImageFormatKind.JpegXr,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormatKind.Jpeg,
        // HEIC, WebP, AVIF… can be decoded with Windows codecs but not encoded: write JPEG.
        _ => ImageFormatKind.Jpeg,
    };

    /// <summary>True when Dropshelf can write this file's own format back (so its extension can be kept).</summary>
    public static bool CanEncode(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".bmp" or ".dib" or ".tif" or ".tiff" or ".gif" or ".jxr" or ".wdp" or ".hdp";

    public static string Extension(ImageFormatKind f) => f switch
    {
        ImageFormatKind.Png => ".png",
        ImageFormatKind.Bmp => ".bmp",
        ImageFormatKind.Tiff => ".tiff",
        ImageFormatKind.Gif => ".gif",
        ImageFormatKind.JpegXr => ".jxr",
        _ => ".jpg",
    };

    /// <summary>Decodes the first frame and applies the EXIF orientation so photos are upright.</summary>
    public static BitmapSource LoadOriented(string path)
    {
        BitmapFrame frame;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        var orientation = ReadOrientation(frame);
        BitmapSource result = frame;
        if (orientation > 1)
        {
            var transform = new TransformGroup();
            switch (orientation)
            {
                case 2: transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: transform.Children.Add(new RotateTransform(180)); break;
                case 4: transform.Children.Add(new ScaleTransform(1, -1)); break;
                case 5: transform.Children.Add(new RotateTransform(90)); transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 6: transform.Children.Add(new RotateTransform(90)); break;
                case 7: transform.Children.Add(new RotateTransform(270)); transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 8: transform.Children.Add(new RotateTransform(270)); break;
            }
            result = new TransformedBitmap(frame, transform);
        }

        result.Freeze();
        return result;
    }

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata meta) return 1;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                if (meta.ContainsQuery(query) && meta.GetQuery(query) is ushort value) return value;
            }
        }
        catch
        {
            // formats without EXIF
        }
        return 1;
    }

    public static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == source.PixelWidth && height == source.PixelHeight) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(width / (double)source.PixelWidth, height / (double)source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    public static bool HasAlpha(BitmapSource source)
        => source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Prgba64
           || source.Format == PixelFormats.Rgba64 || source.Format == PixelFormats.Rgba128Float || source.Format == PixelFormats.Prgba128Float;

    /// <summary>Composites onto white (for JPEG and PDF, which have no transparency).</summary>
    public static BitmapSource Flatten(BitmapSource source)
    {
        if (!HasAlpha(source)) return source;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(source, rect);
        }
        var rtb = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    public static void Save(BitmapSource image, string path, ImageFormatKind format, int quality = 90)
    {
        BitmapEncoder encoder = format switch
        {
            ImageFormatKind.Png => new PngBitmapEncoder(),
            ImageFormatKind.Bmp => new BmpBitmapEncoder(),
            ImageFormatKind.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            ImageFormatKind.Gif => new GifBitmapEncoder(),
            ImageFormatKind.JpegXr => new WmpBitmapEncoder { ImageQualityLevel = Math.Clamp(quality / 100f, 0.01f, 1f) },
            _ => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
        };

        if (format is ImageFormatKind.Jpeg or ImageFormatKind.Bmp) image = Flatten(image);

        // A fresh BitmapFrame carries no metadata, so location and camera data are not copied.
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Encodes as JPEG bytes (used for PDF pages).</summary>
    public static byte[] ToJpegBytes(BitmapSource image, int quality = 90)
    {
        var flat = Flatten(image);
        if (flat.Format != PixelFormats.Bgr24 && flat.Format != PixelFormats.Bgr32)
        {
            var converted = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
            converted.Freeze();
            flat = converted;
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Joins images side by side or top to bottom.</summary>
    public static BitmapSource Stitch(IReadOnlyList<BitmapSource> images, bool vertical, int spacing, bool transparentBackground)
    {
        var width = vertical ? images.Max(i => i.PixelWidth) : images.Sum(i => i.PixelWidth) + spacing * (images.Count - 1);
        var height = vertical ? images.Sum(i => i.PixelHeight) + spacing * (images.Count - 1) : images.Max(i => i.PixelHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (!transparentBackground) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            double offset = 0;
            foreach (var img in images)
            {
                var x = vertical ? (width - img.PixelWidth) / 2.0 : offset;
                var y = vertical ? offset : (height - img.PixelHeight) / 2.0;
                dc.DrawImage(img, new Rect(x, y, img.PixelWidth, img.PixelHeight));
                offset += (vertical ? img.PixelHeight : img.PixelWidth) + spacing;
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
