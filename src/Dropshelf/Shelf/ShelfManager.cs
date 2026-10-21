using System.Windows;
using System.Windows.Threading;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;

namespace Dropshelf.Shelf;

/// <summary>How a new shelf is positioned.</summary>
public sealed record ShelfPlacement(NativeMethods.POINT? CenterAt = null, int? X = null, int? Y = null, ShelfLocation? Location = null)
{
    public static ShelfPlacement Default { get; } = new();
}

/// <summary>Owns every open shelf, Recent Shelves, pinned shelves and the inbound-files rules.</summary>
public sealed class ShelfManager
{
    /// <summary>Label colors handed out in turn when "Use color to distinguish shelves" is on.</summary>
    public static readonly string[] Palette = ["#FF453A", "#FF9F0A", "#FFD60A", "#32D74B", "#64D2FF", "#0A84FF", "#BF5AF2", "#FF375F"];

    private readonly List<ShelfWindow> _shelves = new();
    private readonly Dictionary<ShelfWindow, InstantActionsWindow> _instantDocks = new();
    private readonly List<string> _pendingInbound = new();
    private readonly DispatcherTimer _inboundTimer;
    private ShelfSnapshot? _lastClosedActive;
    private int _colorIndex;
    private bool _restoringPinned;
    private bool _shuttingDown;

    public ShelfManager()
    {
        Instance = this;
        _inboundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _inboundTimer.Tick += (_, _) =>
        {
            _inboundTimer.Stop();
            var paths = _pendingInbound.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _pendingInbound.Clear();
            AddInbound(ItemImporter.FromPaths(paths));
        };
    }

    public static ShelfManager Instance { get; private set; } = null!;

    /// <summary>The shelf currently dragging items out (it must not accept its own drop).</summary>
    public static ShelfWindow? ActiveOutboundShelf { get; set; }

    public IReadOnlyList<ShelfWindow> Shelves => _shelves;
    public ShelfWindow? LastActive { get; private set; }
    public RecentShelvesStore Recent { get; } = new();

    public event Action? ShelvesChanged;

    // ---- Creating shelves -----------------------------------------------------------------

    public ShelfWindow CreateShelf(IEnumerable<ShelfItem>? items = null, IShelfHost? near = null, ShelfPlacement? placement = null,
        bool fromGesture = false, string? name = null, string? color = null, bool activate = false)
    {
        var shelf = new ShelfWindow(this, fromGesture)
        {
            CustomName = name,
            ColorHex = color ?? (SettingsStore.Current.UseColorLabels ? NextColor() : null),
        };
        if (items is not null) shelf.AddItems(items);

        shelf.PlaceInitially(placement ?? ShelfPlacement.Default, near as ShelfWindow);
        _shelves.Add(shelf);
        shelf.Show();
        if (activate || SettingsStore.Current.ShelfTakesFocus) shelf.Activate();
        LastActive = shelf;
        ShelvesChanged?.Invoke();
        return shelf;
    }

    public void NewShelf() => CreateShelf(activate: true);

    public void NewShelfFromClipboard()
    {
        var items = ItemImporter.FromClipboard();
        if (items.Count == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        CreateShelf(items, activate: true);
    }

    /// <summary>A shake (or modifier) mid-drag: open a shelf right under the cursor.</summary>
    public void OnGesture(GestureTrigger trigger)
    {
        // One empty gesture shelf at a time; shaking again just moves attention to it.
        var existing = _shelves.FirstOrDefault(s => s.FromGesture && s.Items.Count == 0 && !s.IsClosed);
        if (existing is not null)
        {
            existing.Present();
            return;
        }

        var shelf = CreateShelf(placement: new ShelfPlacement(CenterAt: trigger.Cursor), fromGesture: true);
        if (SettingsStore.Current.InstantActionsEnabled) ShowInstantActions(shelf);
    }

    public void OnDragEnded()
    {
        foreach (var shelf in _shelves.Where(s => s.FromGesture && s.Items.Count == 0).ToList())
            shelf.CloseIfStillEmptyAfterDrag();

        if (_instantDocks.Count > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                foreach (var dock in _instantDocks.Values.ToList()) dock.Close();
                _instantDocks.Clear();
            };
            timer.Start();
        }
    }

    private void ShowInstantActions(ShelfWindow shelf)
    {
        var actions = SettingsStore.Current.InstantActions
            .Select(ActionCatalog.Find)
            .Where(a => a is not null)
            .Cast<ShelfAction>()
            .ToList();
        if (actions.Count == 0) return;
        var dock = new InstantActionsWindow(shelf, actions);
        _instantDocks[shelf] = dock;
        dock.Closed += (_, _) => _instantDocks.Remove(shelf);
        dock.ShowBelow(shelf);
    }

    /// <summary>Shifts a new shelf diagonally while it would sit exactly on top of another one.</summary>
    public (int Left, int Top) AvoidOverlap(ShelfWindow newShelf, int left, int top, double scale)
    {
        var step = (int)(26 * scale);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var clash = _shelves.Any(s => !ReferenceEquals(s, newShelf) && !s.IsClosed && s.IsVisible &&
                                          Math.Abs(s.CardRectPx().Left - left) < step / 2 &&
                                          Math.Abs(s.CardRectPx().Top - top) < step / 2);
            if (!clash) break;
            left += step;
            top += step;
        }
        return (left, top);
    }

    public string NextColor()
    {
        var color = Palette[_colorIndex % Palette.Length];
        _colorIndex++;
        return color;
    }

    // ---- Files arriving from Send To, the context menu, hotkeys, automations -------------

    /// <summary>Paths from separate process launches (one per file) are merged into one shelf.</summary>
    public void QueueInbound(IEnumerable<string> paths)
    {
        _pendingInbound.AddRange(paths);
        _inboundTimer.Stop();
        _inboundTimer.Start();
    }

    public void AddInbound(IReadOnlyList<ShelfItem> items)
    {
        if (items.Count == 0) return;
        var behavior = SettingsStore.Current.InboundBehavior;
        if (behavior is InboundBehavior.VisibleShelf or InboundBehavior.LastActiveShelf)
        {
            var target = LastActive is { IsClosed: false } last ? last : _shelves.LastOrDefault(s => !s.IsClosed);
            if (target is not null)
            {
                target.AddItems(items);
                target.Present();
                return;
            }

            if (behavior == InboundBehavior.LastActiveShelf && _lastClosedActive is not null)
            {
                var reopened = OpenSnapshot(_lastClosedActive, showErrors: false);
                if (reopened is not null)
                {
                    reopened.AddItems(items);
                    return;
                }
            }
        }
        CreateShelf(items);
    }

    /// <summary>New files from Folder Monitoring or the Screenshot shelf.</summary>
    public void OnFolderFiles(FolderMonitorService.FileBatch batch)
    {
        var location = SettingsStore.Current.DefaultShelfLocation == ShelfLocation.NearCursor
            ? ShelfLocation.BottomRight
            : SettingsStore.Current.DefaultShelfLocation;
        var placement = new ShelfPlacement(Location: location);

        if (batch.NewShelfPerFile)
        {
            foreach (var file in batch.Files)
            {
                var items = ItemImporter.FromPaths([file]);
                CreateShelf(items, placement: placement, name: batch.IsScreenshot ? null : Label(batch.FolderName, items));
            }
            return;
        }

        var all = ItemImporter.FromPaths(batch.Files);
        CreateShelf(all, placement: placement, name: Label(batch.FolderName, all));
    }

    private static string Label(string folder, IReadOnlyCollection<ShelfItem> items)
    {
        var categories = items.Select(i => i.Category).Distinct().ToList();
        var category = categories.Count == 1 ? categories[0] : FileCategory.Other;
        return items.Count == 1
            ? $"New {FileKinds.Singular(category)} in {folder}"
            : $"{items.Count} New {FileKinds.Plural(category)} in {folder}";
    }

    // ---- Shelf lifecycle -----------------------------------------------------------------

    public void NotifyActivated(ShelfWindow shelf) => LastActive = shelf;

    public void OnShelfContentChanged(ShelfWindow shelf)
    {
        if (shelf.IsPinned) SavePinned();
        ShelvesChanged?.Invoke();
    }

    /// <summary>Saves pinned shelves before the app closes every window on exit.</summary>
    public void BeginShutdown()
    {
        if (_shuttingDown) return;
        SavePinned();
        _shuttingDown = true;
    }

    public void OnShelfClosed(ShelfWindow shelf)
    {
        _shelves.Remove(shelf);
        if (_instantDocks.Remove(shelf, out var dock)) dock.Close();

        if (_shuttingDown)
        {
            // Pinned shelves come back on their own next launch; others stay restorable.
            if (!shelf.IsPinned && !shelf.SuppressRecent && shelf.Items.Count > 0) Recent.Add(shelf.ToSnapshot());
            return;
        }

        if (!shelf.SuppressRecent && shelf.Items.Count > 0)
        {
            var snapshot = shelf.ToSnapshot();
            Recent.Add(snapshot);
            if (ReferenceEquals(LastActive, shelf)) _lastClosedActive = snapshot;
        }
        if (ReferenceEquals(LastActive, shelf)) LastActive = _shelves.LastOrDefault();

        SavePinned();
        ShelvesChanged?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var shelf in _shelves.Where(s => !s.IsPinned).ToList()) shelf.Close();
    }

    public void ToggleDockAll()
    {
        var visible = _shelves.Where(s => !s.IsClosed).ToList();
        if (visible.Count == 0) return;
        if (visible.Any(s => !s.IsDocked))
        {
            foreach (var s in visible.Where(s => !s.IsDocked)) s.DockToNearestEdge();
        }
        else
        {
            foreach (var s in visible) s.Undock();
        }
    }

    // ---- Recent shelves --------------------------------------------------------------------

    public void RestorePrevious()
    {
        if (Recent.Items.FirstOrDefault() is { } last) OpenSnapshot(last, showErrors: true);
        else System.Media.SystemSounds.Beep.Play();
    }

    public ShelfWindow? OpenSnapshot(ShelfSnapshot snapshot, bool showErrors)
    {
        var items = snapshot.Items.Select(ShelfItem.FromSnapshot).Where(i => i is not null).Cast<ShelfItem>().ToList();
        if (items.Count == 0)
        {
            if (showErrors)
            {
                MessageBox.Show("This shelf could not be restored because its files were moved, deleted or are no longer accessible.",
                    "Could not restore shelf", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Recent.Remove(snapshot);
            return null;
        }

        Recent.Remove(snapshot);
        var shelf = CreateShelf(items, name: snapshot.CustomName, color: snapshot.Color, activate: true);
        shelf.ShelfId = snapshot.Id;
        return shelf;
    }

    // ---- Pinned shelves (survive restarts) ------------------------------------------------

    public void SavePinned()
    {
        if (_restoringPinned || _shuttingDown) return;
        var pinned = _shelves.Where(s => s.IsPinned && !s.IsClosed).Select(s => s.ToSnapshot()).ToList();
        RecentShelvesStore.SaveList(AppPaths.PinnedShelvesFile, pinned);
        ShelvesChanged?.Invoke();
    }

    public void RestorePinned()
    {
        _restoringPinned = true;
        try
        {
            foreach (var snapshot in RecentShelvesStore.LoadList(AppPaths.PinnedShelvesFile))
            {
                var items = snapshot.Items.Select(ShelfItem.FromSnapshot).Where(i => i is not null).Cast<ShelfItem>().ToList();
                var placement = snapshot.X is int x && snapshot.Y is int y && IsOnAnyScreen(x, y)
                    ? new ShelfPlacement(X: x, Y: y)
                    : ShelfPlacement.Default;
                var shelf = CreateShelf(items, placement: placement, name: snapshot.CustomName, color: snapshot.Color);
                shelf.ShelfId = snapshot.Id;
                shelf.IsPinned = true;
                if (Enum.TryParse<DockEdge>(snapshot.DockEdge, out var edge) && edge != DockEdge.None) shelf.DockTo(edge);
            }
        }
        finally
        {
            _restoringPinned = false;
        }
    }

    private static bool IsOnAnyScreen(int x, int y)
    {
        var p = new NativeMethods.POINT(x + 40, y + 40);
        var wa = WindowHelper.WorkAreaAt(p);
        return wa.Contains(p);
    }

    public IEnumerable<ShelfWindow> PinnedShelves => _shelves.Where(s => s.IsPinned);
}
