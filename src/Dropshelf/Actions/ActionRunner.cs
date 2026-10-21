using System.Windows;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.Shelf;

namespace Dropshelf.Actions;

/// <summary>Runs an action and applies its result to the shelf (renames, removals, outputs, messages).</summary>
public static class ActionRunner
{
    public static async Task<ActionResult> ExecuteAsync(ShelfAction action, ActionInput input, ActionValues values, CancellationToken ct = default)
    {
        ActionCatalog.MarkUsed(action);
        ActionResult result;
        try
        {
            result = await action.RunAsync(input, values, ct);
        }
        catch (OperationCanceledException)
        {
            result = ActionResult.Cancelled();
        }
        catch (Exception ex)
        {
            Log.Error($"Action {action.Id} failed", ex);
            result = ActionResult.Fail(ex.Message);
        }

        Apply(result, input);
        return result;
    }

    private static void Apply(ActionResult result, ActionInput input)
    {
        var shelf = input.Shelf;

        foreach (var (item, newPath) in result.RenamedItems)
        {
            item.Path = newPath;
            ThumbnailService.Load(item);
        }

        if (shelf is not null && result.RemovedItems.Count > 0) shelf.RemoveItems(result.RemovedItems);

        if (result.Outputs.Count > 0)
        {
            var outputs = result.Outputs.Select(ShelfItem.FromPath).ToList();
            switch (result.OutputMode)
            {
                case "shelf" when shelf is not null:
                    shelf.ReplaceItems(result.SourceItems ?? input.Items, outputs);
                    break;
                case "add" when shelf is not null:
                    shelf.AddItems(outputs);
                    break;
                case "explorer":
                    ShellOperations.Reveal(result.Outputs);
                    break;
                case "none":
                    break;
                default:
                    ShelfManager.Instance.CreateShelf(outputs, near: shelf);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            if (shelf is not null) shelf.ShowToast(result.Message!);
            else if (!result.Success) MessageBox.Show(result.Message, "Could not perform the action", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (shelf is null) return;
        if (result.Success && result.CloseShelf) shelf.CloseShelf();
        else if (result.RemovedItems.Count > 0 && shelf.AllItems.Count == 0) shelf.CloseShelf();
    }
}
