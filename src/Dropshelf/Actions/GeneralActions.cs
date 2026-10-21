using System.Diagnostics;
using System.Windows;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.Shelf;

namespace Dropshelf.Actions;

public sealed class OpenAction : ShelfAction
{
    public override string Id => "open";
    public override string Title => "Open";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["launch", "run"];

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var paths = input.AllPaths();
        if (paths.Count > 5 && MessageBox.Show($"You are about to open {paths.Count} files. This might open several apps at once. Continue?",
                "Open files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return Task.FromResult(ActionResult.Cancelled());

        foreach (var item in input.Items)
        {
            if (item.Kind == ShelfItemKind.Url && item.Url is not null) ShellOperations.OpenUrl(item.Url);
            else ShellOperations.Open(item.GetOrCreateFilePath());
        }
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class OpenWithDialogAction : ShelfAction
{
    public override string Id => "open-with";
    public override string Title => "Choose Another App…";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override bool CanRunUnattended => false;

    public override bool IsAvailable(ActionInput input) => input.Items.Count == 1 && input.Items[0].Kind != ShelfItemKind.Folder;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        ShellOperations.ShowOpenWithDialog(input.Items[0].GetOrCreateFilePath(), input.Owner);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class OpenWithAppAction : ShelfAction
{
    public override string Id => "open-with-app";
    public override string Title => "Open with Application";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string? Description => "Opens files with the specified application.";
    public override bool RequiresConfiguration => true;
    public override bool SupportsPreset => true;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input) => [new AppField("app", "Application")];

    public override string? Validate(ActionInput input, ActionValues values)
    {
        var app = values.GetString("app");
        if (string.IsNullOrWhiteSpace(app)) return "No application has been specified.";
        return File.Exists(app) ? null : "The specified application cannot be found.";
    }

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var app = values.GetString("app");
        var psi = new ProcessStartInfo(app) { UseShellExecute = false };
        foreach (var p in input.AllPaths()) psi.ArgumentList.Add(p);
        Process.Start(psi);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class RevealAction : ShelfAction
{
    public override string Id => "reveal";
    public override string Title => "Show in Explorer";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["finder", "folder", "location"];

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var paths = input.AllPaths();
        var folders = paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (folders > 3 && MessageBox.Show($"This will open {folders} Explorer windows. Continue?", "Show in Explorer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return Task.FromResult(ActionResult.Cancelled());

        ShellOperations.Reveal(paths);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class PreviewAction : ShelfAction
{
    public override string Id => "preview";
    public override string Title => "Preview";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["quick look", "view"];
    public override bool CanRunUnattended => false;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        UI.PreviewWindow.ShowItems(input.Items, 0);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class ShareAction : ShelfAction
{
    public override string Id => "share";
    public override string Title => "Share…";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Share;
    public override string[] Keywords => ["send", "nearby", "airdrop", "public"];
    public override bool KeepsShelfOpen => true;
    public override string MenuTitle(ActionInput input) => "Share…";

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var text = input.Items.Count == 1 ? input.Items[0].Text ?? input.Items[0].Url : null;
        var paths = input.Items.Count == 1 && input.Items[0].Kind is ShelfItemKind.Text or ShelfItemKind.Url ? [] : input.AllPaths();
        var ok = await ShareService.ShareAsync(input.Owner, paths, text, ShelfNaming.DefaultName(input.Items));
        return ok ? ActionResult.Ok() : ActionResult.Fail("The Windows share sheet is not available.");
    }
}

public sealed class EmailAction : ShelfAction
{
    public override string Id => "email";
    public override string Title => "Email…";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Share;
    public override string[] Keywords => ["mail", "send", "outlook", "attach"];
    public override bool KeepsShelfOpen => true;
    public override string MenuTitle(ActionInput input) => "Email…";

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var ok = await MailService.ComposeAsync(input.AllPaths(), ShelfNaming.DefaultName(input.Items));
        if (ok) return ActionResult.Ok();

        // No MAPI mail client (e.g. the new Outlook): fall back to the share sheet, which offers Mail.
        var shared = await ShareService.ShareAsync(input.Owner, input.AllPaths(), null, ShelfNaming.DefaultName(input.Items));
        return shared ? ActionResult.Ok() : ActionResult.Fail("No e-mail app is set up to receive attachments.");
    }
}

public sealed class CopyFilesAction : ShelfAction
{
    public override string Id => "copy-files";
    public override string Title => "Copy";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["clipboard", "pasteboard"];
    public override string MenuTitle(ActionInput input) => $"Copy {input.CountLabel}";

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var data = ItemImporter.ToDataObject(input.Items);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(5))); // copy
        Clipboard.SetDataObject(data, true);
        return Task.FromResult(ActionResult.Ok("Copied to clipboard"));
    }
}

public sealed class CopyPathAction : ShelfAction
{
    public override string Id => "copy-path";
    public override string Title => "Copy Path";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["clipboard", "location", "filename"];

    public override bool IsAvailable(ActionInput input) => input.Items.Any(i => i.IsFileBased);

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var paths = input.Items.Where(i => i.IsFileBased && i.Path is not null).Select(i => i.Path!).ToList();
        Clipboard.SetText(string.Join(Environment.NewLine, paths));
        return Task.FromResult(ActionResult.Ok(paths.Count == 1 ? "Copied path to clipboard" : $"Copied {paths.Count} paths to clipboard"));
    }
}

public sealed class CopyTextContentAction : ShelfAction
{
    public override string Id => "copy-text";
    public override string Title => "Copy Text Content";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Text;
    public override string[] Keywords => ["clipboard", "text"];

    public override bool IsAvailable(ActionInput input) => input.Items.Count > 0 && input.Items.All(i =>
        i.Kind is ShelfItemKind.Text or ShelfItemKind.Url || (i.Kind == ShelfItemKind.File && i.Path is not null && FileKinds.IsReadableText(i.Path)));

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var item in input.Items)
        {
            if (item.Kind == ShelfItemKind.Text) parts.Add(item.Text ?? "");
            else if (item.Kind == ShelfItemKind.Url) parts.Add(item.Url ?? "");
            else if (item.Path is not null && new FileInfo(item.Path).Length < 20_000_000) parts.Add(await File.ReadAllTextAsync(item.Path, ct));
        }
        Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, parts));
        return ActionResult.Ok("Content has been copied to your clipboard");
    }
}

public sealed class AddFromClipboardAction : ShelfAction
{
    public override string Id => "add-clipboard";
    public override string Title => "Add From Clipboard";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Shelf;
    public override string[] Keywords => ["paste", "pasteboard"];

    public override bool IsAvailable(ActionInput input) => input.Shelf is not null;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var items = ItemImporter.FromClipboard();
        if (items.Count == 0) return Task.FromResult(ActionResult.Fail("The clipboard is empty."));
        input.Shelf!.AddItems(items);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class PropertiesAction : ShelfAction
{
    public override string Id => "properties";
    public override string Title => "Properties";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["info", "get info", "details"];
    public override bool CanRunUnattended => false;

    public override bool IsAvailable(ActionInput input) => input.Items.Any(i => i.IsFileBased);

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var paths = input.FileItems.Select(i => i.Path!).ToList();
        if (paths.Count > 3 && MessageBox.Show($"This will open {paths.Count} Properties windows. Continue?", "Properties",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return Task.FromResult(ActionResult.Cancelled());
        foreach (var p in paths) ShellOperations.ShowProperties(p, input.Owner);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class NewShelfAction(bool move) : ShelfAction
{
    public override string Id => move ? "new-shelf-move" : "new-shelf-copy";
    public override string Title => move ? "Move to New Shelf" : "Show in New Shelf";
    public override string Glyph => move ? "" : "";
    public override ActionCategory Category => ActionCategory.Shelf;

    public override bool IsAvailable(ActionInput input) => input.Items.Count > 0 && input.Shelf is not null;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var copies = input.Items.Select(i => i.IsFileBased ? ShelfItem.FromPath(i.Path!) : ShelfItem.FromSnapshot(i.ToSnapshot())!)
            .Where(i => i is not null).ToList();
        ShelfManager.Instance.CreateShelf(copies, near: input.Shelf);
        if (move) input.Shelf!.RemoveItems(input.Items);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class RemoveFromShelfAction : ShelfAction
{
    public override string Id => "remove";
    public override string Title => "Remove From Shelf";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Shelf;
    public override string[] Keywords => ["hide", "delete"];

    public override bool IsAvailable(ActionInput input) => input.Items.Count > 0 && input.Shelf is not null;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        input.Shelf!.RemoveItems(input.Items);
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class ClearShelfAction : ShelfAction
{
    public override string Id => "clear";
    public override string Title => "Clear Shelf";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Shelf;
    public override string[] Keywords => ["remove", "empty"];

    public override bool IsAvailable(ActionInput input) => input.Shelf is { AllItems.Count: > 0 };

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        input.Shelf!.ClearItems();
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class DockAction : ShelfAction
{
    public override string Id => "dock";
    public override string Title => "Dock to Edge";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Shelf;
    public override string[] Keywords => ["hide", "retract", "side"];

    public override bool IsAvailable(ActionInput input) => input.Shelf is not null;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        input.Shelf!.DockToNearestEdge();
        return Task.FromResult(ActionResult.Ok());
    }
}

public sealed class SetWallpaperAction : ShelfAction
{
    public override string Id => "wallpaper";
    public override string Title => "Set as Desktop Background";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Image;
    public override string[] Keywords => ["desktop picture", "wallpaper", "background"];

    public override bool IsAvailable(ActionInput input) => input.Items.Count == 1 && input.ImageItems.Count == 1;

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
        => Task.FromResult(ShellOperations.SetWallpaper(input.ImageItems[0].Path!)
            ? ActionResult.Ok("Desktop background changed")
            : ActionResult.Fail("Windows did not accept this image as the desktop background."));
}
