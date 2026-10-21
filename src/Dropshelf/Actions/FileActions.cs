using System.IO.Compression;
using System.Text.RegularExpressions;
using Dropshelf.Core;
using Dropshelf.Native;

namespace Dropshelf.Actions;

public sealed class RenameAction : ShelfAction
{
    public override string Id => "rename";
    public override string Title => "Rename";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["filename", "change"];
    public override bool CanRunUnattended => false;

    public override bool IsAvailable(ActionInput input)
        => input.Items.Count == 1 && input.Items[0].IsFileBased && input.Items[0].Exists;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input)
        => [new TextField("name", "Name", input.Items[0].DisplayName)];

    public override string? Validate(ActionInput input, ActionValues values)
    {
        var name = values.GetString("name").Trim();
        if (AppPaths.ValidateFileName(name) is { } error) return error;
        var target = Path.Combine(Path.GetDirectoryName(input.Items[0].Path!)!, name);
        var isSameFile = string.Equals(target, input.Items[0].Path, StringComparison.OrdinalIgnoreCase);
        if (!isSameFile && (File.Exists(target) || Directory.Exists(target)))
            return "A file with this name already exists. Choose a different name.";
        return null;
    }

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var item = input.Items[0];
        var target = Path.Combine(Path.GetDirectoryName(item.Path!)!, values.GetString("name").Trim());
        if (string.Equals(target, item.Path, StringComparison.Ordinal)) return Task.FromResult(ActionResult.Ok());

        if (item.Kind == ShelfItemKind.Folder) Directory.Move(item.Path!, target);
        else File.Move(item.Path!, target);
        return Task.FromResult(new ActionResult { RenamedItems = new Dictionary<ShelfItem, string> { [item] = target } });
    }
}

public sealed class BatchRenameAction : ShelfAction
{
    public override string Id => "batch-rename";
    public override string Title => "Rename Files";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string? Description => "Rename multiple files using find-and-replace or a custom naming format.";
    public override string[] Keywords => ["rename", "filename", "batch", "counter"];
    public override bool SupportsPreset => true;

    public override bool IsAvailable(ActionInput input) => input.Items.Count > 1 && input.Items.All(i => i.IsFileBased);

    public override IReadOnlyList<ActionField> GetFields(ActionInput input)
    {
        bool Replace(ActionValues v) => v.GetString("mode") == "replace";
        bool Custom(ActionValues v) => v.GetString("mode") == "custom";
        return
        [
            new ChoiceField("mode", "Rename option", [("replace", "Replace Text"), ("custom", "Custom Format")], "replace"),
            new TextField("find", "Find", "") { VisibleWhen = Replace },
            new TextField("replace", "Replace with", "") { VisibleWhen = Replace },
            new ToggleField("case", "Case sensitive", false) { VisibleWhen = Replace },
            new TextField("format", "Format", "Name_#counter", "new_#counter")
            {
                VisibleWhen = Custom,
                Help = "Available variables: #counter, #date and #original",
            },
            new NumberField("start", "Counter start value", 1, 0, 1_000_000) { VisibleWhen = Custom },
            new ToggleField("close", "Close shelf after renaming", false),
            new PreviewField("preview", "Preview", v => Plan(input.Items, v).Select(p => (p.Item.DisplayName, Path.GetFileName(p.NewPath))).ToList()),
        ];
    }

    public override string? Validate(ActionInput input, ActionValues values)
    {
        if (values.GetString("mode") == "custom")
        {
            var format = values.GetString("format");
            if (string.IsNullOrWhiteSpace(format)) return "Field cannot be empty.";
            var bad = format.FirstOrDefault(c => Path.GetInvalidFileNameChars().Contains(c));
            if (bad != default) return $"The following character is not allowed in the custom format: \"{bad}\"";
        }
        else if (string.IsNullOrEmpty(values.GetString("find")))
        {
            return "Enter the text to find.";
        }
        return null;
    }

    private static List<(ShelfItem Item, string NewPath)> Plan(IReadOnlyList<ShelfItem> items, ActionValues v)
    {
        var plan = new List<(ShelfItem, string)>();
        var counter = v.GetInt("start", 1);
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var item in items.Where(i => i.IsFileBased && i.Path is not null))
        {
            var dir = Path.GetDirectoryName(item.Path!)!;
            var ext = item.Kind == ShelfItemKind.Folder ? "" : Path.GetExtension(item.Path!);
            var stem = item.Kind == ShelfItemKind.Folder ? Path.GetFileName(item.Path!) : Path.GetFileNameWithoutExtension(item.Path!);
            string newStem;
            if (v.GetString("mode") == "custom")
            {
                newStem = v.GetString("format")
                    .Replace("#counter", counter.ToString(), StringComparison.OrdinalIgnoreCase)
                    .Replace("#date", date, StringComparison.OrdinalIgnoreCase)
                    .Replace("#original", stem, StringComparison.OrdinalIgnoreCase);
                counter++;
            }
            else
            {
                var find = v.GetString("find");
                newStem = find.Length == 0 ? stem : Regex.Replace(stem, Regex.Escape(find), v.GetString("replace").Replace("$", "$$"),
                    v.GetBool("case") ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            plan.Add((item, Path.Combine(dir, newStem + ext)));
        }
        return plan;
    }

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var renamed = new Dictionary<ShelfItem, string>();
        var failures = 0;
        foreach (var (item, target) in Plan(input.Items, values))
        {
            if (string.Equals(item.Path, target, StringComparison.Ordinal)) continue;
            try
            {
                var caseOnly = string.Equals(item.Path, target, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) throw new IOException("exists");
                if (item.Kind == ShelfItemKind.Folder) Directory.Move(item.Path!, target);
                else File.Move(item.Path!, target);
                renamed[item] = target;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warn($"Rename failed for {item.Path}: {ex.Message}");
            }
        }

        if (renamed.Count == 0 && failures > 0)
            return Task.FromResult(ActionResult.Fail("Failed to rename the files. Make sure they are accessible and the new names are free."));
        return Task.FromResult(new ActionResult
        {
            RenamedItems = renamed,
            Message = failures > 0 ? "Some files could not be renamed." : $"Renamed {renamed.Count} files",
            CloseShelf = values.GetBool("close"),
        });
    }
}

public sealed class ZipAction : ShelfAction
{
    public override string Id => "zip";
    public override string Title => "Create ZIP Archive";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string ProgressText => "Compressing…";
    public override string? Description => "Create a ZIP archive in the specified destination folder.";
    public override string[] Keywords => ["archive", "compress", "zip"];
    public override bool SupportsPreset => true;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input)
    {
        var name = input.Items.Count == 1 ? Path.GetFileNameWithoutExtension(input.Items[0].DisplayName) : "Archive";
        return [new TextField("name", "Filename", name), CommonFields.OutputIn("shelf"), CommonFields.SaveIn(false)];
    }

    public override string? Validate(ActionInput input, ActionValues values)
    {
        var name = values.GetString("name").Trim();
        if (name.Length == 0) return null; // default name is used
        return AppPaths.ValidateFileName(name);
    }

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var paths = input.AllPaths();
        var name = values.GetString("name").Trim();
        if (name.Length == 0) name = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0]) : "Archive";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
        var target = AppPaths.UniquePath(OutputPaths.Folder(input, values), name);

        await Task.Run(() =>
        {
            var temp = OutputPaths.TempFileFor(target);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                    {
                        var root = Path.GetFileName(path.TrimEnd('\\'));
                        var dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).ToList();
                        if (dirs.Count == 0 && !Directory.EnumerateFiles(path).Any()) zip.CreateEntry(root + "/");
                        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            ct.ThrowIfCancellationRequested();
                            var entry = Path.Combine(root, Path.GetRelativePath(path, file)).Replace('\\', '/');
                            zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
                        }
                    }
                    else if (File.Exists(path))
                    {
                        zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
                    }
                }
            }
            File.Move(temp, target);
        }, ct);

        return new ActionResult
        {
            Message = "ZIP archive created",
            Outputs = [target],
            OutputMode = values.GetString("output", "shelf"),
        };
    }
}

public sealed class TransferAction(bool move) : ShelfAction
{
    public override string Id => move ? "move-to" : "copy-to";
    public override string Title => move ? "Move to Folder" : "Copy to Folder";
    public override string Glyph => move ? "" : "";
    public override ActionCategory Category => ActionCategory.General;
    public override string ProgressText => move ? "Moving…" : "Copying…";
    public override string? Description => move ? "Move files to the specified destination." : "Copy files to the specified destination.";
    public override string[] Keywords => ["destination", "folder", move ? "move" : "copy"];
    public override bool RequiresConfiguration => true;
    public override bool SupportsPreset => true;

    public override bool IsAvailable(ActionInput input) => input.Items.Count > 0;

    public override IReadOnlyList<ActionField> GetFields(ActionInput input)
        => [new FolderField("dest", "Destination", AllowSameAsOriginal: false)];

    public override string? Validate(ActionInput input, ActionValues values)
        => string.IsNullOrWhiteSpace(values.GetString("dest")) ? "Please select a destination and try again." : null;

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var dest = Environment.ExpandEnvironmentVariables(values.GetString("dest"));
        Directory.CreateDirectory(dest);
        var items = input.Items.ToList();
        var paths = items.Select(i => i.GetOrCreateFilePath()).ToList();
        var owner = input.Owner;
        var ok = await StaTask.Run(() => move ? ShellOperations.Move(paths, dest, owner) : ShellOperations.Copy(paths, dest, owner));

        var folderName = Path.GetFileName(dest.TrimEnd('\\'));
        if (!move)
            return ok ? ActionResult.Ok($"Copied to {folderName}") : ActionResult.Fail("Some files could not be copied.");

        var renamed = new Dictionary<ShelfItem, string>();
        foreach (var item in items)
        {
            var target = Path.Combine(dest, Path.GetFileName(item.Path!));
            if (File.Exists(target) || Directory.Exists(target)) renamed[item] = target;
        }
        return new ActionResult
        {
            Success = ok || renamed.Count > 0,
            Message = ok ? $"Moved to {folderName}" : "Some files could not be moved.",
            RenamedItems = renamed,
        };
    }
}

public sealed class RecycleAction : ShelfAction
{
    public override string Id => "recycle";
    public override string Title => "Move to Recycle Bin";
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.General;
    public override string[] Keywords => ["delete", "trash", "remove"];

    public override bool IsAvailable(ActionInput input) => input.FileItems.Any();

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        var items = input.FileItems.ToList();
        var owner = input.Owner;
        await StaTask.Run(() => ShellOperations.Recycle(items.Select(i => i.Path!), owner));
        var gone = items.Where(i => !i.Exists).ToList();
        return gone.Count == 0
            ? ActionResult.Fail("The files could not be moved to the Recycle Bin.")
            : new ActionResult { RemovedItems = gone, Message = $"Moved {gone.Count} to the Recycle Bin" };
    }
}
