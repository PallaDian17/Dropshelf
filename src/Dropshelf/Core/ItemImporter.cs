using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Dropshelf.Native;

namespace Dropshelf.Core;

/// <summary>Turns drag-and-drop or clipboard data into shelf items.</summary>
public static class ItemImporter
{
    private const string UrlFormatW = "UniformResourceLocatorW";
    private const string UrlFormat = "UniformResourceLocator";

    /// <summary>True when the data contains anything a shelf can hold.</summary>
    public static bool CanImport(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop)
                   || VirtualFileReader.HasVirtualFiles(data)
                   || data.GetDataPresent(UrlFormatW) || data.GetDataPresent(UrlFormat)
                   || data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap)
                   || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);
        }
        catch
        {
            return false;
        }
    }

    public static List<ShelfItem> FromDataObject(IDataObject data)
    {
        var items = new List<ShelfItem>();
        try
        {
            Import(data, items, AppPaths.TempDir);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read dropped data", ex);
        }
        return items;
    }

    public static List<ShelfItem> FromClipboard()
    {
        var items = new List<ShelfItem>();
        try
        {
            if (Clipboard.GetDataObject() is { } data) Import(data, items, AppPaths.ClipboardImagesDir);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the clipboard", ex);
        }
        return items;
    }

    public static List<ShelfItem> FromPaths(IEnumerable<string> paths)
        => paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(ShelfItem.FromPath).ToList();

    private static void Import(IDataObject data, List<ShelfItem> items, string rawDataDir)
    {
        // 1. Real files from Explorer and most apps.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            items.AddRange(FromPaths(paths));
            if (items.Count > 0) return;
        }

        var url = ReadUrl(data);

        // 2. Virtual files (Outlook, ZIP folders, browser images). A lone ".url" is just a link.
        if (VirtualFileReader.HasVirtualFiles(data))
        {
            var names = VirtualFileReader.ReadDescriptorNames(data);
            var onlyShortcuts = names.Count > 0 && names.All(n => n.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
            if (!(onlyShortcuts && url is not null))
            {
                items.AddRange(FromPaths(VirtualFileReader.Extract(data, rawDataDir)));
                if (items.Count > 0) return;
            }
        }

        // 3. Links.
        if (url is not null)
        {
            items.Add(ShelfItem.FromUrl(url, ReadUrlTitle(data)));
            return;
        }

        // 4. Raw image data (screenshots, "copy image").
        var image = ReadImage(data);
        if (image is not null)
        {
            var file = AppPaths.UniquePath(AppPaths.Ensure(rawDataDir), $"Image {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            using (var fs = File.Create(file))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(fs);
            }
            items.Add(ShelfItem.FromPath(file));
            return;
        }

        // 5. Text (keeps rich formatting unless the user prefers plain text).
        var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string
            : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
        if (!string.IsNullOrEmpty(text))
        {
            var trimmed = text.Trim();
            if (IsWebUrl(trimmed))
            {
                items.Add(ShelfItem.FromUrl(trimmed));
                return;
            }

            string? rtf = null, html = null;
            if (!SettingsStore.Current.ConvertTextToPlain)
            {
                rtf = data.GetDataPresent(DataFormats.Rtf) ? data.GetData(DataFormats.Rtf) as string : null;
                html = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null;
            }
            items.Add(ShelfItem.FromText(text, rtf, html));
        }
    }

    private static string? ReadUrl(IDataObject data)
    {
        try
        {
            foreach (var format in new[] { UrlFormatW, UrlFormat })
            {
                if (!data.GetDataPresent(format) || data.GetData(format) is not MemoryStream ms) continue;
                var encoding = format == UrlFormatW ? Encoding.Unicode : Encoding.Default;
                var value = encoding.GetString(ms.ToArray()).TrimEnd('\0').Trim();
                if (value.Length > 0) return value;
            }
        }
        catch
        {
            // malformed URL data
        }
        return null;
    }

    private static string? ReadUrlTitle(IDataObject data)
    {
        try
        {
            var names = VirtualFileReader.ReadDescriptorNames(data);
            var name = names.FirstOrDefault(n => n.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
            return name is null ? null : Path.GetFileNameWithoutExtension(name);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? ReadImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return decoder.Frames[0];
            }
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read image data: {ex.Message}");
        }
        return null;
    }

    public static bool IsWebUrl(string text)
        => !text.Contains('\n') && !text.Contains(' ')
           && Uri.TryCreate(text, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Builds the data object for dragging items out of a shelf or copying them.</summary>
    public static DataObject ToDataObject(IReadOnlyList<ShelfItem> items)
    {
        var data = new DataObject();
        var paths = new System.Collections.Specialized.StringCollection();
        foreach (var item in items)
        {
            var path = item.GetOrCreateFilePath();
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }
        if (paths.Count > 0) data.SetFileDropList(paths);

        if (items.Count == 1)
        {
            var item = items[0];
            switch (item.Kind)
            {
                case ShelfItemKind.Text:
                    data.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                    if (item.Rtf is not null) data.SetData(DataFormats.Rtf, item.Rtf);
                    if (item.Html is not null) data.SetData(DataFormats.Html, item.Html);
                    break;
                case ShelfItemKind.Url when item.Url is not null:
                    data.SetText(item.Url, TextDataFormat.UnicodeText);
                    data.SetData(UrlFormatW, new MemoryStream(Encoding.Unicode.GetBytes(item.Url + "\0")));
                    break;
            }
        }
        return data;
    }
}
