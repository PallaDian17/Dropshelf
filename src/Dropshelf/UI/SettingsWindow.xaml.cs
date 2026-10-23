using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;
using Dropshelf.Shelf;
using Microsoft.Win32;

namespace Dropshelf.UI;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private readonly AppSettings _settings = SettingsStore.Current;

    private SettingsWindow()
    {
        InitializeComponent();
        DataContext = _settings;
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this, ThemeManager.IsDark);
        Tabs.SelectionChanged += (_, e) =>
        {
            // SelectionChanged also bubbles up from combo boxes and lists inside the pages.
            if (ReferenceEquals(e.OriginalSource, Tabs) && Tabs.SelectedItem is TabItem { Content: FrameworkElement page })
                AnimatePageIn(page);
        };
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _settings.IgnoredApplications.CollectionChanged -= OnIgnoredChanged;
            SettingsStore.Save();
            _instance = null;
        };

        LaunchAtLoginToggle.IsChecked = ShellIntegration.IsLaunchAtLoginEnabled();
        ContextMenuToggle.IsChecked = ShellIntegration.IsContextMenuInstalled();
        SendToToggle.IsChecked = ShellIntegration.IsSendToInstalled();

        _settings.IgnoredApplications.CollectionChanged += OnIgnoredChanged;
        UpdateIgnoredEmpty();

        BuildAppData();
        BuildShortcuts();
        BuildInstantActions();
        BuildFavorites();
        BuildCustomActions();
        BuildScripts();
        BuildFolders();
        BuildAbout();
    }

    public static void ShowSingleton() => ShowSingleton(null);

    public static void ShowSingleton(string? page)
    {
        _instance ??= new SettingsWindow();
        if (page == "about") _instance.Tabs.SelectedItem = _instance.AboutTab;
        WindowHelper.BringToFront(_instance);
    }

    private void OnThemeChanged() => WindowHelper.UseDarkTitleBar(this, ThemeManager.IsDark);

    /// <summary>A newly selected page rises slightly into place as it fades in.</summary>
    private static void AnimatePageIn(FrameworkElement page)
    {
        var shift = new TranslateTransform(0, 12);
        page.RenderTransform = shift;
        Motion.To(page, OpacityProperty, 1, 200, from: 0);
        Motion.To(shift, TranslateTransform.YProperty, 0, 460, Motion.SoftSpring);
    }

    // =====================================================================================
    // General
    // =====================================================================================

    private void OnLaunchAtLoginClick(object sender, RoutedEventArgs e)
    {
        var enable = LaunchAtLoginToggle.IsChecked == true;
        if (!ShellIntegration.SetLaunchAtLogin(enable))
        {
            MessageBox.Show(this, "Launch at sign-in couldn't be changed. You can add Dropshelf manually in Settings › Apps › Startup.",
                "Dropshelf", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _settings.LaunchAtLogin = ShellIntegration.IsLaunchAtLoginEnabled();
        LaunchAtLoginToggle.IsChecked = _settings.LaunchAtLogin;
    }

    private void OnContextMenuClick(object sender, RoutedEventArgs e)
    {
        ShellIntegration.SetContextMenu(ContextMenuToggle.IsChecked == true);
        ContextMenuToggle.IsChecked = ShellIntegration.IsContextMenuInstalled();
    }

    private void OnSendToClick(object sender, RoutedEventArgs e)
    {
        ShellIntegration.SetSendTo(SendToToggle.IsChecked == true);
        SendToToggle.IsChecked = ShellIntegration.IsSendToInstalled();
    }

    private void BuildAppData()
    {
        AppDataList.Children.Clear();
        var folders = new (string Name, string Description, string Path)[]
        {
            ("Temporary Folder", "Data dropped without a file on disk: browser images, mail messages, files from ZIP folders.", AppPaths.TempDir),
            ("Text Files", "Text snippets and links saved as files when dragged out or processed.", AppPaths.TextFilesDir),
            ("Images from Clipboard", "Images added with New Shelf From Clipboard.", AppPaths.ClipboardImagesDir),
        };

        foreach (var (name, description, path) in folders)
        {
            var size = new TextBlock { Text = "Calculating…", Foreground = ThemeManager.Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var reveal = new Button { Content = "Open", Margin = new Thickness(0, 0, 8, 0) };
            reveal.Click += (_, _) => ShellOperations.OpenFolder(path);
            var clear = new Button { Content = "Clear Folder" };
            clear.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Delete everything in \"{name}\"? Shelves and recent shelves that use these files will lose them.",
                        "Clear Folder", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                ClearFolder(path);
                ShelfManager.Instance.Recent.RemoveReferencesTo(path);
                BuildAppData();
            };

            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(size);
            controls.Children.Add(reveal);
            controls.Children.Add(clear);
            AppDataList.Children.Add(new SettingRow { Title = name, Description = description, Content = controls });

            Task.Run(() => AppPaths.DirectorySize(path)).ContinueWith(t => size.Text = AppPaths.FormatBytes(t.Result),
                TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private static void ClearFolder(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(path)) Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not clear {path}: {ex.Message}");
        }
    }

    // =====================================================================================
    // Shelf activation: ignored applications
    // =====================================================================================

    private void OnIgnoredChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateIgnoredEmpty();

    private void UpdateIgnoredEmpty()
        => IgnoredEmpty.Visibility = _settings.IgnoredApplications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddIgnored(string processName)
    {
        processName = Path.GetFileNameWithoutExtension(processName.Trim());
        if (processName.Length == 0) return;
        if (_settings.IgnoredApplications.Any(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase))) return;
        _settings.IgnoredApplications.Add(processName);
    }

    private void OnAddRunningApp(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var names = Process.GetProcesses()
            .Where(p =>
            {
                try
                {
                    return p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId;
                }
                catch
                {
                    return false;
                }
            })
            .Select(p => p.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var item = new MenuItem { Header = new TextBlock { Text = name } };
            item.Click += (_, _) => AddIgnored(name);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnBrowseIgnoredApp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose Application", Filter = "Applications (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) AddIgnored(dialog.FileName);
    }

    private void OnRemoveIgnoredApp(object sender, RoutedEventArgs e)
    {
        if (IgnoredList.SelectedItem is string name) _settings.IgnoredApplications.Remove(name);
    }

    // =====================================================================================
    // Keyboard shortcuts
    // =====================================================================================

    private void BuildShortcuts()
    {
        GlobalShortcuts.Children.Clear();
        foreach (var descriptor in AppSettings.HotkeyDescriptors)
        {
            var binding = _settings.Hotkeys[descriptor.Id];
            var enabled = new CheckBox { IsChecked = binding.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var recorder = new HotkeyRecorder(binding);
            recorder.Validate = candidate =>
            {
                var clash = AppSettings.HotkeyDescriptors.FirstOrDefault(d => d.Id != descriptor.Id &&
                    _settings.Hotkeys[d.Id].SameCombination(candidate));
                if (clash is not null) return $"This combination is already used by \"{clash.Title}\".";

                var previous = _settings.Hotkeys[descriptor.Id];
                _settings.Hotkeys[descriptor.Id] = new HotkeyBinding(previous.Enabled, candidate.Modifiers, candidate.Key);
                if (previous.Enabled && App.Current.ApplyHotkeys().Contains(descriptor.Id))
                {
                    _settings.Hotkeys[descriptor.Id] = previous;
                    App.Current.ApplyHotkeys();
                    return "This shortcut is already in use by Windows or another app. Try a different combination.";
                }
                SettingsStore.NotifyChanged(nameof(AppSettings.Hotkeys));
                return null;
            };

            enabled.Click += (_, _) =>
            {
                var current = _settings.Hotkeys[descriptor.Id];
                current.Enabled = enabled.IsChecked == true;
                if (current.Enabled && App.Current.ApplyHotkeys().Contains(descriptor.Id))
                {
                    current.Enabled = false;
                    enabled.IsChecked = false;
                    App.Current.ApplyHotkeys();
                    MessageBox.Show(this, $"{current} is already in use by Windows or another app. Assign a different shortcut first.",
                        "Cannot Enable Shortcut", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                SettingsStore.NotifyChanged(nameof(AppSettings.Hotkeys));
            };

            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(enabled);
            controls.Children.Add(recorder);
            GlobalShortcuts.Children.Add(new SettingRow { Title = descriptor.Title, Content = controls });
        }

        ShelfShortcuts.Children.Clear();
        var shelfKeys = new (string Keys, string Action)[]
        {
            ("Ctrl+K", "Search actions (command bar)"),
            ("Ctrl+M  or  Menu key", "Open the action menu"),
            ("Space", "Preview"),
            ("Enter", "Open detail view / open selected files"),
            ("Ctrl+C", "Copy shelf content"),
            ("Ctrl+V", "Paste into shelf"),
            ("Ctrl+A", "Select all (detail view)"),
            ("Delete", "Remove selected items from the shelf"),
            ("F2", "Rename selected file"),
            ("Ctrl+Left / Ctrl+Right", "Dock to the left / right edge"),
            ("Ctrl+D", "Dock to nearest edge or undock"),
            ("Ctrl+Delete", "Clear shelf"),
            ("Ctrl+W", "Close shelf"),
            ("Esc", "Go back"),
        };
        foreach (var (keys, action) in shelfKeys)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = action });
            var keyText = new TextBlock { Text = keys, Foreground = ThemeManager.Brush("TextSecondaryBrush") };
            Grid.SetColumn(keyText, 1);
            row.Children.Add(keyText);
            ShelfShortcuts.Children.Add(row);
        }
    }

    // =====================================================================================
    // Instant actions
    // =====================================================================================

    private void BuildInstantActions()
    {
        var selectedId = (InstantList.SelectedItem as ListBoxItem)?.Tag as string;
        InstantList.Items.Clear();
        foreach (var id in _settings.InstantActions)
        {
            var action = ActionCatalog.Find(id);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = action?.Glyph ?? "", FontFamily = (FontFamily)FindResource("IconFont"), Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = action?.Title.TrimEnd('…') ?? $"{id} (missing)", VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = id };
            InstantList.Items.Add(item);
            if (id == selectedId) InstantList.SelectedItem = item;
        }
        InstantCloseRadio.IsChecked = _settings.InstantActionsCloseShelf;
        InstantKeepRadio.IsChecked = !_settings.InstantActionsCloseShelf;
    }

    private void OnAddInstantAction(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var group in ActionCatalog.InstantCandidates().Where(a => !_settings.InstantActions.Contains(a.Id)).GroupBy(a => a.Category).OrderBy(g => g.Key))
        {
            var sub = new MenuItem { Header = new TextBlock { Text = ActionCatalog.CategoryTitle(group.Key) } };
            foreach (var action in group)
            {
                var item = new MenuItem { Header = new TextBlock { Text = action.Title.TrimEnd('…') } };
                item.Click += (_, _) =>
                {
                    _settings.InstantActions.Add(action.Id);
                    BuildInstantActions();
                };
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        menu.IsOpen = true;
    }

    private void MoveInstant(int delta)
    {
        if (InstantList.SelectedItem is not ListBoxItem { Tag: string id }) return;
        var index = _settings.InstantActions.IndexOf(id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _settings.InstantActions.Count) return;
        _settings.InstantActions.Move(index, target);
        BuildInstantActions();
    }

    private void OnInstantUp(object sender, RoutedEventArgs e) => MoveInstant(-1);
    private void OnInstantDown(object sender, RoutedEventArgs e) => MoveInstant(1);

    private void OnRemoveInstantAction(object sender, RoutedEventArgs e)
    {
        if (InstantList.SelectedItem is not ListBoxItem { Tag: string id }) return;
        _settings.InstantActions.Remove(id);
        BuildInstantActions();
    }

    private void OnInstantAfterChanged(object sender, RoutedEventArgs e)
    {
        if (InstantCloseRadio is null || InstantKeepRadio is null) return;
        _settings.InstantActionsCloseShelf = InstantCloseRadio.IsChecked == true;
    }

    // =====================================================================================
    // Favorite actions
    // =====================================================================================

    private void BuildFavorites()
    {
        FavoritesList.Children.Clear();
        var candidates = ActionCatalog.All().Where(a => a.Category is not ActionCategory.Shelf && a.Id is not ("copy-files" or "reveal" or "preview" or "share" or "email"));
        foreach (var action in candidates)
        {
            var box = new CheckBox
            {
                Content = action.Title.TrimEnd('…'),
                IsChecked = _settings.FavoriteActions.Contains(action.Id),
                Margin = new Thickness(0, 4, 0, 4),
            };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true)
                {
                    if (_settings.FavoriteActions.Count >= 5)
                    {
                        box.IsChecked = false;
                        MessageBox.Show(this, "You can select up to 5 favorite actions.", "Favorite Actions", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    _settings.FavoriteActions.Add(action.Id);
                }
                else
                {
                    _settings.FavoriteActions.Remove(action.Id);
                }
            };
            FavoritesList.Children.Add(box);
        }
    }

    // =====================================================================================
    // Custom actions
    // =====================================================================================

    private void BuildCustomActions()
    {
        CustomList.Children.Clear();
        foreach (var definition in _settings.CustomActions.ToList())
        {
            var template = ActionCatalog.BuiltIn.FirstOrDefault(a => a.Id == definition.TemplateId);
            var edit = new Button { Content = "Edit", Margin = new Thickness(0, 0, 8, 0), IsEnabled = template is not null };
            edit.Click += (_, _) => EditCustomAction(template!, definition);
            var delete = new Button { Content = "Delete" };
            delete.Click += (_, _) =>
            {
                _settings.CustomActions.Remove(definition);
                _settings.InstantActions.Remove("custom:" + definition.Id);
                _settings.FavoriteActions.Remove("custom:" + definition.Id);
                BuildCustomActions();
                BuildFavorites();
                BuildInstantActions();
            };
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(edit);
            controls.Children.Add(delete);
            CustomList.Children.Add(new SettingRow
            {
                Title = definition.Name,
                Description = template is null ? "Template no longer available" : $"Based on {template.Title}",
                Content = controls,
            });
        }
        CustomEmpty.Visibility = _settings.CustomActions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCreateCustomAction(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var template in ActionCatalog.PresetTemplates())
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = template.Title },
                Icon = new TextBlock { Text = template.Glyph, FontFamily = (FontFamily)FindResource("IconFont") },
            };
            item.Click += (_, _) => EditCustomAction(template, null);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void EditCustomAction(ShelfAction template, CustomActionDefinition? existing)
    {
        var editor = new CustomActionEditor(template, existing) { Owner = this };
        if (editor.ShowDialog() != true) return;

        if (existing is null)
        {
            _settings.CustomActions.Add(new CustomActionDefinition { Name = editor.ResultName, TemplateId = template.Id, Values = editor.ResultValues });
        }
        else
        {
            existing.Name = editor.ResultName;
            existing.Values = editor.ResultValues;
            SettingsStore.NotifyChanged(nameof(AppSettings.CustomActions));
        }
        BuildCustomActions();
        BuildFavorites();
    }

    // =====================================================================================
    // Custom scripts
    // =====================================================================================

    private void BuildScripts()
    {
        ScriptsList.Children.Clear();
        var scripts = ScriptManager.ScriptFiles();
        foreach (var script in scripts)
        {
            var config = ScriptManager.ConfigFor(script);
            var card = new Border { Style = (Style)FindResource("Card") };
            var panel = new StackPanel();
            card.Child = panel;

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = Path.GetFileName(script), Style = (Style)FindResource("RowTitle"), VerticalAlignment = VerticalAlignment.Center });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var test = new Button { Content = "Run Test…", Margin = new Thickness(0, 0, 8, 0) };
            test.Click += async (_, _) => await TestScript(script);
            var edit = new Button { Content = "Edit", Margin = new Thickness(0, 0, 8, 0) };
            edit.Click += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { script } });
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Move {Path.GetFileName(script)} to the Recycle Bin?", "Remove Script",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                ShellOperations.Recycle([script], WindowHelper.Handle(this));
                _settings.Scripts.Remove(Path.GetFileName(script));
                BuildScripts();
            };
            buttons.Children.Add(test);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);
            Grid.SetColumn(buttons, 1);
            header.Children.Add(buttons);
            panel.Children.Add(header);

            var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBox { Text = config.DisplayName ?? "", Margin = new Thickness(0, 0, 0, 8) };
            name.TextChanged += (_, _) =>
            {
                config.DisplayName = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
                SettingsStore.ScheduleSave();
            };
            var output = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            output.Items.Add(new ComboBoxItem { Content = "Ignore", Tag = ScriptOutputMode.Ignore });
            output.Items.Add(new ComboBoxItem { Content = "Copy to Clipboard", Tag = ScriptOutputMode.Clipboard });
            output.SelectedIndex = config.OutputMode == ScriptOutputMode.Clipboard ? 1 : 0;
            output.SelectionChanged += (_, _) =>
            {
                config.OutputMode = output.SelectedIndex == 1 ? ScriptOutputMode.Clipboard : ScriptOutputMode.Ignore;
                SettingsStore.ScheduleSave();
            };
            var close = new CheckBox { Content = "Close the shelf after the script runs", IsChecked = config.CloseShelf };
            close.Click += (_, _) =>
            {
                config.CloseShelf = close.IsChecked == true;
                SettingsStore.ScheduleSave();
            };

            AddGridRow(grid, 0, "Name", FormControls.WithPlaceholder(name, Path.GetFileNameWithoutExtension(script)));
            AddGridRow(grid, 1, "Script output", output);
            AddGridRow(grid, 2, "", close);
            panel.Children.Add(grid);
            ScriptsList.Children.Add(card);
        }
        ScriptsEmpty.Visibility = scripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AddGridRow(Grid grid, int row, string label, FrameworkElement control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private async Task TestScript(string script)
    {
        var dialog = new OpenFileDialog { Title = "Select the files to pass to the script", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var result = await ScriptManager.RunAsync(script, dialog.FileNames, CancellationToken.None);
            var text = result.ExitCode == 0 ? "Successfully executed script." : $"The script failed with exit code {result.ExitCode}.";
            if (result.Output.Length > 0) text += $"\n\nOutput:\n{Truncate(result.Output)}";
            if (result.Error.Length > 0) text += $"\n\nErrors:\n{Truncate(result.Error)}";
            MessageBox.Show(this, text, Path.GetFileName(script), MessageBoxButton.OK,
                result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to execute script: {ex.Message}", Path.GetFileName(script), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
        }
    }

    private static string Truncate(string s) => s.Length > 1500 ? s[..1500] + "…" : s;

    private void OnRevealScripts(object sender, RoutedEventArgs e) => ShellOperations.OpenFolder(AppPaths.ScriptsDir);

    private void OnImportScript(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Script",
            Filter = "Scripts|*.ps1;*.bat;*.cmd;*.py;*.js;*.vbs;*.exe|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!ScriptManager.SupportedExtensions.Contains(Path.GetExtension(dialog.FileName).ToLowerInvariant()))
        {
            MessageBox.Show(this, "This file type is not supported as a script.", "Could not load the file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ScriptManager.Import(dialog.FileName);
        BuildScripts();
        BuildFavorites();
    }

    private void OnRefreshScripts(object sender, RoutedEventArgs e)
    {
        BuildScripts();
        BuildFavorites();
    }

    private void OnShowScriptLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(ScriptManager.LogFile)) ShellOperations.Open(ScriptManager.LogFile);
        else MessageBox.Show(this, "No script has run yet.", "Script Log", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // =====================================================================================
    // Folder monitoring
    // =====================================================================================

    private void BuildFolders()
    {
        UpdateScreenshotFolderButton();
        var selected = (FolderList.SelectedItem as ListBoxItem)?.Tag as WatchedFolder;
        FolderList.Items.Clear();
        foreach (var folder in _settings.WatchedFolders)
        {
            var status = App.Current.Folders?.StatusOf(folder) ?? FolderStatus.Stopped;
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = folder.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock
            {
                Text = status switch { FolderStatus.Active => "Active", FolderStatus.Missing => "Folder moved or missing", _ => "Not active" },
                FontSize = 11,
                Foreground = ThemeManager.Brush(status == FolderStatus.Missing ? "DangerBrush" : "TextSecondaryBrush"),
            });
            var item = new ListBoxItem { Content = row, Tag = folder, ToolTip = folder.Path };
            FolderList.Items.Add(item);
            if (ReferenceEquals(folder, selected)) FolderList.SelectedItem = item;
        }
        FoldersEmpty.Visibility = _settings.WatchedFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (FolderList.SelectedItem is null && FolderList.Items.Count > 0) FolderList.SelectedIndex = 0;
        if (FolderList.Items.Count == 0) FolderDetail.Children.Clear();
    }

    private void UpdateScreenshotFolderButton()
    {
        var folder = string.IsNullOrWhiteSpace(_settings.ScreenshotFolder) ? ShellOperations.DefaultScreenshotsFolder() : _settings.ScreenshotFolder!;
        ScreenshotFolderButton.Content = new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis };
        ScreenshotFolderButton.ToolTip = folder;
    }

    private void OnChangeScreenshotFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder where screenshots are saved" };
        if (dialog.ShowDialog(this) != true) return;
        _settings.ScreenshotFolder = dialog.FolderName;
        UpdateScreenshotFolderButton();
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick a folder whose new files should appear in a shelf" };
        if (dialog.ShowDialog(this) != true) return;
        if (_settings.WatchedFolders.Any(f => string.Equals(f.Path, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "This folder has already been added.", "Folder Monitoring", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var folder = new WatchedFolder { Path = dialog.FolderName };
        _settings.WatchedFolders.Add(folder);
        BuildFolders();
        FolderList.SelectedItem = FolderList.Items.OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, folder));
    }

    private void OnFolderSelected(object sender, SelectionChangedEventArgs e)
    {
        FolderDetail.Children.Clear();
        if (FolderList.SelectedItem is not ListBoxItem { Tag: WatchedFolder folder }) return;

        var header = new Border { Style = (Style)FindResource("Card") };
        var headerPanel = new StackPanel();
        headerPanel.Children.Add(new TextBlock { Text = folder.DisplayName, Style = (Style)FindResource("RowTitle"), FontWeight = FontWeights.SemiBold });
        headerPanel.Children.Add(new TextBlock { Text = folder.Path, Style = (Style)FindResource("RowSubtitle") });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var open = new Button { Content = "Open", Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => ShellOperations.OpenFolder(folder.Path);
        var reselect = new Button { Content = "Change Folder…", Margin = new Thickness(0, 0, 8, 0) };
        reselect.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Select Folder" };
            if (dialog.ShowDialog(this) != true) return;
            folder.Path = dialog.FolderName;
            BuildFolders();
        };
        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) =>
        {
            _settings.WatchedFolders.Remove(folder);
            BuildFolders();
        };
        buttons.Children.Add(open);
        buttons.Children.Add(reselect);
        buttons.Children.Add(remove);
        headerPanel.Children.Add(buttons);
        header.Child = headerPanel;
        FolderDetail.Children.Add(header);

        FolderDetail.Children.Add(Toggle("Monitoring on", null, folder.Enabled, v => folder.Enabled = v));
        FolderDetail.Children.Add(Toggle("Include nested subfolders", "Watches every subfolder too. Large folder trees can slow things down.", folder.IncludeSubfolders, v => folder.IncludeSubfolders = v));
        FolderDetail.Children.Add(Toggle("Create new shelf for each file", null, folder.NewShelfPerFile, v => folder.NewShelfPerFile = v));
        FolderDetail.Children.Add(Toggle("Delay processing", "Wait 3 seconds after the last new file before showing the shelf.", folder.DelayProcessing, v => folder.DelayProcessing = v));
        FolderDetail.Children.Add(Toggle("Case-sensitive rules", null, folder.CaseSensitive, v => folder.CaseSensitive = v));

        FolderDetail.Children.Add(new TextBlock { Text = "Folder rules", Style = (Style)FindResource("SectionHeader") });
        var rulesCard = new Border { Style = (Style)FindResource("Card") };
        var rulesPanel = new StackPanel();
        rulesCard.Child = rulesPanel;
        FolderDetail.Children.Add(rulesCard);
        BuildRules(folder, rulesPanel);
    }

    private SettingRow Toggle(string title, string? description, bool value, Action<bool> set)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = value };
        toggle.Click += (_, _) =>
        {
            set(toggle.IsChecked == true);
            Dispatcher.BeginInvoke(BuildFolderStatusOnly);
        };
        return new SettingRow { Title = title, Description = description, Content = toggle };
    }

    private void BuildFolderStatusOnly()
    {
        var selected = FolderList.SelectedItem;
        FolderList.SelectionChanged -= OnFolderSelected;
        BuildFolders();
        FolderList.SelectionChanged += OnFolderSelected;
        if (selected is ListBoxItem { Tag: WatchedFolder f })
            FolderList.SelectedItem = FolderList.Items.OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, f));
    }

    private void BuildRules(WatchedFolder folder, StackPanel panel)
    {
        panel.Children.Clear();
        if (folder.Rules.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Without rules, every new file in this folder appears in a shelf. Add rules to limit which files do.",
                Style = (Style)FindResource("RowSubtitle"),
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
        else
        {
            var match = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
            match.Items.Add(new ComboBoxItem { Content = "All rules must match (And)" });
            match.Items.Add(new ComboBoxItem { Content = "Any rule may match (Or)" });
            match.SelectedIndex = folder.MatchAll ? 0 : 1;
            match.SelectionChanged += (_, _) => folder.MatchAll = match.SelectedIndex == 0;
            panel.Children.Add(match);
        }

        foreach (var rule in folder.Rules.ToList()) panel.Children.Add(RuleRow(folder, rule, panel));

        var add = new Button { Content = "Add Rule", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) =>
        {
            folder.Rules.Add(new FolderRule { Criterion = RuleCriterion.Extension, Operator = RuleOperator.Is, Value = "pdf" });
            BuildRules(folder, panel);
        };
        panel.Children.Add(add);
    }

    private FrameworkElement RuleRow(WatchedFolder folder, FolderRule rule, StackPanel panel)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        var criterion = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var (value, text) in new[] { (RuleCriterion.FileName, "File name"), (RuleCriterion.Extension, "File extension"), (RuleCriterion.Kind, "Kind"), (RuleCriterion.Size, "File size (MB)") })
            criterion.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        criterion.SelectedItem = criterion.Items.OfType<ComboBoxItem>().First(i => (RuleCriterion)i.Tag == rule.Criterion);

        var op = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0) };
        var valueHost = new ContentControl { MinWidth = 140, Focusable = false };

        void RebuildOperatorAndValue()
        {
            var ops = rule.Criterion switch
            {
                RuleCriterion.Kind => new[] { (RuleOperator.Is, "Is"), (RuleOperator.IsNot, "Is not") },
                RuleCriterion.Size => new[] { (RuleOperator.GreaterThan, "Greater than"), (RuleOperator.LessThan, "Less than") },
                _ => new[]
                {
                    (RuleOperator.Is, "Is"), (RuleOperator.IsNot, "Is not"), (RuleOperator.Contains, "Contains"),
                    (RuleOperator.DoesNotContain, "Does not contain"), (RuleOperator.BeginsWith, "Begins with"), (RuleOperator.EndsWith, "Ends with"),
                },
            };
            if (ops.All(o => o.Item1 != rule.Operator)) rule.Operator = ops[0].Item1;
            op.Items.Clear();
            foreach (var (value, text) in ops) op.Items.Add(new ComboBoxItem { Content = text, Tag = value });
            op.SelectedItem = op.Items.OfType<ComboBoxItem>().First(i => (RuleOperator)i.Tag == rule.Operator);

            if (rule.Criterion == RuleCriterion.Kind)
            {
                var kinds = new ComboBox { MinWidth = 140 };
                foreach (var (value, text) in FileKinds.RuleKinds) kinds.Items.Add(new ComboBoxItem { Content = text, Tag = value });
                if (FileKinds.RuleKinds.All(k => k.Value != rule.Value)) rule.Value = "Image";
                kinds.SelectedItem = kinds.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == rule.Value);
                kinds.SelectionChanged += (_, _) => rule.Value = (string)((ComboBoxItem)kinds.SelectedItem).Tag;
                valueHost.Content = kinds;
            }
            else
            {
                if (rule.Criterion == RuleCriterion.Size && !double.TryParse(rule.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    rule.Value = "10";
                var box = new TextBox { Text = rule.Value, MinWidth = 140 };
                box.TextChanged += (_, _) => rule.Value = box.Text;
                valueHost.Content = box;
            }
        }

        criterion.SelectionChanged += (_, _) =>
        {
            rule.Criterion = (RuleCriterion)((ComboBoxItem)criterion.SelectedItem).Tag;
            RebuildOperatorAndValue();
        };
        op.SelectionChanged += (_, _) =>
        {
            if (op.SelectedItem is ComboBoxItem item) rule.Operator = (RuleOperator)item.Tag;
        };
        RebuildOperatorAndValue();

        var remove = new Button { Style = (Style)FindResource("IconButton"), Content = "", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove rule" };
        remove.Click += (_, _) =>
        {
            folder.Rules.Remove(rule);
            BuildRules(folder, panel);
        };

        row.Children.Add(criterion);
        row.Children.Add(op);
        row.Children.Add(valueHost);
        row.Children.Add(remove);
        return row;
    }

    // =====================================================================================
    // About
    // =====================================================================================

    private void BuildAbout()
    {
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3) ?? "1.0.0"}";
        try
        {
            AppIcon.Source = BitmapFrame.Create(new Uri("pack://application:,,,/Dropshelf;component/Assets/Dropshelf.ico"));
        }
        catch
        {
            // icon is decorative
        }
    }

    private void OnQuickStart(object sender, RoutedEventArgs e) => new WelcomeWindow { Owner = this }.Show();

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => ShellOperations.OpenFolder(AppPaths.Roaming);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => ShellOperations.OpenFolder(AppPaths.LogsDir);
}
