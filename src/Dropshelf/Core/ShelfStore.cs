using System.Text.Json;

namespace Dropshelf.Core;

/// <summary>Serializable state of a shelf: used for Recent Shelves and for pinned shelves across restarts.</summary>
public sealed class ShelfSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? CustomName { get; set; }
    public string Title { get; set; } = "";
    public string? Color { get; set; }
    public List<ItemSnapshot> Items { get; set; } = [];
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public int? X { get; set; }
    public int? Y { get; set; }
    public string? DockEdge { get; set; }

    public string MenuTitle
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(CustomName) ? Title : CustomName!;
            return Items.Count > 1 && !string.IsNullOrWhiteSpace(CustomName) ? $"{name} ({Items.Count} items)" : name;
        }
    }
}

/// <summary>Most recently closed shelves, newest first.</summary>
public sealed class RecentShelvesStore
{
    private const int MaxEntries = 20;
    private readonly List<ShelfSnapshot> _items;

    public RecentShelvesStore() => _items = LoadList(AppPaths.RecentShelvesFile);

    public event Action? Changed;

    public IReadOnlyList<ShelfSnapshot> Items => _items;

    public void Add(ShelfSnapshot snapshot)
    {
        _items.RemoveAll(s => s.Id == snapshot.Id);
        _items.Insert(0, snapshot);
        if (_items.Count > MaxEntries) _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
        Persist();
    }

    public void Remove(ShelfSnapshot snapshot)
    {
        _items.RemoveAll(s => s.Id == snapshot.Id);
        Persist();
    }

    public void Clear()
    {
        _items.Clear();
        Persist();
    }

    /// <summary>Drops entries whose files are all inside a folder that is being purged.</summary>
    public void RemoveReferencesTo(string folder)
    {
        var count = _items.RemoveAll(s => s.Items.Count > 0 && s.Items.All(i =>
            i.Path is not null && i.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));
        if (count > 0) Persist();
    }

    private void Persist()
    {
        SaveList(AppPaths.RecentShelvesFile, _items);
        Changed?.Invoke();
    }

    public static List<ShelfSnapshot> LoadList(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<List<ShelfSnapshot>>(File.ReadAllText(file), SettingsStore.JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {Path.GetFileName(file)}: {ex.Message}");
        }
        return [];
    }

    public static void SaveList(string file, List<ShelfSnapshot> list)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(list, SettingsStore.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save {Path.GetFileName(file)}", ex);
        }
    }
}
