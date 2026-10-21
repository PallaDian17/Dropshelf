using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>
/// Reads "virtual" files that exist only as data in a drag (Outlook attachments and messages,
/// files inside ZIP folders, images dragged from browsers) and writes them to a temp folder.
/// </summary>
public static class VirtualFileReader
{
    private const int FileDescriptorSize = 592; // sizeof(FILEDESCRIPTORW)
    private const int FileNameOffset = 72;
    private const int AttributesOffset = 36;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    public const string DescriptorFormat = "FileGroupDescriptorW";
    public const string ContentsFormat = "FileContents";

    public static bool HasVirtualFiles(System.Windows.IDataObject data)
        => data.GetDataPresent(DescriptorFormat);

    public static IReadOnlyList<string> ReadDescriptorNames(System.Windows.IDataObject data)
    {
        try
        {
            if (data.GetData(DescriptorFormat) is not MemoryStream ms) return [];
            return ParseDescriptor(ms.ToArray()).Select(d => d.Name).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Extracts all virtual files into a new folder below <paramref name="targetRoot"/>; returns top-level paths.</summary>
    public static IReadOnlyList<string> Extract(System.Windows.IDataObject data, string targetRoot)
    {
        var results = new List<string>();
        if (data.GetData(DescriptorFormat) is not MemoryStream ms) return results;
        if (data is not IDataObject com) return results;

        var descriptors = ParseDescriptor(ms.ToArray());
        if (descriptors.Count == 0) return results;

        var batchDir = AppPaths.Ensure(Path.Combine(targetRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
        var contentsFormat = (short)NativeMethods.RegisterClipboardFormat(ContentsFormat);

        foreach (var d in descriptors)
        {
            var relative = string.Join(Path.DirectorySeparatorChar,
                d.Name.Split('\\', '/').Select(part => AppPaths.SanitizeFileName(part, "File")));
            var target = Path.Combine(batchDir, relative);

            try
            {
                if (d.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    WriteContents(com, contentsFormat, d.Index, target);
                }

                // Only report top-level entries; nested files come along inside their folder.
                if (!relative.Contains(Path.DirectorySeparatorChar)) results.Add(target);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not extract virtual file '{d.Name}': {ex.Message}");
            }
        }

        return results;
    }

    private static void WriteContents(IDataObject com, short format, int index, string target)
    {
        var formatEtc = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTORAGE,
        };

        com.GetData(ref formatEtc, out var medium);
        try
        {
            switch (medium.tymed)
            {
                case TYMED.TYMED_ISTREAM:
                {
                    var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                    using var file = File.Create(target);
                    var buffer = new byte[81920];
                    var readPtr = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        while (true)
                        {
                            stream.Read(buffer, buffer.Length, readPtr);
                            var read = Marshal.ReadInt32(readPtr);
                            if (read <= 0) break;
                            file.Write(buffer, 0, read);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(readPtr);
                        Marshal.ReleaseComObject(stream);
                    }
                    break;
                }
                case TYMED.TYMED_HGLOBAL:
                {
                    var ptr = NativeMethods.GlobalLock(medium.unionmember);
                    try
                    {
                        var size = (int)NativeMethods.GlobalSize(medium.unionmember).ToUInt64();
                        var bytes = new byte[size];
                        Marshal.Copy(ptr, bytes, 0, size);
                        File.WriteAllBytes(target, bytes);
                    }
                    finally
                    {
                        NativeMethods.GlobalUnlock(medium.unionmember);
                    }
                    break;
                }
                case TYMED.TYMED_ISTORAGE:
                {
                    // Outlook hands over e-mail messages as structured storage (.msg).
                    var source = (IStorage)Marshal.GetObjectForIUnknown(medium.unionmember);
                    var hr = NativeMethods.StgCreateDocfile(target,
                        NativeMethods.STGM_CREATE | NativeMethods.STGM_READWRITE | NativeMethods.STGM_SHARE_EXCLUSIVE, 0, out var destination);
                    if (hr != 0) throw new IOException($"StgCreateDocfile failed 0x{hr:X}");
                    try
                    {
                        source.CopyTo(0, IntPtr.Zero, IntPtr.Zero, destination);
                        destination.Commit(0);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(destination);
                        Marshal.ReleaseComObject(source);
                    }
                    break;
                }
            }
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    /// <param name="Index">Position in the descriptor, which is the FileContents lindex.</param>
    private sealed record Descriptor(int Index, string Name, bool IsDirectory);

    private static List<Descriptor> ParseDescriptor(byte[] bytes)
    {
        var list = new List<Descriptor>();
        if (bytes.Length < 4) return list;
        var count = BitConverter.ToInt32(bytes, 0);
        for (var i = 0; i < count; i++)
        {
            var offset = 4 + i * FileDescriptorSize;
            if (offset + FileDescriptorSize > bytes.Length) break;
            var attributes = BitConverter.ToUInt32(bytes, offset + AttributesOffset);
            var name = Encoding.Unicode.GetString(bytes, offset + FileNameOffset, 520);
            var end = name.IndexOf('\0');
            if (end >= 0) name = name[..end];
            if (name.Length == 0) continue;
            list.Add(new Descriptor(i, name, (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0));
        }
        return list;
    }
}
