using System.Text;
using System.Windows;
using Dropshelf.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Dropshelf.Actions;

/// <summary>Shared loop for actions that turn each image into one output file.</summary>
public abstract class PerImageAction : ShelfAction
{
    public override ActionCategory Category => ActionCategory.Image;
    public override bool SupportsPreset => true;
    public override bool IsAvailable(ActionInput input) => input.ImageItems.Count > 0;

    protected abstract string Suffix { get; }
    protected abstract string DoneMessage(int count);

    /// <summary>Produces the output image and its format for one source.</summary>
    protected abstract (System.Windows.Media.Imaging.BitmapSource Image, ImageFormatKind Format, int Quality) Process(
        System.Windows.Media.Imaging.BitmapSource source, string sourcePath, ActionValues values);

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        // Captured once: ImageItems filters on existence, and replaced originals stop existing.
        var sources = input.ImageItems;
        var outputs = new List<string>();
        var failures = new List<string>();
        foreach (var item in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var output = await StaTask.Run(() =>
                {
                    var source = ImageHelper.LoadOriented(item.Path!);
                    var (image, format, quality) = Process(source, item.Path!, values);
                    var keepExtension = ImageHelper.CanEncode(item.Path!) && ImageHelper.FormatFromExtension(item.Path!) == format;
                    var target = OutputPaths.For(input, values, item, Suffix, keepExtension ? null : ImageHelper.Extension(format));
                    var temp = OutputPaths.TempFileFor(target);
                    ImageHelper.Save(image, temp, format, quality);
                    OutputPaths.Commit(temp, target, values.GetBool("copy", true) ? null : item.Path);
                    return target;
                });
                outputs.Add(output);
            }
            catch (Exception ex)
            {
                Log.Warn($"{Title} failed for {item.Path}: {ex.Message}");
                failures.Add($"Could not process {item.DisplayName}: {ex.Message}");
            }
        }

        return OutputPaths.Result(values, outputs, failures, DoneMessage(outputs.Count), sources);
    }
}

public sealed class ResizeImageAction : PerImageAction
{
    public override string Id => "resize";
    public override string Title => "Resize";
    public override string Glyph => "";
    public override string ProgressText => "Resizing…";
    public override string? Description => "Change the image size using a preset or your own dimensions.";
    public override string[] Keywords => ["image", "dimension", "scale", "smaller"];
    protected override string Suffix => "resized";
    protected override string DoneMessage(int count) => count == 1 ? "Image resized" : $"{count} images resized";

    public override IReadOnlyList<ActionField> GetFields(ActionInput input)
    {
        bool Pixels(ActionValues v) => v.GetString("dim") == "pixels";
        bool Percent(ActionValues v) => v.GetString("dim") == "percent";
        return
        [
            new ChoiceField("dim", "Dimension",
            [
                ("small", "Small (max. 640 px)"),
                ("medium", "Medium (max. 1280 px)"),
                ("large", "Large (max. 1920 px)"),
                ("pixels", "Custom (pixels)"),
                ("percent", "Custom (percent)"),
            ], "pixels"),
            new NumberField("maxw", "Max. width (in px)", 1200, 1, 30000) { VisibleWhen = Pixels },
            new NumberField("maxh", "Max. height (in px)", 1200, 1, 30000) { VisibleWhen = Pixels },
            new NumberField("pw", "Width (in %)", 50, 1, 1000) { VisibleWhen = Percent },
            new NumberField("ph", "Height (in %)", 50, 1, 1000) { VisibleWhen = Percent },
            .. CommonFields.Output(),
        ];
    }

    protected override (System.Windows.Media.Imaging.BitmapSource, ImageFormatKind, int) Process(
        System.Windows.Media.Imaging.BitmapSource source, string sourcePath, ActionValues values)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        var dim = values.GetString("dim", "pixels");
        if (dim == "percent")
        {
            w = (int)Math.Round(w * values.GetInt("pw", 50) / 100.0);
            h = (int)Math.Round(h * values.GetInt("ph", 50) / 100.0);
        }
        else
        {
            var (maxW, maxH) = dim switch
            {
                "small" => (640, 640),
                "medium" => (1280, 1280),
                "large" => (1920, 1920),
                _ => (values.GetInt("maxw", 1200), values.GetInt("maxh", 1200)),
            };
            var scale = Math.Min(1.0, Math.Min(maxW / (double)w, maxH / (double)h));
            w = (int)Math.Round(w * scale);
            h = (int)Math.Round(h * scale);
        }
        return (ImageHelper.Resize(source, w, h), ImageHelper.FormatFromExtension(sourcePath), 90);
    }
}

public sealed class CompressImageAction : PerImageAction
{
    public override string Id => "compress";
    public override string Title => "Compress";
    public override string Glyph => "";
    public override string ProgressText => "Compressing…";
    public override string? Description => "Compress images for web pages, social networks or e-mail. Saves as JPEG.";
    public override string[] Keywords => ["image", "quality", "smaller", "jpeg"];
    protected override string Suffix => "compressed";
    protected override string DoneMessage(int count) => count == 1 ? "Image compressed" : $"{count} images compressed";

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) =>
    [
        new ChoiceField("quality", "Image quality", [("low", "Low Quality"), ("medium", "Medium Quality"), ("high", "High Quality")], "medium"),
        .. CommonFields.Output(),
    ];

    protected override (System.Windows.Media.Imaging.BitmapSource, ImageFormatKind, int) Process(
        System.Windows.Media.Imaging.BitmapSource source, string sourcePath, ActionValues values)
    {
        var quality = values.GetString("quality", "medium") switch { "low" => 40, "high" => 85, _ => 65 };
        return (source, ImageFormatKind.Jpeg, quality);
    }
}

public sealed class ConvertImageAction : PerImageAction
{
    public override string Id => "convert";
    public override string Title => "Convert Format";
    public override string Glyph => "";
    public override string ProgressText => "Converting…";
    public override string? Description => "Convert images to the specified format.";
    public override string[] Keywords => ["image", "format", "png", "jpeg", "change"];
    protected override string Suffix => "converted";
    protected override string DoneMessage(int count) => count == 1 ? "Image converted" : $"{count} images converted";

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) =>
    [
        new ChoiceField("format", "Target format",
            [("jpeg", "JPEG"), ("png", "PNG"), ("tiff", "TIFF"), ("bmp", "BMP"), ("gif", "GIF"), ("jxr", "JPEG XR")], "png"),
        new SliderField("quality", "Image quality", 85, 10, 100, q => q switch
        {
            >= 90 => "High quality, minimal compression.",
            >= 75 => "Good quality, moderate compression.",
            >= 55 => "Moderate quality, noticeable compression.",
            >= 35 => "Lower quality, higher compression.",
            _ => "Very low quality, maximum compression.",
        }) { VisibleWhen = v => v.GetString("format") is "jpeg" or "jxr" },
        .. CommonFields.Output(),
    ];

    protected override (System.Windows.Media.Imaging.BitmapSource, ImageFormatKind, int) Process(
        System.Windows.Media.Imaging.BitmapSource source, string sourcePath, ActionValues values)
    {
        var format = values.GetString("format", "png") switch
        {
            "jpeg" => ImageFormatKind.Jpeg,
            "tiff" => ImageFormatKind.Tiff,
            "bmp" => ImageFormatKind.Bmp,
            "gif" => ImageFormatKind.Gif,
            "jxr" => ImageFormatKind.JpegXr,
            _ => ImageFormatKind.Png,
        };
        return (source, format, values.GetInt("quality", 85));
    }
}

public sealed class RemoveMetadataAction : PerImageAction
{
    public override string Id => "remove-metadata";
    public override string Title => "Remove Metadata";
    public override string Glyph => "";
    public override string ProgressText => "Removing metadata…";
    public override string? Description => "Removes location, camera and other metadata by re-encoding the image.";
    public override string[] Keywords => ["image", "exif", "privacy", "gps", "location"];
    protected override string Suffix => "clean";
    protected override string DoneMessage(int count) => count == 1 ? "Metadata removed" : $"Metadata removed from {count} images";

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) => [.. CommonFields.Output()];

    protected override (System.Windows.Media.Imaging.BitmapSource, ImageFormatKind, int) Process(
        System.Windows.Media.Imaging.BitmapSource source, string sourcePath, ActionValues values)
        => (source, ImageHelper.FormatFromExtension(sourcePath), 95);
}

public sealed class StitchImagesAction : ShelfAction
{
    public override string Id => "stitch";
    public override string Title => "Stitch Images";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Image;
    public override string ProgressText => "Stitching…";
    public override string? Description => "Create a single image from multiple images.";
    public override string[] Keywords => ["image", "merge", "combine", "collage"];
    public override bool SupportsPreset => true;

    public override bool IsAvailable(ActionInput input) => input.ImageItems.Count >= 2;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) =>
    [
        new ChoiceField("direction", "Direction", [("vertical", "Top to Bottom"), ("horizontal", "Side by Side")], "vertical"),
        new NumberField("spacing", "Spacing (in px)", 0, 0, 500),
        new ChoiceField("format", "Format", [("png", "PNG"), ("jpeg", "JPEG")], "png"),
        CommonFields.OutputIn(),
        CommonFields.SaveIn(false),
    ];

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var items = input.ImageItems;
        var target = await StaTask.Run(() =>
        {
            var images = items.Select(i => ImageHelper.LoadOriented(i.Path!)).ToList();
            var png = values.GetString("format", "png") == "png";
            var stitched = ImageHelper.Stitch(images, values.GetString("direction") != "horizontal", values.GetInt("spacing"), png);
            var name = AppPaths.SanitizeFileName(Path.GetFileNameWithoutExtension(items[0].Path!) + " (stitched)") + (png ? ".png" : ".jpg");
            var path = AppPaths.UniquePath(OutputPaths.Folder(input, values, items[0]), name);
            ImageHelper.Save(stitched, path, png ? ImageFormatKind.Png : ImageFormatKind.Jpeg, 92);
            return path;
        });
        return OutputPaths.Result(values, [target], [], "Images stitched", items);
    }
}

public sealed class ImagesToPdfAction : ShelfAction
{
    public override string Id => "images-to-pdf";
    public override string Title => "Create PDF";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Image;
    public override string ProgressText => "Creating PDF…";
    public override string? Description => "Create a single PDF file from one or more images.";
    public override string[] Keywords => ["image", "pdf", "document", "merge"];
    public override bool SupportsPreset => true;

    public override bool IsAvailable(ActionInput input) => input.ImageItems.Count > 0;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) =>
    [
        new TextField("name", "Filename", input.ImageItems.Count == 1 ? Path.GetFileNameWithoutExtension(input.ImageItems[0].Path!) : "Images"),
        new ChoiceField("page", "Page size", [("image", "Fit to Image"), ("a4", "A4"), ("letter", "US Letter")], "image"),
        CommonFields.OutputIn(),
        CommonFields.SaveIn(false),
    ];

    public override string? Validate(ActionInput input, ActionValues values)
        => values.GetString("name").Trim().Length == 0 ? null : AppPaths.ValidateFileName(values.GetString("name").Trim());

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var items = input.ImageItems;
        var target = await StaTask.Run(() =>
        {
            var pages = new List<PdfBuilder.Page>();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var image = ImageHelper.LoadOriented(item.Path!);
                pages.Add(PdfBuilder.Layout(ImageHelper.ToJpegBytes(image), image.PixelWidth, image.PixelHeight, values.GetString("page", "image")));
            }
            var name = values.GetString("name").Trim();
            if (name.Length == 0) name = "Images";
            var path = AppPaths.UniquePath(OutputPaths.Folder(input, values, items[0]), name + ".pdf");
            PdfBuilder.Write(path, pages);
            return path;
        });
        return OutputPaths.Result(values, [target], [], "PDF created", items);
    }
}

/// <summary>On-device text recognition with Windows.Media.Ocr (Dropover: "Extract Text").</summary>
public sealed class ExtractTextAction : ShelfAction
{
    public override string Id => "ocr";
    public override string Title => "Extract Text";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Image;
    public override string ProgressText => "Recognizing text…";
    public override string? Description => "Recognizes text in images and copies it to the clipboard or saves it as text files.";
    public override string[] Keywords => ["ocr", "text", "recognize", "scan"];
    public override bool SupportsPreset => true;

    public override bool IsAvailable(ActionInput input) => input.ImageItems.Count > 0;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) =>
    [
        new ChoiceField("dest", "Output", [("clipboard", "Clipboard"), ("file", "Text File")], "clipboard"),
    ];

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return ActionResult.Fail("No text recognition language is installed. Add one in Settings › Time & language › Language.");

        var results = new List<(ShelfItem Item, string Text)>();
        foreach (var item in input.ImageItems)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = await RecognizeAsync(engine, item.Path!);
                if (!string.IsNullOrWhiteSpace(text)) results.Add((item, text));
            }
            catch (Exception ex)
            {
                Log.Warn($"OCR failed for {item.Path}: {ex.Message}");
            }
        }

        if (results.Count == 0)
            return ActionResult.Fail(input.ImageItems.Count == 1 ? "Could not find text in the image" : "Could not find text in the images");

        if (values.GetString("dest", "clipboard") == "clipboard")
        {
            var combined = results.Count == 1
                ? results[0].Text
                : string.Join(Environment.NewLine + Environment.NewLine, results.Select(r => $"Extracted text from {r.Item.DisplayName}:{Environment.NewLine}{r.Text}"));
            Clipboard.SetText(combined);
            return ActionResult.Ok(results.Count < input.ImageItems.Count
                ? "Text from some images has been copied to your clipboard"
                : "Extracted text has been copied to your clipboard");
        }

        var outputs = new List<string>();
        foreach (var (item, text) in results)
        {
            var path = AppPaths.UniquePath(input.DefaultOutputFolder(item), Path.GetFileNameWithoutExtension(item.Path!) + ".txt");
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), ct);
            outputs.Add(path);
        }
        return new ActionResult { Message = "Text files created", Outputs = outputs, OutputMode = "add" };
    }

    private static async Task<string> RecognizeAsync(OcrEngine engine, string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var max = OcrEngine.MaxImageDimension;
        SoftwareBitmap bitmap;
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            var scale = Math.Min(max / (double)decoder.PixelWidth, max / (double)decoder.PixelHeight);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)(decoder.PixelWidth * scale),
                ScaledHeight = (uint)(decoder.PixelHeight * scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        else
        {
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        using (bitmap)
        {
            var result = await engine.RecognizeAsync(bitmap);
            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        }
    }
}
