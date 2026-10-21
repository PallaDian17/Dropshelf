namespace Dropshelf.Core;

public static class ShelfNaming
{
    /// <summary>
    /// Default shelf title, matching Dropover's pattern: the file name for one item,
    /// "4 Images" when everything shares a kind, otherwise "6 Files" / "6 Items".
    /// </summary>
    public static string DefaultName(IReadOnlyCollection<ShelfItem> items)
    {
        if (items.Count == 0) return "Empty Shelf";
        if (items.Count == 1) return items.First().DisplayName;

        var categories = items.Select(i => i.Category).Distinct().ToList();
        if (categories.Count == 1) return FileKinds.Count(items.Count, categories[0]);
        if (items.All(i => i.Kind == ShelfItemKind.File)) return $"{items.Count} Files";
        return $"{items.Count} Items";
    }

    /// <summary>Count-only label, used in menu titles such as "Copy 5 Images".</summary>
    public static string CountLabel(IReadOnlyCollection<ShelfItem> items)
    {
        if (items.Count == 0) return "Items";
        var categories = items.Select(i => i.Category).Distinct().ToList();
        if (categories.Count == 1) return FileKinds.Count(items.Count, categories[0]);
        if (items.All(i => i.Kind == ShelfItemKind.File)) return items.Count == 1 ? "1 File" : $"{items.Count} Files";
        return items.Count == 1 ? "1 Item" : $"{items.Count} Items";
    }

    public static string TotalSize(IEnumerable<ShelfItem> items)
    {
        long total = 0;
        foreach (var i in items) total += i.SizeBytes ?? 0;
        return total > 0 ? AppPaths.FormatBytes(total) : "";
    }
}
