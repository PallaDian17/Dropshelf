using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Dropshelf.Actions;
using Dropshelf.Core;
using Dropshelf.Native;

namespace Dropshelf.UI;

/// <summary>Quick Look for Windows: images, PDFs, video/audio, text, and a large icon for anything else.</summary>
public partial class PreviewWindow : Window
{
    private static PreviewWindow? _instance;
    private IReadOnlyList<ShelfItem> _items = [];
    private int _index;
    private int _loadVersion;
    private bool _mediaPlaying;
    private bool _closeAnimationDone;
    private bool _closing;

    private PreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowHelper.HideFromAltTab(this);
        PreviewKeyDown += OnKey;
        // Zooms open like Quick Look.
        Loaded += (_, _) =>
        {
            if (_closing) return;
            Motion.To(Frame, OpacityProperty, 1, 180);
            Motion.To(FrameScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 1, 460, Motion.SoftSpring, from: 0.92);
            Motion.To(FrameScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 1, 460, Motion.SoftSpring, from: 0.92);
        };
        Closed += (_, _) =>
        {
            MediaView.Close();
            _instance = null;
        };
    }

    public static void ShowItems(IReadOnlyList<ShelfItem> items, int index)
    {
        if (items.Count == 0) return;
        if (_instance is { _closing: true }) return;
        _instance ??= new PreviewWindow();
        _instance._items = items;
        _instance.Show(Math.Clamp(index, 0, items.Count - 1));
        _instance.Show();
        _instance.Activate();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAnimationDone || !Motion.Enabled || !IsVisible || Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        MediaView.Pause();
        Motion.To(FrameScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 0.95, 160, Motion.EaseIn);
        Motion.To(FrameScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 0.95, 160, Motion.EaseIn);
        Motion.To(Frame, OpacityProperty, 0, 150, Motion.EaseIn, completed: () =>
        {
            _closeAnimationDone = true;
            Close();
        });
    }

    /// <summary>Slides the new item in from the side it came from.</summary>
    private void AnimateStage(int direction)
    {
        if (direction == 0) return;
        Motion.To(Stage, OpacityProperty, 1, 200, from: 0.2);
        Motion.To(StageShift, System.Windows.Media.TranslateTransform.XProperty, 0, 420, Motion.SoftSpring, from: 24 * direction);
    }

    private void Show(int index)
    {
        AnimateStage(IsLoaded ? Math.Sign(index - _index) : 0);
        _index = index;
        var item = _items[index];
        var version = ++_loadVersion;

        MediaView.Stop();
        MediaView.Source = null;
        _mediaPlaying = false;
        ImageView.Visibility = MediaView.Visibility = TextView.Visibility = InfoView.Visibility = Visibility.Collapsed;
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _items.Count - 1;
        TitleText.Text = item.DisplayName;
        SubtitleText.Text = _items.Count > 1 ? $"{index + 1} of {_items.Count} · {item.Subtitle}" : item.Subtitle;

        switch (item.Kind)
        {
            case ShelfItemKind.Text:
                ShowText(item.Text ?? "");
                return;
            case ShelfItemKind.Url:
                ShowInfo(item, $"{item.Url}\n\nPress Open to visit the link.");
                return;
        }

        var path = item.Path!;
        if (!item.Exists)
        {
            ShowInfo(item, "This item has been moved or deleted.");
            return;
        }

        switch (item.Category)
        {
            case FileCategory.Image when FileKinds.IsEditableImage(path):
                _ = LoadImageAsync(path, version, item);
                break;
            case FileCategory.Pdf:
                _ = LoadPdfAsync(path, version, item);
                break;
            case FileCategory.Video or FileCategory.Audio:
                MediaView.Source = new Uri(path);
                MediaView.Visibility = Visibility.Visible;
                MediaView.Play();
                _mediaPlaying = true;
                break;
            case FileCategory.Text:
                ShowText(ReadTextPrefix(path));
                break;
            default:
                ShowInfo(item, Details(item));
                break;
        }
    }

    private async Task LoadImageAsync(string path, int version, ShelfItem item)
    {
        try
        {
            var image = await StaTask.Run(() => ImageHelper.LoadOriented(path));
            if (version != _loadVersion) return;
            ImageView.Source = image;
            ImageView.Visibility = Visibility.Visible;
            Motion.To(ImageView, OpacityProperty, 1, 220, from: 0);
            SubtitleText.Text += $" · {image.PixelWidth} × {image.PixelHeight}";
        }
        catch
        {
            if (version == _loadVersion) ShowInfo(item, Details(item));
        }
    }

    private async Task LoadPdfAsync(string path, int version, ShelfItem item)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            using var page = pdf.GetPage(0);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1400 });
            stream.Seek(0);

            var bitmap = new BitmapImage();
            using (var net = stream.AsStream())
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = net;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            if (version != _loadVersion) return;
            ImageView.Source = bitmap;
            ImageView.Visibility = Visibility.Visible;
            Motion.To(ImageView, OpacityProperty, 1, 220, from: 0);
            SubtitleText.Text += $" · {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}";
        }
        catch
        {
            if (version == _loadVersion) ShowInfo(item, Details(item));
        }
    }

    private void ShowText(string text)
    {
        TextView.Text = text;
        TextView.ScrollToHome();
        TextView.Visibility = Visibility.Visible;
    }

    private void ShowInfo(ShelfItem item, string text)
    {
        InfoIcon.Source = item.Kind is ShelfItemKind.Text or ShelfItemKind.Url || item.Path is null
            ? item.Thumbnail as BitmapSource
            : ThumbnailService.GetThumbnail(item.Path, 256) ?? item.Thumbnail as BitmapSource;
        InfoText.Text = text;
        InfoView.Visibility = Visibility.Visible;
    }

    private static string Details(ShelfItem item)
    {
        var sb = new StringBuilder();
        if (item.Path is not null)
        {
            sb.AppendLine(item.Path);
            try
            {
                var modified = item.Kind == ShelfItemKind.Folder ? Directory.GetLastWriteTime(item.Path) : File.GetLastWriteTime(item.Path);
                sb.Append("Modified ").Append(modified.ToString("g"));
            }
            catch
            {
                // inaccessible
            }
        }
        return sb.ToString();
    }

    private static string ReadTextPrefix(string path)
    {
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[512 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);
            return reader.EndOfStream ? text : text + "\n\n… (preview truncated)";
        }
        catch (Exception ex)
        {
            return $"Could not read this file: {ex.Message}";
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Space:
                Close();
                break;
            case Key.Left or Key.Up when _index > 0:
                Show(_index - 1);
                break;
            case Key.Right or Key.Down when _index < _items.Count - 1:
                Show(_index + 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnMediaClick(object sender, MouseButtonEventArgs e)
    {
        if (_mediaPlaying) MediaView.Pause();
        else MediaView.Play();
        _mediaPlaying = !_mediaPlaying;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnPrevious(object sender, RoutedEventArgs e)
    {
        if (_index > 0) Show(_index - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index < _items.Count - 1) Show(_index + 1);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var item = _items[_index];
        if (item.Kind == ShelfItemKind.Url && item.Url is not null) ShellOperations.OpenUrl(item.Url);
        else ShellOperations.Open(item.GetOrCreateFilePath());
    }

    private void OnReveal(object sender, RoutedEventArgs e) => ShellOperations.Reveal([_items[_index].GetOrCreateFilePath()]);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
