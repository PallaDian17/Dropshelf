using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>
/// Explorer-quality thumbnails (photos, videos, PDFs, Office files) through IShellItemImageFactory,
/// produced on a dedicated STA thread because some thumbnail handlers require it.
/// </summary>
public static class ThumbnailService
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    private static readonly Guid ImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly BlockingCollection<ThumbnailRequest> Queue = new();
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new();
    private static Thread? _worker;

    private sealed record ThumbnailRequest(string Path, int Size, bool IconOnly, Action<BitmapSource?> Callback);

    public static void Request(string path, int sizePx, Action<BitmapSource?> onUiThread, bool iconOnly = false)
    {
        EnsureWorker();
        var dispatcher = Application.Current.Dispatcher;
        Queue.Add(new ThumbnailRequest(path, sizePx, iconOnly, bmp => dispatcher.BeginInvoke(() => onUiThread(bmp))));
    }

    /// <summary>Loads a thumbnail for <paramref name="item"/> into its Thumbnail property.</summary>
    public static void Load(ShelfItem item, int sizePx = 192)
    {
        if (item.Kind is ShelfItemKind.Text or ShelfItemKind.Url)
        {
            item.Thumbnail = UI.TextCardRenderer.Render(item);
            return;
        }
        if (item.Path is null) return;
        Request(item.Path, sizePx, bmp =>
        {
            if (bmp is not null) item.Thumbnail = bmp;
        });
    }

    private static void EnsureWorker()
    {
        if (_worker is not null) return;
        _worker = new Thread(() =>
        {
            foreach (var request in Queue.GetConsumingEnumerable())
            {
                BitmapSource? result = null;
                try
                {
                    result = GetThumbnail(request.Path, request.Size, request.IconOnly);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Thumbnail failed for {request.Path}: {ex.Message}");
                }
                request.Callback(result);
            }
        })
        {
            IsBackground = true,
            Name = "Dropshelf thumbnails",
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    /// <summary>Synchronous version; call from an STA thread.</summary>
    public static BitmapSource? GetThumbnail(string path, int sizePx, bool iconOnly = false)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;

        string key;
        try
        {
            key = $"{path}|{sizePx}|{iconOnly}|{File.GetLastWriteTimeUtc(path).Ticks}";
        }
        catch
        {
            key = $"{path}|{sizePx}|{iconOnly}";
        }
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var bmp = Fetch(path, sizePx, iconOnly ? SIIGBF_ICONONLY : SIIGBF_BIGGERSIZEOK)
                  ?? (iconOnly ? null : Fetch(path, sizePx, SIIGBF_ICONONLY));
        if (bmp is null) return null;

        if (Cache.Count > 400) Cache.Clear();
        Cache[key] = bmp;
        return bmp;
    }

    private static BitmapSource? Fetch(string path, int sizePx, int flags)
    {
        IShellItemImageFactory? factory = null;
        var hbitmap = IntPtr.Zero;
        try
        {
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ImageFactoryGuid, out factory);
            var size = new NativeMethods.SIZE { cx = sizePx, cy = sizePx };
            if (factory.GetImage(size, flags, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            return FromHBitmap(hbitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) NativeMethods.DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies an HBITMAP keeping its alpha channel (Imaging.CreateBitmapSourceFromHBitmap drops it,
    /// which turns icon backgrounds black).
    /// </summary>
    private static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        var bm = new NativeMethods.BITMAP();
        if (NativeMethods.GetObject(hbitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), ref bm) == 0) return null;
        var width = bm.bmWidth;
        var height = Math.Abs(bm.bmHeight);
        if (width <= 0 || height <= 0) return null;

        var header = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var stride = width * 4;
        var pixels = new byte[stride * height];
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }
}
