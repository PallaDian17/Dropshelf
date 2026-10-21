using System.Runtime.InteropServices;
using Dropshelf.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Dropshelf.Native;

/// <summary>
/// Opens the Windows share sheet (Nearby Sharing, Mail, Teams, Phone Link…), the Windows
/// counterpart of Dropover's AirDrop / Mail / Messages share actions.
/// </summary>
public static class ShareService
{
    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public static async Task<bool> ShareAsync(IntPtr hwnd, IReadOnlyList<string> paths, string? text, string title)
    {
        try
        {
            var storageItems = new List<IStorageItem>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                else if (File.Exists(path)) storageItems.Add(await StorageFile.GetFileFromPathAsync(path));
            }

            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerIid;
            var pointer = interop.GetForWindow(hwnd, ref iid);
            var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer);

            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                var data = args.Request.Data;
                data.Properties.Title = title;
                if (storageItems.Count > 0) data.SetStorageItems(storageItems);
                if (!string.IsNullOrEmpty(text)) data.SetText(text);
                manager.DataRequested -= handler;
            };
            manager.DataRequested += handler;
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Share sheet failed", ex);
            return false;
        }
    }
}

/// <summary>Opens a new message in the default desktop mail client with the files attached (Simple MAPI).</summary>
public static class MailService
{
    public static Task<bool> ComposeAsync(IReadOnlyList<string> attachments, string subject)
        => StaTask.Run(() => Compose(attachments, subject));

    private static bool Compose(IReadOnlyList<string> attachments, string subject)
    {
        var allocations = new List<IntPtr>();
        IntPtr Str(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            allocations.Add(p);
            return p;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.MapiFileDescW>();
            var files = Marshal.AllocHGlobal(size * Math.Max(1, attachments.Count));
            allocations.Add(files);
            for (var i = 0; i < attachments.Count; i++)
            {
                var desc = new NativeMethods.MapiFileDescW
                {
                    nPosition = uint.MaxValue,
                    lpszPathName = Str(attachments[i]),
                    lpszFileName = Str(Path.GetFileName(attachments[i])),
                };
                Marshal.StructureToPtr(desc, files + i * size, false);
            }

            var message = new NativeMethods.MapiMessageW
            {
                lpszSubject = Str(subject),
                lpszNoteText = Str(""),
                nFileCount = (uint)attachments.Count,
                lpFiles = attachments.Count > 0 ? files : IntPtr.Zero,
            };
            // Modeless returns once the compose window is open, so the shelf is not blocked while
            // the user writes the message. Older mail clients only support the modal dialog.
            var result = NativeMethods.MAPISendMailW(IntPtr.Zero, IntPtr.Zero, ref message,
                NativeMethods.MAPI_LOGON_UI | NativeMethods.MAPI_DIALOG_MODELESS, 0);
            if (result > 1)
            {
                result = NativeMethods.MAPISendMailW(IntPtr.Zero, IntPtr.Zero, ref message,
                    NativeMethods.MAPI_LOGON_UI | NativeMethods.MAPI_DIALOG, 0);
            }
            // 0 = success, 1 = user aborted.
            if (result > 1) Log.Warn($"MAPISendMailW returned {result}");
            return result <= 1;
        }
        catch (Exception ex)
        {
            Log.Error("Mail compose failed", ex);
            return false;
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }
}
