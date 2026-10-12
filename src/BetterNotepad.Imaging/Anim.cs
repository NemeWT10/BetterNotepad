using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BetterNotepad.Imaging;

/// <summary>The app's micro-interactions (same curves and timings as BetterNotepad.UI.Motion), gated by the Animations switch.</summary>
internal static class Anim
{
    public static Func<bool> Enabled = () => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseBack = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    private static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    public static (ScaleTransform Scale, TranslateTransform Translate) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup g && g.Children.Count == 2 &&
            g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t && !g.IsFrozen)
            return (s, t);
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { scale, translate } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    private static void To(IAnimatable target, DependencyProperty dp, double to, double ms, IEasingFunction? ease = null, double? from = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease ?? EaseOut };
        if (from.HasValue) a.From = from;
        target.BeginAnimation(dp, a);
    }

    /// <summary>Selection bounce 1 → 1.28 → 0.92 → 1 (BetterShot).</summary>
    public static void Bounce(UIElement el, double peak = 1.28)
    {
        if (!Enabled()) return;
        var (s, _) = Transforms(el);
        foreach (var dp in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), EaseOut));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), EaseOut));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)), EaseOut));
            s.BeginAnimation(dp, a);
        }
    }

    /// <summary>Panel / popover appear: fade + small rise + spring scale.</summary>
    public static void Appear(UIElement el, double rise = 8, double fromScale = 0.97, double ms = 260)
    {
        el.Visibility = Visibility.Visible;
        if (!Enabled())
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            return;
        }
        var (s, t) = Transforms(el);
        To(el, UIElement.OpacityProperty, 1, ms * 0.8, EaseOut, 0);
        To(t, TranslateTransform.YProperty, 0, ms, EaseBack, rise);
        To(s, ScaleTransform.ScaleXProperty, 1, ms, EaseBack, fromScale);
        To(s, ScaleTransform.ScaleYProperty, 1, ms, EaseBack, fromScale);
    }

    /// <summary>Fade + sink, then collapse.</summary>
    public static void Disappear(UIElement el, double ms = 150)
    {
        if (el.Visibility != Visibility.Visible) return;
        if (!Enabled())
        {
            el.Visibility = Visibility.Collapsed;
            return;
        }
        var (_, t) = Transforms(el);
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = EaseOut };
        fade.Completed += (_, _) =>
        {
            if (el.Opacity < 0.01) el.Visibility = Visibility.Collapsed;
        };
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        To(t, TranslateTransform.YProperty, 5, ms);
    }

    /// <summary>Hover lift / press dip of a button (the app's IconButton feel).</summary>
    public static void Hover(UIElement el, bool over)
    {
        var (s, _) = Transforms(el);
        if (!Enabled())
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var to = over ? 1.08 : 1;
        To(s, ScaleTransform.ScaleXProperty, to, over ? 140 : 120, over ? EaseBack : EaseOut);
        To(s, ScaleTransform.ScaleYProperty, to, over ? 140 : 120, over ? EaseBack : EaseOut);
    }
}
