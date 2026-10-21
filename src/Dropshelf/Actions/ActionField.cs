using System.Globalization;

namespace Dropshelf.Actions;

/// <summary>Declarative description of one row in an action's configuration panel.</summary>
public abstract record ActionField(string Key, string Label)
{
    public Func<ActionValues, bool>? VisibleWhen { get; init; }
    public string? Help { get; init; }
    public abstract string DefaultValue { get; }
    public bool IsVisible(ActionValues v) => VisibleWhen?.Invoke(v) ?? true;
}

public sealed record ChoiceField(string Key, string Label, IReadOnlyList<(string Value, string Text)> Options, string Default)
    : ActionField(Key, Label)
{
    public override string DefaultValue => Default;
}

public sealed record NumberField(string Key, string Label, int Default, int Min, int Max) : ActionField(Key, Label)
{
    public override string DefaultValue => Default.ToString(CultureInfo.InvariantCulture);
}

public sealed record TextField(string Key, string Label, string Default, string? Placeholder = null, bool Password = false)
    : ActionField(Key, Label)
{
    public override string DefaultValue => Default;
}

public sealed record ToggleField(string Key, string Label, bool Default) : ActionField(Key, Label)
{
    public override string DefaultValue => Default ? "true" : "false";
}

/// <summary>Folder picker. An empty value means "same folder as the original file" when <see cref="AllowSameAsOriginal"/>.</summary>
public sealed record FolderField(string Key, string Label, string Default = "", bool AllowSameAsOriginal = true)
    : ActionField(Key, Label)
{
    public override string DefaultValue => Default;
}

/// <summary>Executable picker (Open with Application).</summary>
public sealed record AppField(string Key, string Label) : ActionField(Key, Label)
{
    public override string DefaultValue => "";
}

public sealed record SliderField(string Key, string Label, int Default, int Min, int Max, Func<int, string>? Describe = null)
    : ActionField(Key, Label)
{
    public override string DefaultValue => Default.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Read-only explanation text.</summary>
public sealed record InfoField(string Key, string Text) : ActionField(Key, "")
{
    public override string DefaultValue => "";
}

/// <summary>Live "current name → new name" preview (Batch Rename).</summary>
public sealed record PreviewField(string Key, string Label, Func<ActionValues, IReadOnlyList<(string Before, string After)>> Compute)
    : ActionField(Key, Label)
{
    public override string DefaultValue => "";
}

public static class CommonFields
{
    public static ChoiceField OutputIn(string defaultMode = "shelf") => new("output", "Show output in",
    [
        ("shelf", "Current Shelf"),
        ("newshelf", "New Shelf"),
        ("explorer", "File Explorer"),
    ], defaultMode);

    public static ToggleField CreateCopy(bool defaultValue = true) => new("copy", "Create copy", defaultValue);

    public static FolderField SaveIn(bool onlyWithCopy = true) => new("dest", "Save in")
    {
        VisibleWhen = onlyWithCopy ? v => v.GetBool("copy", true) : null,
    };

    /// <summary>The standard trio shown under image actions: Show output in / Create copy / Save in.</summary>
    public static IEnumerable<ActionField> Output(bool withCopyToggle = true)
    {
        yield return OutputIn();
        if (withCopyToggle) yield return CreateCopy();
        yield return SaveIn(withCopyToggle);
    }
}
