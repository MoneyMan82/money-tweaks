using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

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
    public static void Stagger(Panel panel, int stepMs = 45, int max = 12, int skip = 0)
    {
        if (!Enabled) return;
        int i = 0;
        foreach (UIElement c in panel.Children)
        {
            if (skip-- > 0) continue;
            if (c.Visibility != Visibility.Visible) continue;
            if (i >= max) break;
            FadeIn(c, i * stepMs, 16, 380);
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
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>
    /// Closing animation for a borderless (AllowsTransparency) window: the first Close is held while its content fades
    /// and shrinks a little, then the window really closes. The dialog's result is already decided when Close is called.
    /// </summary>
    public static void AnimateClose(Window win, FrameworkElement content)
    {
        bool done = false;
        win.Closing += (_, e) =>
        {
            if (done || !Enabled) return;
            e.Cancel = true;
            done = true;
            win.IsHitTestVisible = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            if (content.RenderTransform is not ScaleTransform s || s.IsFrozen) { s = new ScaleTransform(1, 1); content.RenderTransform = s; content.RenderTransformOrigin = new Point(0.5, 0.5); }
            var shrink = new DoubleAnimation(s.ScaleX, 0.96, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            var fade = new DoubleAnimation(content.Opacity, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            // Close on the next dispatcher turn: closing from inside Closing isn't allowed.
            fade.Completed += (_, _) => win.Dispatcher.BeginInvoke(() => { try { win.Close(); } catch (InvalidOperationException) { } });
            content.BeginAnimation(UIElement.OpacityProperty, fade);
        };
    }

    private static readonly IEasingFunction OutQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Draws a line chart in from left to right (dash offset animation, then the dash is removed).</summary>
    public static void DrawIn(Shape line, int ms = 800, int delayMs = 0)
    {
        if (!Enabled) return;
        double len = line switch
        {
            Polyline pl => Length(pl.Points),
            Path { Data: { } g } => g.GetFlattenedPathGeometry().Figures.Sum(f => Length(f.Segments.OfType<PolyLineSegment>().SelectMany(s => s.Points).Prepend(f.StartPoint).ToList())),
            _ => 0,
        };
        if (len <= 1 || line.StrokeThickness <= 0 || line.StrokeDashArray is { Count: > 0 }) return;
        var units = len / line.StrokeThickness + 2;
        line.StrokeDashArray = [units, units];
        var a = new DoubleAnimation(units, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = OutQuart };
        a.Completed += (_, _) => { line.BeginAnimation(Shape.StrokeDashOffsetProperty, null); line.StrokeDashArray = null; };
        line.StrokeDashOffset = units;
        line.BeginAnimation(Shape.StrokeDashOffsetProperty, a);
    }

    private static double Length(IList<Point> pts)
    {
        double d = 0;
        for (int i = 1; i < pts.Count; i++) d += (pts[i] - pts[i - 1]).Length;
        return d;
    }

    /// <summary>Bars grow up from their baseline (scale only, no layout), staggered by <paramref name="delayMs"/>.</summary>
    public static void GrowUp(FrameworkElement bar, int delayMs = 0, int ms = 460)
    {
        if (!Enabled) return;
        var s = new ScaleTransform(1, 0);
        bar.RenderTransformOrigin = new Point(0.5, 1);
        bar.RenderTransform = s;
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = OutQuint });
    }

    /// <summary>Short brightness pulse (selection arrived, value updated).</summary>
    public static void Pulse(UIElement el, double from = 0.35, int ms = 420)
    {
        if (!Enabled) return;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out });
    }

    // ---- progress bars glide to new values (app-wide) --------------------------------------------------------------

    private static readonly ConditionalWeakTable<ProgressBar, object> Gliding = new();

    /// <summary>Every determinate progress bar animates to each new value instead of jumping (installed once).</summary>
    public static void InstallProgressGlide() =>
        EventManager.RegisterClassHandler(typeof(ProgressBar), RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(OnProgress));

    private static void OnProgress(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!Enabled || sender is not ProgressBar pb || pb.IsIndeterminate || !pb.IsVisible || Gliding.TryGetValue(pb, out _)) return;
        if (Math.Abs(e.NewValue - e.OldValue) < (pb.Maximum - pb.Minimum) * 0.004) return;
        Gliding.Add(pb, true);
        var a = new DoubleAnimation(e.OldValue, e.NewValue, TimeSpan.FromMilliseconds(320)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) => Gliding.Remove(pb);
        pb.BeginAnimation(RangeBase.ValueProperty, a);
    }

    // ---- square window corners (Windows 11 rounds top-level windows by default) ------------------------------------

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Asks Windows 11 not to round this window's corners (no effect on Windows 10, which never rounds).</summary>
    public static void SquareCorners(Window w)
    {
        void Apply()
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int pref = 1; // DWMWCP_DONOTROUND
            try { DwmSetWindowAttribute(h, 33, ref pref, sizeof(int)); } catch { }
        }
        if (new System.Windows.Interop.WindowInteropHelper(w).Handle != IntPtr.Zero) Apply(); else w.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>Every window gets square corners as soon as it's created (installed once).</summary>
    public static void InstallSquareWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => SquareCorners((Window)s)));

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
