using System.Globalization;
using System.Text;

namespace Dropshelf.Actions;

/// <summary>Minimal PDF 1.4 writer: one JPEG image per page. Enough for "Create PDF from images".</summary>
public static class PdfBuilder
{
    public sealed record Page(byte[] Jpeg, int PixelWidth, int PixelHeight, double Width, double Height,
        double ImageX, double ImageY, double ImageWidth, double ImageHeight);

    /// <summary>Page geometry in points. pageSize: "image", "a4" or "letter".</summary>
    public static Page Layout(byte[] jpeg, int pixelWidth, int pixelHeight, string pageSize)
    {
        if (pageSize == "image")
        {
            // 96 px per inch, the same scale a browser would use.
            var w = pixelWidth * 0.75;
            var h = pixelHeight * 0.75;
            return new Page(jpeg, pixelWidth, pixelHeight, w, h, 0, 0, w, h);
        }

        double pw = 595.28, ph = 841.89; // A4
        if (pageSize == "letter")
        {
            pw = 612;
            ph = 792;
        }
        if (pixelWidth > pixelHeight) (pw, ph) = (ph, pw);

        const double margin = 36;
        var scale = Math.Min((pw - 2 * margin) / pixelWidth, (ph - 2 * margin) / pixelHeight);
        scale = Math.Min(scale, 0.75); // never upscale beyond natural size
        var iw = pixelWidth * scale;
        var ih = pixelHeight * scale;
        return new Page(jpeg, pixelWidth, pixelHeight, pw, ph, (pw - iw) / 2, (ph - ih) / 2, iw, ih);
    }

    public static void Write(string path, IReadOnlyList<Page> pages)
    {
        using var fs = File.Create(path);
        var offsets = new List<long>();

        void Raw(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            fs.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(int number)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = fs.Position;
            Raw($"{number} 0 obj\n");
        }

        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        Raw("%PDF-1.4\n%âãÏÓ\n");

        // Object numbering: 1 catalog, 2 page tree, then 3 objects per page (page, image, content).
        var kids = string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"));

        BeginObject(1);
        Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObject(2);
        Raw($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var pageObj = 3 + i * 3;
            var imageObj = pageObj + 1;
            var contentObj = pageObj + 2;

            BeginObject(pageObj);
            Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(p.Width)} {N(p.Height)}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            BeginObject(imageObj);
            Raw($"<< /Type /XObject /Subtype /Image /Width {p.PixelWidth} /Height {p.PixelHeight} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            fs.Write(p.Jpeg, 0, p.Jpeg.Length);
            Raw("\nendstream\nendobj\n");

            var content = $"q {N(p.ImageWidth)} 0 0 {N(p.ImageHeight)} {N(p.ImageX)} {N(p.ImageY)} cm /Im0 Do Q";
            BeginObject(contentObj);
            Raw($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        var xref = fs.Position;
        Raw($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Raw($"{offset:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
