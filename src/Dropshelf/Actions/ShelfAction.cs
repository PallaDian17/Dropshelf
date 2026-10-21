using System.Globalization;
using Dropshelf.Core;

namespace Dropshelf.Actions;

public enum ActionCategory { General, Share, Image, Text, Shelf, Scripts, Custom }

/// <summary>
/// A file action available from a shelf's action menu, the command bar, Instant Actions and
/// Custom Actions. Actions with fields show an inline configuration panel first (Dropover 5's
/// "actions expand directly in the shelf").
/// </summary>
public abstract class ShelfAction
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract ActionCategory Category { get; }
    public virtual string? Description => null;
    public virtual string[] Keywords => [];

    /// <summary>Text shown while the action runs, for example "Compressing…".</summary>
    public virtual string ProgressText => "Working…";

    /// <summary>Menu label for the given targets (for example "Copy 5 Images").</summary>
    public virtual string MenuTitle(ActionInput input) => GetFields(input).Count > 0 ? Title + "…" : Title;

    public virtual bool IsAvailable(ActionInput input) => input.Items.Count > 0;

    public virtual IReadOnlyList<ActionField> GetFields(ActionInput input) => [];

    /// <summary>True when the defaults are not enough to run (e.g. a destination folder is required).</summary>
    public virtual bool RequiresConfiguration => false;

    /// <summary>Can be saved as a Custom Action with preset values.</summary>
    public virtual bool SupportsPreset => false;

    /// <summary>
    /// The action hands the files to UI anchored on the shelf that outlives RunAsync (the share
    /// sheet, a mail window), so an Instant Action must not close the shelf afterwards.
    /// </summary>
    public virtual bool KeepsShelfOpen => false;

    /// <summary>Can run from an Instant Action tile or a Custom Action without showing UI.</summary>
    public virtual bool CanRunUnattended => Category is not ActionCategory.Shelf;

    public virtual string? Validate(ActionInput input, ActionValues values) => null;

    public abstract Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct);
}

public interface IShelfHost
{
    IntPtr Handle { get; }
    IReadOnlyList<ShelfItem> AllItems { get; }
    void AddItems(IEnumerable<ShelfItem> items);
    void RemoveItems(IEnumerable<ShelfItem> items);
    void ReplaceItems(IReadOnlyList<ShelfItem> oldItems, IEnumerable<ShelfItem> newItems);
    void ClearItems();
    void CloseShelf();
    void DockToNearestEdge();
    void ShowToast(string message);
}

public sealed class ActionInput
{
    public ActionInput(IReadOnlyList<ShelfItem> items, IShelfHost? shelf)
    {
        Items = items;
        Shelf = shelf;
    }

    public IReadOnlyList<ShelfItem> Items { get; }
    public IShelfHost? Shelf { get; }
    public IntPtr Owner => Shelf?.Handle ?? IntPtr.Zero;

    public IEnumerable<ShelfItem> FileItems => Items.Where(i => i.IsFileBased && i.Exists);

    public IReadOnlyList<ShelfItem> ImageItems =>
        Items.Where(i => i.Kind == ShelfItemKind.File && i.Path is not null && FileKinds.IsEditableImage(i.Path) && i.Exists).ToList();

    /// <summary>Paths for every item, writing text snippets and links to files where needed.</summary>
    public IReadOnlyList<string> AllPaths() => Items.Select(i => i.GetOrCreateFilePath()).Where(p => p.Length > 0).ToList();

    public string CountLabel => ShelfNaming.CountLabel(Items);

    /// <summary>Folder used for outputs when the user picked "Same folder as original".</summary>
    public string DefaultOutputFolder(ShelfItem? item = null)
    {
        var source = item ?? Items.FirstOrDefault(i => i.IsFileBased && !i.IsTemporary);
        var dir = source?.Path is { } p ? Path.GetDirectoryName(p) : null;
        return string.IsNullOrEmpty(dir) || (source?.IsTemporary ?? true) ? AppPaths.DesktopDir : dir;
    }
}

/// <summary>What happened, and how the shelf should change.</summary>
public sealed class ActionResult
{
    public bool Success { get; init; } = true;
    public string? Message { get; init; }
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>Items the outputs replace in "Current Shelf" mode (defaults to all targeted items).</summary>
    public IReadOnlyList<ShelfItem>? SourceItems { get; init; }

    /// <summary>"shelf" replaces the sources with the outputs, "add" appends, "newshelf", "explorer" or "none".</summary>
    public string OutputMode { get; init; } = "shelf";

    public IReadOnlyList<ShelfItem> RemovedItems { get; init; } = [];
    public IReadOnlyDictionary<ShelfItem, string> RenamedItems { get; init; } = new Dictionary<ShelfItem, string>();
    public bool CloseShelf { get; init; }

    public static ActionResult Ok(string? message = null) => new() { Message = message };
    public static ActionResult Fail(string message) => new() { Success = false, Message = message };
    public static ActionResult Cancelled() => new() { Success = false };
}

/// <summary>Field values from a configuration panel or a saved Custom Action, kept as invariant strings.</summary>
public sealed class ActionValues
{
    private readonly Dictionary<string, string> _values;

    public ActionValues(IDictionary<string, string>? values = null)
        => _values = values is null ? new() : new Dictionary<string, string>(values);

    public static ActionValues Defaults(IEnumerable<ActionField> fields, IDictionary<string, string>? overrides = null)
    {
        var v = new ActionValues();
        foreach (var f in fields) v[f.Key] = f.DefaultValue;
        if (overrides is not null)
        {
            foreach (var (k, value) in overrides) v[k] = value;
        }
        return v;
    }

    public string this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : "";
        set => _values[key] = value;
    }

    public IReadOnlyDictionary<string, string> AsDictionary() => _values;

    public string GetString(string key, string fallback = "") => _values.TryGetValue(key, out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public int GetInt(string key, int fallback = 0)
        => _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public void SetBool(string key, bool value) => _values[key] = value ? "true" : "false";
    public void SetInt(string key, int value) => _values[key] = value.ToString(CultureInfo.InvariantCulture);
}
