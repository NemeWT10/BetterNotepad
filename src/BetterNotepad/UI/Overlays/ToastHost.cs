using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Small notifications in the bottom-right corner (save ✓ draws itself).</summary>
public sealed class ToastHost : StackPanel
{
    private const int MaxToasts = 3;

    public ToastHost()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 18, 38);
        IsHitTestVisible = true;
    }

    /// <summary>A toast came or went (a native child window under the corner — a WebView2 preview — steps aside meanwhile).</summary>
    public event Action? Changed;

    public void Show(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null, double seconds = 2.8)
    {
        while (Children.Count >= MaxToasts) Remove((Border)Children[0]);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9, 14, 9),
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        var shadow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.4 };
        shadow.Color = BetterNotepad.UI.Themes.ThemeManager.ShadowColor;
        card.Effect = shadow;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(BuildIcon(kind));
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            FontSize = 13,
            Margin = new Thickness(10, 0, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.PanelText");
        row.Children.Add(label);

        if (actionText is not null && action is not null)
        {
            var button = new Button { Content = actionText, Margin = new Thickness(14, -2, -4, -2), Padding = new Thickness(10, 4, 10, 4) };
            button.SetResourceReference(StyleProperty, "SecondaryButton");
            button.Click += (_, _) =>
            {
                action();
                Dismiss(card);
            };
            row.Children.Add(button);
        }

        card.Child = row;
        card.MouseLeftButtonUp += (_, _) => Dismiss(card);
        Children.Add(card);
        if (IsVisible) Motion.Appear(card, rise: 12, fromScale: 0.96, ms: 280);
        else card.Opacity = 1; // hidden window (tray): no animation, nothing to render

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (card.IsMouseOver)
            {
                timer.Interval = TimeSpan.FromSeconds(1.2);
                timer.Start();
                return;
            }
            Dismiss(card);
        };
        _timers[card] = timer;
        timer.Start();
        Changed?.Invoke();
    }

    /// <summary>Drops every toast at once, timers included, without animation (the window went to the tray or closed).</summary>
    public void Clear()
    {
        foreach (var card in Children.OfType<Border>().ToList()) Remove(card);
    }

    private void Dismiss(Border card)
    {
        if (!Children.Contains(card)) return;
        if (!IsVisible)
        {
            Remove(card); // nobody sees a fade in a hidden window, and it would keep WPF rendering frames
            return;
        }
        Motion.Disappear(card, () => Remove(card), sink: 8, ms: 180);
    }

    private void Remove(Border card)
    {
        if (_timers.Remove(card, out var timer)) timer.Stop();
        Children.Remove(card);
        Changed?.Invoke();
    }

    private readonly Dictionary<Border, DispatcherTimer> _timers = new();

    private static FrameworkElement BuildIcon(ToastKind kind)
    {
        var size = 20.0;
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var circle = new Ellipse();
        circle.SetResourceReference(Shape.FillProperty, kind switch
        {
            ToastKind.Success => "Brush.SuccessSoft",
            ToastKind.Warning => "Brush.WarningSoft",
            ToastKind.Error => "Brush.DangerSoft",
            _ => "Brush.AccentSoft",
        });
        grid.Children.Add(circle);

        if (kind == ToastKind.Success)
        {
            // the ✓ draws itself
            var check = new Path
            {
                Data = Geometry.Parse("M 5.5,10.5 L 8.6,13.4 L 14.5,7"),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            check.SetResourceReference(Shape.StrokeProperty, "Brush.Success");
            const double length = 14;
            check.StrokeDashArray = new DoubleCollection { length / 1.8, length / 1.8 };
            if (Motion.I.Enabled)
            {
                check.StrokeDashOffset = length / 1.8;
                check.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation
                {
                    To = 0,
                    BeginTime = TimeSpan.FromMilliseconds(90),
                    Duration = TimeSpan.FromMilliseconds(320),
                    EasingFunction = Motion.EaseOut,
                });
            }
            grid.Children.Add(check);
            Motion.Bounce(grid, 1.18);
        }
        else
        {
            var icon = new Icon
            {
                Glyph = kind switch { ToastKind.Warning => Glyphs.Warning, ToastKind.Error => Glyphs.Error, _ => Glyphs.Info },
                FontSize = 11,
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, kind switch
            {
                ToastKind.Warning => "Brush.Warning",
                ToastKind.Error => "Brush.Danger",
                _ => "Brush.AccentText",
            });
            grid.Children.Add(icon);
        }
        return grid;
    }
}
