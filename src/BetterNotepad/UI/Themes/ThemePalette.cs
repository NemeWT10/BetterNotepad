using System.Windows.Media;

namespace BetterNotepad.UI.Themes;

/// <summary>
/// One theme = one palette. Base tokens match BetterShot's themes 1:1 (same family of apps);
/// editor and syntax tokens are BetterNotepad's own. Every token is `required`, so a theme
/// that forgets one does not compile.
/// </summary>
public sealed record ThemePalette
{
    public required string Id { get; init; }
    public required string NameKey { get; init; }
    public required bool IsDark { get; init; }

    // ── accent ──
    public required Color Accent { get; init; }
    public required Color Accent2 { get; init; }
    public required Color Glow { get; init; }
    public required Color Shadow { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color AccentGlow { get; init; }
    public required Color[] AccentGradient { get; init; }
    public required Color OnAccent { get; init; }

    // ── surfaces & text ──
    public required Color Bg { get; init; }
    public required Color BgAlt { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceHover { get; init; }
    public required Color SurfacePressed { get; init; }
    public required Color Border { get; init; }
    public required Color BorderStrong { get; init; }
    public required Color Text { get; init; }
    public required Color TextDim { get; init; }
    public required Color TextFaint { get; init; }

    // ── floating panels (menus, popovers, palette, toasts) ──
    public required Color Panel { get; init; }
    public required Color PanelBorder { get; init; }
    public required Color PanelText { get; init; }
    public required Color PanelTextDim { get; init; }
    public required Color PanelHover { get; init; }
    public required Color PanelPressed { get; init; }
    public required Color PanelSeparator { get; init; }
    public required Color Dim { get; init; }

    // ── status ──
    public required Color Success { get; init; }
    public required Color Danger { get; init; }
    public required Color Warning { get; init; }
    public required Color Info { get; init; }
    public required Color ToggleOff { get; init; }

    // ── window chrome (title + tab bar), drawn over Mica when it is on ──
    public required Color ChromeOverMica { get; init; }
    public required Color ChromeSolid { get; init; }
    public required Color TabActive { get; init; }

    // ── editor ──
    public required Color EditorBg { get; init; }
    public required Color EditorText { get; init; }
    public required Color LineNumber { get; init; }
    public required Color LineNumberActive { get; init; }
    public required Color CurrentLine { get; init; }
    public required Color Selection { get; init; }
    public required Color SelectionInactive { get; init; }
    public required Color Caret { get; init; }
    public required Color FindMatch { get; init; }
    public required Color FindMatchActive { get; init; }
    public required Color Occurrence { get; init; }
    public required Color BracketMatch { get; init; }
    public required Color IndentGuide { get; init; }
    public required Color Whitespace { get; init; }

    public required SyntaxPalette Syntax { get; init; }
}

/// <summary>
/// Semantic syntax colors. Highlighting definitions (.xshd) name these tokens instead of colors,
/// so every language follows the active theme.
/// </summary>
public sealed record SyntaxPalette
{
    public required Color Keyword { get; init; }
    public required Color Control { get; init; }
    public required Color Type { get; init; }
    public required Color Function { get; init; }
    public required Color Variable { get; init; }
    public required Color Property { get; init; }
    public required Color String { get; init; }
    public required Color Number { get; init; }
    public required Color Constant { get; init; }
    public required Color Comment { get; init; }
    public required Color Preprocessor { get; init; }
    public required Color Operator { get; init; }
    public required Color Punctuation { get; init; }
    public required Color Attribute { get; init; }
    public required Color Tag { get; init; }
    public required Color AttrName { get; init; }

    /// <summary>Comments in italics (most themes).</summary>
    public bool ItalicComments { get; init; } = true;

    /// <summary>Keywords in semi-bold (monochrome themes lean on weight instead of hue).</summary>
    public bool BoldKeywords { get; init; }
}

public enum SyntaxToken
{
    Keyword, Control, Type, Function, Variable, Property, String, Number, Constant, Comment, DocComment,
    Preprocessor, Operator, Punctuation, Attribute, Tag, AttrName, AttrValue, Entity, Escape, Regex,
    Register, Instruction, Directive, Label, Heading, Emphasis, Strong, Link, Code, Quote, ListMarker,
    Selector, CssProperty, Key, Section, Invalid, LogError, LogWarning, LogInfo, LogDebug, LogTrace,
    LogTimestamp, DiffAdded, DiffRemoved, DiffHeader,
}

public readonly record struct SyntaxStyle(Color Color, bool Bold = false, bool Italic = false, bool Underline = false);
