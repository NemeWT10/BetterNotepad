using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Themes;

namespace BetterNotepad.Editor.Export;

/// <summary>What a code image shows besides the code. Sizes in DIP; the bitmap is rendered at <see cref="Scale"/>.</summary>
public sealed record CodeImageOptions
{
    public required ThemePalette Palette { get; init; }

    /// <summary>File name in the title row (null = none).</summary>
    public string? Title { get; init; }

    /// <summary>Language label in the title row (null = none).</summary>
    public string? Language { get; init; }

    public bool LineNumbers { get; init; }
    public int FirstLineNumber { get; init; } = 1;
    public string FontFamily { get; init; } = "Cascadia Mono";
    public double FontSize { get; init; } = 14;
    public double Scale { get; init; } = 2;
}

/// <param name="Lines">Source lines drawn (the last one may be cut when <paramref name="Truncated"/>).</param>
/// <param name="Card">The card inside the image, in DIP (the rest is transparent room for the shadow).</param>
public sealed record CodeImageResult(BitmapSource Image, int Lines, bool Truncated, Rect Card);

/// <summary>
/// "Copy as image": code as a rounded card — a quiet title row with the file name and language, the code in the
/// editor font and the palette's syntax colors, optional dimmed line numbers, a 1 px edge and a soft two-layer shadow
/// on a transparent background. Built as an off-screen WPF visual and rendered with RenderTargetBitmap (never the screen).
/// </summary>
public static class CodeImage
{
    /// <summary>Longer lines wrap (hanging under their indentation).</summary>
    public const int MaxColumns = 120;

    /// <summary>Drawn rows at most; beyond that the image ends with "⋯" (a taller picture is no use on a slide or in a chat).</summary>
    public const int MaxRows = 220;

    /// <summary>Transparent room around the card that the shadow falls into.</summary>
    public static readonly Thickness ShadowRoom = new(36, 26, 36, 56);

    private const double Radius = 12;
    private const double LineSpacing = 1.62;
    private const double MinCardWidth = 320;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static CodeImageResult Render(CodeBlock block, CodeImageOptions o)
    {
        var p = o.Palette;
        var rows = CodeWrap.Rows(block, MaxColumns, MaxRows, out var truncated, out var lines);
        var bg = Opaque(p.EditorBg, p.Bg);
        var text = Opaque(p.EditorText, bg);
        var edge = Opaque(p.IsDark ? ThemeManager.Blend(p.Border, p.BorderStrong, 0.55) : ThemeManager.Blend(p.Border, p.BorderStrong, 0.45), bg);
        var titleBg = ThemeManager.Blend(bg, Opaque(p.Text, bg), p.IsDark ? 0.03 : 0.022);
        var separator = Opaque(p.IsDark ? p.Border : ThemeManager.Blend(p.Border, bg, 0.2), bg);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (TitleRow(o, titleBg, separator) is { } title) content.Children.Add(title);

        var surface = new CodeSurface(rows, truncated, o, text, Opaque(p.LineNumber, bg), bg)
        {
            Margin = new Thickness(o.LineNumbers ? 18 : 22, 16, 26, 18),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetRow(surface, 1);
        content.Children.Add(surface);

        var card = new Border
        {
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(1),
            BorderBrush = Frozen(edge),
            Background = Frozen(bg),
            MinWidth = MinCardWidth,
            Child = content,
        };

        card.Margin = ShadowRoom;
        var root = new Grid { UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.Children.Add(card);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale); // a bitmap gets no ClearType fringes
        VisualTreeHelper.SetRootDpi(root, new DpiScale(o.Scale, o.Scale));
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = root.DesiredSize;

        // the shadow is blurred in software: at half resolution it costs a quarter and, being soft, looks the same scaled up
        root.Children.Insert(0, Shadow(size, p, o.Scale / 2));
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * o.Scale), (int)Math.Ceiling(size.Height * o.Scale),
            96 * o.Scale, 96 * o.Scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        var cardRect = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
        return new CodeImageResult(rtb, lines, truncated, cardRect);
    }

    /// <summary>
    /// Two layers under the card: a tight contact shadow that defines the edge and a wide, soft drop that falls a little
    /// below it. Rendered once at <paramref name="scale"/> and stretched over the whole image.
    /// </summary>
    private static Image Shadow(Size size, ThemePalette p, double scale)
    {
        var room = ShadowRoom;
        var color = Frozen(p.Shadow);
        var drop = new Border
        {
            CornerRadius = new CornerRadius(Radius + 6),
            Background = color,
            Opacity = p.IsDark ? 0.3 : 0.15,
            Margin = new Thickness(room.Left + 14, room.Top + 20, room.Right + 14, room.Bottom - 16),
            Effect = new BlurEffect { Radius = 38, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
        };
        var contact = new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = color,
            Opacity = p.IsDark ? 0.22 : 0.09,
            Margin = new Thickness(room.Left, room.Top + 0.5, room.Right, room.Bottom - 1.5),
            Effect = new BlurEffect { Radius = 4, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
        };
        var layers = new Grid { Width = size.Width, Height = size.Height };
        layers.Children.Add(drop);
        layers.Children.Add(contact);
        VisualTreeHelper.SetRootDpi(layers, new DpiScale(scale, scale));
        layers.Measure(size);
        layers.Arrange(new Rect(size));
        layers.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(layers);
        rtb.Freeze();
        var image = new Image { Source = rtb, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>File name on the left, language as a small outlined label on the right; without a name the language takes its place.</summary>
    private static Border? TitleRow(CodeImageOptions o, Color background, Color separator)
    {
        var p = o.Palette;
        var name = string.IsNullOrWhiteSpace(o.Title) ? null : o.Title.Trim();
        var language = string.IsNullOrWhiteSpace(o.Language) ? null : o.Language.Trim();
        if (name is null && language is null) return null;

        var dim = Opaque(p.TextDim, background);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = Glyphs.Code,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Frozen(Opaque(p.Caret, background)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 9, 0),
        };
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = name ?? language,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12.5,
            FontWeight = name is null ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = Frozen(name is null ? dim : Opaque(p.Text, background)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (name is not null && language is not null)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Frozen(Opaque(p.IsDark ? p.BorderStrong : p.Border, background)),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = language,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 11,
                    Foreground = Frozen(dim),
                },
            };
            Grid.SetColumn(pill, 2);
            grid.Children.Add(pill);
        }

        return new Border
        {
            Background = Frozen(background),
            BorderBrush = Frozen(separator),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(Radius - 1, Radius - 1, 0, 0),
            Padding = new Thickness(16, 0, 14, 0),
            Height = 40,
            Child = grid,
        };
    }

    /// <summary>PNG with alpha (chats, Word, Docs and browsers read it), keeping the 2× DPI so Office sizes it at 1×.</summary>
    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The image over an opaque background: the classic clipboard bitmap has no alpha (a shadow over black would look broken).</summary>
    public static BitmapSource Flatten(BitmapSource image, Color background)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, image.Width, image.Height);
            dc.DrawRectangle(Frozen(background), null, r);
            dc.DrawImage(image, r);
        }
        var rtb = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        var opaque = new FormatConvertedBitmap(rtb, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>A palette color with alpha laid over <paramref name="under"/> (the image has no Mica behind it).</summary>
    private static Color Opaque(Color c, Color under)
    {
        if (c.A == 255) return c;
        var a = c.A / 255.0;
        return Color.FromRgb((byte)Math.Round(c.R * a + under.R * (1 - a)), (byte)Math.Round(c.G * a + under.G * (1 - a)), (byte)Math.Round(c.B * a + under.B * (1 - a)));
    }

    /// <summary>The code rows (and line numbers) as FormattedText, laid out on a fixed line grid.</summary>
    private sealed class CodeSurface : FrameworkElement
    {
        private readonly List<(Point At, FormattedText Text)> _texts = [];
        private readonly Size _size;

        public CodeSurface(IReadOnlyList<WrappedRow> rows, bool truncated, CodeImageOptions o, Color text, Color numbers, Color bg)
        {
            var typeface = new Typeface(new FontFamily(o.FontFamily + ", Cascadia Mono, Cascadia Code, Consolas, Courier New"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var brushes = new Dictionary<Color, Brush>();
            Brush B(Color c)
            {
                if (!brushes.TryGetValue(c, out var b)) brushes[c] = b = Frozen(Opaque(c, bg));
                return b;
            }
            FormattedText F(string s, Color c) => new(s, Inv, FlowDirection.LeftToRight, typeface, o.FontSize, B(c), o.Scale);

            var cw = F(new string('0', 20), text).WidthIncludingTrailingWhitespace / 20;
            var lineHeight = Math.Ceiling(o.FontSize * LineSpacing);
            var dy = Math.Round((lineHeight - F("Mg", text).Height) / 2 * o.Scale) / o.Scale; // glyphs centred in the line, on the pixel grid

            var gutter = 0.0;
            var numbersWidth = 0.0;
            if (o.LineNumbers && rows.Count > 0)
            {
                var digits = Math.Max(2, (o.FirstLineNumber + rows[^1].Source).ToString(Inv).Length);
                numbersWidth = digits * cw;
                gutter = numbersWidth + Math.Round(cw * 2.4);
            }

            var width = 0.0;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var y = i * lineHeight + dy;
                if (o.LineNumbers && row.First)
                {
                    var n = F((o.FirstLineNumber + row.Source).ToString(Inv), numbers);
                    _texts.Add((new Point(numbersWidth - n.WidthIncludingTrailingWhitespace, y), n));
                }
                if (row.Runs.Count == 0) continue;
                var ft = F(row.Text, text);
                var at = 0;
                foreach (var run in row.Runs)
                {
                    var len = run.Text.Length;
                    if (len == 0) continue;
                    if (run.Color is { } c) ft.SetForegroundBrush(B(c), at, len);
                    if (run.Bold) ft.SetFontWeight(FontWeights.SemiBold, at, len);
                    if (run.Italic) ft.SetFontStyle(FontStyles.Italic, at, len);
                    at += len;
                }
                var x = gutter + row.Indent * cw;
                _texts.Add((new Point(x, y), ft));
                width = Math.Max(width, x + ft.WidthIncludingTrailingWhitespace);
            }

            var count = rows.Count;
            if (truncated)
            {
                _texts.Add((new Point(gutter, count * lineHeight + dy), F("⋯", numbers)));
                count++;
            }
            _size = new Size(Math.Ceiling(Math.Max(width, gutter + cw)), Math.Max(1, count) * lineHeight);
        }

        protected override Size MeasureOverride(Size availableSize) => _size;

        protected override void OnRender(DrawingContext dc)
        {
            foreach (var (at, text) in _texts) dc.DrawText(text, at);
        }
    }
}
