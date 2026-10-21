using System.Windows;
using System.Windows.Threading;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.Shelf;
using Dropshelf.UI;

namespace Dropshelf;

public partial class App : Application
{
    private SingleInstance? _instance;
    private DragGestureService? _gestures;
    private HotkeyService? _hotkeys;
    private FolderMonitorService? _folders;
    private TrayIcon? _tray;
    private ShelfManager? _shelves;
    private DispatcherTimer? _folderApplyTimer;

    public static new App Current => (App)Application.Current;
    public HotkeyService? Hotkeys => _hotkeys;
    public FolderMonitorService? Folders => _folders;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = SingleInstance.Acquire(e.Args);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);

        SettingsStore.Load();
        ThemeManager.Initialize();
        ShellIntegration.RefreshRegistrations();
        Log.Info($"Dropshelf {typeof(App).Assembly.GetName().Version} starting");

        _shelves = new ShelfManager();

        _gestures = new DragGestureService(Dispatcher);
        _gestures.Triggered += _shelves.OnGesture;
        _gestures.DragEnded += _shelves.OnDragEnded;
        _gestures.Start();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();

        _folders = new FolderMonitorService(Dispatcher);
        _folders.FilesDetected += _shelves.OnFolderFiles;
        _folders.Apply(SettingsStore.Current);

        _tray = new TrayIcon(_shelves);
        SettingsStore.Changed += OnSettingChanged;

        _instance.ArgumentsReceived += args => HandleArguments(args, fromSecondInstance: true);
        _instance.StartListening(Dispatcher);

        _shelves.RestorePinned();
        HandleArguments(e.Args, fromSecondInstance: false);

        if (!SettingsStore.Current.FirstRunCompleted && !e.Args.Contains("--background"))
        {
            new WelcomeWindow().Show();
        }
    }

    /// <summary>
    /// Command line: <c>--add &lt;paths&gt;</c> (Send To / context menu; bare paths work too),
    /// <c>--new-shelf</c>, <c>--clipboard</c>, <c>--settings</c>, <c>--background</c>.
    /// Launching again with no arguments opens Settings, as reopening Dropover does.
    /// </summary>
    private void HandleArguments(string[] args, bool fromSecondInstance)
    {
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (paths.Count > 0)
        {
            _shelves!.QueueInbound(paths);
            return;
        }

        if (args.Contains("--new-shelf")) _shelves!.NewShelf();
        else if (args.Contains("--clipboard")) _shelves!.NewShelfFromClipboard();
        else if (args.Contains("--settings") || (fromSecondInstance && args.Length == 0)) SettingsWindow.ShowSingleton();
    }

    private void OnHotkey(string id)
    {
        switch (id)
        {
            case "new-shelf": _shelves!.NewShelf(); break;
            case "new-clipboard-shelf": _shelves!.NewShelfFromClipboard(); break;
            case "reopen-last": _shelves!.RestorePrevious(); break;
            case "close-all": _shelves!.CloseAll(); break;
            case "toggle-dock-all": _shelves!.ToggleDockAll(); break;
        }
    }

    /// <summary>Registers enabled global shortcuts; returns ids that another app already uses.</summary>
    public IReadOnlyList<string> ApplyHotkeys() => _hotkeys?.Apply(SettingsStore.Current.Hotkeys) ?? [];

    private void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.ShakeEnabled):
            case nameof(AppSettings.ShakeSensitivity):
            case nameof(AppSettings.ActivationModifier):
                _gestures?.ApplySettings(SettingsStore.Current);
                break;
            case nameof(AppSettings.ShowTrayIcon):
                _tray?.SetVisible(SettingsStore.Current.ShowTrayIcon);
                break;
            case nameof(AppSettings.WatchedFolders):
            case nameof(AppSettings.ScreenshotShelfEnabled):
            case nameof(AppSettings.ScreenshotFolder):
                // Rule text changes per keystroke; restart the watchers once typing pauses.
                _folderApplyTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
                {
                    _folderApplyTimer!.Stop();
                    _folders?.Apply(SettingsStore.Current);
                }, Dispatcher);
                _folderApplyTimer.Stop();
                _folderApplyTimer.Start();
                break;
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    public void Quit()
    {
        _shelves?.BeginShutdown();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _shelves?.BeginShutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.Flush();
        _tray?.Dispose();
        _gestures?.Dispose();
        _hotkeys?.Dispose();
        _folders?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
