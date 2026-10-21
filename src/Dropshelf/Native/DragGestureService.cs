using System.Diagnostics;
using System.Windows.Threading;
using Dropshelf.Core;

namespace Dropshelf.Native;

public enum GestureSource { Shake, Modifier }

public readonly record struct GestureTrigger(GestureSource Source, NativeMethods.POINT Cursor, NativeMethods.POINT DragStart);

/// <summary>
/// Watches every left-button drag on the desktop and reports when the user shakes the cursor
/// (or holds the activation modifier) mid-drag. Windows exposes no "a file drag is in progress"
/// signal, so window moves, our own windows, and ignored apps are filtered on the UI thread.
/// </summary>
public sealed class DragGestureService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly LowLevelMouseHook _hook = new();
    private readonly ShakeDetector _shake = new();
    private readonly int _ownProcessId = Environment.ProcessId;

    // Hook-thread state.
    private bool _pressed;
    private bool _dragging;
    private bool _triggered;
    private NativeMethods.POINT _down;
    private double _scale = 1;

    // Settings snapshot, written on the UI thread and read on the hook thread.
    private volatile bool _shakeEnabled;
    private volatile int _modifier;
    private volatile int _sensitivity;

    public DragGestureService(Dispatcher ui)
    {
        _ui = ui;
        _hook.Event += OnHookEvent;
    }

    /// <summary>A drag has begun anywhere on screen (UI thread).</summary>
    public event Action? DragStarted;

    /// <summary>The left button was released after a drag (UI thread).</summary>
    public event Action? DragEnded;

    /// <summary>A shake or modifier gesture that passed all filters (UI thread).</summary>
    public event Action<GestureTrigger>? Triggered;

    public bool IsDragging { get; private set; }

    public void Start()
    {
        ApplySettings(SettingsStore.Current);
        _hook.Start();
    }

    public void ApplySettings(AppSettings s)
    {
        _shakeEnabled = s.ShakeEnabled;
        _modifier = (int)s.ActivationModifier;
        _sensitivity = (int)s.ShakeSensitivity;
    }

    private void OnHookEvent(MouseHookEvent e)
    {
        switch (e.Message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                _pressed = true;
                _dragging = false;
                _triggered = false;
                _down = new NativeMethods.POINT(e.X, e.Y);
                _scale = WindowHelper.ScaleAt(_down);
                _shake.Configure((ShakeSensitivity)_sensitivity, _scale);
                _shake.Reset();
                break;

            case NativeMethods.WM_MOUSEMOVE when _pressed:
            {
                var dx = e.X - _down.X;
                var dy = e.Y - _down.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (!_dragging)
                {
                    if (distance < 6 * _scale) return;
                    _dragging = true;
                    _ui.BeginInvoke(() =>
                    {
                        IsDragging = true;
                        DragStarted?.Invoke();
                    });
                }

                if (_triggered) return;

                GestureSource? source = null;
                if (_shakeEnabled && _shake.Add(e.X, e.Y, e.Time))
                    source = GestureSource.Shake;
                else if (_modifier != (int)ActivationModifier.None && distance > 24 * _scale && ModifierDown((ActivationModifier)_modifier))
                    source = GestureSource.Modifier;

                if (source is { } s)
                {
                    _triggered = true;
                    var trigger = new GestureTrigger(s, new NativeMethods.POINT(e.X, e.Y), _down);
                    _ui.BeginInvoke(() => OnTriggered(trigger));
                }
                break;
            }

            case NativeMethods.WM_LBUTTONUP:
                var wasDragging = _dragging;
                _pressed = false;
                _dragging = false;
                if (wasDragging)
                {
                    _ui.BeginInvoke(() =>
                    {
                        IsDragging = false;
                        DragEnded?.Invoke();
                    });
                }
                break;
        }
    }

    private static bool ModifierDown(ActivationModifier m) => m switch
    {
        ActivationModifier.Alt => NativeMethods.IsKeyDown(NativeMethods.VK_MENU),
        ActivationModifier.Control => NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL),
        ActivationModifier.Shift => NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT),
        _ => false,
    };

    private void OnTriggered(GestureTrigger trigger)
    {
        if (!IsDragging) return;

        // Shaking a window by its title bar is a window move, not a file drag.
        if (WindowHelper.IsWindowMoveSizeInProgress()) return;

        var (pid, name) = WindowHelper.ProcessAt(trigger.DragStart);
        if (pid == _ownProcessId) return;
        if (name.Length > 0 && SettingsStore.Current.IgnoredApplications
                .Any(a => string.Equals(Path.GetFileNameWithoutExtension(a), name, StringComparison.OrdinalIgnoreCase)))
        {
            Debug.WriteLine($"Gesture ignored for {name}");
            return;
        }

        Triggered?.Invoke(trigger);
    }

    public void Dispose() => _hook.Dispose();
}
