using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Dropshelf.Core;

/// <summary>Loads settings once, saves them (debounced) whenever something changes.</summary>
public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static DispatcherTimer? _saveTimer;

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Raised on the UI thread with the name of the changed setting.</summary>
    public static event Action<string>? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable, using defaults", ex);
            Current = new AppSettings();
        }

        Current.EnsureDefaults();
        Track(Current);
        Current.IgnoredApplications.CollectionChanged += OnCollectionChanged;
        Current.InstantActions.CollectionChanged += OnCollectionChanged;
        Current.FavoriteActions.CollectionChanged += OnCollectionChanged;
        Current.CustomActions.CollectionChanged += OnCollectionChanged;
        Current.WatchedFolders.CollectionChanged += OnWatchedFoldersChanged;
        foreach (var folder in Current.WatchedFolders) TrackFolder(folder);
        foreach (var action in Current.CustomActions) Track(action);
    }

    public static void Track(INotifyPropertyChanged source) => source.PropertyChanged += OnPropertyChanged;

    public static void NotifyChanged(string name)
    {
        ScheduleSave();
        Changed?.Invoke(name);
    }

    private static void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = sender is AppSettings ? e.PropertyName ?? "" : sender?.GetType().Name ?? "";
        NotifyChanged(name);
    }

    private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>()) Track(item);
        }

        var name = ReferenceEquals(sender, Current.IgnoredApplications) ? nameof(AppSettings.IgnoredApplications)
            : ReferenceEquals(sender, Current.InstantActions) ? nameof(AppSettings.InstantActions)
            : ReferenceEquals(sender, Current.FavoriteActions) ? nameof(AppSettings.FavoriteActions)
            : ReferenceEquals(sender, Current.CustomActions) ? nameof(AppSettings.CustomActions)
            : "Collection";
        NotifyChanged(name);
    }

    private static void OnWatchedFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var folder in e.NewItems.OfType<WatchedFolder>()) TrackFolder(folder);
        }

        NotifyChanged(nameof(AppSettings.WatchedFolders));
    }

    private static void TrackFolder(WatchedFolder folder)
    {
        folder.PropertyChanged += (_, _) => NotifyChanged(nameof(AppSettings.WatchedFolders));
        folder.Rules.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (var rule in e.NewItems.OfType<FolderRule>())
                    rule.PropertyChanged += (_, _) => NotifyChanged(nameof(AppSettings.WatchedFolders));
            }

            NotifyChanged(nameof(AppSettings.WatchedFolders));
        };
        foreach (var rule in folder.Rules)
            rule.PropertyChanged += (_, _) => NotifyChanged(nameof(AppSettings.WatchedFolders));
    }

    public static void ScheduleSave()
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher)
        {
            Save();
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(ScheduleSave);
            return;
        }

        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Save();
        }, dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    /// <summary>Writes any pending change immediately (used on exit).</summary>
    public static void Flush()
    {
        if (_saveTimer?.IsEnabled == true)
        {
            _saveTimer.Stop();
            Save();
        }
    }
}
