using System.Windows;
using System.Windows.Media;

namespace BetterNotepad.Ui.Sample;

/// <summary>
/// Color.* / Brush.* resources as ThemeManager.Build makes them, from the Aurora (dark) and Daylight (light) palettes in
/// src\BetterNotepad\UI\Themes\Palettes.cs — so the libraries render here exactly as in the app.
/// </summary>
internal static class FakeTheme
{
    private static Color H(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static ResourceDictionary Aurora() => Build(true, new()
    {
        ["Accent"] = "#7C5CFF", ["Accent2"] = "#22D3EE", ["Glow"] = "#8B6CFF", ["Shadow"] = "#000000",
        ["AccentSoft"] = "#2E7C5CFF", ["AccentGlow"] = "#667C5CFF", ["OnAccent"] = "#FFFFFF", ["AccentText"] = "#B39DFF",
        ["G0"] = "#8B5CFF", ["G1"] = "#5B7BFF", ["G2"] = "#22D3EE",
        ["Bg"] = "#0F1117", ["BgAlt"] = "#0B0D12", ["Surface"] = "#161922", ["SurfaceHover"] = "#1E2230", ["SurfacePressed"] = "#262B3B",
        ["Border"] = "#252A37", ["BorderStrong"] = "#353B4D", ["Text"] = "#EEF0F7", ["TextDim"] = "#A2A8B8", ["TextFaint"] = "#6A7185",
        ["Panel"] = "#EB141722", ["PanelBorder"] = "#2EFFFFFF", ["PanelText"] = "#E9ECF5", ["PanelTextDim"] = "#9AA1B4",
        ["PanelHover"] = "#1CFFFFFF", ["PanelPressed"] = "#2AFFFFFF", ["PanelSeparator"] = "#22FFFFFF", ["Dim"] = "#80000000",
        ["Success"] = "#34D399", ["Danger"] = "#F43F5E", ["Warning"] = "#FBBF24", ["Info"] = "#60A5FA", ["ToggleOff"] = "#2C3242",
        ["Editor.Bg"] = "#12141C", ["Editor.Text"] = "#E4E7F0", ["Editor.Selection"] = "#4D7C5CFF", ["Editor.SelectionInactive"] = "#2A7C5CFF",
        ["Editor.CurrentLine"] = "#0CFFFFFF", ["Editor.LineNumber"] = "#5C6274", ["Editor.LineNumberActive"] = "#A2A8B8", ["Editor.FindMatch"] = "#40FBBF24",
    });

    public static ResourceDictionary Daylight() => Build(false, new()
    {
        ["Accent"] = "#4F46E5", ["Accent2"] = "#06B6D4", ["Glow"] = "#6366F1", ["Shadow"] = "#1E2340",
        ["AccentSoft"] = "#1F4F46E5", ["AccentGlow"] = "#554F46E5", ["OnAccent"] = "#FFFFFF", ["AccentText"] = "#4F46E5",
        ["G0"] = "#6D5AF7", ["G1"] = "#4F6BF0", ["G2"] = "#0EA5E9",
        ["Bg"] = "#F5F6FA", ["BgAlt"] = "#ECEEF5", ["Surface"] = "#FFFFFF", ["SurfaceHover"] = "#F1F3F9", ["SurfacePressed"] = "#E6E9F2",
        ["Border"] = "#E2E5EE", ["BorderStrong"] = "#CBD0DD", ["Text"] = "#161A26", ["TextDim"] = "#586074", ["TextFaint"] = "#7E8699",
        ["Panel"] = "#F2FFFFFF", ["PanelBorder"] = "#1C1E2340", ["PanelText"] = "#1B2030", ["PanelTextDim"] = "#5F677A",
        ["PanelHover"] = "#121E2340", ["PanelPressed"] = "#221E2340", ["PanelSeparator"] = "#1A1E2340", ["Dim"] = "#70000000",
        ["Success"] = "#10B981", ["Danger"] = "#E11D48", ["Warning"] = "#D97706", ["Info"] = "#2563EB", ["ToggleOff"] = "#D5D9E4",
        ["Editor.Bg"] = "#FFFFFF", ["Editor.Text"] = "#1B2030", ["Editor.Selection"] = "#334F46E5", ["Editor.SelectionInactive"] = "#1A4F46E5",
        ["Editor.CurrentLine"] = "#0A4F46E5", ["Editor.LineNumber"] = "#8F94A0", ["Editor.LineNumberActive"] = "#586074", ["Editor.FindMatch"] = "#40F59E0B",
    });

    private static ResourceDictionary Build(bool dark, Dictionary<string, string> v)
    {
        var d = new ResourceDictionary();

        void C(string name, Color c)
        {
            d["Color." + name] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            d["Brush." + name] = b;
        }

        foreach (var (k, hex) in v)
            if (k is not ("G0" or "G1" or "G2")) C(k, H(hex));

        d["Theme.IsDark"] = dark;
        d["Shadow.Opacity"] = dark ? 0.45 : 0.16;
        var stops = new[] { H(v["G0"]), H(v["G1"]), H(v["G2"]) };
        d["Brush.AccentGradient"] = Gradient(stops, new Point(0, 0), new Point(1, 1));
        d["Brush.AccentGradientH"] = Gradient(stops, new Point(0, 0.5), new Point(1, 0.5));
        d["Brush.AccentFill"] = Gradient(stops, new Point(0, 0), new Point(1, 1));

        var shadow = H(v["Shadow"]);
        C("TabHover", dark ? A(Colors.White, 0x0E) : A(shadow, 0x0C));
        C("SuccessSoft", A(H(v["Success"]), 0x26));
        C("DangerSoft", A(H(v["Danger"]), 0x26));
        C("WarningSoft", A(H(v["Warning"]), 0x26));
        C("InfoSoft", A(H(v["Info"]), 0x26));

        var eb = H(v["Editor.Bg"]);
        var tx = H(v["Text"]);
        C("Md.CodeBg", Blend(eb, tx, dark ? 0.045 : 0.035));
        C("Md.QuoteBar", H(v["Accent"]));
        C("Md.TableHeader", Blend(eb, tx, dark ? 0.06 : 0.045));
        C("Md.TableStripe", Blend(eb, tx, dark ? 0.025 : 0.018));
        return d;
    }

    private static LinearGradientBrush Gradient(Color[] stops, Point start, Point end)
    {
        var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var i = 0; i < stops.Length; i++) g.GradientStops.Add(new GradientStop(stops[i], (double)i / (stops.Length - 1)));
        g.Freeze();
        return g;
    }
}
