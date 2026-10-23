using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.UI;

namespace Dropshelf.Shelf;

/// <summary>
/// Dropover's Instant Actions: a row of drop targets that appears under a freshly shaken shelf.
/// Dropping files on a tile adds them to the shelf and runs that action straight away.
/// </summary>
public partial class InstantActionsWindow : Window
{
    private readonly ShelfWindow _shelf;
    private readonly List<Tile> _tiles = new();
    private bool _closeAnimationDone;
    private bool _closing;

    private sealed class Tile
    {
        public required Grid Root { get; init; }
        public required Border Highlight { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Shift { get; init; }
        public bool Hot { get; set; }
    }

    public InstantActionsWindow(ShelfWindow shelf, IReadOnlyList<ShelfAction> actions)
    {
        InitializeComponent();
        _shelf = shelf;
        SourceInitialized += (_, _) => WindowHelper.HideFromAltTab(this);
        Loaded += (_, _) => PlayAppear();
        foreach (var action in actions)
        {
            var tile = CreateTile(action);
            _tiles.Add(tile);
            Tiles.Children.Add(tile.Root);
        }
    }

    private Tile CreateTile(ShelfAction action)
    {
        var highlight = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            Background = ThemeManager.Brush("SelectionBrush"),
            BorderBrush = ThemeManager.Brush("AccentBrush"),
            Opacity = 0,
        };
        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        var root = new Grid
        {
            Width = 76,
            Height = 66,
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            AllowDrop = true,
            ToolTip = action.Title,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, shift } },
        };
        root.Children.Add(highlight);
        root.Children.Add(new StackPanel
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
        });

        var tile = new Tile { Root = root, Highlight = highlight, Scale = scale, Shift = shift };
        root.DragEnter += (_, e) => Highlight(tile, e, true);
        root.DragOver += (_, e) => Highlight(tile, e, true);
        root.DragLeave += (_, e) => Highlight(tile, e, false);
        root.Drop += (_, e) =>
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

    private static void Highlight(Tile tile, DragEventArgs e, bool on)
    {
        e.Effects = on && ItemImporter.CanImport(e.Data) && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
        if (tile.Hot == on) return;
        tile.Hot = on;
        Motion.To(tile.Highlight, OpacityProperty, on ? 1 : 0, on ? 90 : 180);
        Motion.To(tile.Scale, ScaleTransform.ScaleXProperty, on ? 1.08 : 1, 420, Motion.Spring);
        Motion.To(tile.Scale, ScaleTransform.ScaleYProperty, on ? 1.08 : 1, 420, Motion.Spring);
    }

    /// <summary>The row drops down from the shelf and its tiles follow one after another.</summary>
    private void PlayAppear()
    {
        if (_closing) return;
        Motion.To(Bubble, OpacityProperty, 1, 160);
        Motion.To(BubbleShift, TranslateTransform.YProperty, 0, 460, Motion.SoftSpring, from: -10);
        Motion.To(BubbleScale, ScaleTransform.ScaleXProperty, 1, 460, Motion.SoftSpring, from: 0.94);
        Motion.To(BubbleScale, ScaleTransform.ScaleYProperty, 1, 460, Motion.SoftSpring, from: 0.94);
        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            Motion.To(tile.Root, OpacityProperty, 1, 160, delayMs: 60 + i * 40);
            Motion.Set(tile.Shift, TranslateTransform.YProperty, 8);
            Motion.To(tile.Shift, TranslateTransform.YProperty, 0, 420, Motion.Spring, delayMs: 60 + i * 40);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAnimationDone || !Motion.Enabled || !IsVisible || Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsHitTestVisible = false;
        Motion.To(BubbleShift, TranslateTransform.YProperty, -6, 160, Motion.EaseIn);
        Motion.To(Bubble, OpacityProperty, 0, 150, Motion.EaseIn, completed: () =>
        {
            _closeAnimationDone = true;
            Close();
        });
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
