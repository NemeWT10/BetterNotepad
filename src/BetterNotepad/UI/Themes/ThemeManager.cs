using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace BetterNotepad.UI.Themes;

/// <summary>
/// Turns a palette into one ResourceDictionary of Color.* / Brush.* keys and swaps it live.
/// All XAML uses {DynamicResource Brush.X}; code that paints by itself listens to <see cref="Changed"/>.
/// </summary>
public sealed class ThemeManager
{
    public static ThemeManager I { get; } = new();

    private ResourceDictionary? _dictionary;
    private readonly Dictionary<string, IReadOnlyDictionary<SyntaxToken, SyntaxStyle>> _syntaxCache = new();

    public ThemePalette Current { get; private set; } = Palettes.Aurora;

    /// <summary>True when the window chrome is drawn over the Mica backdrop.</summary>
    public bool MicaActive { get; private set; }

    /// <summary>The setting value the current palette was resolved from ("system" or an id).</summary>
    public string Setting { get; private set; } = "system";

    public event Action? Changed;

    public IReadOnlyDictionary<SyntaxToken, SyntaxStyle> Syntax => SyntaxFor(Current);

    public void Apply(string setting, bool mica)
    {
        Setting = setting;
        _micaRequested = mica;
        var palette = Resolve(setting);
        var micaActive = mica && WindowEffects.IsBackdropSupported;
        if (_dictionary is not null && palette == Current && micaActive == MicaActive) return;

        Current = palette;
        MicaActive = micaActive;
        var dict = Build(palette, micaActive);
        var merged = Application.Current.Resources.MergedDictionaries;
        var index = _dictionary is null ? -1 : merged.IndexOf(_dictionary);
        if (index >= 0) merged[index] = dict;
        else merged.Insert(0, dict);
        _dictionary = dict;
        Changed?.Invoke();
    }

    /// <summary>Re-evaluates "system" after Windows switched between light and dark.</summary>
    public void OnSystemThemeChanged()
    {
        if (Setting == "system") Apply(Setting, _micaRequested);
    }

    private bool _micaRequested = true;

    public static ThemePalette Resolve(string? setting)
        => setting is null or "system" ? (SystemUsesLightTheme() ? Palettes.Daylight : Palettes.Aurora) : Palettes.ById(setting);

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyDictionary<SyntaxToken, SyntaxStyle> SyntaxFor(ThemePalette p)
    {
        if (I._syntaxCache.TryGetValue(p.Id, out var cached)) return cached;
        var s = p.Syntax;
        var kwBold = s.BoldKeywords;
        var map = new Dictionary<SyntaxToken, SyntaxStyle>
        {
            [SyntaxToken.Keyword] = new(s.Keyword, Bold: kwBold),
            [SyntaxToken.Control] = new(s.Control, Bold: kwBold),
            [SyntaxToken.Type] = new(s.Type),
            [SyntaxToken.Function] = new(s.Function),
            [SyntaxToken.Variable] = new(s.Variable),
            [SyntaxToken.Property] = new(s.Property),
            [SyntaxToken.String] = new(s.String),
            [SyntaxToken.Number] = new(s.Number),
            [SyntaxToken.Constant] = new(s.Constant),
            [SyntaxToken.Comment] = new(s.Comment, Italic: s.ItalicComments),
            [SyntaxToken.DocComment] = new(Blend(s.Comment, p.EditorText, 0.18), Italic: s.ItalicComments),
            [SyntaxToken.Preprocessor] = new(s.Preprocessor),
            [SyntaxToken.Operator] = new(s.Operator),
            [SyntaxToken.Punctuation] = new(s.Punctuation),
            [SyntaxToken.Attribute] = new(s.Attribute),
            [SyntaxToken.Tag] = new(s.Tag),
            [SyntaxToken.AttrName] = new(s.AttrName),
            [SyntaxToken.AttrValue] = new(s.String),
            [SyntaxToken.Entity] = new(s.Constant),
            [SyntaxToken.Escape] = new(s.Constant),
            [SyntaxToken.Regex] = new(Blend(s.String, s.Constant, 0.35)),
            [SyntaxToken.Register] = new(s.Property),
            [SyntaxToken.Instruction] = new(s.Keyword, Bold: kwBold),
            [SyntaxToken.Directive] = new(s.Preprocessor),
            [SyntaxToken.Label] = new(s.Function, Bold: true),
            [SyntaxToken.Heading] = new(s.Keyword, Bold: true),
            [SyntaxToken.Emphasis] = new(p.EditorText, Italic: true),
            [SyntaxToken.Strong] = new(p.EditorText, Bold: true),
            [SyntaxToken.Link] = new(s.Function, Underline: true),
            [SyntaxToken.Code] = new(s.String),
            [SyntaxToken.Quote] = new(s.Comment, Italic: true),
            [SyntaxToken.ListMarker] = new(s.Control, Bold: true),
            [SyntaxToken.Selector] = new(s.Tag),
            [SyntaxToken.CssProperty] = new(s.Property),
            [SyntaxToken.Key] = new(s.Property),
            [SyntaxToken.Section] = new(s.Type, Bold: true),
            [SyntaxToken.Invalid] = new(p.Danger, Underline: true),
            [SyntaxToken.LogError] = new(p.Danger, Bold: true),
            [SyntaxToken.LogWarning] = new(p.IsDark ? p.Warning : Blend(p.Warning, Colors.Black, 0.3)),
            [SyntaxToken.LogInfo] = new(p.Info),
            [SyntaxToken.LogDebug] = new(p.TextDim),
            [SyntaxToken.LogTrace] = new(p.TextFaint),
            [SyntaxToken.LogTimestamp] = new(s.Comment),
            [SyntaxToken.DiffAdded] = new(p.IsDark ? p.Success : Blend(p.Success, Colors.Black, 0.35)),
            [SyntaxToken.DiffRemoved] = new(p.Danger),
            [SyntaxToken.DiffHeader] = new(s.Function, Bold: true),
        };
        I._syntaxCache[p.Id] = map;
        return map;
    }

    private static ResourceDictionary Build(ThemePalette p, bool mica)
    {
        var d = new ResourceDictionary();

        void C(string name, Color c)
        {
            d["Color." + name] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            d["Brush." + name] = b;
        }

        d["Color.Accent"] = p.Accent;
        d["Color.Accent2"] = p.Accent2;
        d["Color.Glow"] = p.Glow;
        d["Color.Shadow"] = p.Shadow;
        d["Theme.IsDark"] = p.IsDark;
        d["Shadow.Opacity"] = p.IsDark ? 0.45 : 0.16;

        C("Accent", p.Accent);
        C("Accent2", p.Accent2);
        C("AccentSoft", p.AccentSoft);
        C("AccentGlow", p.AccentGlow);
        C("AccentText", p.Caret);
        C("OnAccent", p.OnAccent);
        d["Brush.AccentGradient"] = Gradient(p.AccentGradient, new Point(0, 0), new Point(1, 1));
        d["Brush.AccentGradientH"] = Gradient(p.AccentGradient, new Point(0, 0.5), new Point(1, 0.5));
        // the same gradient for surfaces that carry OnAccent text (buttons, selected segments): every stop stays readable
        d["Brush.AccentFill"] = Gradient(FillStops(p), new Point(0, 0), new Point(1, 1));

        C("Bg", p.Bg);
        C("BgAlt", p.BgAlt);
        C("Surface", p.Surface);
        C("SurfaceHover", p.SurfaceHover);
        C("SurfacePressed", p.SurfacePressed);
        C("Border", p.Border);
        C("BorderStrong", p.BorderStrong);
        C("Text", p.Text);
        C("TextDim", p.TextDim);
        C("TextFaint", p.TextFaint);

        C("Panel", p.Panel);
        C("PanelBorder", p.PanelBorder);
        C("PanelText", p.PanelText);
        C("PanelTextDim", p.PanelTextDim);
        C("PanelHover", p.PanelHover);
        C("PanelPressed", p.PanelPressed);
        C("PanelSeparator", p.PanelSeparator);
        C("Dim", p.Dim);

        C("Success", p.Success);
        C("Danger", p.Danger);
        C("Warning", p.Warning);
        C("Info", p.Info);
        C("SuccessSoft", WithAlpha(p.Success, 0x26));
        C("DangerSoft", WithAlpha(p.Danger, 0x26));
        C("WarningSoft", WithAlpha(p.Warning, 0x26));
        C("InfoSoft", WithAlpha(p.Info, 0x26));
        C("ToggleOff", p.ToggleOff);

        C("WindowBg", mica ? Colors.Transparent : p.Bg);
        C("Chrome", mica ? p.ChromeOverMica : p.ChromeSolid);
        C("TabActive", p.TabActive);
        C("TabHover", p.IsDark ? WithAlpha(Colors.White, 0x0E) : WithAlpha(p.Shadow, 0x0C));

        C("Editor.Bg", p.EditorBg);
        C("Editor.Text", p.EditorText);
        C("Editor.LineNumber", p.LineNumber);
        C("Editor.LineNumberActive", p.LineNumberActive);
        C("Editor.CurrentLine", p.CurrentLine);
        C("Editor.Selection", p.Selection);
        C("Editor.SelectionInactive", p.SelectionInactive);
        C("Editor.Caret", p.Caret);
        C("Editor.FindMatch", p.FindMatch);
        C("Editor.FindMatchActive", p.FindMatchActive);
        C("Editor.Occurrence", p.Occurrence);
        C("Editor.BracketMatch", p.BracketMatch);
        C("Editor.IndentGuide", p.IndentGuide);
        C("Editor.Whitespace", p.Whitespace);

        C("Minimap.Viewport", WithAlpha(p.Text, p.IsDark ? (byte)0x12 : (byte)0x10));
        C("Minimap.ViewportBorder", WithAlpha(p.Text, 0x24));
        C("Diff.Added", WithAlpha(p.Success, p.IsDark ? (byte)0x22 : (byte)0x1C));
        C("Diff.Removed", WithAlpha(p.Danger, p.IsDark ? (byte)0x22 : (byte)0x1C));
        C("Diff.AddedStrong", WithAlpha(p.Success, 0x48));
        C("Diff.RemovedStrong", WithAlpha(p.Danger, 0x48));

        C("Md.CodeBg", Blend(p.EditorBg, p.Text, p.IsDark ? 0.045 : 0.035));
        C("Md.QuoteBar", p.Accent);
        C("Md.TableHeader", Blend(p.EditorBg, p.Text, p.IsDark ? 0.06 : 0.045));
        C("Md.TableStripe", Blend(p.EditorBg, p.Text, p.IsDark ? 0.025 : 0.018));

        foreach (var (token, style) in SyntaxFor(p))
            C("Syntax." + token, style.Color);

        return d;
    }

    private static LinearGradientBrush Gradient(Color[] stops, Point start, Point end)
    {
        var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var i = 0; i < stops.Length; i++)
            g.GradientStops.Add(new GradientStop(stops[i], stops.Length == 1 ? 0 : (double)i / (stops.Length - 1)));
        g.Freeze();
        return g;
    }

    /// <summary>Accent gradient stops nudged (darker under light text, lighter under dark text) until OnAccent reads at 3.2:1.</summary>
    public static Color[] FillStops(ThemePalette p)
    {
        var toward = Luminance(p.OnAccent) > 0.5 ? Colors.Black : Colors.White;
        return p.AccentGradient.Select(stop =>
        {
            var c = stop;
            for (var t = 0.06; t <= 0.6 && Contrast(p.OnAccent, c) < 3.2; t += 0.06) c = Blend(stop, toward, t);
            return c;
        }).ToArray();
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(Color c)
    {
        static double L(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>WCAG contrast ratio of two opaque colors.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Shadow color of the active theme, for effects built in code (Freezables cannot use DynamicResource).</summary>
    public static Color ShadowColor => Application.Current?.TryFindResource("Color.Shadow") is Color c ? c : Colors.Black;

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
