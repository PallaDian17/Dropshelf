using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;

namespace Dropshelf.Shelf;

/// <summary>
/// Dropover's Instant Actions: a row of drop targets that appears under a freshly shaken shelf.
/// Dropping files on a tile adds them to the shelf and runs that action straight away.
/// </summary>
public partial class InstantActionsWindow : Window
{
    private readonly ShelfWindow _shelf;

    public InstantActionsWindow(ShelfWindow shelf, IReadOnlyList<ShelfAction> actions)
    {
        InitializeComponent();
        _shelf = shelf;
        SourceInitialized += (_, _) => WindowHelper.HideFromAltTab(this);
        foreach (var action in actions) Tiles.Children.Add(CreateTile(action));
    }

    private Border CreateTile(ShelfAction action)
    {
        var tile = new Border
        {
            Width = 76,
            Height = 66,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            AllowDrop = true,
            ToolTip = action.Title,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = action.Glyph,
                        FontFamily = (FontFamily)FindResource("IconFont"),
                        FontSize = 20,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = action.Title.TrimEnd('…'),
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(4, 6, 4, 0),
                    },
                },
            },
        };

        tile.DragEnter += (_, e) => Highlight(tile, e, true);
        tile.DragOver += (_, e) => Highlight(tile, e, true);
        tile.DragLeave += (_, e) => Highlight(tile, e, false);
        tile.Drop += (_, e) =>
        {
            e.Handled = true;
            Highlight(tile, e, false);
            var items = ItemImporter.FromDataObject(e.Data);
            if (items.Count == 0) return;
            _shelf.AddItems(items, fromDrop: false);
            var targets = _shelf.Items.Where(i => items.Any(n => n.IdentityKey == i.IdentityKey)).ToList();
            Close();
            _shelf.RunInstantAction(action, targets);
        };
        return tile;
    }

    private static void Highlight(Border tile, DragEventArgs e, bool on)
    {
        e.Effects = on && ItemImporter.CanImport(e.Data) && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
        tile.Background = on ? UI.ThemeManager.Brush("SelectionBrush") : Brushes.Transparent;
        tile.BorderBrush = on ? UI.ThemeManager.Brush("AccentBrush") : Brushes.Transparent;
    }

    /// <summary>Shows the tiles centred under the shelf, or above it near the bottom of the screen.</summary>
    public void ShowBelow(ShelfWindow shelf)
    {
        var hwnd = WindowHelper.Handle(this);
        Show();
        UpdateLayout();

        var card = shelf.CardRectPx();
        var own = WindowHelper.GetRect(hwnd);
        var wa = WindowHelper.WorkAreaAt(new NativeMethods.POINT((card.Left + card.Right) / 2, card.Bottom));
        var x = (card.Left + card.Right) / 2 - own.Width / 2;
        var y = card.Bottom - (int)(6 * VisualTreeHelper.GetDpi(this).DpiScaleY);
        if (y + own.Height > wa.Bottom) y = card.Top - own.Height + (int)(6 * VisualTreeHelper.GetDpi(this).DpiScaleY);
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - own.Width));
        WindowHelper.MoveTo(hwnd, x, y);
    }
}
