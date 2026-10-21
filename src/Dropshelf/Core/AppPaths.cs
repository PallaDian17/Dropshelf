namespace Dropshelf.Core;

/// <summary>
/// All folders the app reads or writes. Roaming data (settings, scripts) follows the user;
/// local data (temporary drops, generated files, logs) stays on this machine.
/// </summary>
public static class AppPaths
{
    public const string AppName = "Dropshelf";

    public static string Roaming => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));

    public static string Local => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string RecentShelvesFile => Path.Combine(Roaming, "recent-shelves.json");
    public static string PinnedShelvesFile => Path.Combine(Roaming, "pinned-shelves.json");
    public static string ScriptsDir => Ensure(Path.Combine(Roaming, "Scripts"));

    /// <summary>Raw data dropped without a file on disk (images from browsers, virtual files, mail messages).</summary>
    public static string TempDir => Ensure(Path.Combine(Local, "Temporary"));
    public static string TextFilesDir => Ensure(Path.Combine(Local, "Text Files"));
    public static string ClipboardImagesDir => Ensure(Path.Combine(Local, "Images from Clipboard"));
    public static string ActionOutputDir => Ensure(Path.Combine(Local, "Action Output"));
    public static string LogsDir => Ensure(Path.Combine(Local, "Logs"));

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static bool IsInsideAppData(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(Local, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns <paramref name="fileName"/> inside <paramref name="dir"/>, adding " 2", " 3"… until unused.</summary>
    public static string UniquePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{name} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static string SanitizeFileName(string name, string fallback = "Untitled")
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80].Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    /// <summary>Validates a user-entered file name. Returns an error message or null.</summary>
    public static string? ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a valid file name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The file name contains a character that cannot be used.";
        if (name.Length > 240) return "The file name is too long.";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "The file name cannot end with a dot or a space.";
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3"];
        if (reserved.Contains(stem)) return "This name is reserved by Windows.";
        return null;
    }

    public static long DirectorySize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }
}
