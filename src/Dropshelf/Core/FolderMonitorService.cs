using System.Windows.Threading;
using Dropshelf.Native;

namespace Dropshelf.Core;

public enum FolderStatus { Active, Missing, Stopped }

/// <summary>
/// Dropover's Folder Monitoring and Screenshot shelf: new files that match a folder's rules
/// are shown in a shelf.
/// </summary>
public sealed class FolderMonitorService : IDisposable
{
    private const string ScreenshotWatchId = "__screenshots";

    private readonly Dispatcher _ui;
    private readonly Dictionary<string, Watcher> _watchers = new();

    public FolderMonitorService(Dispatcher ui) => _ui = ui;

    /// <summary>Raised on the UI thread with a batch of new files for one folder.</summary>
    public event Action<FileBatch>? FilesDetected;

    public sealed record FileBatch(string FolderName, IReadOnlyList<string> Files, bool NewShelfPerFile, bool IsScreenshot);

    public FolderStatus StatusOf(WatchedFolder folder)
    {
        if (!folder.Enabled) return FolderStatus.Stopped;
        if (!Directory.Exists(folder.Path)) return FolderStatus.Missing;
        return _watchers.ContainsKey(folder.Id) ? FolderStatus.Active : FolderStatus.Stopped;
    }

    public void Apply(AppSettings settings)
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();

        foreach (var folder in settings.WatchedFolders.Where(f => f.Enabled))
        {
            if (!Directory.Exists(folder.Path)) continue;
            var snapshot = Clone(folder);
            Start(snapshot.Id, snapshot.Path, snapshot.IncludeSubfolders, snapshot.DelayProcessing ? 3000 : 600,
                path => Matches(snapshot, path),
                files => new FileBatch(snapshot.DisplayName, files, snapshot.NewShelfPerFile, false));
        }

        if (settings.ScreenshotShelfEnabled)
        {
            var dir = string.IsNullOrWhiteSpace(settings.ScreenshotFolder)
                ? ShellOperations.DefaultScreenshotsFolder()
                : settings.ScreenshotFolder!;
            try
            {
                Directory.CreateDirectory(dir);
                Start(ScreenshotWatchId, dir, false, 700, FileKinds.IsImage,
                    files => new FileBatch("Screenshots", files, true, true));
            }
            catch (Exception ex)
            {
                Log.Warn($"Screenshot folder unavailable: {ex.Message}");
            }
        }
    }

    private void Start(string id, string path, bool recursive, int delayMs, Func<string, bool> filter, Func<List<string>, FileBatch> makeBatch)
    {
        try
        {
            var watcher = new Watcher(path, recursive, delayMs, filter, files =>
                _ui.BeginInvoke(() => FilesDetected?.Invoke(makeBatch(files))));
            _watchers[id] = watcher;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch {path}: {ex.Message}");
        }
    }

    private static WatchedFolder Clone(WatchedFolder f)
    {
        var copy = new WatchedFolder
        {
            Id = f.Id,
            Path = f.Path,
            IncludeSubfolders = f.IncludeSubfolders,
            NewShelfPerFile = f.NewShelfPerFile,
            DelayProcessing = f.DelayProcessing,
            MatchAll = f.MatchAll,
            CaseSensitive = f.CaseSensitive,
        };
        foreach (var r in f.Rules) copy.Rules.Add(new FolderRule { Criterion = r.Criterion, Operator = r.Operator, Value = r.Value });
        return copy;
    }

    public static bool Matches(WatchedFolder folder, string path)
    {
        if (folder.Rules.Count == 0) return true;
        var results = folder.Rules.Select(r => Matches(r, path, folder.CaseSensitive));
        return folder.MatchAll ? results.All(x => x) : results.Any(x => x);
    }

    private static bool Matches(FolderRule rule, string path, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (rule.Criterion)
        {
            case RuleCriterion.Kind:
            {
                var isKind = FileKinds.MatchesRuleKind(path, rule.Value);
                return rule.Operator == RuleOperator.IsNot ? !isKind : isKind;
            }
            case RuleCriterion.Size:
            {
                if (!double.TryParse(rule.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var mb)) return false;
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    return false;
                }
                var limit = (long)(mb * 1_000_000);
                return rule.Operator switch
                {
                    RuleOperator.GreaterThan => size > limit,
                    RuleOperator.LessThan => size < limit,
                    RuleOperator.Is => size == limit,
                    RuleOperator.IsNot => size != limit,
                    _ => false,
                };
            }
            default:
            {
                var subject = rule.Criterion == RuleCriterion.Extension
                    ? Path.GetExtension(path).TrimStart('.')
                    : Path.GetFileName(path);
                var value = rule.Criterion == RuleCriterion.Extension ? rule.Value.Trim().TrimStart('.') : rule.Value;
                return rule.Operator switch
                {
                    RuleOperator.Is => string.Equals(subject, value, comparison),
                    RuleOperator.IsNot => !string.Equals(subject, value, comparison),
                    RuleOperator.Contains => subject.Contains(value, comparison),
                    RuleOperator.DoesNotContain => !subject.Contains(value, comparison),
                    RuleOperator.BeginsWith => subject.StartsWith(value, comparison),
                    RuleOperator.EndsWith => subject.EndsWith(value, comparison),
                    _ => false,
                };
            }
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }

    private sealed class Watcher : IDisposable
    {
        private static readonly string[] PartialSuffixes = [".tmp", ".crdownload", ".part", ".partial", ".download", ".opdownload", "~"];

        private readonly FileSystemWatcher _fsw;
        private readonly Func<string, bool> _filter;
        private readonly Action<List<string>> _emit;
        private readonly int _delayMs;
        private readonly object _gate = new();
        private readonly List<string> _pending = new();
        private readonly Timer _timer;

        public Watcher(string path, bool recursive, int delayMs, Func<string, bool> filter, Action<List<string>> emit)
        {
            _filter = filter;
            _emit = emit;
            _delayMs = delayMs;
            _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _fsw = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Created += (_, e) => Consider(e.FullPath);
            _fsw.Renamed += (_, e) => Consider(e.FullPath); // browsers rename "x.crdownload" to "x" when done
            _fsw.EnableRaisingEvents = true;
        }

        private void Consider(string path)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("~$") || name.StartsWith('.') || PartialSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                return;

            Task.Run(async () =>
            {
                if (!await WaitUntilReadable(path)) return;
                try
                {
                    if (!_filter(path)) return;
                }
                catch
                {
                    return;
                }
                lock (_gate)
                {
                    if (!_pending.Contains(path, StringComparer.OrdinalIgnoreCase)) _pending.Add(path);
                }
                _timer.Change(_delayMs, Timeout.Infinite);
            });
        }

        private static async Task<bool> WaitUntilReadable(string path)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (Directory.Exists(path)) return true;
                if (!File.Exists(path)) return false;
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        return true;
                }
                catch (IOException)
                {
                    await Task.Delay(250);
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
            return false;
        }

        private void Flush()
        {
            List<string> batch;
            lock (_gate)
            {
                batch = _pending.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                _pending.Clear();
            }
            if (batch.Count > 0) _emit(batch);
        }

        public void Dispose()
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
            _timer.Dispose();
        }
    }
}
