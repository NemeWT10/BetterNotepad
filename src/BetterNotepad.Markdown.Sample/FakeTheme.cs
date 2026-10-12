using System.Windows;
using System.Windows.Media;

namespace BetterNotepad.Markdown.Sample;

/// <summary>Brush.* resources copied from the app's Aurora / Daylight palettes (ThemeManager.Build).</summary>
internal static class FakeTheme
{
    private static Color H(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Blend(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static ResourceDictionary Aurora() => Build(
        dark: true, accent: "#7C5CFF", accentSoft: "#2E7C5CFF", onAccent: "#FFFFFF",
        bg: "#0F1117", border: "#252A37", borderStrong: "#353B4D", text: "#EEF0F7", textDim: "#A2A8B8", textFaint: "#6A7185",
        success: "#34D399", danger: "#F43F5E", warning: "#FBBF24", info: "#60A5FA",
        editorBg: "#12141C", selection: "#4D7C5CFF", caret: "#B39DFF",
        syntax: new()
        {
            ["Keyword"] = "#A78BFA", ["Control"] = "#C792EA", ["Type"] = "#67E8F9", ["Function"] = "#82AAFF", ["Variable"] = "#E4E7F0",
            ["Property"] = "#A5B4FC", ["String"] = "#5EEAD4", ["Number"] = "#F9A8D4", ["Constant"] = "#FDBA74", ["Comment"] = "#5F667B",
            ["Preprocessor"] = "#F0ABFC", ["Operator"] = "#94A3B8", ["Punctuation"] = "#7C8599", ["Attribute"] = "#FDE68A", ["Tag"] = "#7DD3FC", ["AttrName"] = "#C4B5FD",
        });

    public static ResourceDictionary Daylight() => Build(
        dark: false, accent: "#4F46E5", accentSoft: "#1F4F46E5", onAccent: "#FFFFFF",
        bg: "#F5F6FA", border: "#E2E5EE", borderStrong: "#CBD0DD", text: "#161A26", textDim: "#586074", textFaint: "#8B93A7",
        success: "#10B981", danger: "#E11D48", warning: "#D97706", info: "#2563EB",
        editorBg: "#FFFFFF", selection: "#334F46E5", caret: "#4F46E5",
        syntax: new()
        {
            ["Keyword"] = "#7C3AED", ["Control"] = "#A21CAF", ["Type"] = "#0E7490", ["Function"] = "#1D4ED8", ["Variable"] = "#1F2937",
            ["Property"] = "#4338CA", ["String"] = "#047857", ["Number"] = "#B45309", ["Constant"] = "#C2410C", ["Comment"] = "#8B93A7",
            ["Preprocessor"] = "#9333EA", ["Operator"] = "#475569", ["Punctuation"] = "#64748B", ["Attribute"] = "#A16207", ["Tag"] = "#0369A1", ["AttrName"] = "#7C3AED",
        });

    private static ResourceDictionary Build(
        bool dark, string accent, string accentSoft, string onAccent, string bg, string border, string borderStrong,
        string text, string textDim, string textFaint, string success, string danger, string warning, string info,
        string editorBg, string selection, string caret, Dictionary<string, string> syntax)
    {
        var d = new ResourceDictionary();

        void C(string name, Color c)
        {
            d["Color." + name] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            d["Brush." + name] = b;
        }

        C("Accent", H(accent));
        C("AccentSoft", H(accentSoft));
        C("AccentText", H(caret));
        C("OnAccent", H(onAccent));
        C("Bg", H(bg));
        C("Border", H(border));
        C("BorderStrong", H(borderStrong));
        C("Text", H(text));
        C("TextDim", H(textDim));
        C("TextFaint", H(textFaint));
        C("Success", H(success));
        C("Danger", H(danger));
        C("Warning", H(warning));
        C("Info", H(info));
        C("SuccessSoft", WithAlpha(H(success), 0x26));
        C("DangerSoft", WithAlpha(H(danger), 0x26));
        C("WarningSoft", WithAlpha(H(warning), 0x26));
        C("InfoSoft", WithAlpha(H(info), 0x26));
        C("Editor.Bg", H(editorBg));
        C("Editor.Text", H(text));
        C("Editor.Selection", H(selection));

        var eb = H(editorBg);
        var tx = H(text);
        C("Md.CodeBg", Blend(eb, tx, dark ? 0.045 : 0.035));
        C("Md.QuoteBar", H(accent));
        C("Md.TableHeader", Blend(eb, tx, dark ? 0.06 : 0.045));
        C("Md.TableStripe", Blend(eb, tx, dark ? 0.025 : 0.018));

        foreach (var (token, hex) in syntax) C("Syntax." + token, H(hex));
        return d;
    }
}
