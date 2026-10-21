using System.Runtime.InteropServices;
using Dropshelf.Core;

namespace Dropshelf.Native;

public readonly record struct MouseHookEvent(int Message, int X, int Y, uint Time);

/// <summary>
/// Global WH_MOUSE_LL hook on its own thread with its own message loop, so a busy UI thread
/// can never delay system-wide mouse input (Windows silently drops slow low-level hooks).
/// <see cref="Event"/> is raised on the hook thread and must return quickly.
/// </summary>
public sealed class LowLevelMouseHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    public LowLevelMouseHook() => _proc = HookProc;

    public event Action<MouseHookEvent>? Event;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Dropshelf mouse hook" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        _ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is NativeMethods.WM_MOUSEMOVE or NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                try
                {
                    Event?.Invoke(new MouseHookEvent(message, data.pt.X, data.pt.Y, data.time));
                }
                catch (Exception ex)
                {
                    Log.Error("Mouse hook handler failed", ex);
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }
}
