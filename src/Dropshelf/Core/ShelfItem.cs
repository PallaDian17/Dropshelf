using System.Text;
using System.Windows.Media;

namespace Dropshelf.Core;

public enum ShelfItemKind { File, Folder, Text, Url }

/// <summary>
/// One thing on a shelf. Files and folders are references (nothing is copied); text snippets
/// and links are written to a file only when something needs a path (drag-out, actions).
/// </summary>
public sealed class ShelfItem : ObservableObject
{
    private string? _path;
    private ImageSource? _thumbnail;
    private bool _isMissing;
    private long? _size;

    private ShelfItem(ShelfItemKind kind) => Kind = kind;

    public Guid Id { get; } = Guid.NewGuid();
    public ShelfItemKind Kind { get; }

    /// <summary>File system path for files and folders; the materialized file for text and links once created.</summary>
    public string? Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value)) return;
            _size = null;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string? Text { get; private init; }
    public string? Rtf { get; private init; }
    public string? Html { get; private init; }
    public string? Url { get; private init; }
    public string? UrlTitle { get; private init; }

    /// <summary>True when the file lives in Dropshelf's own data folder (created from raw dropped data).</summary>
    public bool IsTemporary => _path is not null && AppPaths.IsInsideAppData(_path);

    public bool IsFileBased => Kind is ShelfItemKind.File or ShelfItemKind.Folder;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (Set(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool IsMissing { get => _isMissing; set => Set(ref _isMissing, value); }

    public FileCategory Category => Kind switch
    {
        ShelfItemKind.Text => FileCategory.TextSnippet,
        ShelfItemKind.Url => FileCategory.Link,
        ShelfItemKind.Folder => FileCategory.Folder,
        _ => _path is null ? FileCategory.Other : FileKinds.Of(_path),
    };

    public string Glyph => FileKinds.Glyph(Category);

    public bool IsImage => Kind == ShelfItemKind.File && Category == FileCategory.Image;

    public string DisplayName => Kind switch
    {
        ShelfItemKind.File or ShelfItemKind.Folder => System.IO.Path.GetFileName(_path?.TrimEnd('\\')) is { Length: > 0 } n ? n : _path ?? "",
        ShelfItemKind.Text => FirstLine(Text, 60),
        ShelfItemKind.Url => !string.IsNullOrWhiteSpace(UrlTitle) ? UrlTitle! : HostOf(Url),
        _ => "",
    };

    public string Subtitle => Kind switch
    {
        ShelfItemKind.Text => $"Text · {Text?.Length ?? 0:N0} characters",
        ShelfItemKind.Url => Url ?? "",
        ShelfItemKind.Folder => "Folder",
        _ => SizeBytes is { } s ? $"{FileKinds.Singular(Category)} · {AppPaths.FormatBytes(s)}" : FileKinds.Singular(Category),
    };

    public long? SizeBytes
    {
        get
        {
            if (_size is null && Kind == ShelfItemKind.File && _path is not null)
            {
                try
                {
                    var fi = new FileInfo(_path);
                    if (fi.Exists) _size = fi.Length;
                }
                catch
                {
                    // inaccessible
                }
            }
            return _size;
        }
    }

    public bool Exists => Kind switch
    {
        ShelfItemKind.File => _path is not null && File.Exists(_path),
        ShelfItemKind.Folder => _path is not null && Directory.Exists(_path),
        _ => true,
    };

    public static ShelfItem FromPath(string path)
    {
        var full = path;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch
        {
            // keep as-is
        }
        return new ShelfItem(Directory.Exists(full) ? ShelfItemKind.Folder : ShelfItemKind.File) { _path = full };
    }

    public static ShelfItem FromText(string text, string? rtf = null, string? html = null)
        => new(ShelfItemKind.Text) { Text = text, Rtf = rtf, Html = html };

    public static ShelfItem FromUrl(string url, string? title = null)
        => new(ShelfItemKind.Url) { Url = url.Trim(), UrlTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim() };

    /// <summary>A path that can be handed to Explorer or other apps, writing text/links to disk on first use.</summary>
    public string GetOrCreateFilePath()
    {
        if (_path is not null && (IsFileBased || File.Exists(_path))) return _path;

        switch (Kind)
        {
            case ShelfItemKind.Text:
            {
                var name = AppPaths.SanitizeFileName(FirstLine(Text, 40), "Text") + ".txt";
                var file = AppPaths.UniquePath(AppPaths.TextFilesDir, name);
                File.WriteAllText(file, Text ?? "", new UTF8Encoding(true));
                _path = file;
                return file;
            }
            case ShelfItemKind.Url:
            {
                var name = AppPaths.SanitizeFileName(DisplayName, "Link") + ".url";
                var file = AppPaths.UniquePath(AppPaths.TextFilesDir, name);
                File.WriteAllText(file, $"[InternetShortcut]\r\nURL={Url}\r\n");
                _path = file;
                return file;
            }
            default:
                return _path ?? "";
        }
    }

    /// <summary>Key used to avoid adding the same thing twice to a shelf.</summary>
    public string IdentityKey => Kind switch
    {
        ShelfItemKind.Text => "text:" + Text,
        ShelfItemKind.Url => "url:" + Url,
        _ => "path:" + _path?.ToUpperInvariant(),
    };

    public ItemSnapshot ToSnapshot() => new()
    {
        Kind = Kind,
        Path = IsFileBased ? _path : null,
        Text = Text,
        Url = Url,
        Title = UrlTitle,
    };

    public static ShelfItem? FromSnapshot(ItemSnapshot s)
    {
        switch (s.Kind)
        {
            case ShelfItemKind.Text when s.Text is not null:
                return FromText(s.Text);
            case ShelfItemKind.Url when s.Url is not null:
                return FromUrl(s.Url, s.Title);
            case ShelfItemKind.File or ShelfItemKind.Folder when s.Path is not null:
                return File.Exists(s.Path) || Directory.Exists(s.Path) ? FromPath(s.Path) : null;
            default:
                return null;
        }
    }

    public void RefreshSize()
    {
        _size = null;
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(Subtitle));
    }

    private static string FirstLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Text";
        var line = text.TrimStart().Split('\n')[0].Trim();
        if (line.Length == 0) line = text.Trim();
        return line.Length > max ? line[..max].TrimEnd() + "…" : line;
    }

    private static string HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url ?? "Link";
}

public sealed class ItemSnapshot
{
    public ShelfItemKind Kind { get; set; }
    public string? Path { get; set; }
    public string? Text { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
}
