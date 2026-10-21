using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Dropshelf.Core;

public enum ShakeSensitivity { Normal, High, Highest }

/// <summary>Key that, held while dragging, opens a shelf (Dropover: "Activate with modifier key").</summary>
public enum ActivationModifier { None, Alt, Control, Shift }

/// <summary>Where files sent from Send To, the context menu, or hotkeys end up.</summary>
public enum InboundBehavior { NewShelf, VisibleShelf, LastActiveShelf }

public enum ShelfLocation { NearCursor, Center, TopLeft, TopRight, BottomLeft, BottomRight }

public enum DoubleClickAction { Open, Preview, Reveal, Nothing }

public enum EdgeDoubleClickAction { Dock, Nothing }

/// <summary>Whether a shelf closes after its items are dragged out.</summary>
public enum OutboundDragBehavior { OnlyWhenMoved, Always, Never }

public enum ItemViewMode { Grid, List }

public enum ScriptOutputMode { Ignore, Clipboard }

public enum RuleCriterion { FileName, Extension, Kind, Size }

public enum RuleOperator { Is, IsNot, Contains, DoesNotContain, BeginsWith, EndsWith, GreaterThan, LessThan }

public sealed class AppSettings : ObservableObject
{
    // ---- General -------------------------------------------------------------------------
    private bool _launchAtLogin;
    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }

    private bool _showTrayIcon = true;
    public bool ShowTrayIcon { get => _showTrayIcon; set => Set(ref _showTrayIcon, value); }

    private bool _shelfTakesFocus;
    public bool ShelfTakesFocus { get => _shelfTakesFocus; set => Set(ref _shelfTakesFocus, value); }

    private bool _useColorLabels = true;
    public bool UseColorLabels { get => _useColorLabels; set => Set(ref _useColorLabels, value); }

    private bool _firstRunCompleted;
    public bool FirstRunCompleted { get => _firstRunCompleted; set => Set(ref _firstRunCompleted, value); }

    // ---- Shelf activation ---------------------------------------------------------------
    private bool _shakeEnabled = true;
    public bool ShakeEnabled { get => _shakeEnabled; set => Set(ref _shakeEnabled, value); }

    private ShakeSensitivity _shakeSensitivity = ShakeSensitivity.Normal;
    public ShakeSensitivity ShakeSensitivity { get => _shakeSensitivity; set => Set(ref _shakeSensitivity, value); }

    private ActivationModifier _activationModifier = ActivationModifier.None;
    public ActivationModifier ActivationModifier { get => _activationModifier; set => Set(ref _activationModifier, value); }

    public ObservableCollection<string> IgnoredApplications { get; set; } = [];

    private InboundBehavior _inboundBehavior = InboundBehavior.NewShelf;
    public InboundBehavior InboundBehavior { get => _inboundBehavior; set => Set(ref _inboundBehavior, value); }

    // ---- Shelf interaction --------------------------------------------------------------
    private ShelfLocation _defaultShelfLocation = ShelfLocation.NearCursor;
    public ShelfLocation DefaultShelfLocation { get => _defaultShelfLocation; set => Set(ref _defaultShelfLocation, value); }

    private bool _snapIntoPlace = true;
    public bool SnapIntoPlace { get => _snapIntoPlace; set => Set(ref _snapIntoPlace, value); }

    private bool _retractAfterFirstDrop;
    public bool RetractAfterFirstDrop { get => _retractAfterFirstDrop; set => Set(ref _retractAfterFirstDrop, value); }

    private DoubleClickAction _doubleClickFiles = DoubleClickAction.Open;
    public DoubleClickAction DoubleClickFiles { get => _doubleClickFiles; set => Set(ref _doubleClickFiles, value); }

    private EdgeDoubleClickAction _doubleClickEdge = EdgeDoubleClickAction.Dock;
    public EdgeDoubleClickAction DoubleClickEdge { get => _doubleClickEdge; set => Set(ref _doubleClickEdge, value); }

    private OutboundDragBehavior _closeOnDragOut = OutboundDragBehavior.OnlyWhenMoved;
    public OutboundDragBehavior CloseOnDragOut { get => _closeOnDragOut; set => Set(ref _closeOnDragOut, value); }

    private bool _alwaysCopyOnDragOut;
    public bool AlwaysCopyOnDragOut { get => _alwaysCopyOnDragOut; set => Set(ref _alwaysCopyOnDragOut, value); }

    private bool _convertTextToPlain;
    public bool ConvertTextToPlain { get => _convertTextToPlain; set => Set(ref _convertTextToPlain, value); }

    private bool _autoCloseDetailView = true;
    public bool AutoCloseDetailView { get => _autoCloseDetailView; set => Set(ref _autoCloseDetailView, value); }

    private ItemViewMode _viewMode = ItemViewMode.Grid;
    public ItemViewMode ViewMode { get => _viewMode; set => Set(ref _viewMode, value); }

    // ---- Keyboard shortcuts --------------------------------------------------------------
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new();

    // ---- Actions ---------------------------------------------------------------------------
    private bool _instantActionsEnabled;
    public bool InstantActionsEnabled { get => _instantActionsEnabled; set => Set(ref _instantActionsEnabled, value); }

    public ObservableCollection<string> InstantActions { get; set; } = ["share", "zip", "copy-files", "copy-path"];

    private bool _instantActionsCloseShelf = true;
    public bool InstantActionsCloseShelf { get => _instantActionsCloseShelf; set => Set(ref _instantActionsCloseShelf, value); }

    public ObservableCollection<string> FavoriteActions { get; set; } = ["resize", "zip", "copy-path"];

    public ObservableCollection<CustomActionDefinition> CustomActions { get; set; } = [];

    public Dictionary<string, ScriptConfig> Scripts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- Screenshot shelf & folder monitoring ----------------------------------------
    private bool _screenshotShelfEnabled;
    public bool ScreenshotShelfEnabled { get => _screenshotShelfEnabled; set => Set(ref _screenshotShelfEnabled, value); }

    private string? _screenshotFolder;
    public string? ScreenshotFolder { get => _screenshotFolder; set => Set(ref _screenshotFolder, value); }

    public ObservableCollection<WatchedFolder> WatchedFolders { get; set; } = [];

    // ---- Defaults -------------------------------------------------------------------------
    public static IReadOnlyList<HotkeyDescriptor> HotkeyDescriptors { get; } =
    [
        new("new-shelf", "New Shelf", new HotkeyBinding(true, ModifierKeys.Control | ModifierKeys.Alt, Key.D)),
        new("new-clipboard-shelf", "New Shelf From Clipboard", new HotkeyBinding(true, ModifierKeys.Control | ModifierKeys.Alt, Key.V)),
        new("reopen-last", "Reopen Last Closed Shelf", new HotkeyBinding(true, ModifierKeys.Control | ModifierKeys.Alt, Key.Z)),
        new("close-all", "Close All Shelves", new HotkeyBinding(false, ModifierKeys.Control | ModifierKeys.Alt, Key.W)),
        new("toggle-dock-all", "Toggle Docking for All Shelves", new HotkeyBinding(false, ModifierKeys.Control | ModifierKeys.Alt, Key.E)),
    ];

    /// <summary>Fills in anything missing after loading an older or partial settings file.</summary>
    public void EnsureDefaults()
    {
        foreach (var d in HotkeyDescriptors)
        {
            if (!Hotkeys.TryGetValue(d.Id, out var binding) || binding is null)
                Hotkeys[d.Id] = d.Default.Clone();
        }

        if (Scripts.Comparer != StringComparer.OrdinalIgnoreCase)
            Scripts = new Dictionary<string, ScriptConfig>(Scripts, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record HotkeyDescriptor(string Id, string Title, HotkeyBinding Default);

public sealed class HotkeyBinding
{
    public HotkeyBinding()
    {
    }

    public HotkeyBinding(bool enabled, ModifierKeys modifiers, Key key)
    {
        Enabled = enabled;
        Modifiers = modifiers;
        Key = key;
    }

    public bool Enabled { get; set; }
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }

    public HotkeyBinding Clone() => new(Enabled, Modifiers, Key);

    public bool SameCombination(HotkeyBinding other) => Modifiers == other.Modifiers && Key == other.Key;

    public override string ToString() => Format(Modifiers, Key);

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (key != Key.None) parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        _ => key.ToString(),
    };
}

public sealed class CustomActionDefinition : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = "Untitled Action";
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Id of the built-in action this preset runs (for example "resize").</summary>
    public string TemplateId { get; set; } = "";

    /// <summary>Saved field values, stored as invariant strings.</summary>
    public Dictionary<string, string> Values { get; set; } = new();
}

public sealed class ScriptConfig
{
    public string? DisplayName { get; set; }
    public ScriptOutputMode OutputMode { get; set; } = ScriptOutputMode.Ignore;
    public bool CloseShelf { get; set; } = true;
}

public sealed class WatchedFolder : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _path = "";
    public string Path { get => _path; set { if (Set(ref _path, value)) OnPropertyChanged(nameof(DisplayName)); } }

    public string DisplayName => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private bool _includeSubfolders;
    public bool IncludeSubfolders { get => _includeSubfolders; set => Set(ref _includeSubfolders, value); }

    private bool _newShelfPerFile;
    public bool NewShelfPerFile { get => _newShelfPerFile; set => Set(ref _newShelfPerFile, value); }

    private bool _delayProcessing = true;
    public bool DelayProcessing { get => _delayProcessing; set => Set(ref _delayProcessing, value); }

    private bool _matchAll = true;
    public bool MatchAll { get => _matchAll; set => Set(ref _matchAll, value); }

    private bool _caseSensitive;
    public bool CaseSensitive { get => _caseSensitive; set => Set(ref _caseSensitive, value); }

    public ObservableCollection<FolderRule> Rules { get; set; } = [];
}

public sealed class FolderRule : ObservableObject
{
    private RuleCriterion _criterion = RuleCriterion.Extension;
    public RuleCriterion Criterion { get => _criterion; set => Set(ref _criterion, value); }

    private RuleOperator _operator = RuleOperator.Is;
    public RuleOperator Operator { get => _operator; set => Set(ref _operator, value); }

    /// <summary>Text to compare; a file kind name for Kind; megabytes for Size.</summary>
    private string _value = "";
    public string Value { get => _value; set => Set(ref _value, value); }
}
