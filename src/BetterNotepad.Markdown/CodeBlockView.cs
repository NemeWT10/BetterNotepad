using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BetterNotepad.Markdown;

/// <summary>A rounded code block: optional language header, horizontal scroll for long lines, hover "copy".</summary>
internal sealed class CodeBlockView : Border
{
    private static readonly FontFamily BodyFallback = new("Segoe UI Variable Text, Segoe UI");

    private readonly string _code;
    private readonly MarkdownLabels _labels;
    private readonly Border _copyButton;
    private readonly TextBlock _copyLabel;
    private DispatcherTimer? _timer;

    public CodeBlockView(string? language, string code, MarkdownOptions options, int relativeLine)
    {
        _code = code;
        _labels = options.Labels;
        Tag = Res.Box(relativeLine);
        CornerRadius = new CornerRadius(9);
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, K.CodeBg);
        SetResourceReference(BorderBrushProperty, K.Border);

        var mono = new FontFamily(options.CodeFont);
        var hasHeader = !string.IsNullOrEmpty(language);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // copy button (shown on hover)
        _copyLabel = new TextBlock
        {
            Text = _labels.Copy,
            FontFamily = BodyFallback,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _copyLabel.SetResourceReference(TextBlock.ForegroundProperty, K.TextDim);
        _copyButton = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3, 9, 4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Opacity = 0,
            Child = _copyLabel,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _copyButton.MouseEnter += (_, _) => _copyButton.SetResourceReference(BackgroundProperty, K.Border);
        _copyButton.MouseLeave += (_, _) => _copyButton.Background = Brushes.Transparent;
        _copyButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Copy();
        };

        if (hasHeader)
        {
            var label = new TextBlock
            {
                Text = language,
                FontFamily = BodyFallback,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 1),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, K.TextFaint);

            var header = new Grid { Height = 34 };
            header.Children.Add(label);
            header.Children.Add(_copyButton);

            var headerBorder = new Border
            {
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = header,
            };
            headerBorder.SetResourceReference(BackgroundProperty, K.TableStripe);
            headerBorder.SetResourceReference(BorderBrushProperty, K.Border);
            grid.Children.Add(headerBorder);
        }

        var text = new TextBlock
        {
            FontFamily = mono,
            FontSize = Math.Round(options.FontSize * 0.9, 1),
            LineHeight = Math.Round(options.FontSize * 0.9 * 1.52),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(16, hasHeader ? 12 : 14, 16, 14),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, K.Text);
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);

        IEnumerable<System.Windows.Documents.Inline>? inlines = null;
        if (hasHeader && options.Highlighter is { } hl)
        {
            try
            {
                inlines = hl(language!, code);
            }
            catch
            {
                inlines = null;
            }
        }

        if (inlines is null) text.Text = code;
        else text.Inlines.AddRange(inlines);

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
            Focusable = false,
        };
        scroller.PreviewMouseWheel += ForwardWheel;
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);

        if (!hasHeader)
        {
            _copyButton.VerticalAlignment = VerticalAlignment.Top;
            _copyButton.Margin = new Thickness(0, 6, 6, 0);
            Grid.SetRow(_copyButton, 1);
            grid.Children.Add(_copyButton);
        }

        Child = grid;
        MouseEnter += (_, _) => _copyButton.Opacity = 1;
        MouseLeave += (_, _) => _copyButton.Opacity = 0;
    }

    private static void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender };
        (((FrameworkElement)sender).Parent as UIElement)?.RaiseEvent(args);
    }

    private void Copy()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetText(_code);
                break;
            }
            catch
            {
                // clipboard busy; try once more, then give up silently
            }
        }

        _copyLabel.Text = _labels.Copied;
        _copyLabel.SetResourceReference(TextBlock.ForegroundProperty, K.Success);
        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1400) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _timer = null;
            _copyLabel.Text = _labels.Copy;
            _copyLabel.SetResourceReference(TextBlock.ForegroundProperty, K.TextDim);
        };
        _timer.Start();
    }
}
