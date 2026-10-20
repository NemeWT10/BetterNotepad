using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterNotepad.UI.Controls;

namespace BetterNotepad.UI;

public enum DialogButtonKind
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}

public sealed record DialogButton(string Id, string Text, DialogButtonKind Kind = DialogButtonKind.Secondary, bool IsDefault = false, bool IsCancel = false);

/// <summary>In-window modal card over a soft scrim. One at a time; others wait in a queue.</summary>
public sealed class DialogHost : Grid
{
    private readonly Border _scrim = new();
    private readonly Queue<Func<Task>> _queue = new();
    private bool _busy;

    public DialogHost()
    {
        Visibility = Visibility.Collapsed;
        _scrim.SetResourceReference(Border.BackgroundProperty, "Brush.Dim");
        Children.Add(_scrim);
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>A dialog opened or the last one closed (<see cref="IsOpen"/> changed).</summary>
    public event Action? OpenChanged;

    /// <summary>
    /// Shows a dialog; returns the id of the pressed button (cancel / Esc → the cancel button's id or null).
    /// <paramref name="validate"/> runs for every non-cancel button before the dialog closes: false keeps it open
    /// (e.g. a wrong password — the caller shows the inline error). <paramref name="focus"/> gets the keyboard focus
    /// instead of the default button.
    /// </summary>
    public Task<string?> Show(string title, string message, IReadOnlyList<DialogButton> buttons, UIElement? extra = null, string? glyph = null,
        Func<string, Task<bool>>? validate = null, IInputElement? focus = null)
    {
        var tcs = new TaskCompletionSource<string?>();
        _queue.Enqueue(async () => tcs.SetResult(await ShowNow(title, message, buttons, extra, glyph, validate, focus)));
        Pump();
        return tcs.Task;
    }

    private async void Pump()
    {
        if (_busy) return;
        _busy = true;
        while (_queue.Count > 0) await _queue.Dequeue()();
        _busy = false;
    }

    private Task<string?> ShowNow(string title, string message, IReadOnlyList<DialogButton> buttons, UIElement? extra, string? glyph,
        Func<string, Task<bool>>? validate, IInputElement? focus)
    {
        var tcs = new TaskCompletionSource<string?>();
        var previousFocus = Keyboard.FocusedElement as IInputElement;

        var card = new Border
        {
            Width = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 22, 24, 20),
            Opacity = 0,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        var shadow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.5 };
        shadow.Color = BetterNotepad.UI.Themes.ThemeManager.ShadowColor;
        card.Effect = shadow;

        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        if (glyph is not null)
        {
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0) };
            badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
            var ic = new Icon { Glyph = glyph, FontSize = 15 };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "Brush.AccentText");
            badge.Child = ic;
            head.Children.Add(badge);
        }
        var titleBlock = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        titleBlock.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        head.Children.Add(titleBlock);
        stack.Children.Add(head);

        if (!string.IsNullOrEmpty(message))
        {
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            stack.Children.Add(msg);
        }
        if (extra is not null)
        {
            var host = new ContentControl { Content = extra, Margin = new Thickness(0, 14, 0, 0), Focusable = false };
            stack.Children.Add(host);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Button? defaultButton = null;
        var validating = false;
        string? cancelId = buttons.FirstOrDefault(b => b.IsCancel)?.Id;
        foreach (var b in buttons)
        {
            var btn = new Button { Content = b.Text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 92 };
            btn.SetResourceReference(StyleProperty, b.Kind switch
            {
                DialogButtonKind.Primary => "PrimaryButton",
                DialogButtonKind.Ghost => "GhostButton",
                _ => "SecondaryButton",
            });
            if (b.Kind == DialogButtonKind.Danger) btn.SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
            var id = b.Id;
            btn.Click += (_, _) => Press(id);
            if (b.IsDefault) defaultButton = btn;
            row.Children.Add(btn);
        }
        stack.Children.Add(row);
        card.Child = stack;
        Children.Add(card);

        async void Press(string id)
        {
            if (tcs.Task.IsCompleted || validating) return;
            if (validate is not null && id != cancelId)
            {
                validating = true;
                bool ok;
                try
                {
                    ok = await validate(id);
                }
                catch (Exception ex)
                {
                    BetterNotepad.Core.Log.Warn($"dialog: validation failed: {ex.Message}");
                    ok = false;
                }
                finally
                {
                    validating = false;
                }
                if (!ok) return;
            }
            Finish(id);
        }

        void Finish(string? id)
        {
            if (tcs.Task.IsCompleted || validating) return;
            PreviewKeyDown -= OnKey;
            Motion.Disappear(card, () =>
            {
                Children.Remove(card);
                if (Children.Count <= 1)
                {
                    Visibility = Visibility.Collapsed;
                    OpenChanged?.Invoke();
                }
                previousFocus?.Focus();
            }, sink: 6, ms: 140);
            tcs.TrySetResult(id);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(cancelId);
            }
            else if (e.Key == Key.Enter && defaultButton is not null && Keyboard.FocusedElement is not Button && Keyboard.FocusedElement is not TextBox { AcceptsReturn: true })
            {
                e.Handled = true;
                defaultButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
        }

        PreviewKeyDown += OnKey;
        var opening = Visibility != Visibility.Visible;
        Visibility = Visibility.Visible;
        if (opening) OpenChanged?.Invoke();
        Motion.Appear(card, rise: 14, fromScale: 0.96, ms: 300);
        _scrim.Opacity = 0;
        _scrim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(Motion.I.Enabled ? 180 : 0)));
        Dispatcher.BeginInvoke(() =>
        {
            if (focus is not null) Keyboard.Focus(focus);
            else (defaultButton ?? row.Children.OfType<Button>().LastOrDefault())?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
        return tcs.Task;
    }
}
