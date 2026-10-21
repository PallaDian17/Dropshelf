using System.Diagnostics;
using System.Runtime.InteropServices;
using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>Explorer-backed file operations so they get native progress, conflict and undo UI.</summary>
public static class ShellOperations
{
    public static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    public static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    public static void OpenUrl(string url) => Open(url);

    /// <summary>Opens Explorer windows with the given items selected (one window per folder).</summary>
    public static void Reveal(IEnumerable<string> paths)
    {
        foreach (var group in paths.Where(p => File.Exists(p) || Directory.Exists(p))
                     .GroupBy(p => Path.GetDirectoryName(p) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            var folder = NativeMethods.ILCreateFromPathW(group.Key);
            var items = group.Select(NativeMethods.ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
            try
            {
                if (folder == IntPtr.Zero || NativeMethods.SHOpenFolderAndSelectItems(folder, (uint)items.Length, items, 0) != 0)
                    Process.Start("explorer.exe", $"/select,\"{group.First()}\"");
            }
            finally
            {
                if (folder != IntPtr.Zero) NativeMethods.ILFree(folder);
                foreach (var item in items) NativeMethods.ILFree(item);
            }
        }
    }

    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Open(dir);
    }

    public static void ShowProperties(string path, IntPtr owner)
        => NativeMethods.SHObjectProperties(owner, NativeMethods.SHOP_FILEPATH, path, null);

    public static void ShowOpenWithDialog(string path, IntPtr owner)
    {
        var info = new NativeMethods.OPENASINFO
        {
            pcszFile = path,
            pcszClass = null,
            oaifInFlags = NativeMethods.OAIF_ALLOW_REGISTRATION | NativeMethods.OAIF_EXEC,
        };
        NativeMethods.SHOpenWithDialog(owner, ref info);
    }

    /// <summary>
    /// Sends files to the Recycle Bin. Files that cannot be recycled (network shares, removable
    /// drives, oversized files) trigger Windows' "delete permanently?" warning instead of vanishing.
    /// Returns false if the user cancelled or it failed.
    /// </summary>
    public static bool Recycle(IEnumerable<string> paths, IntPtr owner)
        => FileOperation(NativeMethods.FO_DELETE, paths, null, owner,
            (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_WANTNUKEWARNING));

    public static bool Move(IEnumerable<string> paths, string destination, IntPtr owner)
        => FileOperation(NativeMethods.FO_MOVE, paths, destination, owner,
            (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMMKDIR));

    public static bool Copy(IEnumerable<string> paths, string destination, IntPtr owner)
        => FileOperation(NativeMethods.FO_COPY, paths, destination, owner,
            (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMMKDIR));

    private static bool FileOperation(uint func, IEnumerable<string> paths, string? destination, IntPtr owner, ushort flags)
    {
        var list = paths.ToList();
        if (list.Count == 0) return true;
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = func,
            pFrom = string.Join("\0", list) + "\0\0",
            pTo = destination is null ? null : destination + "\0\0",
            fFlags = flags,
        };
        var result = NativeMethods.SHFileOperation(ref op);
        if (result != 0) Log.Warn($"SHFileOperation {func} failed with 0x{result:X}");
        return result == 0 && !op.fAnyOperationsAborted;
    }

    public static bool SetWallpaper(string imagePath)
        => NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETDESKWALLPAPER, 0, imagePath,
            NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);

    public static string? KnownFolder(Guid id)
    {
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) == 0)
            {
                var path = Marshal.PtrToStringUni(ptr);
                Marshal.FreeCoTaskMem(ptr);
                return path;
            }
        }
        catch
        {
            // not available on this Windows version
        }
        return null;
    }

    public static string DefaultScreenshotsFolder()
        => KnownFolder(ScreenshotsFolderId)
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public static string DownloadsFolder()
        => KnownFolder(DownloadsFolderId)
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Builds a shell IDataObject for files (used to invoke "Open with" handlers).</summary>
    public static System.Runtime.InteropServices.ComTypes.IDataObject? CreateShellDataObject(IReadOnlyList<string> paths)
    {
        var pidls = paths.Select(NativeMethods.ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
        try
        {
            var iid = new Guid("0000010e-0000-0000-C000-000000000046");
            if (NativeMethods.SHCreateDataObject(IntPtr.Zero, (uint)pidls.Length, pidls, IntPtr.Zero, iid, out var ptr) != 0)
                return null;
            var obj = (System.Runtime.InteropServices.ComTypes.IDataObject)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            return obj;
        }
        finally
        {
            foreach (var p in pidls) NativeMethods.ILFree(p);
        }
    }
}
