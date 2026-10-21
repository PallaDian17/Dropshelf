using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using Dropshelf.Core;
using Dropshelf.Shelf;
using Forms = System.Windows.Forms;

namespace Dropshelf.UI;

/// <summary>The notification-area icon and menu: Windows' stand-in for Dropover's menu bar item.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly ShelfManager _shelves;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private Icon? _currentIcon;

    public TrayIcon(ShelfManager shelves)
    {
        _shelves = shelves;
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9f) };
        _menu.Opening += (_, _) => Rebuild();

        _icon = new Forms.NotifyIcon
        {
            Text = "Dropshelf",
            ContextMenuStrip = _menu,
            Visible = SettingsStore.Current.ShowTrayIcon,
        };
        UpdateIcon();
        ThemeManager.ThemeChanged += UpdateIcon;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowMenu();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _shelves.NewShelf();
        };
    }

    public void SetVisible(bool visible) => _icon.Visible = visible;

    private void ShowMenu()
    {
        // NotifyIcon only opens its menu on right-click; reuse its private helper for left-click.
        typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
    }

    private void Rebuild()
    {
        var dark = !ThemeManager.SystemUsesLightTaskbar();
        _menu.Renderer = new MenuRenderer(dark);
        _menu.ForeColor = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(25, 25, 25);
        _menu.Items.Clear();

        var hotkeys = SettingsStore.Current.Hotkeys;
        string Shortcut(string id) => hotkeys.TryGetValue(id, out var b) && b.Enabled ? b.ToString() : "";

        Add("New Shelf", Shortcut("new-shelf"), _shelves.NewShelf);
        Add("New Shelf From Clipboard", Shortcut("new-clipboard-shelf"), _shelves.NewShelfFromClipboard);
        var restore = Add("Restore Previous Shelf", Shortcut("reopen-last"), _shelves.RestorePrevious);
        restore.Enabled = _shelves.Recent.Items.Count > 0;

        var pinned = _shelves.PinnedShelves.ToList();
        if (pinned.Count > 0)
        {
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(new Forms.ToolStripLabel("Pinned Shelves") { ForeColor = Color.Gray });
            foreach (var shelf in pinned)
            {
                var s = shelf;
                Add("   " + Escape(s.ShelfTitle), "", () =>
                {
                    s.Activate();
                    s.Present();
                });
            }
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());

        var recent = new Forms.ToolStripMenuItem("Recent Shelves");
        foreach (var snapshot in _shelves.Recent.Items)
        {
            var snap = snapshot;
            var item = new Forms.ToolStripMenuItem($"{Escape(snap.MenuTitle)}   ·   {snap.SavedAt:g}");
            item.Click += (_, _) =>
            {
                // Hold Shift to remove a single entry instead of reopening it.
                if (Forms.Control.ModifierKeys.HasFlag(Forms.Keys.Shift)) _shelves.Recent.Remove(snap);
                else _shelves.OpenSnapshot(snap, showErrors: true);
            };
            recent.DropDownItems.Add(item);
        }
        if (recent.DropDownItems.Count > 0)
        {
            recent.DropDownItems.Add(new Forms.ToolStripSeparator());
            recent.DropDownItems.Add(new Forms.ToolStripLabel("Hold Shift while clicking to remove one entry") { ForeColor = Color.Gray });
            var clear = new Forms.ToolStripMenuItem("Clear Recent Shelves");
            clear.Click += (_, _) => _shelves.Recent.Clear();
            recent.DropDownItems.Add(clear);
        }
        recent.Enabled = recent.DropDownItems.Count > 0;
        _menu.Items.Add(recent);

        var all = new Forms.ToolStripMenuItem("All Shelves");
        foreach (var shelf in _shelves.Shelves.ToList())
        {
            var s = shelf;
            var item = new Forms.ToolStripMenuItem($"{Escape(s.ShelfTitle)}  ({s.Items.Count})");
            item.Click += (_, _) =>
            {
                s.Activate();
                s.Present();
            };
            all.DropDownItems.Add(item);
        }
        all.Enabled = all.DropDownItems.Count > 0;
        _menu.Items.Add(all);
        var closeAll = Add("Close All Shelves", Shortcut("close-all"), _shelves.CloseAll);
        closeAll.Enabled = _shelves.Shelves.Count > 0;

        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Settings…", "", () => SettingsWindow.ShowSingleton());
        Add("Quick Start Guide…", "", () => new WelcomeWindow().Show());
        Add("About Dropshelf", "", () => SettingsWindow.ShowSingleton("about"));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Quit Dropshelf", "", () => App.Current.Quit());

        foreach (Forms.ToolStripItem item in _menu.Items) item.ForeColor = item is Forms.ToolStripLabel ? Color.Gray : _menu.ForeColor;
    }

    private Forms.ToolStripMenuItem Add(string text, string shortcut, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
        item.Click += (_, _) => onClick();
        _menu.Items.Add(item);
        return item;
    }

    private static string Escape(string text) => text.Replace("&", "&&");

    private void UpdateIcon()
    {
        var light = ThemeManager.SystemUsesLightTaskbar();
        var previous = _currentIcon;
        _currentIcon = DrawIcon(light ? Color.FromArgb(30, 30, 30) : Color.White);
        _icon.Icon = _currentIcon;
        if (previous is not null)
        {
            Native.NativeMethods.DestroyIcon(previous.Handle);
            previous.Dispose();
        }
    }

    /// <summary>A tray glyph: an open shelf with a document dropping in.</summary>
    private static Icon DrawIcon(Color color)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(color, 2.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

            // Tray
            using (var tray = new GraphicsPath())
            {
                tray.AddLine(4, 17, 4, 25);
                tray.AddArc(4, 21, 6, 6, 180, -90);
                tray.AddLine(7, 28, 25, 28);
                tray.AddArc(22, 21, 6, 6, 90, -90);
                tray.AddLine(28, 24, 28, 17);
                g.DrawPath(pen, tray);
            }
            g.DrawLine(pen, 4, 17, 10, 17);
            g.DrawLine(pen, 22, 17, 28, 17);
            g.DrawArc(pen, 10, 14, 12, 7, 180, -180);

            // Document
            using var doc = new GraphicsPath();
            doc.AddLines(new[] { new PointF(11, 15), new PointF(11, 4), new PointF(18, 4), new PointF(21, 7), new PointF(21, 15) });
            g.DrawPath(pen, doc);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= UpdateIcon;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_currentIcon is not null) Native.NativeMethods.DestroyIcon(_currentIcon.Handle);
    }

    private sealed class MenuRenderer(bool dark) : Forms.ToolStripProfessionalRenderer(new MenuColors(dark))
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? e.Item.ForeColor : Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = dark ? Color.Gainsboro : Color.DimGray;
            base.OnRenderArrow(e);
        }
    }

    private sealed class MenuColors(bool dark) : Forms.ProfessionalColorTable
    {
        private Color Back => dark ? Color.FromArgb(43, 43, 46) : Color.FromArgb(249, 249, 249);
        private Color Hover => dark ? Color.FromArgb(62, 62, 66) : Color.FromArgb(229, 229, 229);
        private Color Border => dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(214, 214, 214);

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Back;
    }
}
