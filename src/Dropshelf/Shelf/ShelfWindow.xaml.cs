using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.UI;

namespace Dropshelf.Shelf;

public enum DockEdge { None, Left, Right }

/// <summary>
/// A shelf: a small floating, always-on-top window that holds references to dragged files,
/// text and links. It has three faces: the stacked preview, the detail grid/list, and an inline
/// panel for action settings, shelf customization and the command bar.
/// </summary>
public partial class ShelfWindow : Window, IShelfHost
{
    private enum ShelfMode { Stack, Detail, Panel }

    private const double MarginDip = 14;
    private const double PanelWidth = 372;
    private const double PeekDip = 18;
    private static readonly Size StackSize = new(152, 172);
    private static readonly Size DetailSize = new(352, 420);

    /// <summary>Last values used per action, so the panel opens with the user's previous choices.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> RememberedValues = new();

    private readonly ShelfManager _manager;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _retractTimer;
    private readonly DispatcherTimer _stackRefreshTimer;

    private IntPtr _hwnd;
    private bool _closed;
    private ShelfMode _mode = ShelfMode.Stack;
    private ShelfMode _modeBeforePanel = ShelfMode.Stack;
    private Func<Task<bool>>? _panelPrimary;
    private CancellationTokenSource? _actionCts;
    private bool _busy;
    private DockEdge _dock = DockEdge.None;
    private NativeMethods.RECT _dockWorkArea;
    private bool _revealed;
    private bool _dragInside;
    private bool _hadDrop;
    private int _openMenus;
    private Point? _stackDragStart;
    private Point? _listDragStart;
    private ShelfItem? _listPressedItem;
    private bool _deferredSelection;
    private string? _customName;
    private string? _color;
    private bool _isPinned;

    // Motion state.
    private Size _cardSize = StackSize;
    private int _resizeVersion;
    private bool _resizing;
    private bool _highlighted;
    private bool _closeAnimationDone;
    private bool _closingAnimating;
    private bool? _pillShown;
    private bool? _emptyShown;
    private bool _toastShown;
    private readonly RectangleGeometry _cardClip = new();
    private readonly Dictionary<Guid, StackVisual> _stackVisuals = new();

    public ShelfWindow(ShelfManager manager, bool fromGesture)
    {
        InitializeComponent();
        _manager = manager;
        FromGesture = fromGesture;

        ItemsList.ItemsSource = Items;
        ItemsList.SelectionChanged += (_, _) => UpdateDetailHeader();
        Items.CollectionChanged += (_, _) => OnItemsChanged();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };
        _retractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _retractTimer.Tick += (_, _) =>
        {
            _retractTimer.Stop();
            RetractIfIdle();
        };
        _stackRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _stackRefreshTimer.Tick += (_, _) =>
        {
            _stackRefreshTimer.Stop();
            RebuildStack();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = WindowHelper.Handle(this);
            WindowHelper.HideFromAltTab(this);
        };
        Activated += (_, _) => _manager.NotifyActivated(this);
        Deactivated += OnDeactivatedHandler;
        PreviewKeyDown += OnPreviewKeyDown;
        MouseEnter += (_, _) =>
        {
            _retractTimer.Stop();
            if (_dock != DockEdge.None && !_revealed) Reveal();
        };
        MouseLeave += (_, _) => ScheduleRetract();
        Loaded += (_, _) => PlayAppear();

        ApplyViewMode(SettingsStore.Current.ViewMode);
        SetMode(ShelfMode.Stack, animate: false);
        UpdateChrome();
    }

    public ObservableCollection<ShelfItem> Items { get; } = new();
    public bool FromGesture { get; }
    public string ShelfId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>When set, closing does not add the shelf to Recent Shelves (empty gesture shelves).</summary>
    public bool SuppressRecent { get; set; }

    public IntPtr Handle => _hwnd != IntPtr.Zero ? _hwnd : (_hwnd = WindowHelper.Handle(this));
    public IReadOnlyList<ShelfItem> AllItems => Items;
    /// <summary>Closed, or playing its close animation (treated as gone everywhere else).</summary>
    public bool IsClosed => _closed || _closingAnimating;
    public bool IsDocked => _dock != DockEdge.None;
    public DockEdge Dock => _dock;
    public bool IsDragTarget => _dragInside;

    public string ShelfTitle => _customName ?? ShelfNaming.DefaultName(Items);

    public string? CustomName
    {
        get => _customName;
        set
        {
            _customName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            UpdateChrome();
        }
    }

    public string? ColorHex
    {
        get => _color;
        set
        {
            _color = string.IsNullOrWhiteSpace(value) ? null : value;
            UpdateChrome();
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            _isPinned = value;
            UpdateChrome();
            _manager.SavePinned();
        }
    }

    private double Scale => _hwnd == IntPtr.Zero ? 1.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
    private int MarginPx => (int)Math.Round(MarginDip * Scale);

    // =====================================================================================
    // Items
    // =====================================================================================

    public void AddItems(IEnumerable<ShelfItem> items) => AddItems(items, fromDrop: false);

    public int AddItems(IEnumerable<ShelfItem> items, bool fromDrop)
    {
        var keys = new HashSet<string>(Items.Select(i => i.IdentityKey));
        var added = 0;
        var duplicates = 0;
        foreach (var item in items)
        {
            if (!keys.Add(item.IdentityKey))
            {
                duplicates++;
                continue;
            }
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
            ThumbnailService.Load(item);
            added++;
        }

        if (added == 0 && duplicates > 0) ShowToast("These items are already in the shelf");

        if (added > 0 && fromDrop && FromGesture && !_hadDrop)
        {
            _hadDrop = true;
            if (SettingsStore.Current.RetractAfterFirstDrop)
                Dispatcher.BeginInvoke(DockToNearestEdge, DispatcherPriority.ApplicationIdle);
        }
        if (added > 0) _manager.OnShelfContentChanged(this);
        return added;
    }

    public void RemoveItems(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.ToList())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            Items.Remove(item);
        }
        if (Items.Count == 0 && _mode == ShelfMode.Detail) SetMode(ShelfMode.Stack);
        _manager.OnShelfContentChanged(this);
    }

    public void ReplaceItems(IReadOnlyList<ShelfItem> oldItems, IEnumerable<ShelfItem> newItems)
    {
        var positions = oldItems.Select(o => Items.IndexOf(o)).Where(i => i >= 0).ToList();
        var index = positions.Count > 0 ? positions.Min() : Items.Count;
        foreach (var item in oldItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            Items.Remove(item);
        }

        index = Math.Min(index, Items.Count);
        var keys = new HashSet<string>(Items.Select(i => i.IdentityKey));
        foreach (var item in newItems)
        {
            if (!keys.Add(item.IdentityKey)) continue;
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Insert(index++, item);
            ThumbnailService.Load(item);
        }
        _manager.OnShelfContentChanged(this);
    }

    public void ClearItems()
    {
        foreach (var item in Items) item.PropertyChanged -= OnItemPropertyChanged;
        Items.Clear();
        if (_mode == ShelfMode.Detail) SetMode(ShelfMode.Stack);
        _manager.OnShelfContentChanged(this);
    }

    public void CloseShelf() => Close();

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShelfItem.Thumbnail) or nameof(ShelfItem.Path)) ScheduleStackRefresh();
        if (e.PropertyName == nameof(ShelfItem.Path)) UpdateChrome();
    }

    private void OnItemsChanged()
    {
        UpdateChrome();
        ScheduleStackRefresh();
    }

    private IReadOnlyList<ShelfItem> SelectedItems() => ItemsList.SelectedItems.Cast<ShelfItem>().ToList();

    /// <summary>The selection in the detail view, otherwise everything.</summary>
    private IReadOnlyList<ShelfItem> CurrentTargets()
    {
        if (_mode == ShelfMode.Detail && ItemsList.SelectedItems.Count > 0) return SelectedItems();
        return Items.ToList();
    }

    // =====================================================================================
    // Appearance
    // =====================================================================================

    private void UpdateChrome()
    {
        var title = ShelfTitle;
        Title = $"{title} – Dropshelf";
        FooterText.Text = title;
        // Compare against the intended state, not Opacity: a just-started animation still reads
        // its start value until the next frame.
        var showPill = Items.Count > 0 || _customName is not null;
        FooterPill.IsHitTestVisible = showPill;
        if (_pillShown != showPill)
        {
            _pillShown = showPill;
            if (IsLoaded) Motion.To(FooterPill, OpacityProperty, showPill ? 1 : 0, 180);
            else Motion.Set(FooterPill, OpacityProperty, showPill ? 1 : 0);
        }

        var empty = Items.Count == 0;
        if (_emptyShown != empty)
        {
            _emptyShown = empty;
            if (!IsLoaded)
            {
                Motion.Set(EmptyState, OpacityProperty, 1);
                EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (empty)
            {
                Motion.FadeIn(EmptyState, 200);
            }
            else
            {
                Motion.FadeOut(EmptyState, 120);
            }
        }
        PinGlyph.Visibility = _isPinned ? Visibility.Visible : Visibility.Collapsed;

        Brush? colorBrush = null;
        if (_color is not null)
        {
            try
            {
                colorBrush = (Brush)new BrushConverter().ConvertFromString(_color)!;
            }
            catch
            {
                colorBrush = null;
            }
        }
        FooterDot.Fill = colorBrush;
        DetailDot.Fill = colorBrush;
        FooterDot.Visibility = colorBrush is null ? Visibility.Collapsed : Visibility.Visible;
        DetailDot.Visibility = FooterDot.Visibility;

        UpdateDetailHeader();
    }

    private void UpdateDetailHeader()
    {
        if (_mode != ShelfMode.Detail) return;
        DetailTitle.Text = ShelfTitle;
        var selected = ItemsList.SelectedItems.Count;
        if (selected > 0)
        {
            DetailSubtitle.Text = $"Selected: {ShelfNaming.CountLabel(SelectedItems())}";
        }
        else
        {
            var size = ShelfNaming.TotalSize(Items);
            DetailSubtitle.Text = size.Length > 0 ? $"{ShelfNaming.CountLabel(Items)} · {size}" : ShelfNaming.CountLabel(Items);
        }
    }

    private void ScheduleStackRefresh()
    {
        _stackRefreshTimer.Stop();
        _stackRefreshTimer.Start();
    }

    /// <summary>One thumbnail in the fanned stack; kept across rebuilds so it can animate.</summary>
    private sealed class StackVisual
    {
        public required Grid Host { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required RotateTransform Rotate { get; init; }
        public required TranslateTransform Shift { get; init; }
        public object? ContentKey { get; set; }
    }

    /// <summary>
    /// Shows up to three of the newest items as a slightly fanned stack. New items drop in and
    /// settle with a spring, existing ones rotate to their new place, removed ones shrink away.
    /// </summary>
    private void RebuildStack()
    {
        var shown = Items.Skip(Math.Max(0, Items.Count - 3)).ToList();
        double[] angles = shown.Count switch
        {
            1 => [0],
            2 => [-7, 4],
            _ => [-9, 7, 0],
        };
        var shownIds = shown.Select(i => i.Id).ToHashSet();

        foreach (var (id, old) in _stackVisuals.Where(kv => !shownIds.Contains(kv.Key)).ToList())
        {
            _stackVisuals.Remove(id);
            var host = old.Host;
            Motion.To(old.Scale, ScaleTransform.ScaleXProperty, 0.8, 180, Motion.EaseIn);
            Motion.To(old.Scale, ScaleTransform.ScaleYProperty, 0.8, 180, Motion.EaseIn);
            Motion.To(host, OpacityProperty, 0, 180, completed: () => StackHost.Children.Remove(host));
        }

        for (var i = 0; i < shown.Count; i++)
        {
            var item = shown[i];
            var angle = angles[Math.Min(i, angles.Length - 1)];
            var isNew = !_stackVisuals.TryGetValue(item.Id, out var visual);
            if (isNew)
            {
                visual = CreateStackVisual();
                _stackVisuals[item.Id] = visual;
                StackHost.Children.Add(visual.Host);
            }
            Panel.SetZIndex(visual!.Host, i);
            UpdateStackVisualContent(visual, item, fade: !isNew);

            if (isNew)
            {
                // Drops in from slightly above, a little larger and tilted, then settles.
                Motion.Set(visual.Host, OpacityProperty, 0);
                visual.Rotate.Angle = angle + (i % 2 == 0 ? 12 : -12);
                Motion.To(visual.Host, OpacityProperty, 1, 140, delayMs: i * 40);
                Motion.To(visual.Shift, TranslateTransform.YProperty, 0, 560, Motion.Spring, from: -26);
                Motion.To(visual.Scale, ScaleTransform.ScaleXProperty, 1, 560, Motion.Spring, from: 1.22);
                Motion.To(visual.Scale, ScaleTransform.ScaleYProperty, 1, 560, Motion.Spring, from: 1.22);
                Motion.To(visual.Rotate, RotateTransform.AngleProperty, angle, 620, Motion.Spring);
            }
            else
            {
                Motion.To(visual.Rotate, RotateTransform.AngleProperty, angle, 460, Motion.Spring);
            }
        }
    }

    private static StackVisual CreateStackVisual()
    {
        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var shift = new TranslateTransform();
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, rotate, shift } },
        };
        return new StackVisual { Host = host, Scale = scale, Rotate = rotate, Shift = shift };
    }

    /// <summary>Puts the item's thumbnail (or its kind glyph while loading) into the stack visual.</summary>
    private void UpdateStackVisualContent(StackVisual visual, ShelfItem item, bool fade)
    {
        object key = (object?)item.Thumbnail ?? item.Glyph;
        if (Equals(visual.ContentKey, key)) return;
        visual.ContentKey = key;

        FrameworkElement content;
        if (item.Thumbnail is { } source)
        {
            var image = new Image { Source = source, Stretch = Stretch.Uniform, MaxWidth = 92, MaxHeight = 84 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            content = item.IsImage
                ? new Border
                {
                    Child = image,
                    BorderThickness = new Thickness(2),
                    BorderBrush = UI.ThemeManager.Brush("ThumbBorderBrush"),
                    Background = UI.ThemeManager.Brush("ThumbBorderBrush"),
                    CornerRadius = new CornerRadius(3),
                    Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 },
                }
                : image;
        }
        else
        {
            content = new TextBlock
            {
                Text = item.Glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 46,
                Foreground = UI.ThemeManager.Brush("TextSecondaryBrush"),
            };
        }

        visual.Host.Children.Clear();
        visual.Host.Children.Add(content);
        if (fade) Motion.To(content, OpacityProperty, 1, 220, from: 0);
    }

    private void OnStackMouseEnter(object sender, MouseEventArgs e)
    {
        if (Items.Count == 0) return;
        Motion.To(StackLift, ScaleTransform.ScaleXProperty, 1.05, 420, Motion.Spring);
        Motion.To(StackLift, ScaleTransform.ScaleYProperty, 1.05, 420, Motion.Spring);
    }

    private void OnStackMouseLeave(object sender, MouseEventArgs e)
    {
        Motion.To(StackLift, ScaleTransform.ScaleXProperty, 1, 380, Motion.SoftSpring);
        Motion.To(StackLift, ScaleTransform.ScaleYProperty, 1, 380, Motion.SoftSpring);
    }

    private void ApplyViewMode(ItemViewMode mode)
    {
        var grid = mode == ItemViewMode.Grid;
        ItemsList.ItemTemplate = (DataTemplate)FindResource(grid ? "GridItemTemplate" : "ListItemTemplate");
        ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource(grid ? "GridPanel" : "ListPanel");
        GridToggle.IsChecked = grid;
        ListToggle.IsChecked = !grid;
    }

    public void ShowToast(string message)
    {
        if (_closed) return;
        ToastText.Text = message;
        var alreadyShown = _toastShown;
        _toastShown = true;
        Toast.Visibility = Visibility.Visible;
        Motion.To(Toast, OpacityProperty, 1, 160);
        if (!alreadyShown) Motion.To(ToastShift, TranslateTransform.YProperty, 0, 460, Motion.Spring, from: 12);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastShown = false;
        Motion.To(ToastShift, TranslateTransform.YProperty, 6, 200, Motion.EaseIn);
        Motion.FadeOut(Toast, 200);
    }

    // =====================================================================================
    // Modes and panels
    // =====================================================================================

    private FrameworkElement ViewOf(ShelfMode mode) => mode switch
    {
        ShelfMode.Stack => StackView,
        ShelfMode.Detail => DetailView,
        _ => PanelView,
    };

    /// <summary>Card size (outer, in DIPs) for a mode; panels are measured to fit their content.</summary>
    private Size TargetSize(ShelfMode mode) => mode switch
    {
        ShelfMode.Stack => StackSize,
        ShelfMode.Detail => DetailSize,
        _ => new Size(PanelWidth, MeasurePanelHeight()),
    };

    private double MeasurePanelHeight()
    {
        // A height pinned while the panel was fading out would otherwise become its "desired" height.
        PanelView.ClearValue(HeightProperty);
        PanelView.Visibility = Visibility.Visible;
        PanelView.Measure(new Size(PanelWidth - 2, double.PositiveInfinity));
        return Math.Clamp(Math.Ceiling(PanelView.DesiredSize.Height) + 2, 110, 640);
    }

    /// <summary>
    /// Switches between stack, detail and panel. The card morphs to the new size with a soft
    /// spring while the old content fades out and the new content fades and slides in, already
    /// laid out at its final size so it is revealed rather than reflowed.
    /// </summary>
    private void SetMode(ShelfMode mode, bool animate = true)
    {
        var previous = _mode;
        var oldView = ViewOf(previous);
        var newView = ViewOf(mode);
        _mode = mode;

        if (mode == ShelfMode.Stack) ItemsList.SelectedItems.Clear();
        newView.Visibility = Visibility.Visible;
        if (mode == ShelfMode.Detail) UpdateDetailHeader();

        var target = TargetSize(mode);
        if (previous == mode)
        {
            // Same view with new content (one panel replacing another): just follow its size.
            ResizeCard(target, animate && Motion.Enabled && IsLoaded && IsVisible);
            return;
        }

        var animated = animate && Motion.Enabled && IsLoaded && IsVisible;
        if (!animated)
        {
            foreach (var view in new[] { StackView, DetailView, PanelView })
            {
                UnpinView(view);
                Motion.Set(view, OpacityProperty, 1);
                if (!ReferenceEquals(view, newView)) view.Visibility = Visibility.Collapsed;
            }
            ResizeCard(target, animate: false);
            return;
        }

        PinView(oldView, _cardSize, pinHeight: true);
        Motion.To(oldView, OpacityProperty, 0, 110, completed: () =>
        {
            oldView.Visibility = Visibility.Collapsed;
            UnpinView(oldView);
            Motion.Set(oldView, OpacityProperty, 1);
        });

        PinView(newView, target, pinHeight: mode != ShelfMode.Panel);
        var slide = new TranslateTransform(0, mode == ShelfMode.Stack ? -8 : 8);
        newView.RenderTransform = slide;
        Motion.Set(newView, OpacityProperty, 0);
        Motion.To(newView, OpacityProperty, 1, 200, delayMs: 70);
        Motion.To(slide, TranslateTransform.YProperty, 0, 460, Motion.SoftSpring);
        ResizeCard(target, animate: true, completed: () =>
        {
            if (_mode == mode) UnpinView(newView);
        });
    }

    /// <summary>Fixes a view's size and top-left anchors it, so it doesn't reflow while the card animates.</summary>
    private static void PinView(FrameworkElement view, Size cardSize, bool pinHeight)
    {
        view.HorizontalAlignment = HorizontalAlignment.Left;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Width = Math.Max(0, cardSize.Width - 2);
        if (pinHeight) view.Height = Math.Max(0, cardSize.Height - 2);
        else view.ClearValue(HeightProperty);
    }

    private static void UnpinView(FrameworkElement view)
    {
        view.ClearValue(WidthProperty);
        view.ClearValue(HeightProperty);
        view.ClearValue(HorizontalAlignmentProperty);
        view.ClearValue(VerticalAlignmentProperty);
    }

    /// <summary>
    /// Resizes the card. The window first grows to fit the whole animation (its transparent area is
    /// click-through), the card springs to its new size, then the window shrinks to fit exactly.
    /// </summary>
    private void ResizeCard(Size target, bool animate, Action? completed = null)
    {
        var version = ++_resizeVersion;
        var from = new Size(
            Root.ActualWidth > 0 ? Root.ActualWidth : _cardSize.Width,
            Root.ActualHeight > 0 ? Root.ActualHeight : _cardSize.Height);
        _cardSize = target;

        if (!animate || !Motion.Enabled)
        {
            _resizing = false;
            Motion.Set(Root, WidthProperty, target.Width);
            Motion.Set(Root, HeightProperty, target.Height);
            SetWindowSizeFor(target);
            if (_hwnd != IntPtr.Zero && IsLoaded) Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Loaded);
            completed?.Invoke();
            return;
        }

        _resizing = true;
        SetWindowSizeFor(new Size(Math.Max(from.Width, target.Width) * 1.05, Math.Max(from.Height, target.Height) * 1.05));
        KeepInView(target);

        const double duration = 400;
        Motion.To(Root, WidthProperty, target.Width, duration, Motion.SoftSpring);
        Motion.To(Root, HeightProperty, target.Height, duration, Motion.SoftSpring, completed: () =>
        {
            if (version != _resizeVersion) return;
            _resizing = false;
            SetWindowSizeFor(target);
            completed?.Invoke();
            if (_mode == ShelfMode.Panel) Dispatcher.BeginInvoke(RefitPanel, DispatcherPriority.Loaded);
        });
    }

    private void SetWindowSizeFor(Size card)
    {
        Width = Math.Ceiling(card.Width + 2 * MarginDip);
        Height = Math.Ceiling(card.Height + 2 * MarginDip);
    }

    /// <summary>Slides the window along with a growing card so the card ends up fully on screen.</summary>
    private void KeepInView(Size target)
    {
        if (_hwnd == IntPtr.Zero || _closed || (_dock != DockEdge.None && !_revealed)) return;
        var r = WindowHelper.GetRect(Handle);
        var m = MarginPx;
        var left = r.Left + m;
        var top = r.Top + m;
        var w = (int)Math.Ceiling(target.Width * Scale);
        var h = (int)Math.Ceiling(target.Height * Scale);
        var wa = WindowHelper.WorkAreaAt(new NativeMethods.POINT(left + (int)(_cardSize.Width * Scale / 2), top + 10));
        var newLeft = Math.Clamp(left, wa.Left, Math.Max(wa.Left, wa.Right - w));
        var newTop = Math.Clamp(top, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        if (newLeft != left || newTop != top)
            WindowHelper.AnimateTo(Handle, newLeft - m, newTop - m, 380, () => _manager.SavePinned());
    }

    private void OnPanelHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mode == ShelfMode.Panel && !_resizing && IsLoaded) RefitPanel();
    }

    /// <summary>Panel content changed height (fields shown or hidden, search results): follow it.</summary>
    private void RefitPanel()
    {
        if (_mode != ShelfMode.Panel || _closed || _resizing) return;
        var target = TargetSize(ShelfMode.Panel);
        if (Math.Abs(target.Height - _cardSize.Height) < 1) return;
        ResizeCard(target, animate: true);
    }

    /// <summary>Rounded clipping so content never pokes out of the card while it animates.</summary>
    private void OnCardContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _cardClip.RadiusX = 15;
        _cardClip.RadiusY = 15;
        _cardClip.Rect = new Rect(e.NewSize);
        if (!ReferenceEquals(CardContent.Clip, _cardClip)) CardContent.Clip = _cardClip;
    }

    /// <summary>The shelf grows softly into place when it first appears.</summary>
    private void PlayAppear()
    {
        if (_closingAnimating) return;
        if (!Motion.Enabled)
        {
            Motion.Set(Root, OpacityProperty, 1);
            return;
        }
        Motion.To(Root, OpacityProperty, 1, 200);
        Motion.To(RootScale, ScaleTransform.ScaleXProperty, 1, 480, Motion.SoftSpring, from: 0.88);
        Motion.To(RootScale, ScaleTransform.ScaleYProperty, 1, 480, Motion.SoftSpring, from: 0.88);
    }

    /// <summary>Ring and gentle lift while something is dragged over the shelf.</summary>
    private void SetDropHighlight(bool on)
    {
        if (_highlighted == on) return;
        _highlighted = on;
        Motion.To(DropHighlight, OpacityProperty, on ? 1 : 0, on ? 100 : 200);
        Motion.To(LiftScale, ScaleTransform.ScaleXProperty, on ? 1.035 : 1, 460, Motion.Spring);
        Motion.To(LiftScale, ScaleTransform.ScaleYProperty, on ? 1.035 : 1, 460, Motion.Spring);
    }

    private void ShowPanel(string title, string subtitle, FrameworkElement content, string? primaryText, Func<Task<bool>>? onPrimary)
    {
        if (_mode != ShelfMode.Panel) _modeBeforePanel = _mode;
        _retractTimer.Stop();
        if (_dock != DockEdge.None && !_revealed) Reveal();

        PanelTitle.Text = title;
        PanelSubtitle.Text = subtitle;
        PanelHost.Content = content;
        PanelPrimary.Content = primaryText ?? "";
        PanelPrimary.Visibility = primaryText is null ? Visibility.Collapsed : Visibility.Visible;
        _panelPrimary = onPrimary;
        SetMode(ShelfMode.Panel);

        Activate();
        Dispatcher.BeginInvoke(() => content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Loaded);
    }

    private void ClosePanel()
    {
        _panelPrimary = null;
        SetMode(_modeBeforePanel == ShelfMode.Detail && Items.Count > 0 ? ShelfMode.Detail : ShelfMode.Stack);

        // Keep the content while it fades out; drop it afterwards unless a new panel replaced it.
        var content = PanelHost.Content;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_mode != ShelfMode.Panel && ReferenceEquals(PanelHost.Content, content)) PanelHost.Content = null;
        };
        timer.Start();
        ScheduleRetract();
    }

    private async void OnPanelPrimary(object sender, RoutedEventArgs e)
    {
        if (_panelPrimary is null || _busy) return;
        PanelPrimary.IsEnabled = false;
        try
        {
            if (await _panelPrimary() && !_closed) ClosePanel();
        }
        finally
        {
            PanelPrimary.IsEnabled = true;
        }
    }

    private void OnPanelBack(object sender, RoutedEventArgs e) => ClosePanel();

    private void ShowCustomizePanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock { Text = "Shelf name", Style = (Style)FindResource("RowSubtitle"), Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Text = _customName ?? "" };
        panel.Children.Add(FormControls.WithPlaceholder(nameBox, ShelfNaming.DefaultName(Items)));

        panel.Children.Add(new TextBlock { Text = "Indicator color", Style = (Style)FindResource("RowSubtitle"), Margin = new Thickness(0, 14, 0, 6) });
        var selected = _color;
        var swatches = new WrapPanel();
        var options = new List<string?> { null };
        options.AddRange(ShelfManager.Palette);
        var rings = new List<(Border Ring, string? Color)>();
        foreach (var option in options)
        {
            var ring = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = option is null ? "No color" : null,
                Child = option is null
                    ? new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = UI.ThemeManager.Brush("TextSecondaryBrush") }
                    : new Ellipse { Width = 18, Height = 18, Fill = (Brush)new BrushConverter().ConvertFromString(option)! },
            };
            rings.Add((ring, option));
            ring.MouseLeftButtonUp += (_, _) =>
            {
                selected = option;
                foreach (var (r, c) in rings) r.BorderBrush = c == selected ? UI.ThemeManager.Brush("AccentBrush") : Brushes.Transparent;
            };
            ring.BorderBrush = option == selected ? UI.ThemeManager.Brush("AccentBrush") : Brushes.Transparent;
            swatches.Children.Add(ring);
        }
        panel.Children.Add(swatches);

        var pin = new CheckBox { Content = "Pin shelf (keeps it across restarts)", IsChecked = _isPinned, Style = (Style)FindResource("ToggleSwitch"), Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(pin);
        panel.Children.Add(new TextBlock { Text = "These preferences apply only to this shelf.", Style = (Style)FindResource("RowSubtitle"), Margin = new Thickness(0, 12, 0, 0) });

        ShowPanel("Customize", ShelfTitle, panel, "Done", () =>
        {
            CustomName = nameBox.Text;
            ColorHex = selected;
            IsPinned = pin.IsChecked == true;
            return Task.FromResult(true);
        });
    }

    /// <summary>The "Perform action…" search (Ctrl+K), with recent actions first.</summary>
    private void ShowCommandBar()
    {
        var targets = CurrentTargets();
        var input = new ActionInput(targets, this);
        var box = new TextBox();
        var list = new ListBox { MaxHeight = 320, Margin = new Thickness(0, 8, 0, 0) };

        void Refresh()
        {
            list.Items.Clear();
            var query = box.Text.Trim();
            var results = new List<ShelfAction>();
            if (query.Length == 0)
            {
                var recent = ActionCatalog.RecentActions(input).ToList();
                if (recent.Count > 0)
                {
                    list.Items.Add(SectionLabel("Recent Actions"));
                    foreach (var a in recent) list.Items.Add(CommandRow(a, input));
                    list.Items.Add(SectionLabel("Suggested"));
                }
                var recentIds = recent.Select(a => a.Id).ToHashSet();
                results.AddRange(ActionCatalog.Search("", input).Where(a => !recentIds.Contains(a.Id)));
            }
            else
            {
                results.AddRange(ActionCatalog.Search(query, input));
            }
            foreach (var a in results) list.Items.Add(CommandRow(a, input));
            list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ShelfAction);
        }

        void RunSelected()
        {
            if (list.SelectedItem is ListBoxItem { Tag: ShelfAction action })
            {
                ClosePanel();
                RunAction(action, targets);
            }
        }

        box.TextChanged += (_, _) => Refresh();
        box.PreviewKeyDown += (_, e) =>
        {
            var rows = list.Items.OfType<ListBoxItem>().Where(i => i.Tag is ShelfAction).ToList();
            var index = list.SelectedItem is ListBoxItem current ? rows.IndexOf(current) : -1;
            switch (e.Key)
            {
                case Key.Down when rows.Count > 0:
                    list.SelectedItem = rows[Math.Min(rows.Count - 1, index + 1)];
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up when rows.Count > 0:
                    list.SelectedItem = rows[Math.Max(0, index - 1)];
                    list.ScrollIntoView(list.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    RunSelected();
                    e.Handled = true;
                    break;
            }
        };
        list.MouseDoubleClick += (_, _) => RunSelected();
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem { Tag: ShelfAction }) RunSelected();
        };

        var panel = new StackPanel();
        panel.Children.Add(FormControls.WithPlaceholder(box, "Perform action…"));
        panel.Children.Add(list);
        Refresh();
        ShowPanel("Actions", input.CountLabel, panel, null, null);
        Dispatcher.BeginInvoke(() => box.Focus(), DispatcherPriority.Input);
    }

    private ListBoxItem SectionLabel(string text) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = UI.ThemeManager.Brush("TextSecondaryBrush") },
        IsEnabled = false,
        Focusable = false,
    };

    private ListBoxItem CommandRow(ShelfAction action, ActionInput input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = action.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Width = 26, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = action.MenuTitle(input), VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = action };
    }

    // =====================================================================================
    // Running actions
    // =====================================================================================

    public void RunActionById(string id, IReadOnlyList<ShelfItem> targets)
    {
        if (ActionCatalog.Find(id) is { } action) RunAction(action, targets);
    }

    public async void RunAction(ShelfAction action, IReadOnlyList<ShelfItem> targets)
    {
        if (_busy || _closed) return;
        var input = new ActionInput(targets, this);
        if (!action.IsAvailable(input))
        {
            ShowToast("This action is not available for these items");
            return;
        }

        var fields = action.GetFields(input);
        if (fields.Count == 0)
        {
            if (action.Validate(input, new ActionValues()) is { } problem)
            {
                ShowToast(problem);
                return;
            }
            await ExecuteAsync(action, input, new ActionValues());
            return;
        }

        RememberedValues.TryGetValue(action.Id, out var remembered);
        var values = ActionValues.Defaults(fields, remembered);
        var form = new ActionFormView(fields, values, action.Description);
        ShowPanel(action.Title, input.CountLabel, form, "Continue", async () =>
        {
            if (action.Validate(input, form.Values) is { } error)
            {
                form.ShowError(error);
                return false;
            }
            Remember(action, fields, form.Values);
            await ExecuteAsync(action, input, form.Values);
            return true;
        });
    }

    /// <summary>Runs an Instant Action on items just dropped onto its tile.</summary>
    public async void RunInstantAction(ShelfAction action, IReadOnlyList<ShelfItem> items)
    {
        if (items.Count == 0) return;
        var input = new ActionInput(items, this);
        if (!action.IsAvailable(input))
        {
            ShowToast("The operation cannot be performed with this type of files");
            return;
        }

        var values = action is CustomPresetAction preset ? preset.ValuesFor(input) : ActionValues.Defaults(action.GetFields(input));
        if (action.Validate(input, values) is { } problem)
        {
            ShowToast(problem);
            return;
        }

        var result = await ExecuteAsync(action, input, values);
        if (result.Success && SettingsStore.Current.InstantActionsCloseShelf && !action.KeepsShelfOpen && !_closed && !_isPinned) Close();
    }

    private static void Remember(ShelfAction action, IReadOnlyList<ActionField> fields, ActionValues values)
    {
        // Free text (file names, find/replace) depends on the files, so only choices are remembered.
        var keep = fields.Where(f => f is ChoiceField or ToggleField or NumberField or SliderField or FolderField or AppField).Select(f => f.Key);
        RememberedValues[action.Id] = keep.ToDictionary(k => k, k => values[k]);
    }

    private async Task<ActionResult> ExecuteAsync(ShelfAction action, ActionInput input, ActionValues values)
    {
        _busy = true;
        _actionCts = new CancellationTokenSource();
        var busyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        busyTimer.Tick += (_, _) =>
        {
            busyTimer.Stop();
            BusyText.Text = action.ProgressText;
            Motion.FadeIn(BusyOverlay, 160);
        };
        busyTimer.Start();
        try
        {
            return await ActionRunner.ExecuteAsync(action, input, values, _actionCts.Token);
        }
        finally
        {
            busyTimer.Stop();
            Motion.FadeOut(BusyOverlay, 160);
            _busy = false;
            _actionCts.Dispose();
            _actionCts = null;
        }
    }

    private void OnCancelAction(object sender, RoutedEventArgs e) => _actionCts?.Cancel();

    // =====================================================================================
    // Action menu
    // =====================================================================================

    private void OnMenuClick(object sender, RoutedEventArgs e) => OpenActionMenu(sender as UIElement ?? MenuButton);

    private void OpenActionMenu(UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var entry in BuildMenuItems(CurrentTargets())) menu.Items.Add(entry);
        TrackMenu(menu);
        menu.IsOpen = true;
    }

    private void TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) =>
        {
            _openMenus++;
            _retractTimer.Stop();
        };
        menu.Closed += (_, _) =>
        {
            _openMenus = Math.Max(0, _openMenus - 1);
            ScheduleRetract();
        };
    }

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var targets = SelectedItems();
        if (targets.Count == 0 || ItemsList.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        foreach (var entry in BuildMenuItems(targets, forSelection: true)) menu.Items.Add(entry);
    }

    private List<object> BuildMenuItems(IReadOnlyList<ShelfItem> targets, bool forSelection = false)
    {
        var list = new List<object>();
        var added = new HashSet<string>();
        var input = new ActionInput(targets, this);

        void AddAction(string id)
        {
            if (added.Contains(id) || ActionCatalog.Find(id) is not { } action || !action.IsAvailable(input)) return;
            added.Add(id);
            list.Add(ActionMenuItem(action, input));
        }

        void Separator()
        {
            if (list.Count > 0 && list[^1] is not System.Windows.Controls.Separator) list.Add(new System.Windows.Controls.Separator());
        }

        if (targets.Count > 0)
        {
            if (OpenWithMenu(targets) is { } openWith) list.Add(openWith);
            AddAction("reveal");
            AddAction("preview");
            Separator();
            AddAction("share");
            AddAction("email");
            Separator();
        }

        if (!forSelection) AddAction("add-clipboard");
        if (targets.Count > 0)
        {
            AddAction("copy-files");
            foreach (var id in SettingsStore.Current.FavoriteActions.Take(5)) AddAction(id);
            if (forSelection)
            {
                AddAction("rename");
                AddAction("remove");
                AddAction("new-shelf-copy");
                AddAction("new-shelf-move");
            }
            Separator();
            list.Add(AllActionsMenu(input));
        }
        list.Add(CommandItem("Search Actions…", "", "Ctrl+K", ShowCommandBar));

        if (!forSelection)
        {
            Separator();
            list.Add(CommandItem("Customize Shelf…", "", null, ShowCustomizePanel));
            list.Add(CommandItem(_isPinned ? "Unpin Shelf" : "Pin Shelf", _isPinned ? "" : "", null, () => IsPinned = !_isPinned));
            list.Add(CommandItem(IsDocked ? "Undock Shelf" : "Dock to Edge", "", "Ctrl+D", ToggleDock));
            if (Items.Count > 0) list.Add(CommandItem("Clear Shelf", "", "Ctrl+Del", ClearItems));
            list.Add(CommandItem("Close Shelf", "", "Ctrl+W", Close));
        }
        return list;
    }

    private MenuItem AllActionsMenu(ActionInput input)
    {
        var root = new MenuItem { Header = Text("All Actions"), Icon = Glyph("") };
        foreach (var group in ActionCatalog.All().Where(a => a.IsAvailable(input)).GroupBy(a => a.Category).OrderBy(g => g.Key))
        {
            var sub = new MenuItem { Header = Text(ActionCatalog.CategoryTitle(group.Key)) };
            foreach (var action in group) sub.Items.Add(ActionMenuItem(action, input));
            root.Items.Add(sub);
        }
        return root;
    }

    /// <summary>"Open With ›" listing Explorer's recommended apps, loaded when the submenu opens.</summary>
    private MenuItem? OpenWithMenu(IReadOnlyList<ShelfItem> targets)
    {
        var files = targets.Where(t => t.Kind == ShelfItemKind.File && t.Exists).ToList();
        if (files.Count == 0 || files.Count != targets.Count) return null;

        var root = new MenuItem { Header = Text("Open With"), Icon = Glyph("") };
        root.Items.Add(new MenuItem { Header = Text("Loading…"), IsEnabled = false });
        var loaded = false;
        root.SubmenuOpened += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            root.Items.Clear();

            var paths = files.Select(f => f.Path!).ToList();
            var extensions = paths.Select(p => System.IO.Path.GetExtension(p).ToLowerInvariant()).Distinct().ToList();
            root.Items.Add(CommandItem("Default App", "", null, () => RunActionById("open", files)));
            if (extensions.Count == 1)
            {
                var handlers = OpenWithHandlers.ForExtension(extensions[0]);
                if (handlers.Count > 0) root.Items.Add(new System.Windows.Controls.Separator());
                foreach (var handler in handlers)
                {
                    var item = new MenuItem { Header = Text(handler.Name) };
                    item.Click += (_, _) =>
                    {
                        if (!OpenWithHandlers.Invoke(handler, paths)) ShowToast($"Could not open with {handler.Name}");
                    };
                    root.Items.Add(item);
                }
            }
            if (files.Count == 1)
            {
                root.Items.Add(new System.Windows.Controls.Separator());
                root.Items.Add(CommandItem("Choose Another App…", "", null, () => ShellOperations.ShowOpenWithDialog(paths[0], Handle)));
            }
        };
        return root;
    }

    private MenuItem ActionMenuItem(ShelfAction action, ActionInput input)
    {
        var item = new MenuItem { Header = Text(action.MenuTitle(input)), Icon = Glyph(action.Glyph) };
        item.Click += (_, _) => RunAction(action, input.Items);
        return item;
    }

    private MenuItem CommandItem(string header, string glyph, string? gesture, Action onClick)
    {
        var item = new MenuItem { Header = Text(header), Icon = Glyph(glyph), InputGestureText = gesture ?? "" };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Plain TextBlocks so underscores in file and app names are not taken as access keys.
    private static TextBlock Text(string s) => new() { Text = s };

    private TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)FindResource("IconFont"),
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // =====================================================================================
    // Drag in
    // =====================================================================================

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        HandleDragOver(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        HandleDragOver(e);
    }

    private void HandleDragOver(DragEventArgs e)
    {
        e.Handled = true;
        if (ReferenceEquals(ShelfManager.ActiveOutboundShelf, this) || _busy || _mode == ShelfMode.Panel || !ItemImporter.CanImport(e.Data))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        // Shelves only reference files, so never accept a Move: the source would delete the original.
        e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : DragDropEffects.None;

        _dragInside = true;
        _retractTimer.Stop();
        SetDropHighlight(true);
        if (_dock != DockEdge.None && !_revealed) Reveal();
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        // Moving between child elements raises DragLeave too, but the DragEnter/DragOver that
        // follows immediately sets the flag again, so it is safe to always clear it here; the
        // visual is only dropped if the drag is still outside once that has happened.
        _dragInside = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_dragInside) SetDropHighlight(false);
        }, DispatcherPriority.Background);
        ScheduleRetract();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;
        _dragInside = false;
        SetDropHighlight(false);
        if (ReferenceEquals(ShelfManager.ActiveOutboundShelf, this)) return;

        var items = ItemImporter.FromDataObject(e.Data);
        if (items.Count == 0)
        {
            ShowToast("The operation could not be performed");
            return;
        }
        AddItems(items, fromDrop: true);
        ScheduleRetract();
    }

    /// <summary>Closes a shake-created shelf that received nothing once the drag ends.</summary>
    public async void CloseIfStillEmptyAfterDrag()
    {
        await Task.Delay(900);
        if (_closed || Items.Count > 0 || _dragInside || _busy || _isPinned || _customName is not null) return;
        SuppressRecent = true;
        Close();
    }

    // =====================================================================================
    // Drag out
    // =====================================================================================

    private void OnStackMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (Items.Count > 0) SetMode(ShelfMode.Detail);
            return;
        }
        _stackDragStart = e.GetPosition(this);
        StackArea.CaptureMouse();
    }

    private void OnStackMouseMove(object sender, MouseEventArgs e)
    {
        if (_stackDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _stackDragStart = null;
        StackArea.ReleaseMouseCapture();
        if (Items.Count == 0)
        {
            BeginUserMove();
            return;
        }
        StartDragOut(Items.ToList(), StackArea);
    }

    private void OnStackMouseUp(object sender, MouseButtonEventArgs e)
    {
        _stackDragStart = null;
        StackArea.ReleaseMouseCapture();
    }

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _listDragStart = e.GetPosition(ItemsList);
        var container = ItemsControl.ContainerFromElement(ItemsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        _listPressedItem = container?.DataContext as ShelfItem;
        _deferredSelection = false;

        // Keep a multi-selection intact when the user starts dragging one of its items.
        if (container is { IsSelected: true } && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1 && ItemsList.SelectedItems.Count > 1)
        {
            e.Handled = true;
            _deferredSelection = true;
            ItemsList.Focus();
        }
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_listDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _listPressedItem is null) return;
        var p = e.GetPosition(ItemsList);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _listDragStart = null;
        _deferredSelection = false;
        var selected = SelectedItems().ToList();
        if (!selected.Contains(_listPressedItem)) selected = [_listPressedItem];
        StartDragOut(selected, ItemsList);
    }

    private void OnListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferredSelection && _listPressedItem is not null)
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = _listPressedItem;
        }
        _deferredSelection = false;
        _listDragStart = null;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ItemsList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: ShelfItem item })
            return;
        PerformDoubleClick([item]);
    }

    private void PerformDoubleClick(IReadOnlyList<ShelfItem> items)
    {
        switch (SettingsStore.Current.DoubleClickFiles)
        {
            case DoubleClickAction.Open: RunActionById("open", items); break;
            case DoubleClickAction.Preview: RunActionById("preview", items); break;
            case DoubleClickAction.Reveal: RunActionById("reveal", items); break;
        }
    }

    private void StartDragOut(IReadOnlyList<ShelfItem> items, UIElement source)
    {
        var data = ItemImporter.ToDataObject(items);
        var allowed = SettingsStore.Current.AlwaysCopyOnDragOut
            ? DragDropEffects.Copy
            : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

        DragDropEffects result;
        ShelfManager.ActiveOutboundShelf = this;
        try
        {
            result = DragDrop.DoDragDrop(source, data, allowed);
        }
        catch (Exception ex)
        {
            Log.Error("Drag out failed", ex);
            result = DragDropEffects.None;
        }
        finally
        {
            ShelfManager.ActiveOutboundShelf = null;
        }

        AfterDragOut(items, result);
    }

    /// <summary>
    /// Mirrors Dropover's "Close on drag out": by default the shelf closes only when its items
    /// were moved away. Explorer may finish a move asynchronously, so existence is re-checked.
    /// </summary>
    private async void AfterDragOut(IReadOnlyList<ShelfItem> items, DragDropEffects result)
    {
        var behavior = SettingsStore.Current.CloseOnDragOut;
        if (result == DragDropEffects.None && behavior != OutboundDragBehavior.OnlyWhenMoved)
        {
            ScheduleRetract();
            return;
        }

        if (behavior == OutboundDragBehavior.Always && result != DragDropEffects.None && !_isPinned)
        {
            Close();
            return;
        }

        foreach (var delay in new[] { 150, 1200 })
        {
            await Task.Delay(delay);
            if (_closed) return;
            var moved = items.Where(i => i.IsFileBased && Items.Contains(i) && !i.Exists).ToList();
            if (moved.Count == 0) continue;
            RemoveItems(moved);
            if (Items.Count == 0 && behavior == OutboundDragBehavior.OnlyWhenMoved && !_isPinned)
            {
                Close();
                return;
            }
        }
        ScheduleRetract();
    }

    // =====================================================================================
    // Moving, snapping, docking
    // =====================================================================================

    /// <summary>Places the window before it is first shown, in physical pixels.</summary>
    public void PlaceInitially(ShelfPlacement placement, ShelfWindow? near)
    {
        var hwnd = Handle;
        if (placement.X is int px && placement.Y is int py)
        {
            WindowHelper.MoveTo(hwnd, px, py);
            return;
        }

        var anchor = placement.CenterAt ?? WindowHelper.CursorPosition();
        var scale = WindowHelper.ScaleAt(anchor);
        var cardW = (int)(StackSize.Width * scale);
        var cardH = (int)(StackSize.Height * scale);
        var margin = (int)(MarginDip * scale);
        var inset = (int)(24 * scale);
        var wa = WindowHelper.WorkAreaAt(anchor);
        int left, top;

        if (placement.CenterAt is { } c)
        {
            left = c.X - cardW / 2;
            top = c.Y - cardH / 2;
        }
        else if (near is not null && !near.IsClosed)
        {
            var r = near.CardRectPx();
            wa = WindowHelper.WorkAreaAt(new NativeMethods.POINT(r.Left, r.Top));
            left = r.Left + (int)(28 * scale);
            top = r.Top + (int)(28 * scale);
        }
        else
        {
            switch (placement.Location ?? SettingsStore.Current.DefaultShelfLocation)
            {
                case ShelfLocation.Center:
                    left = wa.Left + (wa.Width - cardW) / 2;
                    top = wa.Top + (wa.Height - cardH) / 2;
                    break;
                case ShelfLocation.TopLeft:
                    left = wa.Left + inset;
                    top = wa.Top + inset;
                    break;
                case ShelfLocation.TopRight:
                    left = wa.Right - inset - cardW;
                    top = wa.Top + inset;
                    break;
                case ShelfLocation.BottomLeft:
                    left = wa.Left + inset;
                    top = wa.Bottom - inset - cardH;
                    break;
                case ShelfLocation.BottomRight:
                    left = wa.Right - inset - cardW;
                    top = wa.Bottom - inset - cardH;
                    break;
                default:
                    left = anchor.X - cardW / 2;
                    top = anchor.Y - cardH - (int)(16 * scale);
                    break;
            }
            (left, top) = _manager.AvoidOverlap(this, left, top, scale);
        }

        left = Math.Clamp(left, wa.Left, Math.Max(wa.Left, wa.Right - cardW));
        top = Math.Clamp(top, wa.Top, Math.Max(wa.Top, wa.Bottom - cardH));
        WindowHelper.MoveTo(hwnd, left - margin, top - margin);
    }

    /// <summary>
    /// The card's rectangle in physical pixels, from its final (target) size: while a resize is
    /// animating the window is temporarily larger than the card.
    /// </summary>
    public NativeMethods.RECT CardRectPx()
    {
        var r = WindowHelper.GetRect(Handle);
        var m = MarginPx;
        var left = r.Left + m;
        var top = r.Top + m;
        return new NativeMethods.RECT(left, top,
            left + (int)Math.Round(_cardSize.Width * Scale), top + (int)Math.Round(_cardSize.Height * Scale));
    }

    private void MoveCardTo(int cardLeft, int cardTop, bool animate)
    {
        var m = MarginPx;
        if (animate) WindowHelper.AnimateTo(Handle, cardLeft - m, cardTop - m, 300, () => _manager.SavePinned());
        else WindowHelper.MoveTo(Handle, cardLeft - m, cardTop - m);
    }

    private void EnsureOnScreen()
    {
        if (_closed || (_dock != DockEdge.None && !_revealed)) return;
        var c = CardRectPx();
        var wa = WindowHelper.WorkAreaAt(new NativeMethods.POINT((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2));
        var left = c.Left;
        var top = c.Top;
        if (c.Right > wa.Right) left = wa.Right - c.Width;
        if (left < wa.Left) left = wa.Left;
        if (c.Bottom > wa.Bottom) top = wa.Bottom - c.Height;
        if (top < wa.Top) top = wa.Top;
        if (left != c.Left || top != c.Top) MoveCardTo(left, top, animate: true);
    }

    private void OnTopBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (SettingsStore.Current.DoubleClickEdge == EdgeDoubleClickAction.Dock) ToggleDock();
            return;
        }
        BeginUserMove();
    }

    private void BeginUserMove()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        WindowHelper.StopAnimation(Handle);
        var before = WindowHelper.GetRect(Handle);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        // A plain click returns from DragMove without moving; it must not undock or snap.
        var after = WindowHelper.GetRect(Handle);
        if (after.Left == before.Left && after.Top == before.Top) return;
        AfterUserMove();
    }

    /// <summary>
    /// True when no other monitor continues past this edge. Docking against a shared edge would
    /// park the "hidden" shelf on the neighbouring screen, fully visible.
    /// </summary>
    private bool IsFreeEdge(DockEdge edge)
    {
        var c = CardRectPx();
        var center = new NativeMethods.POINT((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2);
        var monitor = WindowHelper.MonitorBoundsAt(center);
        var y = Math.Clamp(center.Y, monitor.Top, monitor.Bottom - 1);
        var beyond = edge == DockEdge.Left ? new NativeMethods.POINT(monitor.Left - 1, y) : new NativeMethods.POINT(monitor.Right, y);
        return !WindowHelper.HasMonitorAt(beyond);
    }

    private void AfterUserMove()
    {
        var c = CardRectPx();
        var wa = _dock != DockEdge.None
            ? _dockWorkArea
            : WindowHelper.WorkAreaAt(new NativeMethods.POINT((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2));

        // Pushing a shelf past an outer screen edge docks it there.
        if (c.Left < wa.Left - c.Width * 0.35 && IsFreeEdge(DockEdge.Left))
        {
            DockTo(DockEdge.Left);
            return;
        }
        if (c.Right > wa.Right + c.Width * 0.35 && IsFreeEdge(DockEdge.Right))
        {
            DockTo(DockEdge.Right);
            return;
        }

        _dock = DockEdge.None;
        _revealed = false;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (SettingsStore.Current.SnapIntoPlace && !ctrl) SnapIntoPlace();
        else EnsureOnScreen();
        _manager.SavePinned();
    }

    /// <summary>Aligns the shelf with screen edges and neighbouring shelves (hold Ctrl to skip).</summary>
    private void SnapIntoPlace()
    {
        var c = CardRectPx();
        var wa = WindowHelper.WorkAreaAt(new NativeMethods.POINT((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2));
        var tolerance = (int)(24 * Scale);
        var gap = (int)(10 * Scale);

        var xs = new List<int> { wa.Left + gap, wa.Right - gap - c.Width };
        var ys = new List<int> { wa.Top + gap, wa.Bottom - gap - c.Height };
        foreach (var other in _manager.Shelves.Where(o => !ReferenceEquals(o, this) && o.IsVisible && !o.IsDocked && !o.IsClosed))
        {
            var r = other.CardRectPx();
            var verticalOverlap = c.Top < r.Bottom + tolerance && c.Bottom > r.Top - tolerance;
            var horizontalOverlap = c.Left < r.Right + tolerance && c.Right > r.Left - tolerance;
            if (verticalOverlap)
            {
                xs.Add(r.Right + gap);
                xs.Add(r.Left - gap - c.Width);
                ys.Add(r.Top);
            }
            if (horizontalOverlap)
            {
                ys.Add(r.Bottom + gap);
                ys.Add(r.Top - gap - c.Height);
                xs.Add(r.Left);
            }
        }

        static int Nearest(int current, List<int> options, int tolerance)
        {
            var best = current;
            var bestDistance = tolerance + 1;
            foreach (var o in options)
            {
                var d = Math.Abs(o - current);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = o;
                }
            }
            return best;
        }

        var left = Math.Clamp(Nearest(c.Left, xs, tolerance), wa.Left, Math.Max(wa.Left, wa.Right - c.Width));
        var top = Math.Clamp(Nearest(c.Top, ys, tolerance), wa.Top, Math.Max(wa.Top, wa.Bottom - c.Height));
        if (left != c.Left || top != c.Top) MoveCardTo(left, top, animate: true);
    }

    public void ToggleDock()
    {
        if (_dock == DockEdge.None) DockToNearestEdge();
        else Undock();
    }

    public void DockToNearestEdge()
    {
        if (_closed) return;
        var c = CardRectPx();
        var wa = WindowHelper.WorkAreaAt(new NativeMethods.POINT((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2));
        var nearest = (c.Left + c.Right) / 2 < (wa.Left + wa.Right) / 2 ? DockEdge.Left : DockEdge.Right;
        var other = nearest == DockEdge.Left ? DockEdge.Right : DockEdge.Left;
        if (IsFreeEdge(nearest)) DockTo(nearest);
        else if (IsFreeEdge(other)) DockTo(other);
        else ShowToast("This screen has no free edge to dock to");
    }

    public void DockTo(DockEdge edge)
    {
        if (_closed) return;
        if (edge == DockEdge.None)
        {
            Undock();
            return;
        }
        if (_dock == DockEdge.None && !IsFreeEdge(edge))
        {
            ShowToast("Another screen continues past this edge");
            return;
        }
        // Collapse instantly: a card still shrinking from the detail size would overhang the screen
        // while it slides to the edge.
        if (_mode != ShelfMode.Stack) SetMode(ShelfMode.Stack, animate: false);

        var c = CardRectPx();
        if (_dock == DockEdge.None)
            _dockWorkArea = WindowHelper.WorkAreaAt(new NativeMethods.POINT((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2));
        _dock = edge;
        _revealed = false;
        var (x, y) = DockedPosition(hidden: true);
        WindowHelper.AnimateTo(Handle, x, y, 340, () => _manager.SavePinned());
    }

    public void Undock()
    {
        if (_dock == DockEdge.None) return;
        var (x, y) = DockedPosition(hidden: false);
        _dock = DockEdge.None;
        _revealed = false;
        WindowHelper.AnimateTo(Handle, x, y, 300, () => _manager.SavePinned());
    }

    private (int X, int Y) DockedPosition(bool hidden)
    {
        var c = CardRectPx();
        var m = MarginPx;
        var cardW = c.Width;
        var cardH = c.Height;
        var cardTop = c.Top;
        var peek = (int)(PeekDip * Scale);
        var gap = (int)(8 * Scale);
        var wa = _dockWorkArea;

        var cardLeft = _dock == DockEdge.Left
            ? (hidden ? wa.Left - cardW + peek : wa.Left + gap)
            : (hidden ? wa.Right - peek : wa.Right - cardW - gap);
        cardTop = Math.Clamp(cardTop, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - cardH - gap));
        return (cardLeft - m, cardTop - m);
    }

    public void Reveal()
    {
        if (_dock == DockEdge.None || _closed) return;
        _revealed = true;
        var (x, y) = DockedPosition(hidden: false);
        WindowHelper.AnimateTo(Handle, x, y, 260);
    }

    private void ScheduleRetract()
    {
        if (_dock == DockEdge.None || !_revealed || _closed) return;
        _retractTimer.Stop();
        _retractTimer.Start();
    }

    private void RetractIfIdle()
    {
        if (_dock == DockEdge.None || !_revealed || _closed) return;
        if (IsMouseOver || _dragInside || _mode != ShelfMode.Stack || _busy || _openMenus > 0)
        {
            ScheduleRetract();
            return;
        }
        _revealed = false;
        var (x, y) = DockedPosition(hidden: true);
        WindowHelper.AnimateTo(Handle, x, y, 340, ease: Motion.EaseInOut);
    }

    /// <summary>Brings the shelf into view (reveals a docked shelf) with a short pulse of its ring.</summary>
    public void Present()
    {
        if (_closed) return;
        if (_dock != DockEdge.None && !_revealed) Reveal();
        if (!Motion.Enabled)
        {
            // No animation: show the ring briefly instead.
            Motion.Set(DropHighlight, OpacityProperty, 1);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_highlighted) Motion.Set(DropHighlight, OpacityProperty, 0);
                ScheduleRetract();
            };
            timer.Start();
            return;
        }
        Motion.To(LiftScale, ScaleTransform.ScaleXProperty, _highlighted ? 1.035 : 1, 560, Motion.Spring, from: 1.07);
        Motion.To(LiftScale, ScaleTransform.ScaleYProperty, _highlighted ? 1.035 : 1, 560, Motion.Spring, from: 1.07);
        Motion.To(DropHighlight, OpacityProperty, 1, 120, completed: () =>
        {
            if (!_highlighted) Motion.To(DropHighlight, OpacityProperty, 0, 420, delayMs: 300);
            ScheduleRetract();
        });
    }

    // =====================================================================================
    // Buttons, keyboard, lifecycle
    // =====================================================================================

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ClearItems();
        else Close();
    }

    private void OnFooterClick(object sender, RoutedEventArgs e)
    {
        if (Items.Count > 0) SetMode(ShelfMode.Detail);
        else ShowCustomizePanel();
    }

    private void OnBackToStack(object sender, RoutedEventArgs e) => SetMode(ShelfMode.Stack);

    private void OnCustomizeClick(object sender, RoutedEventArgs e) => ShowCustomizePanel();

    private void OnGridMode(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.ViewMode = ItemViewMode.Grid;
        ApplyViewMode(ItemViewMode.Grid);
    }

    private void OnListMode(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.ViewMode = ItemViewMode.List;
        ApplyViewMode(ItemViewMode.List);
    }

    private void OnDeactivatedHandler(object? sender, EventArgs e)
    {
        if (_mode != ShelfMode.Detail || !SettingsStore.Current.AutoCloseDetailView) return;
        Dispatcher.BeginInvoke(() =>
        {
            // Stay open while our own windows (preview, dialogs) have focus.
            if (_closed || _mode != ShelfMode.Detail || Application.Current.Windows.OfType<Window>().Any(w => w.IsActive)) return;
            SetMode(ShelfMode.Stack);
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape)
        {
            if (_mode == ShelfMode.Panel) ClosePanel();
            else if (_mode == ShelfMode.Detail) SetMode(ShelfMode.Stack);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.W when ctrl: Close(); break;
            case Key.Delete or Key.Back when ctrl: ClearItems(); break;
            case Key.C when ctrl: RunActionById("copy-files", CurrentTargets()); break;
            case Key.V when ctrl: RunActionById("add-clipboard", []); break;
            case Key.K when ctrl: ShowCommandBar(); break;
            case Key.M when ctrl: OpenActionMenu(MenuButton); break;
            case Key.Apps: OpenActionMenu(MenuButton); break;
            case Key.Left when ctrl: DockTo(DockEdge.Left); break;
            case Key.Right when ctrl: DockTo(DockEdge.Right); break;
            case Key.D when ctrl: ToggleDock(); break;
            case Key.A when ctrl && _mode == ShelfMode.Detail: ItemsList.SelectAll(); break;
            case Key.Space when Items.Count > 0 && _mode != ShelfMode.Panel: RunActionById("preview", CurrentTargets()); break;
            case Key.Enter when _mode == ShelfMode.Stack && Items.Count > 0: SetMode(ShelfMode.Detail); break;
            case Key.Enter when _mode == ShelfMode.Detail && ItemsList.SelectedItems.Count > 0: PerformDoubleClick(SelectedItems()); break;
            case Key.Delete when _mode == ShelfMode.Detail && ItemsList.SelectedItems.Count > 0: RemoveItems(SelectedItems()); break;
            case Key.F2 when _mode == ShelfMode.Detail && ItemsList.SelectedItems.Count == 1: RunActionById("rename", SelectedItems()); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Plays a short shrink-and-fade before the window really closes.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        var canAnimate = !_closeAnimationDone && Motion.Enabled && IsVisible
                         && !_manager.IsShuttingDown && !Dispatcher.HasShutdownStarted;
        if (canAnimate)
        {
            e.Cancel = true;
            if (_closingAnimating) return;
            _closingAnimating = true;
            IsHitTestVisible = false;
            AllowDrop = false;
            _toastTimer.Stop();
            _retractTimer.Stop();
            _actionCts?.Cancel();
            Motion.To(Root, OpacityProperty, 0, 160, Motion.EaseIn);
            Motion.To(RootScale, ScaleTransform.ScaleXProperty, 0.9, 180, Motion.EaseIn);
            Motion.To(RootScale, ScaleTransform.ScaleYProperty, 0.9, 180, Motion.EaseIn, completed: () =>
            {
                _closeAnimationDone = true;
                Close();
            });
            return;
        }
        _actionCts?.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        _toastTimer.Stop();
        _retractTimer.Stop();
        _stackRefreshTimer.Stop();
        if (_hwnd != IntPtr.Zero) WindowHelper.StopAnimation(_hwnd);
        foreach (var item in Items) item.PropertyChanged -= OnItemPropertyChanged;
        _manager.OnShelfClosed(this);
    }

    public ShelfSnapshot ToSnapshot()
    {
        var r = _hwnd != IntPtr.Zero ? WindowHelper.GetRect(_hwnd) : default;
        if (_dock != DockEdge.None)
        {
            // Store the revealed position so a restored shelf starts on-screen.
            (r.Left, r.Top) = DockedPosition(hidden: false);
        }
        return new ShelfSnapshot
        {
            Id = ShelfId,
            CustomName = _customName,
            Title = ShelfTitle,
            Color = _color,
            Items = Items.Select(i => i.ToSnapshot()).ToList(),
            SavedAt = DateTime.Now,
            X = _hwnd != IntPtr.Zero ? r.Left : null,
            Y = _hwnd != IntPtr.Zero ? r.Top : null,
            DockEdge = _dock == DockEdge.None ? null : _dock.ToString(),
        };
    }
}
