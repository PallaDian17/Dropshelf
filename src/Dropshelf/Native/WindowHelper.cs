using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dropshelf.Core;

namespace Dropshelf.Native;

/// <summary>
/// Window placement in physical pixels. WPF's Left/Top are DIPs relative to a DPI that changes
/// between monitors, so shelves are positioned with SetWindowPos instead.
/// </summary>
public static class WindowHelper
{
    private static readonly Dictionary<IntPtr, MoveAnimation> Animations = new();
    private static readonly Dictionary<uint, string> ProcessNames = new();
    private static bool _renderingHooked;

    private sealed class MoveAnimation
    {
        public required IntPtr Hwnd { get; init; }
        public required int FromX { get; init; }
        public required int FromY { get; init; }
        public required int ToX { get; init; }
        public required int ToY { get; init; }
        public required double DurationMs { get; init; }
        public required IEasingFunction Ease { get; init; }
        public Action? Completed { get; init; }
        public Stopwatch? Clock { get; set; }
    }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    public static void HideFromAltTab(Window w)
    {
        var hwnd = Handle(w);
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex = (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    public static void UseDarkTitleBar(Window w, bool dark)
    {
        var value = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(Handle(w), NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public static NativeMethods.POINT CursorPosition()
    {
        NativeMethods.GetCursorPos(out var p);
        return p;
    }

    public static NativeMethods.RECT GetRect(IntPtr hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var r);
        return r;
    }

    public static NativeMethods.RECT WorkAreaAt(NativeMethods.POINT p)
    {
        var monitor = NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        return info.rcWork;
    }

    public static NativeMethods.RECT MonitorBoundsAt(NativeMethods.POINT p)
    {
        var monitor = NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        return info.rcMonitor;
    }

    public static bool HasMonitorAt(NativeMethods.POINT p)
        => NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero;

    public static NativeMethods.RECT WorkAreaOf(IntPtr hwnd)
    {
        var r = GetRect(hwnd);
        return WorkAreaAt(new NativeMethods.POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
    }

    /// <summary>DPI scale (1.0 = 96 DPI) of the monitor containing <paramref name="p"/>.</summary>
    public static double ScaleAt(NativeMethods.POINT p)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch
        {
            // shcore unavailable: assume 100 %
        }
        return 1.0;
    }

    public static void MoveTo(IntPtr hwnd, int x, int y)
    {
        StopAnimation(hwnd);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Moves a window to (x, y), stepping once per rendered frame (CompositionTarget.Rendering)
    /// so the motion stays in sync with the display instead of a coarse 15.6 ms timer.
    /// </summary>
    public static void AnimateTo(IntPtr hwnd, int x, int y, int durationMs = 240, Action? completed = null, IEasingFunction? ease = null)
    {
        StopAnimation(hwnd);
        var start = GetRect(hwnd);
        if (start.Left == x && start.Top == y)
        {
            completed?.Invoke();
            return;
        }
        if (!UI.Motion.Enabled || durationMs <= 0)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            completed?.Invoke();
            return;
        }

        Animations[hwnd] = new MoveAnimation
        {
            Hwnd = hwnd,
            FromX = start.Left,
            FromY = start.Top,
            ToX = x,
            ToY = y,
            DurationMs = durationMs,
            Ease = ease ?? UI.Motion.EaseOutQuint,
            Completed = completed,
        };
        if (!_renderingHooked)
        {
            _renderingHooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static readonly List<MoveAnimation> FrameBuffer = new();

    private static void OnRendering(object? sender, EventArgs e)
    {
        // Reused buffer: this runs every frame, and completions may start or stop animations.
        FrameBuffer.Clear();
        FrameBuffer.AddRange(Animations.Values);
        foreach (var a in FrameBuffer)
        {
            a.Clock ??= Stopwatch.StartNew();
            var t = Math.Min(1.0, a.Clock.Elapsed.TotalMilliseconds / a.DurationMs);
            var k = t >= 1.0 ? 1.0 : a.Ease.Ease(t);
            var cx = (int)Math.Round(a.FromX + (a.ToX - a.FromX) * k);
            var cy = (int)Math.Round(a.FromY + (a.ToY - a.FromY) * k);
            NativeMethods.SetWindowPos(a.Hwnd, IntPtr.Zero, cx, cy, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            if (t < 1.0) continue;
            if (Animations.TryGetValue(a.Hwnd, out var current) && ReferenceEquals(current, a)) Animations.Remove(a.Hwnd);
            a.Completed?.Invoke();
        }

        if (Animations.Count == 0 && _renderingHooked)
        {
            _renderingHooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    public static void StopAnimation(IntPtr hwnd) => Animations.Remove(hwnd);

    public static bool IsAnimating(IntPtr hwnd) => Animations.ContainsKey(hwnd);

    /// <summary>True while the user is dragging or resizing a window (so a shake moves a window, not files).</summary>
    public static bool IsWindowMoveSizeInProgress()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var thread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        var info = new NativeMethods.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        return NativeMethods.GetGUIThreadInfo(thread, ref info) && (info.flags & NativeMethods.GUI_INMOVESIZE) != 0;
    }

    /// <summary>Process id and executable name of the top-level window under a screen point.</summary>
    public static (uint ProcessId, string ProcessName) ProcessAt(NativeMethods.POINT p)
    {
        var hwnd = NativeMethods.WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return (0, "");
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        NativeMethods.GetWindowThreadProcessId(root == IntPtr.Zero ? hwnd : root, out var pid);
        return (pid, ProcessName(pid));
    }

    public static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        if (ProcessNames.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch
        {
            name = "";
        }
        if (ProcessNames.Count > 256) ProcessNames.Clear();
        ProcessNames[pid] = name;
        return name;
    }

    /// <summary>Brings an existing window to the front even when another app is active.</summary>
    public static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        NativeMethods.SetForegroundWindow(Handle(w));
    }
}
