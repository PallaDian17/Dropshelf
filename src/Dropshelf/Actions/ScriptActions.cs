using System.Diagnostics;
using System.Text;
using System.Windows;
using Dropshelf.Core;

namespace Dropshelf.Actions;

/// <summary>
/// Custom Scripts: any PowerShell, batch, Python, JScript/VBScript or .exe placed in the Scripts
/// folder becomes a shelf action. Shelf file paths are passed as arguments and in DROPSHELF_FILES.
/// </summary>
public static class ScriptManager
{
    public static readonly string[] SupportedExtensions = [".ps1", ".bat", ".cmd", ".py", ".js", ".vbs", ".exe"];

    public static IReadOnlyList<string> ScriptFiles()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ScriptsDir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static ScriptConfig ConfigFor(string scriptPath)
    {
        var key = Path.GetFileName(scriptPath);
        var scripts = SettingsStore.Current.Scripts;
        if (!scripts.TryGetValue(key, out var config))
        {
            config = new ScriptConfig();
            scripts[key] = config;
        }
        return config;
    }

    public static string DisplayName(string scriptPath)
        => ConfigFor(scriptPath).DisplayName is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(scriptPath);

    /// <summary>Copies a script into the Scripts folder; returns its new path.</summary>
    public static string Import(string source)
    {
        var target = AppPaths.UniquePath(AppPaths.ScriptsDir, Path.GetFileName(source));
        File.Copy(source, target);
        return target;
    }

    public sealed record RunResult(int ExitCode, string Output, string Error);

    public static async Task<RunResult> RunAsync(string script, IReadOnlyList<string> files, CancellationToken ct)
    {
        var psi = BuildStartInfo(script, files);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.WorkingDirectory = files.Count > 0 && Path.GetDirectoryName(files[0]) is { Length: > 0 } dir ? dir : AppPaths.ScriptsDir;
        psi.Environment["DROPSHELF_FILES"] = string.Join("\n", files);
        psi.Environment["DROPSHELF_COUNT"] = files.Count.ToString();

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The script could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited
            }
            throw;
        }

        var result = new RunResult(process.ExitCode, (await stdout).TrimEnd(), (await stderr).TrimEnd());
        LogRun(script, files, result);
        return result;
    }

    private static ProcessStartInfo BuildStartInfo(string script, IReadOnlyList<string> files)
    {
        var ext = Path.GetExtension(script).ToLowerInvariant();
        ProcessStartInfo psi;
        switch (ext)
        {
            case ".ps1":
                psi = new ProcessStartInfo("powershell.exe");
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
                break;
            case ".bat" or ".cmd":
                // cmd.exe needs the whole command quoted once more; /s makes that rule predictable.
                psi = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/d /s /c \"\"{script}\" {string.Join(" ", files.Select(f => $"\"{f}\""))}\"",
                };
                return psi;
            case ".py":
                psi = new ProcessStartInfo(FindOnPath("py.exe") is not null ? "py.exe" : "python.exe");
                psi.ArgumentList.Add(script);
                break;
            case ".js" or ".vbs":
                psi = new ProcessStartInfo("cscript.exe");
                psi.ArgumentList.Add("//nologo");
                psi.ArgumentList.Add(script);
                break;
            default:
                psi = new ProcessStartInfo(script);
                break;
        }
        foreach (var f in files) psi.ArgumentList.Add(f);
        return psi;
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim(), exe)).FirstOrDefault(File.Exists);
    }

    public static string LogFile => Path.Combine(AppPaths.LogsDir, "scripts.log");

    private static void LogRun(string script, IReadOnlyList<string> files, RunResult r)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Path.GetFileName(script)} (exit {r.ExitCode})");
            foreach (var f in files) sb.AppendLine($"  arg: {f}");
            if (r.Output.Length > 0) sb.AppendLine("stdout:").AppendLine(r.Output);
            if (r.Error.Length > 0) sb.AppendLine("stderr:").AppendLine(r.Error);
            File.AppendAllText(LogFile, sb.ToString());
        }
        catch
        {
            // best effort
        }
    }
}

public sealed class ScriptAction(string scriptPath) : ShelfAction
{
    public string ScriptPath { get; } = scriptPath;
    public override string Id => "script:" + Path.GetFileName(ScriptPath);
    public override string Title => ScriptManager.DisplayName(ScriptPath);
    public override string Glyph => "";
    public override ActionCategory Category => ActionCategory.Scripts;
    public override string ProgressText => "Running script…";
    public override string[] Keywords => ["script", Path.GetFileNameWithoutExtension(ScriptPath)];

    public override async Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
    {
        if (!File.Exists(ScriptPath)) return ActionResult.Fail("The script was not found. It may have been deleted or renamed.");
        var config = ScriptManager.ConfigFor(ScriptPath);
        var result = await ScriptManager.RunAsync(ScriptPath, input.AllPaths(), ct);
        if (result.ExitCode != 0)
        {
            var detail = result.Error.Length > 0 ? result.Error : result.Output;
            if (detail.Length > 300) detail = detail[..300] + "…";
            return ActionResult.Fail($"The script failed (exit code {result.ExitCode}). {detail}".Trim());
        }

        if (config.OutputMode == ScriptOutputMode.Clipboard && result.Output.Length > 0)
        {
            Clipboard.SetText(result.Output);
            return new ActionResult { Message = "Script output copied to clipboard", CloseShelf = config.CloseShelf };
        }
        return new ActionResult { Message = "Script finished", CloseShelf = config.CloseShelf };
    }
}

/// <summary>A user preset: a built-in action with saved values that runs without a panel.</summary>
public sealed class CustomPresetAction(CustomActionDefinition definition, ShelfAction template) : ShelfAction
{
    public CustomActionDefinition Definition { get; } = definition;
    public ShelfAction Template { get; } = template;

    public override string Id => "custom:" + Definition.Id;
    public override string Title => Definition.Name;
    public override string Glyph => Template.Glyph;
    public override ActionCategory Category => ActionCategory.Custom;
    public override string ProgressText => Template.ProgressText;
    public override string[] Keywords => Template.Keywords;
    public override bool KeepsShelfOpen => Template.KeepsShelfOpen;
    public override string MenuTitle(ActionInput input) => Definition.Name;

    public override bool IsAvailable(ActionInput input) => Template.IsAvailable(input);

    public ActionValues ValuesFor(ActionInput input)
        => ActionValues.Defaults(Template.GetFields(input), Definition.Values);

    public override string? Validate(ActionInput input, ActionValues values) => Template.Validate(input, ValuesFor(input));

    public override Task<ActionResult> RunAsync(ActionInput input, ActionValues values, CancellationToken ct)
        => Template.RunAsync(input, ValuesFor(input), ct);
}
