namespace Dropshelf.Core;

public enum FileCategory
{
    Folder,
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Presentation,
    Spreadsheet,
    Archive,
    Text,
    Application,
    Other,
    TextSnippet,
    Link,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts)
        {
            foreach (var e in exts) map["." + e] = c;
        }

        Add(FileCategory.Image, "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "webp", "heic", "heif",
            "avif", "ico", "svg", "jxr", "wdp", "hdp", "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "psd");
        Add(FileCategory.Video, "mp4", "m4v", "mov", "avi", "mkv", "wmv", "webm", "mpg", "mpeg", "flv", "3gp", "ts", "mts", "m2ts");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "aac", "m4a", "ogg", "oga", "wma", "opus", "aiff", "aif", "mid", "midi");
        Add(FileCategory.Pdf, "pdf");
        Add(FileCategory.Document, "doc", "docx", "docm", "dot", "dotx", "rtf", "odt", "pages", "epub", "xps", "oxps");
        Add(FileCategory.Presentation, "ppt", "pptx", "pptm", "pps", "ppsx", "odp", "key");
        Add(FileCategory.Spreadsheet, "xls", "xlsx", "xlsm", "xlsb", "ods", "numbers");
        Add(FileCategory.Archive, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "iso", "img", "wim");
        Add(FileCategory.Text, "txt", "md", "markdown", "csv", "tsv", "json", "xml", "yaml", "yml", "ini", "cfg", "conf", "log",
            "html", "htm", "css", "js", "ts", "tsx", "jsx", "cs", "py", "java", "c", "h", "cpp", "hpp", "go", "rs", "rb", "php",
            "sh", "ps1", "psm1", "bat", "cmd", "sql", "toml", "srt", "vtt", "tex", "swift", "kt");
        Add(FileCategory.Application, "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "lnk", "com", "scr");
        return map;
    }

    /// <summary>Text extensions that hold human-readable content (used by "Copy Text Content").</summary>
    public static bool IsReadableText(string path) => Of(path) == FileCategory.Text;

    public static FileCategory Of(string path)
    {
        if (Directory.Exists(path)) return FileCategory.Folder;
        return ByExtension.TryGetValue(Path.GetExtension(path), out var c) ? c : FileCategory.Other;
    }

    public static bool IsImage(string path) => Of(path) == FileCategory.Image;

    /// <summary>Images that Windows Imaging Component can decode without extra codecs.</summary>
    public static bool IsEditableImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".gif" or ".bmp" or ".dib" or ".tif" or ".tiff"
            or ".jxr" or ".wdp" or ".hdp" or ".ico" or ".webp" or ".heic" or ".heif" or ".avif";
    }

    public static string Singular(FileCategory c) => c switch
    {
        FileCategory.Folder => "Folder",
        FileCategory.Image => "Image",
        FileCategory.Video => "Video",
        FileCategory.Audio => "Audio File",
        FileCategory.Pdf => "PDF",
        FileCategory.Document => "Document",
        FileCategory.Presentation => "Presentation",
        FileCategory.Spreadsheet => "Spreadsheet",
        FileCategory.Archive => "Archive",
        FileCategory.Text => "Text File",
        FileCategory.Application => "App",
        FileCategory.TextSnippet => "Text Snippet",
        FileCategory.Link => "Link",
        _ => "File",
    };

    public static string Plural(FileCategory c) => c switch
    {
        FileCategory.Pdf => "PDFs",
        _ => Singular(c) + "s",
    };

    public static string Count(int n, FileCategory c) => n == 1 ? $"1 {Singular(c)}" : $"{n} {Plural(c)}";

    /// <summary>Kinds offered by folder-monitoring rules (mirrors Dropover's rule editor).</summary>
    public static IReadOnlyList<(string Value, string Title)> RuleKinds { get; } =
    [
        ("Application", "Application"),
        ("Archive", "Archive"),
        ("Audio", "Audio"),
        ("Document", "Document"),
        ("Folder", "Folder"),
        ("Image", "Image"),
        ("Pdf", "PDF"),
        ("Presentation", "Presentation"),
        ("Text", "Text"),
        ("Video", "Video"),
    ];

    public static bool MatchesRuleKind(string path, string kind)
    {
        var c = Of(path);
        return kind switch
        {
            "Document" => c is FileCategory.Document or FileCategory.Spreadsheet or FileCategory.Pdf or FileCategory.Presentation,
            _ => Enum.TryParse<FileCategory>(kind, true, out var k) && k == c,
        };
    }

    /// <summary>Segoe Fluent Icons / MDL2 glyph for a category.</summary>
    public static string Glyph(FileCategory c) => c switch
    {
        FileCategory.Folder => "",
        FileCategory.Image => "",
        FileCategory.Video => "",
        FileCategory.Audio => "",
        FileCategory.Pdf => "",
        FileCategory.Document => "",
        FileCategory.Presentation => "",
        FileCategory.Spreadsheet => "",
        FileCategory.Archive => "",
        FileCategory.Text => "",
        FileCategory.Application => "",
        FileCategory.TextSnippet => "",
        FileCategory.Link => "",
        _ => "",
    };
}
