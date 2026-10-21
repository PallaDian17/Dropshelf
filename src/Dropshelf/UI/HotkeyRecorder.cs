using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dropshelf.Core;

namespace Dropshelf.UI;

/// <summary>Click, then press a key combination; Esc cancels.</summary>
public sealed class HotkeyRecorder : Button
{
    private HotkeyBinding _binding;
    private bool _recording;

    public HotkeyRecorder(HotkeyBinding binding)
    {
        _binding = binding.Clone();
        // Implicit styles match the exact type, so pick up the themed Button style explicitly.
        SetResourceReference(StyleProperty, typeof(Button));
        MinWidth = 150;
        Focusable = true;
        Click += (_, _) => StartRecording();
        LostKeyboardFocus += (_, _) => StopRecording();
        PreviewKeyDown += OnKey;
        Render();
    }

    /// <summary>Raised with the new combination; return an error message to reject it.</summary>
    public Func<HotkeyBinding, string?>? Validate { get; set; }

    public void SetBinding(HotkeyBinding binding)
    {
        _binding = binding.Clone();
        Render();
    }

    private void StartRecording()
    {
        _recording = true;
        Focus();
        Content = "Press shortcut…";
    }

    private void StopRecording()
    {
        _recording = false;
        Render();
    }

    private void Render() => Content = _binding.Key == Key.None ? "Not set" : _binding.ToString();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Shortcuts must include Ctrl, Alt or Win. Shift can be added to one of these.",
                "Cannot Assign Shortcut", MessageBoxButton.OK, MessageBoxImage.Information);
            StopRecording();
            return;
        }

        var candidate = new HotkeyBinding(true, modifiers, key);
        if (Validate?.Invoke(candidate) is { } error)
        {
            MessageBox.Show(Window.GetWindow(this)!, error, "Cannot Assign Shortcut", MessageBoxButton.OK, MessageBoxImage.Information);
            StopRecording();
            return;
        }
        _binding = candidate;
        StopRecording();
    }
}
