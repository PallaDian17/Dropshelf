using System.Windows.Input;
using System.Windows.Interop;
using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>System-wide keyboard shortcuts via RegisterHotKey on a message-only window.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _registered = new();
    private int _nextId = 0x2000;

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("DropshelfHotkeys")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event Action<string>? Pressed;

    /// <summary>Re-registers all enabled shortcuts. Returns the ids that the system refused.</summary>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, HotkeyBinding> bindings)
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (var (id, binding) in bindings)
        {
            if (!binding.Enabled || binding.Key == Key.None) continue;
            if (!TryRegister(id, binding)) failed.Add(id);
        }
        return failed;
    }

    public bool TryRegister(string name, HotkeyBinding binding)
    {
        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, ToNative(binding.Modifiers) | NativeMethods.MOD_NOREPEAT, vk))
        {
            Log.Warn($"Hotkey {binding} for {name} is in use by another app");
            return false;
        }
        _registered[id] = name;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private static uint ToNative(ModifierKeys m)
    {
        uint result = 0;
        if (m.HasFlag(ModifierKeys.Alt)) result |= NativeMethods.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Control)) result |= NativeMethods.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Shift)) result |= NativeMethods.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) result |= NativeMethods.MOD_WIN;
        return result;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var name))
        {
            handled = true;
            Pressed?.Invoke(name);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
