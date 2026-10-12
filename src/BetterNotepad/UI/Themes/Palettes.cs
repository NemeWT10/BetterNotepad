using System.Windows.Media;

namespace BetterNotepad.UI.Themes;

public static class Palettes
{
    private static Color H(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Aurora: deep night glass, violet → cyan accent (default dark).</summary>
    public static readonly ThemePalette Aurora = new()
    {
        Id = "aurora", NameKey = "theme.aurora", IsDark = true,
        Accent = H("#7C5CFF"), Accent2 = H("#22D3EE"), Glow = H("#8B6CFF"), Shadow = H("#000000"),
        AccentSoft = H("#2E7C5CFF"), AccentGlow = H("#667C5CFF"),
        AccentGradient = [H("#8B5CFF"), H("#5B7BFF"), H("#22D3EE")], OnAccent = H("#FFFFFF"),
        Bg = H("#0F1117"), BgAlt = H("#0B0D12"), Surface = H("#161922"), SurfaceHover = H("#1E2230"), SurfacePressed = H("#262B3B"),
        Border = H("#252A37"), BorderStrong = H("#353B4D"),
        Text = H("#EEF0F7"), TextDim = H("#A2A8B8"), TextFaint = H("#6A7185"),
        Panel = H("#EB141722"), PanelBorder = H("#2EFFFFFF"), PanelText = H("#E9ECF5"), PanelTextDim = H("#9AA1B4"),
        PanelHover = H("#1CFFFFFF"), PanelPressed = H("#2AFFFFFF"), PanelSeparator = H("#22FFFFFF"), Dim = H("#80000000"),
        Success = H("#34D399"), Danger = H("#F43F5E"), Warning = H("#FBBF24"), Info = H("#60A5FA"), ToggleOff = H("#2C3242"),
        ChromeOverMica = H("#B30F1117"), ChromeSolid = H("#0D0F15"), TabActive = H("#12141C"),
        EditorBg = H("#12141C"), EditorText = H("#E4E7F0"),
        LineNumber = H("#5C6274"), LineNumberActive = H("#A2A8B8"), CurrentLine = H("#0CFFFFFF"),
        Selection = H("#4D7C5CFF"), SelectionInactive = H("#2A7C5CFF"), Caret = H("#B39DFF"),
        FindMatch = H("#40FBBF24"), FindMatchActive = H("#80FBBF24"), Occurrence = H("#1AFFFFFF"), BracketMatch = H("#4522D3EE"),
        IndentGuide = H("#1F2430"), Whitespace = H("#3A4052"),
        Syntax = new()
        {
            Keyword = H("#A78BFA"), Control = H("#C792EA"), Type = H("#67E8F9"), Function = H("#82AAFF"),
            Variable = H("#E4E7F0"), Property = H("#A5B4FC"), String = H("#5EEAD4"), Number = H("#F9A8D4"),
            Constant = H("#FDBA74"), Comment = H("#5F667B"), Preprocessor = H("#F0ABFC"), Operator = H("#94A3B8"),
            Punctuation = H("#7C8599"), Attribute = H("#FDE68A"), Tag = H("#7DD3FC"), AttrName = H("#C4B5FD"),
        },
    };

    /// <summary>Graphite: monochrome, modernist premium — white/silver accent on near-black.</summary>
    public static readonly ThemePalette Graphite = new()
    {
        Id = "graphite", NameKey = "theme.graphite", IsDark = true,
        Accent = H("#F4F4F5"), Accent2 = H("#C9C9D1"), Glow = H("#FFFFFF"), Shadow = H("#000000"),
        AccentSoft = H("#26FFFFFF"), AccentGlow = H("#40FFFFFF"),
        AccentGradient = [H("#FFFFFF"), H("#E4E4E9"), H("#B9B9C3")], OnAccent = H("#0D0D0F"),
        Bg = H("#0D0D0F"), BgAlt = H("#09090A"), Surface = H("#151517"), SurfaceHover = H("#1D1D20"), SurfacePressed = H("#26262A"),
        Border = H("#2A2A2F"), BorderStrong = H("#3B3B42"),
        Text = H("#F4F4F5"), TextDim = H("#A1A1AA"), TextFaint = H("#6B6B75"),
        Panel = H("#F0141416"), PanelBorder = H("#38FFFFFF"), PanelText = H("#F4F4F5"), PanelTextDim = H("#A1A1AA"),
        PanelHover = H("#1CFFFFFF"), PanelPressed = H("#2AFFFFFF"), PanelSeparator = H("#26FFFFFF"), Dim = H("#85000000"),
        Success = H("#4ADE80"), Danger = H("#F87171"), Warning = H("#FBBF24"), Info = H("#93C5FD"), ToggleOff = H("#2E2E34"),
        ChromeOverMica = H("#B80D0D0F"), ChromeSolid = H("#0B0B0D"), TabActive = H("#111113"),
        EditorBg = H("#111113"), EditorText = H("#E8E8EB"),
        LineNumber = H("#606067"), LineNumberActive = H("#A1A1AA"), CurrentLine = H("#0AFFFFFF"),
        Selection = H("#38FFFFFF"), SelectionInactive = H("#1EFFFFFF"), Caret = H("#F4F4F5"),
        FindMatch = H("#40FBBF24"), FindMatchActive = H("#80FBBF24"), Occurrence = H("#1CFFFFFF"), BracketMatch = H("#40FFFFFF"),
        IndentGuide = H("#202024"), Whitespace = H("#3A3A40"),
        Syntax = new()
        {
            Keyword = H("#ECECEF"), Control = H("#FFFFFF"), Type = H("#B8C4D6"), Function = H("#DAD7D3"),
            Variable = H("#D4D4D8"), Property = H("#C2C2CA"), String = H("#A9BDA5"), Number = H("#D9BBA3"),
            Constant = H("#C8B8E2"), Comment = H("#62626C"), Preprocessor = H("#B4B4BE"), Operator = H("#9F9FA8"),
            Punctuation = H("#77777F"), Attribute = H("#CFC4A8"), Tag = H("#DADADF"), AttrName = H("#A6A6AF"),
            BoldKeywords = true,
        },
    };

    /// <summary>Daylight: airy light theme, indigo → cyan accent.</summary>
    public static readonly ThemePalette Daylight = new()
    {
        Id = "daylight", NameKey = "theme.daylight", IsDark = false,
        Accent = H("#4F46E5"), Accent2 = H("#06B6D4"), Glow = H("#6366F1"), Shadow = H("#1E2340"),
        AccentSoft = H("#1F4F46E5"), AccentGlow = H("#554F46E5"),
        AccentGradient = [H("#6D5AF7"), H("#4F6BF0"), H("#0EA5E9")], OnAccent = H("#FFFFFF"),
        Bg = H("#F5F6FA"), BgAlt = H("#ECEEF5"), Surface = H("#FFFFFF"), SurfaceHover = H("#F1F3F9"), SurfacePressed = H("#E6E9F2"),
        Border = H("#E2E5EE"), BorderStrong = H("#CBD0DD"),
        Text = H("#161A26"), TextDim = H("#586074"), TextFaint = H("#7E8699") /* #8B93A7 read at 2.8:1 on Bg */,
        Panel = H("#F2FFFFFF"), PanelBorder = H("#1C1E2340"), PanelText = H("#1B2030"), PanelTextDim = H("#5F677A"),
        PanelHover = H("#121E2340"), PanelPressed = H("#221E2340"), PanelSeparator = H("#1A1E2340"), Dim = H("#70000000"),
        Success = H("#10B981"), Danger = H("#E11D48"), Warning = H("#D97706"), Info = H("#2563EB"), ToggleOff = H("#D5D9E4"),
        ChromeOverMica = H("#99F5F6FA"), ChromeSolid = H("#EEF0F6"), TabActive = H("#FFFFFF"),
        EditorBg = H("#FFFFFF"), EditorText = H("#1B2030"),
        LineNumber = H("#8F94A0"), LineNumberActive = H("#586074"), CurrentLine = H("#0A4F46E5"),
        Selection = H("#334F46E5"), SelectionInactive = H("#1A4F46E5"), Caret = H("#4F46E5"),
        FindMatch = H("#40F59E0B"), FindMatchActive = H("#80F59E0B"), Occurrence = H("#164F46E5"), BracketMatch = H("#3306B6D4"),
        IndentGuide = H("#EDEFF5"), Whitespace = H("#C9CED9"),
        Syntax = new()
        {
            Keyword = H("#7C3AED"), Control = H("#A21CAF"), Type = H("#0E7490"), Function = H("#1D4ED8"),
            Variable = H("#1F2937"), Property = H("#4338CA"), String = H("#047857"), Number = H("#B45309"),
            Constant = H("#C2410C"), Comment = H("#8B93A7"), Preprocessor = H("#9333EA"), Operator = H("#475569"),
            Punctuation = H("#64748B"), Attribute = H("#A16207"), Tag = H("#0369A1"), AttrName = H("#7C3AED"),
        },
    };

    /// <summary>Neon (toned down): near-black with a soft magenta → violet → cyan accent.</summary>
    public static readonly ThemePalette Neon = new()
    {
        Id = "neon", NameKey = "theme.neon", IsDark = true,
        Accent = H("#D65BC4"), Accent2 = H("#4CCFDC"), Glow = H("#C85CC0"), Shadow = H("#000000"),
        AccentSoft = H("#26D65BC4"), AccentGlow = H("#4DD65BC4"),
        AccentGradient = [H("#D65BC4"), H("#8E6AE6"), H("#4CCFDC")], OnAccent = H("#FFFFFF"),
        Bg = H("#0A0A10"), BgAlt = H("#07070B"), Surface = H("#12121B"), SurfaceHover = H("#1A1A26"), SurfacePressed = H("#222232"),
        Border = H("#232334"), BorderStrong = H("#32324A"),
        Text = H("#F2F1FA"), TextDim = H("#A3A0C0"), TextFaint = H("#66637F"),
        Panel = H("#EE0D0D15"), PanelBorder = H("#38B06AD0"), PanelText = H("#F2F1FA"), PanelTextDim = H("#A3A0C0"),
        PanelHover = H("#1CD65BC4"), PanelPressed = H("#30D65BC4"), PanelSeparator = H("#28B06AD0"), Dim = H("#85000000"),
        Success = H("#4ADE80"), Danger = H("#FB7185"), Warning = H("#FACC15"), Info = H("#7ADDE6"), ToggleOff = H("#262638"),
        ChromeOverMica = H("#B80A0A10"), ChromeSolid = H("#08080D"), TabActive = H("#0D0D15"),
        EditorBg = H("#0D0D15"), EditorText = H("#E9E7F5"),
        LineNumber = H("#5F5D73"), LineNumberActive = H("#A3A0C0"), CurrentLine = H("#0CD65BC4"),
        Selection = H("#45D65BC4"), SelectionInactive = H("#24D65BC4"), Caret = H("#E89BDD"),
        FindMatch = H("#40FACC15"), FindMatchActive = H("#80FACC15"), Occurrence = H("#1AFFFFFF"), BracketMatch = H("#404CCFDC"),
        IndentGuide = H("#1B1B29"), Whitespace = H("#38364D"),
        Syntax = new()
        {
            Keyword = H("#D98AE6"), Control = H("#F0A6E8"), Type = H("#7ADDE6"), Function = H("#A994F0"),
            Variable = H("#E9E7F5"), Property = H("#B4A9F2"), String = H("#7EE0C3"), Number = H("#F9B475"),
            Constant = H("#FF9DC6"), Comment = H("#66637E"), Preprocessor = H("#C4A0F5"), Operator = H("#9F9CBE"),
            Punctuation = H("#77749A"), Attribute = H("#F2D27A"), Tag = H("#7ADDE6"), AttrName = H("#D98AE6"),
        },
    };

    /// <summary>Sunset: warm dark, orange → pink.</summary>
    public static readonly ThemePalette Sunset = new()
    {
        Id = "sunset", NameKey = "theme.sunset", IsDark = true,
        Accent = H("#FF6B3D"), Accent2 = H("#FF3D8B"), Glow = H("#FF7A45"), Shadow = H("#000000"),
        AccentSoft = H("#2EFF6B3D"), AccentGlow = H("#66FF6B3D"),
        AccentGradient = [H("#FFB03D"), H("#FF6B3D"), H("#FF3D8B")], OnAccent = H("#2B1108") /* dark: white fails contrast on the orange stops */,
        Bg = H("#15100F"), BgAlt = H("#110C0B"), Surface = H("#1F1715"), SurfaceHover = H("#2A1F1C"), SurfacePressed = H("#342622"),
        Border = H("#33241F"), BorderStrong = H("#47322B"),
        Text = H("#FFF3EC"), TextDim = H("#CBAEA3"), TextFaint = H("#8C7068"),
        Panel = H("#EC1D1513"), PanelBorder = H("#33FFB199"), PanelText = H("#FFF3EC"), PanelTextDim = H("#C7A99E"),
        PanelHover = H("#22FFB199"), PanelPressed = H("#33FFB199"), PanelSeparator = H("#26FFB199"), Dim = H("#80000000"),
        Success = H("#4ADE80"), Danger = H("#FB7185"), Warning = H("#FACC15"), Info = H("#FDBA74"), ToggleOff = H("#3A2B26"),
        ChromeOverMica = H("#B815100F"), ChromeSolid = H("#120D0C"), TabActive = H("#18120F"),
        EditorBg = H("#18120F"), EditorText = H("#FBE9DF"),
        LineNumber = H("#715E57"), LineNumberActive = H("#CBAEA3"), CurrentLine = H("#0DFFB199"),
        Selection = H("#45FF6B3D"), SelectionInactive = H("#24FF6B3D"), Caret = H("#FF8A65"),
        FindMatch = H("#40FACC15"), FindMatchActive = H("#80FACC15"), Occurrence = H("#1CFFFFFF"), BracketMatch = H("#40FF3D8B"),
        IndentGuide = H("#271C19"), Whitespace = H("#4A3832"),
        Syntax = new()
        {
            Keyword = H("#FF8A65"), Control = H("#FF7AA8"), Type = H("#FFD08A"), Function = H("#FFB07A"),
            Variable = H("#FBE9DF"), Property = H("#F4B6A0"), String = H("#B5D99C"), Number = H("#F7A4C4"),
            Constant = H("#FFC46B"), Comment = H("#7E655D"), Preprocessor = H("#FF9E80"), Operator = H("#CBAEA3"),
            Punctuation = H("#9C807A"), Attribute = H("#FFD27A"), Tag = H("#FF8A65"), AttrName = H("#FFB07A"),
        },
    };

    public static readonly IReadOnlyList<ThemePalette> All = [Aurora, Graphite, Daylight, Neon, Sunset];

    public static ThemePalette ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Aurora;
}
