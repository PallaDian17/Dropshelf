using System.Windows;
using System.Windows.Controls;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.Shelf;

namespace Dropshelf.UI;

/// <summary>Dialog for creating or editing a Custom Action: a name plus the template's options.</summary>
public sealed class CustomActionEditor : Window
{
    private readonly TextBox _name;
    private readonly ActionFormView _form;

    public CustomActionEditor(ShelfAction template, CustomActionDefinition? existing)
    {
        Title = existing is null ? $"New Custom Action – {template.Title}" : $"Edit {existing.Name}";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont");
        FontSize = 13;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this, ThemeManager.IsDark);

        ActionTemplate = template;
        // Options are edited without real files; use an empty input so defaults are generic.
        var input = new ActionInput([], null);
        var fields = template.GetFields(input).Where(f => f is not PreviewField).ToList();
        var values = ActionValues.Defaults(fields, existing?.Values);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Display name", Margin = new Thickness(0, 0, 0, 6) });
        _name = new TextBox { Text = existing?.Name ?? template.Title };
        root.Children.Add(_name);
        if (template.Description is { } description)
            root.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("RowSubtitle"), Margin = new Thickness(0, 12, 0, 0) });

        _form = new ActionFormView(fields, values) { Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(_form);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Save", MinWidth = 90, Style = (Style)FindResource("AccentButton"), IsDefault = true, Padding = new Thickness(16, 5, 16, 5) };
        save.Click += (_, _) => OnSave();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        Content = root;
    }

    public ShelfAction ActionTemplate { get; }
    public string ResultName => _name.Text.Trim();
    public Dictionary<string, string> ResultValues => _form.Values.AsDictionary().ToDictionary(kv => kv.Key, kv => kv.Value);

    private void OnSave()
    {
        if (ResultName.Length == 0)
        {
            _form.ShowError("Enter a display name.");
            return;
        }
        if (ActionTemplate.RequiresConfiguration && ActionTemplate.Validate(new ActionInput([], null), _form.Values) is { } error)
        {
            _form.ShowError(error);
            return;
        }
        DialogResult = true;
    }
}
