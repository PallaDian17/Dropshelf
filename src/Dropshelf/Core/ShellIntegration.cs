using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Dropshelf.Native;
using Microsoft.Win32;

namespace Dropshelf.Core;

/// <summary>
/// Windows equivalents of Dropover's system hooks: login item, the "Add to Dropover" Services
/// menu (here: Explorer context menu and Send To), all per-user, no admin rights needed.
/// </summary>
public static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string FileVerbKey = @"Software\Classes\*\shell\Dropshelf";
    private const string FolderVerbKey = @"Software\Classes\Directory\shell\Dropshelf";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Dropshelf.exe");

    private static string SendToShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Dropshelf.lnk");

    // ---- Launch at login --------------------------------------------------------------
    public static bool IsLaunchAtLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppPaths.AppName) is string;
    }

    public static bool SetLaunchAtLogin(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(AppPaths.AppName, $"\"{ExecutablePath}\" --background");
            else key.DeleteValue(AppPaths.AppName, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Launch at login could not be changed", ex);
            return false;
        }
    }

    // ---- Explorer context menu --------------------------------------------------------
    public static bool IsContextMenuInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(FileVerbKey);
        return key is not null;
    }

    public static bool SetContextMenu(bool enabled)
    {
        try
        {
            foreach (var path in new[] { FileVerbKey, FolderVerbKey })
            {
                if (!enabled)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(path, false);
                    continue;
                }

                using var verb = Registry.CurrentUser.CreateSubKey(path);
                verb.SetValue("", "Add to Dropshelf");
                verb.SetValue("Icon", $"\"{ExecutablePath}\",0");
                verb.SetValue("MultiSelectModel", "Player");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", $"\"{ExecutablePath}\" --add \"%1\"");
            }
            NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Context menu registration failed", ex);
            return false;
        }
    }

    // ---- Send To ------------------------------------------------------------------------
    public static bool IsSendToInstalled() => File.Exists(SendToShortcut);

    public static bool SetSendTo(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(SendToShortcut)) File.Delete(SendToShortcut);
                return true;
            }

            var link = (IShellLinkW)new ShellLinkCoClass();
            link.SetPath(ExecutablePath);
            link.SetArguments("--add");
            link.SetDescription("Add to a Dropshelf shelf");
            link.SetIconLocation(ExecutablePath, 0);
            link.SetWorkingDirectory(Path.GetDirectoryName(ExecutablePath) ?? "");
            ((IPersistFile)link).Save(SendToShortcut, true);
            Marshal.ReleaseComObject(link);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Send To shortcut failed", ex);
            return false;
        }
    }

    /// <summary>Keeps registrations pointing at the current exe after the app was moved or updated.</summary>
    public static void RefreshRegistrations()
    {
        if (IsContextMenuInstalled()) SetContextMenu(true);
        if (IsSendToInstalled()) SetSendTo(true);
        if (IsLaunchAtLoginEnabled()) SetLaunchAtLogin(true);
    }
}
