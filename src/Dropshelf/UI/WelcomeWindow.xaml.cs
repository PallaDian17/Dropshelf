using System.Windows;
using System.Windows.Shapes;
using Dropshelf.Core;
using Dropshelf.Native;

namespace Dropshelf.UI;

public partial class WelcomeWindow : Window
{
    private static readonly (string Glyph, string Title, string Text)[] Pages =
    [
        ("", "Drag and drop, minus the juggling",
            "Dropshelf gives you small floating windows called shelves. Park files, images, text and links on a shelf, then drop them wherever you need them."),
        ("", "Shake to make a shelf",
            "Start dragging something from Explorer, a browser or any app, then shake the mouse left and right. A shelf appears right under the cursor — just let go."),
        ("", "A shelf that does the work",
            "Click the arrow on a shelf to resize or convert images, make ZIP files and PDFs, extract text, rename in bulk, share, or run your own scripts."),
        ("", "Always one click away",
            "Dropshelf lives in the notification area. Use it for new shelves, recent and pinned shelves, and Settings. Try Ctrl+Alt+D for a new shelf anywhere."),
    ];

    private int _page;

    public WelcomeWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowHelper.UseDarkTitleBar(this, ThemeManager.IsDark);
        Render();
    }

    private void Render()
    {
        var (glyph, title, text) = Pages[_page];
        Illustration.Text = glyph;
        PageTitle.Text = title;
        PageText.Text = text;
        BackButton.Visibility = _page == 0 ? Visibility.Hidden : Visibility.Visible;
        SkipButton.Visibility = _page == Pages.Length - 1 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = _page == 0 ? "Get Started" : _page == Pages.Length - 1 ? "Done" : "Next";

        Dots.Children.Clear();
        for (var i = 0; i < Pages.Length; i++)
        {
            Dots.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = i == _page ? ThemeManager.Brush("AccentBrush") : ThemeManager.Brush("TextTertiaryBrush"),
            });
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_page < Pages.Length - 1)
        {
            _page++;
            Render();
            return;
        }
        Finish();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_page == 0) return;
        _page--;
        Render();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        var firstRun = !SettingsStore.Current.FirstRunCompleted;
        SettingsStore.Current.FirstRunCompleted = true;
        Close();

        if (firstRun && !ShellIntegration.IsLaunchAtLoginEnabled())
        {
            var answer = MessageBox.Show("Would you like to start Dropshelf when you sign in to Windows?\n\nYou can change this later in Settings.",
                "Dropshelf", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes && ShellIntegration.SetLaunchAtLogin(true))
                SettingsStore.Current.LaunchAtLogin = true;
        }
    }
}
