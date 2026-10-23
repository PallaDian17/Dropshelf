using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace Dropshelf.UI;

/// <summary>
/// Shared animation vocabulary: durations, easing curves and helpers that leave properties at
/// their final value. Respects Windows' "Animation effects" setting (Accessibility › Visual effects).
/// </summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, int>> Versions = new();

    /// <summary>False when the user turned off animation effects in Windows.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseOutQuint = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>Lively spring (~9% overshoot) for small things: thumbnails, pops, bumps.</summary>
    public static readonly IEasingFunction Spring = Frozen(new SpringEase { Damping = 7, Frequency = 9 });

    /// <summary>Calm spring (~3% overshoot) for window-sized changes.</summary>
    public static readonly IEasingFunction SoftSpring = Frozen(new SpringEase { Damping = 9, Frequency = 8 });

    /// <summary>Used from XAML storyboards (x:Static) so they also honor the system setting.</summary>
    public static Duration ThumbnailFade => new(TimeSpan.FromMilliseconds(Enabled ? 220 : 0));
    public static Duration ItemFade => new(TimeSpan.FromMilliseconds(Enabled ? 180 : 0));
    public static Duration HoverIn => new(TimeSpan.FromMilliseconds(Enabled ? 110 : 0));
    public static Duration HoverOut => new(TimeSpan.FromMilliseconds(Enabled ? 200 : 0));
    public static Duration Quick => new(TimeSpan.FromMilliseconds(Enabled ? 70 : 0));
    public static Duration Slide => new(TimeSpan.FromMilliseconds(Enabled ? 180 : 0));
    public static Duration MenuOpen => new(TimeSpan.FromMilliseconds(Enabled ? 150 : 0));

    /// <summary>Animation frame rate: the display's refresh rate, so 120/144 Hz screens get every frame.</summary>
    public static int FrameRate { get; private set; } = 60;

    /// <summary>Raises WPF's default 60 fps cap to the monitor's refresh rate. Call once at startup.</summary>
    public static void Initialize()
    {
        try
        {
            var hdc = Native.NativeMethods.GetDC(IntPtr.Zero);
            var refresh = Native.NativeMethods.GetDeviceCaps(hdc, 116); // VREFRESH
            Native.NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            if (refresh > 1) FrameRate = Math.Clamp(refresh, 60, 165);
        }
        catch
        {
            FrameRate = 60;
        }

        if (FrameRate <= 60) return;
        try
        {
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = FrameRate });
        }
        catch (Exception ex)
        {
            // Metadata can only be overridden before first use; animations then simply run at 60 fps.
            Core.Log.Warn($"Could not raise animation frame rate: {ex.Message}");
        }
    }

    /// <summary>
    /// Animates a double property to <paramref name="to"/>, then sets it as the base value and
    /// releases the animation, so later code reads (and later animations start from) the end value.
    /// <paramref name="completed"/> only runs if no newer animation replaced this one.
    /// </summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, double ms,
        IEasingFunction? ease = null, double delayMs = 0, double? from = null, Action? completed = null)
    {
        var obj = (DependencyObject)target;
        var version = NextVersion(obj, property);

        if (!Enabled || ms <= 0)
        {
            target.BeginAnimation(property, null);
            obj.SetValue(property, to);
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (from is { } f) animation.From = f;
        animation.Completed += (_, _) =>
        {
            if (CurrentVersion(obj, property) != version) return;
            obj.SetValue(property, to);
            target.BeginAnimation(property, null);
            completed?.Invoke();
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops any running animation on the property and sets a value immediately.</summary>
    public static void Set(IAnimatable target, DependencyProperty property, double value)
    {
        var obj = (DependencyObject)target;
        NextVersion(obj, property);
        target.BeginAnimation(property, null);
        obj.SetValue(property, value);
    }

    public static void FadeIn(UIElement element, double ms = 160, double delayMs = 0)
    {
        element.Visibility = Visibility.Visible;
        To(element, UIElement.OpacityProperty, 1, ms, EaseOut, delayMs);
    }

    /// <summary>Fades out, then collapses (unless something faded it back in meanwhile).</summary>
    public static void FadeOut(UIElement element, double ms = 140, Action? completed = null)
    {
        if (element.Visibility != Visibility.Visible)
        {
            completed?.Invoke();
            return;
        }
        To(element, UIElement.OpacityProperty, 0, ms, EaseOut, completed: () =>
        {
            element.Visibility = Visibility.Collapsed;
            completed?.Invoke();
        });
    }

    private static int NextVersion(DependencyObject obj, DependencyProperty property)
    {
        var map = Versions.GetOrCreateValue(obj);
        map.TryGetValue(property, out var v);
        map[property] = ++v;
        return v;
    }

    private static int CurrentVersion(DependencyObject obj, DependencyProperty property)
        => Versions.TryGetValue(obj, out var map) && map.TryGetValue(property, out var v) ? v : 0;

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}

/// <summary>
/// Damped spring: x(t) = 1 − e^(−damping·t)·cos(frequency·t). Starts at 0, overshoots slightly,
/// settles at 1. Use with the default EaseIn mode, which applies the curve as written.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public double Damping { get; set; } = 7;
    public double Frequency { get; set; } = 9;

    public SpringEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        return 1 - Math.Exp(-Damping * t) * Math.Cos(Frequency * t);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase { Damping = Damping, Frequency = Frequency };
}
