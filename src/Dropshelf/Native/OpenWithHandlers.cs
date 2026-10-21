using System.Runtime.InteropServices;
using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>Recommended "Open with" apps for a file type, as listed in Explorer's Open with menu.</summary>
public static class OpenWithHandlers
{
    public sealed class Handler
    {
        internal Handler(IAssocHandler com, string name) => (Com, Name) = (com, name);

        internal IAssocHandler Com { get; }
        public string Name { get; }
    }

    public static IReadOnlyList<Handler> ForExtension(string extension)
    {
        var result = new List<Handler>();
        if (string.IsNullOrEmpty(extension)) return result;
        try
        {
            if (NativeMethods.SHAssocEnumHandlers(extension, NativeMethods.ASSOC_FILTER_RECOMMENDED, out var enumerator) != 0)
                return result;
            var buffer = new IAssocHandler[1];
            while (enumerator.Next(1, buffer, out var fetched) == 0 && fetched == 1)
            {
                var handler = buffer[0];
                if (handler.GetUIName(out var name) == 0 && !string.IsNullOrWhiteSpace(name)
                    && result.All(r => r.Name != name))
                {
                    result.Add(new Handler(handler, name));
                }
                if (result.Count >= 12) break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Open-with handlers unavailable for {extension}: {ex.Message}");
        }
        return result;
    }

    public static bool Invoke(Handler handler, IReadOnlyList<string> paths)
    {
        try
        {
            var data = ShellOperations.CreateShellDataObject(paths);
            if (data is null) return false;
            var hr = handler.Com.Invoke(data);
            Marshal.ReleaseComObject(data);
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Open with {handler.Name} failed", ex);
            return false;
        }
    }
}
