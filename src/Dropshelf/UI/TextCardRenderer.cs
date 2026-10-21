using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dropshelf.Core;

namespace Dropshelf.UI;

/// <summary>Draws a small "note card" thumbnail for text snippets and links, which have no file icon.</summary>
public static class TextCardRenderer
{
    private const int Size = 160;

    public static BitmapSource Render(ShelfItem item)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var card = new Rect(12, 6, Size - 24, Size - 12);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xF8)), new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1), card, 8, 8);

            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (item.Kind == ShelfItemKind.Url)
            {
                var glyph = new FormattedText("", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    44, new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 1.0);
                dc.DrawText(glyph, new Point(card.Left + (card.Width - glyph.Width) / 2, card.Top + 22));

                var host = new FormattedText(Uri.TryCreate(item.Url, UriKind.Absolute, out var u) ? u.Host : item.Url ?? "",
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 13, Brushes.DimGray, 1.0)
                {
                    MaxTextWidth = card.Width - 16,
                    MaxLineCount = 2,
                    Trimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                };
                dc.DrawText(host, new Point(card.Left + 8, card.Top + 84));
            }
            else
            {
                var text = new FormattedText((item.Text ?? "").Replace("\r", "").Trim(), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, 11, new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 1.0)
                {
                    MaxTextWidth = card.Width - 18,
                    MaxTextHeight = card.Height - 16,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(text, new Point(card.Left + 9, card.Top + 8));
            }
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
