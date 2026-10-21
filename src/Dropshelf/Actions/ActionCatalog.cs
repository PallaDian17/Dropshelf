using Dropshelf.Core;

namespace Dropshelf.Actions;

public static class ActionCatalog
{
    private static readonly List<string> Recent = [];

    public static IReadOnlyList<ShelfAction> BuiltIn { get; } =
    [
        new OpenAction(),
        new OpenWithDialogAction(),
        new OpenWithAppAction(),
        new RevealAction(),
        new PreviewAction(),
        new ShareAction(),
        new EmailAction(),
        new CopyFilesAction(),
        new CopyPathAction(),
        new CopyTextContentAction(),
        new RenameAction(),
        new BatchRenameAction(),
        new ZipAction(),
        new TransferAction(move: true),
        new TransferAction(move: false),
        new RecycleAction(),
        new PropertiesAction(),
        new ResizeImageAction(),
        new CompressImageAction(),
        new ConvertImageAction(),
        new StitchImagesAction(),
        new ImagesToPdfAction(),
        new RemoveMetadataAction(),
        new ExtractTextAction(),
        new SetWallpaperAction(),
        new AddFromClipboardAction(),
        new NewShelfAction(move: false),
        new NewShelfAction(move: true),
        new RemoveFromShelfAction(),
        new ClearShelfAction(),
        new DockAction(),
    ];

    public static IEnumerable<ShelfAction> Scripts() => ScriptManager.ScriptFiles().Select(f => new ScriptAction(f));

    public static IEnumerable<ShelfAction> Custom()
    {
        foreach (var def in SettingsStore.Current.CustomActions)
        {
            if (BuiltIn.FirstOrDefault(a => a.Id == def.TemplateId) is { } template)
                yield return new CustomPresetAction(def, template);
        }
    }

    public static IEnumerable<ShelfAction> All() => BuiltIn.Concat(Custom()).Concat(Scripts());

    public static ShelfAction? Find(string id) => All().FirstOrDefault(a => a.Id == id);

    /// <summary>Templates offered when creating a Custom Action.</summary>
    public static IEnumerable<ShelfAction> PresetTemplates() => BuiltIn.Where(a => a.SupportsPreset);

    /// <summary>Actions that can be an Instant Action tile (they run without asking anything).</summary>
    public static IEnumerable<ShelfAction> InstantCandidates()
        => All().Where(a => a.CanRunUnattended && (!a.RequiresConfiguration || a is CustomPresetAction));

    public static string CategoryTitle(ActionCategory c) => c switch
    {
        ActionCategory.General => "General Actions",
        ActionCategory.Share => "Share Actions",
        ActionCategory.Image => "Image Actions",
        ActionCategory.Text => "Text Actions",
        ActionCategory.Shelf => "Shelf",
        ActionCategory.Scripts => "Custom Scripts",
        ActionCategory.Custom => "Custom Actions",
        _ => c.ToString(),
    };

    public static void MarkUsed(ShelfAction action)
    {
        Recent.Remove(action.Id);
        Recent.Insert(0, action.Id);
        if (Recent.Count > 5) Recent.RemoveAt(Recent.Count - 1);
    }

    public static IEnumerable<ShelfAction> RecentActions(ActionInput input)
        => Recent.Select(Find).Where(a => a is not null && a.IsAvailable(input)).Cast<ShelfAction>();

    /// <summary>Command bar search over titles and keywords.</summary>
    public static IEnumerable<ShelfAction> Search(string query, ActionInput input)
    {
        var available = All().Where(a => a.IsAvailable(input)).ToList();
        if (string.IsNullOrWhiteSpace(query)) return available;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return available
            .Select(a => (Action: a, Score: Score(a, input, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Action);
    }

    private static int Score(ShelfAction a, ActionInput input, string[] terms)
    {
        var title = a.MenuTitle(input);
        var score = 0;
        foreach (var t in terms)
        {
            if (title.StartsWith(t, StringComparison.OrdinalIgnoreCase)) score += 6;
            else if (title.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 4;
            else if (a.Keywords.Any(k => k.Contains(t, StringComparison.OrdinalIgnoreCase))) score += 2;
            else return 0;
        }
        return score;
    }
}
