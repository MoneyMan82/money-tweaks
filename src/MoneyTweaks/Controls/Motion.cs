using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MoneyTweaks.Controls;

/// <summary>
/// App-wide motion: entrances, expand/collapse, hover lift, dialog pop-in, frame-synced tweens and smooth wheel scrolling.
/// Everything here becomes an instant change when reduced motion is on (Settings › Appearance, or Windows' animation
/// setting), so it never gets in the way.
/// </summary>
public static class Motion
{
    public static bool Enabled => !App.ReduceMotion;

    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction OutQuart = new QuarticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fade + rise into place, optionally after a delay.</summary>
    public static void FadeIn(UIElement el, int delayMs = 0, double fromY = 12, int ms = 320)
    {
        if (!Enabled) { el.BeginAnimation(UIElement.OpacityProperty, null); el.Opacity = 1; return; }
        var t = el.RenderTransform as TranslateTransform;
        if (t is null || t.IsFrozen) { t = new TranslateTransform(); el.RenderTransform = t; }
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms * 0.75)) { BeginTime = begin, EasingFunction = Out });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = OutQuart });
    }

    /// <summary>Staggered entrance for the first children of a panel (cards on a page). Later children appear immediately.</summary>
    public static void Stagger(Panel panel, int stepMs = 40, int max = 10, int skip = 0)
    {
        if (!Enabled) return;
        int i = 0;
        foreach (UIElement c in panel.Children)
        {
            if (skip-- > 0) continue;
            if (c.Visibility != Visibility.Visible) continue;
            if (i >= max) break;
            FadeIn(c, i * stepMs, 14);
            i++;
        }
    }

    /// <summary>Opens a collapsed element by growing its height, then hands height back to layout.</summary>
    public static void Expand(FrameworkElement el, int ms = 220)
    {
        el.Visibility = Visibility.Visible;
        if (!Enabled) { el.BeginAnimation(FrameworkElement.HeightProperty, null); el.Height = double.NaN; return; }
        el.Measure(new Size(el.ActualWidth > 0 ? el.ActualWidth : double.PositiveInfinity, double.PositiveInfinity));
        var target = el.DesiredSize.Height;
        var a = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        a.Completed += (_, _) => { el.BeginAnimation(FrameworkElement.HeightProperty, null); el.Height = double.NaN; };
        el.BeginAnimation(FrameworkElement.HeightProperty, a);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    /// <summary>Closes an element by shrinking its height, then collapses it.</summary>
    public static void Collapse(FrameworkElement el, int ms = 200)
    {
        if (!Enabled || el.ActualHeight <= 0) { el.BeginAnimation(FrameworkElement.HeightProperty, null); el.Height = double.NaN; el.Visibility = Visibility.Collapsed; return; }
        var a = new DoubleAnimation(el.ActualHeight, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        a.Completed += (_, _) =>
        {
            el.Visibility = Visibility.Collapsed;
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Height = double.NaN;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
        };
        el.BeginAnimation(FrameworkElement.HeightProperty, a);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms * 0.8)));
    }

    /// <summary>Rotates an element (chevrons) to an angle.</summary>
    public static void Rotate(UIElement el, double angle, int ms = 200)
    {
        if (el.RenderTransform is not RotateTransform r || r.IsFrozen) { r = new RotateTransform(); el.RenderTransform = r; el.RenderTransformOrigin = new Point(0.5, 0.5); }
        if (!Enabled) { r.BeginAnimation(RotateTransform.AngleProperty, null); r.Angle = angle; return; }
        r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    /// <summary>Subtle lift on hover for clickable cards.</summary>
    public static void HoverLift(FrameworkElement el, double lift = 3)
    {
        var t = new TranslateTransform();
        el.RenderTransform = t;
        el.MouseEnter += (_, _) => { if (Enabled) t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-lift, TimeSpan.FromMilliseconds(180)) { EasingFunction = Out }); };
        el.MouseLeave += (_, _) => { if (Enabled) t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Out }); };
    }

    /// <summary>Pop-in for dialogs: fade and grow slightly from 96%.</summary>
    public static void PopIn(FrameworkElement el)
    {
        if (!Enabled) return;
        var s = new ScaleTransform(0.96, 0.96);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = s;
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Out });
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // ---- frame-synced tween (for values WPF can't animate directly, like a grid column width) ----------------------

    private sealed class TweenState { public EventHandler? Handler; }
    private static readonly ConditionalWeakTable<object, TweenState> Tweens = new();

    /// <summary>Animates any value on the render loop. A new tween for the same key replaces the running one.</summary>
    public static void Tween(object key, double from, double to, int ms, Action<double> apply, Action? done = null)
    {
        var st = Tweens.GetOrCreateValue(key);
        if (st.Handler is not null) { CompositionTarget.Rendering -= st.Handler; st.Handler = null; }
        if (!Enabled || ms <= 0 || Math.Abs(to - from) < 0.01) { apply(to); done?.Invoke(); return; }
        var start = DateTime.UtcNow;
        EventHandler h = null!;
        h = (_, _) =>
        {
            double p = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / ms);
            double e = 1 - Math.Pow(1 - p, 3); // cubic ease-out
            apply(from + (to - from) * e);
            if (p >= 1) { CompositionTarget.Rendering -= h; st.Handler = null; done?.Invoke(); }
        };
        st.Handler = h;
        CompositionTarget.Rendering += h;
    }

    // ---- smooth wheel scrolling for every ScrollViewer -------------------------------------------------------------

    private sealed class ScrollState { public double Target = double.NaN; }
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> Scrolls = new();
    private static bool _smoothInstalled;

    /// <summary>Installs smooth wheel scrolling app-wide (once). Item-based lists keep Windows' normal scrolling.</summary>
    public static void InstallSmoothScrolling()
    {
        if (_smoothInstalled) return;
        _smoothInstalled = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    private static ScrollViewer? InnermostScrollable(DependencyObject? from, int delta)
    {
        for (var d = from; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer sv && sv.ScrollableHeight > 0 && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
                bool canMove = delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight;
                if (canMove) return sv;
            }
        }
        return null;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Enabled || sender is not ScrollViewer sv || Keyboard.Modifiers != ModifierKeys.None) return;
        // Tunnelling goes outer → inner; only the innermost viewer that can move in this direction scrolls.
        if (!ReferenceEquals(InnermostScrollable(e.OriginalSource as DependencyObject, e.Delta), sv)) return;
        // Virtualizing lists that scroll by item keep their native behaviour (their offsets are items, not pixels).
        if (sv.CanContentScroll && sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Item) return;
        if (sv.TemplatedParent is ComboBox) return; // dropdowns keep native scrolling
        e.Handled = true;
        var st = Scrolls.GetOrCreateValue(sv);
        double current = sv.VerticalOffset;
        double baseTarget = double.IsNaN(st.Target) || Math.Abs(st.Target - current) > sv.ViewportHeight * 2 ? current : st.Target;
        double target = Math.Clamp(baseTarget - e.Delta * 0.9, 0, sv.ScrollableHeight);
        st.Target = target;
        Tween(sv, current, target, 260, v => sv.ScrollToVerticalOffset(v), () => st.Target = double.NaN);
    }

}
