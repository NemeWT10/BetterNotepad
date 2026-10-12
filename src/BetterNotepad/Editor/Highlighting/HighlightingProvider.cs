using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using BetterNotepad.Core;
using BetterNotepad.UI.Themes;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace BetterNotepad.Editor.Highlighting;

/// <summary>
/// Loads syntax definitions and paints them with the active theme. Our .xshd files name semantic
/// tokens (Keyword, String, …) instead of colors; AvalonEdit's built-in ones are remapped by name.
/// Definitions are cached per theme and rebuilt on theme change.
/// </summary>
public static class HighlightingProvider
{
    private const string ResourcePrefix = "BetterNotepad.Editor.Highlighting.Definitions.";
    private static readonly Dictionary<string, IHighlightingDefinition?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Resolver ResolverInstance = new();
    private static string _themeId = "";

    /// <summary>Raised after the theme changed and editors should re-fetch their definition.</summary>
    public static event Action? Invalidated;

    static HighlightingProvider()
    {
        ThemeManager.I.Changed += () =>
        {
            if (_themeId == ThemeManager.I.Current.Id) return;
            Cache.Clear();
            Invalidated?.Invoke();
        };
    }

    public static IHighlightingDefinition? ForLanguage(string languageId)
    {
        var lang = Languages.Get(languageId);
        return lang.Definition is null ? null : ByName(lang.Definition);
    }

    /// <summary>By definition name: "CSharp" (our resource) or "builtin:C#".</summary>
    public static IHighlightingDefinition? ByName(string name)
    {
        _themeId = ThemeManager.I.Current.Id;
        if (Cache.TryGetValue(name, out var def)) return def;
        Cache[name] = null; // guards against recursive self-references while loading
        try
        {
            def = Load(name);
        }
        catch (Exception ex)
        {
            Log.Error($"highlighting: '{name}' failed to load", ex);
            def = null;
        }
        Cache[name] = def;
        return def;
    }

    private static IHighlightingDefinition? Load(string name)
    {
        Stream? stream;
        if (name.StartsWith("builtin:", StringComparison.Ordinal))
        {
            stream = BuiltinStream(name["builtin:".Length..]);
        }
        else
        {
            stream = typeof(HighlightingProvider).Assembly.GetManifestResourceStream(ResourcePrefix + name + ".xshd");
            if (stream is null && BuiltinFallback.TryGetValue(name, out var builtin)) stream = BuiltinStream(builtin);
        }
        if (stream is null) return null;

        XshdSyntaxDefinition xshd;
        using (stream)
        using (var reader = XmlReader.Create(stream))
            xshd = HighlightingLoader.LoadXshd(reader);

        Paint(xshd);
        return HighlightingLoader.Load(xshd, ResolverInstance);
    }

    /// <summary>Until our own definition for a language exists, AvalonEdit's built-in one is themed instead.</summary>
    private static readonly Dictionary<string, string> BuiltinFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CSharp"] = "CSharp-Mode",
        ["JavaScript"] = "JavaScript-Mode",
        ["TypeScript"] = "JavaScript-Mode",
        ["Html"] = "HTML-Mode",
        ["Css"] = "CSS-Mode",
        ["Scss"] = "CSS-Mode",
        ["Cpp"] = "CPP-Mode",
        ["C"] = "CPP-Mode",
        ["Java"] = "Java-Mode",
        ["Php"] = "PHP-Mode",
        ["Python"] = "Python-Mode",
        ["PowerShell"] = "PowerShell",
        ["Sql"] = "TSQL-Mode",
        ["Xml"] = "XML-Mode",
        ["Markdown"] = "MarkDown-Mode",
        ["Json"] = "Json",
        ["VB"] = "VB-Mode",
        ["Diff"] = "Patch-Mode",
        ["Latex"] = "Tex-Mode",
    };

    private static Stream? BuiltinStream(string fileStem)
    {
        var asm = typeof(HighlightingManager).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n =>
            n.EndsWith("." + fileStem + ".xshd", StringComparison.OrdinalIgnoreCase));
        return res is null ? null : asm.GetManifestResourceStream(res);
    }

    private static void Paint(XshdSyntaxDefinition xshd)
    {
        var styles = ThemeManager.I.Syntax;
        foreach (var color in xshd.Elements.OfType<XshdColor>())
        {
            if (color.Name is null) continue;
            var token = TokenFor(color.Name);
            var style = styles[token];
            color.Foreground = new SimpleHighlightingBrush(style.Color);
            color.Background = null;
            color.FontWeight = style.Bold ? FontWeights.SemiBold : FontWeights.Normal;
            color.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
            color.Underline = style.Underline ? true : null;
            color.FontFamily = null;
            color.FontSize = null;
        }
    }

    /// <summary>Exact token names first (our definitions), then heuristics for built-in color names.</summary>
    public static SyntaxToken TokenFor(string colorName)
    {
        if (Enum.TryParse<SyntaxToken>(colorName, ignoreCase: true, out var exact)) return exact;
        var n = colorName.ToLowerInvariant();
        if (n.Contains("xmldoc") || n.Contains("doccomment")) return SyntaxToken.DocComment;
        if (n.Contains("comment")) return SyntaxToken.Comment;
        if (n.Contains("attributevalue")) return SyntaxToken.AttrValue;
        if (n.Contains("attributename")) return SyntaxToken.AttrName;
        if (n.Contains("entity")) return SyntaxToken.Entity;
        if (n.Contains("regex")) return SyntaxToken.Regex;
        if (n.Contains("string") || n.Contains("char") || n is "value" or "cdata") return SyntaxToken.String;
        if (n.Contains("number") || n.Contains("digit")) return SyntaxToken.Number;
        if (n.Contains("preprocessor") || n.Contains("doctype") || n.Contains("declaration") || n.Contains("directive")) return SyntaxToken.Preprocessor;
        if (n.Contains("truefalse") || n.Contains("null") || n.Contains("literal") || n.Contains("constant") || n.Contains("thisorbase")) return SyntaxToken.Constant;
        if (n.Contains("goto") || n.Contains("exception") || n.Contains("control")) return SyntaxToken.Control;
        if (n.Contains("valuetype") || n.Contains("referencetype") || n.Contains("type") || n.Contains("class")) return SyntaxToken.Type;
        if (n.Contains("method") || n.Contains("function") || n.Contains("command") || n.Contains("intrinsic")) return SyntaxToken.Function;
        if (n.Contains("strongemphasis") || n == "strong") return SyntaxToken.Strong;
        if (n.Contains("emphasis")) return SyntaxToken.Emphasis;
        if (n.Contains("heading") || n.Contains("header")) return SyntaxToken.Heading;
        if (n.Contains("blockquote")) return SyntaxToken.Quote;
        if (n.Contains("code")) return SyntaxToken.Code;
        if (n.Contains("link") || n.Contains("image") || n.Contains("url")) return SyntaxToken.Link;
        if (n.Contains("tag")) return SyntaxToken.Tag;
        if (n.Contains("selector")) return SyntaxToken.Selector;
        if (n.Contains("property")) return SyntaxToken.Property;
        if (n.Contains("variable") || n.Contains("parameter")) return SyntaxToken.Variable;
        if (n.Contains("operator")) return SyntaxToken.Operator;
        if (n.Contains("punctuation") || n.Contains("brace") || n.Contains("colon") || n.Contains("bracket")) return SyntaxToken.Punctuation;
        if (n.Contains("added") || n.Contains("addedtext")) return SyntaxToken.DiffAdded;
        if (n.Contains("removed") || n.Contains("deleted")) return SyntaxToken.DiffRemoved;
        if (n.Contains("keyword") || n.Contains("modifier") || n.Contains("visibility") || n.Contains("namespace") || n.Contains("unsafe") || n.Contains("checked")) return SyntaxToken.Keyword;
        return SyntaxToken.Keyword;
    }

    /// <summary>Resolves cross-definition references (HTML → JavaScript/CSS) to our themed definitions.</summary>
    private sealed class Resolver : IHighlightingDefinitionReferenceResolver
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["C#"] = "CSharp", ["JavaScript"] = "JavaScript", ["CSS"] = "Css", ["HTML"] = "Html", ["XML"] = "Xml",
            ["C++"] = "Cpp", ["PHP"] = "Php", ["Python"] = "Python", ["TSQL"] = "Sql", ["PowerShell"] = "PowerShell",
            ["MarkDown"] = "Markdown", ["Java"] = "Java", ["Json"] = "Json", ["VB"] = "VB", ["Patch"] = "Diff", ["TeX"] = "Latex",
        };

        public IHighlightingDefinition? GetDefinition(string name)
            => ByName(Aliases.TryGetValue(name, out var mapped) ? mapped : name) ?? HighlightingManager.Instance.GetDefinition(name);
    }
}
