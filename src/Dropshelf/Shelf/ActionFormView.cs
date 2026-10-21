using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;
using Microsoft.Win32;

namespace Dropshelf.Shelf;

/// <summary>Builds an action's configuration panel from its declared fields (label on the left, control on the right).</summary>
public sealed class ActionFormView : UserControl
{
    private readonly List<(ActionField Field, FrameworkElement Row)> _rows = new();
    private readonly List<Action> _refreshers = new();
    private readonly TextBlock _error;

    public ActionFormView(IReadOnlyList<ActionField> fields, ActionValues values, string? description = null)
    {
        Values = values;
        var stack = new StackPanel();
        if (!string.IsNullOrWhiteSpace(description))
            stack.Children.Add(new TextBlock { Text = description, Style = StyleOf("RowSubtitle"), Margin = new Thickness(0, 0, 0, 10) });

        var group = new StackPanel();
        var card = new Border
        {
            Background = UI.ThemeManager.Brush("ControlFillBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Child = group,
        };
        stack.Children.Add(card);

        foreach (var field in fields)
        {
            var row = BuildRow(field);
            _rows.Add((field, row));
            group.Children.Add(row);
        }

        _error = new TextBlock
        {
            Foreground = UI.ThemeManager.Brush("DangerBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(2, 8, 2, 0),
        };
        stack.Children.Add(_error);

        Content = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 470,
            Focusable = false,
        };
        RefreshVisibility();
    }

    public ActionValues Values { get; }

    public void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private void Changed()
    {
        _error.Visibility = Visibility.Collapsed;
        RefreshVisibility();
        foreach (var refresh in _refreshers) refresh();
    }

    private void RefreshVisibility()
    {
        foreach (var (field, row) in _rows) row.Visibility = field.IsVisible(Values) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);

    private FrameworkElement BuildRow(ActionField field) => field switch
    {
        ChoiceField c => Inline(c.Label, Choice(c)),
        NumberField n => Inline(n.Label, Number(n)),
        ToggleField t => Inline(t.Label, Toggle(t)),
        FolderField f => Inline(f.Label, Folder(f)),
        AppField a => Inline(a.Label, App(a)),
        TextField t => Stacked(t.Label, Text(t), t.Help),
        SliderField s => Stacked(s.Label, Slider(s), null),
        PreviewField p => Stacked(p.Label, Preview(p), null),
        InfoField i => new TextBlock { Text = i.Text, Style = StyleOf("RowSubtitle"), Margin = new Thickness(0, 6, 0, 6) },
        _ => new TextBlock { Text = field.Label },
    };

    private static FrameworkElement Inline(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5), MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }

    private static FrameworkElement Stacked(string label, FrameworkElement control, string? help)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(control);
        if (help is not null) panel.Children.Add(new TextBlock { Text = help, Style = StyleOf("RowSubtitle") });
        return panel;
    }

    private FrameworkElement Choice(ChoiceField field)
    {
        var combo = new ComboBox { MinWidth = 160 };
        foreach (var (value, text) in field.Options) combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Values[field.Key])
                             ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                Values[field.Key] = (string)item.Tag;
                Changed();
            }
        };
        if (combo.SelectedItem is ComboBoxItem selected) Values[field.Key] = (string)selected.Tag;
        return combo;
    }

    private FrameworkElement Number(NumberField field)
    {
        var box = new TextBox { Width = 90, TextAlignment = TextAlignment.Right, Text = Values[field.Key] };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string s || !s.All(char.IsDigit)) e.CancelCommand();
        });
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                Values[field.Key] = Math.Clamp(n, field.Min, field.Max).ToString(CultureInfo.InvariantCulture);
                Changed();
            }
        };
        box.LostFocus += (_, _) => box.Text = Values[field.Key];
        return box;
    }

    private FrameworkElement Toggle(ToggleField field)
    {
        var toggle = new CheckBox { Style = StyleOf("ToggleSwitch"), IsChecked = Values.GetBool(field.Key, field.Default) };
        toggle.Checked += (_, _) =>
        {
            Values.SetBool(field.Key, true);
            Changed();
        };
        toggle.Unchecked += (_, _) =>
        {
            Values.SetBool(field.Key, false);
            Changed();
        };
        return toggle;
    }

    private FrameworkElement Text(TextField field)
    {
        if (field.Password)
        {
            var password = new PasswordBox();
            password.PasswordChanged += (_, _) =>
            {
                Values[field.Key] = password.Password;
                Changed();
            };
            return password;
        }

        var box = new TextBox { Text = Values[field.Key] };
        box.TextChanged += (_, _) =>
        {
            Values[field.Key] = box.Text;
            Changed();
        };
        box.Loaded += (_, _) =>
        {
            // Rename-style fields: select the name without its extension, like Explorer.
            var dot = box.Text.LastIndexOf('.');
            if (dot > 0) box.Select(0, dot);
            else box.SelectAll();
        };
        return field.Placeholder is null ? box : FormControls.WithPlaceholder(box, field.Placeholder);
    }

    private FrameworkElement Slider(SliderField field)
    {
        var slider = new Slider { Minimum = field.Min, Maximum = field.Max, Value = Values.GetInt(field.Key, field.Default), SmallChange = 1, LargeChange = 10 };
        var description = new TextBlock { Style = StyleOf("RowSubtitle") };
        void Describe() => description.Text = $"{(int)slider.Value}%" + (field.Describe is null ? "" : $" · {field.Describe((int)slider.Value)}");
        slider.ValueChanged += (_, _) =>
        {
            Values.SetInt(field.Key, (int)Math.Round(slider.Value));
            Describe();
            Changed();
        };
        Describe();
        var panel = new StackPanel();
        panel.Children.Add(slider);
        panel.Children.Add(description);
        return panel;
    }

    private FrameworkElement Folder(FolderField field)
    {
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                    label,
                    new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };

        void UpdateLabel()
        {
            var value = Values[field.Key];
            label.Text = value.Length == 0
                ? (field.AllowSameAsOriginal ? "Same as Original" : "Choose Folder…")
                : System.IO.Path.GetFileName(value.TrimEnd('\\')) is { Length: > 0 } name ? name : value;
            button.ToolTip = value.Length == 0 ? null : value;
        }

        void Set(string value)
        {
            Values[field.Key] = value;
            UpdateLabel();
            Changed();
        }

        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            void Add(string header, Action onClick)
            {
                var item = new MenuItem { Header = new TextBlock { Text = header } };
                item.Click += (_, _) => onClick();
                menu.Items.Add(item);
            }

            if (field.AllowSameAsOriginal) Add("Same Folder as Original", () => Set(""));
            Add("Desktop", () => Set(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
            Add("Downloads", () => Set(ShellOperations.DownloadsFolder()));
            Add("Documents", () => Set(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            Add("Pictures", () => Set(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
            menu.Items.Add(new Separator());
            Add("Choose Folder…", () =>
            {
                var dialog = new OpenFolderDialog { Title = "Select Folder" };
                if (Values[field.Key] is { Length: > 0 } current && Directory.Exists(current)) dialog.InitialDirectory = current;
                if (dialog.ShowDialog(Window.GetWindow(this)) == true) Set(dialog.FolderName);
            });
            menu.IsOpen = true;
        };
        UpdateLabel();
        return button;
    }

    private FrameworkElement App(AppField field)
    {
        var button = new Button();
        void UpdateLabel()
        {
            var value = Values[field.Key];
            button.Content = value.Length == 0 ? "Choose Application…" : System.IO.Path.GetFileNameWithoutExtension(value);
            button.ToolTip = value.Length == 0 ? null : value;
        }
        button.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose Application",
                Filter = "Applications (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                Values[field.Key] = dialog.FileName;
                UpdateLabel();
                Changed();
            }
        };
        UpdateLabel();
        return button;
    }

    private FrameworkElement Preview(PreviewField field)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Refresh()
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            var rows = field.Compute(Values).Take(8).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var before = new TextBlock { Text = rows[i].Before, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = UI.ThemeManager.Brush("TextSecondaryBrush"), Margin = new Thickness(0, 2, 0, 2) };
                var arrow = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                var after = new TextBlock { Text = rows[i].After, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2) };
                Grid.SetRow(before, i);
                Grid.SetRow(arrow, i);
                Grid.SetRow(after, i);
                Grid.SetColumn(arrow, 1);
                Grid.SetColumn(after, 2);
                grid.Children.Add(before);
                grid.Children.Add(arrow);
                grid.Children.Add(after);
            }
        }

        _refreshers.Add(Refresh);
        Refresh();
        return grid;
    }
}

public static class FormControls
{
    /// <summary>Overlays hint text on an empty TextBox.</summary>
    public static FrameworkElement WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = new TextBlock
        {
            Text = placeholder,
            Foreground = UI.ThemeManager.Brush("TextTertiaryBrush"),
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update();
        Update();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }
}
