using Dropshelf.Core;
using Dropshelf.Native;

namespace Dropshelf.Actions;

/// <summary>Decides where generated files go, following the panel's "Create copy" and "Save in" fields.</summary>
public static class OutputPaths
{
    /// <summary>
    /// Path for the processed version of <paramref name="source"/>. With "Create copy" off the
    /// original is replaced (same name, or same stem when the extension changes).
    /// </summary>
    public static string For(ActionInput input, ActionValues values, ShelfItem source, string suffix, string? newExtension = null)
    {
        var sourcePath = source.Path!;
        var ext = newExtension ?? Path.GetExtension(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var sourceDir = Path.GetDirectoryName(sourcePath)!;

        if (!values.GetBool("copy", true))
        {
            return string.Equals(ext, Path.GetExtension(sourcePath), StringComparison.OrdinalIgnoreCase)
                ? sourcePath
                : AppPaths.UniquePath(sourceDir, stem + ext);
        }

        var dir = Folder(input, values, source);
        var sameFolder = string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(sourceDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var name = sameFolder ? $"{stem} ({suffix}){ext}" : stem + ext;
        return AppPaths.UniquePath(dir, name);
    }

    /// <summary>Destination folder from the "Save in" field (empty = next to the original).</summary>
    public static string Folder(ActionInput input, ActionValues values, ShelfItem? source = null)
    {
        var dest = values.GetString("dest");
        var dir = string.IsNullOrWhiteSpace(dest) ? input.DefaultOutputFolder(source) : Environment.ExpandEnvironmentVariables(dest);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Writes via a temp file so a failure never leaves a half-written original, then sends the
    /// replaced original to the Recycle Bin when the extension changed.
    /// </summary>
    public static void Commit(string tempFile, string target, string? originalToRecycle)
    {
        File.Move(tempFile, target, overwrite: true);
        if (originalToRecycle is not null
            && !string.Equals(originalToRecycle, target, StringComparison.OrdinalIgnoreCase)
            && File.Exists(originalToRecycle))
        {
            ShellOperations.Recycle([originalToRecycle], IntPtr.Zero);
        }
    }

    public static string TempFileFor(string target) => Path.Combine(AppPaths.TempDir, $"{Guid.NewGuid():N}{Path.GetExtension(target)}");

    /// <summary>The result shape shared by all actions that produce files.</summary>
    public static ActionResult Result(ActionValues values, IReadOnlyList<string> outputs, IReadOnlyList<string> failures,
        string successMessage, IReadOnlyList<ShelfItem>? sources = null)
    {
        if (outputs.Count == 0)
            return ActionResult.Fail(failures.Count > 0 ? failures[0] : "The action did not produce any files.");

        var message = failures.Count > 0 ? $"{successMessage} ({failures.Count} failed)" : successMessage;
        return new ActionResult
        {
            Message = message,
            Outputs = outputs,
            OutputMode = values.GetString("output", "shelf"),
            SourceItems = sources,
        };
    }
}
