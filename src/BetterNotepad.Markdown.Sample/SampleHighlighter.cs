using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace BetterNotepad.Markdown.Sample;

/// <summary>
/// Stand-in for the app's AvalonEdit-based highlighter: shows the contract (language, code) -> inlines
/// whose colors are Brush.Syntax.* resource references, so they follow the theme without a re-render.
/// </summary>
internal static class SampleHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "public", "private", "static", "class", "return", "var", "new", "void", "int", "string", "bool", "if", "else", "for", "foreach", "while",
        "using", "namespace", "async", "await", "def", "import", "from", "const", "let", "function", "true", "false", "null", "None", "True", "False",
        "in", "is", "readonly", "record", "get", "set", "this", "self", "lambda", "try", "catch", "except", "throw", "raise", "yield", "fn", "pub", "impl",
    };

    private static readonly Regex Token = new(
        @"(?<comment>//[^\n]*|#[^\n]*)|(?<string>""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*')|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>[A-Za-z_][A-Za-z0-9_]*)|(?<punct>[{}()\[\];,.:=<>+\-*/!&|?]+)",
        RegexOptions.Compiled);

    public static IEnumerable<Inline>? Highlight(string language, string code)
    {
        if (language is not ("csharp" or "cs" or "js" or "javascript" or "ts" or "python" or "py" or "json" or "rust" or "bash" or "sh")) return null;
        var json = language == "json";
        var list = new List<Inline>();
        var pos = 0;
        foreach (Match m in Token.Matches(code))
        {
            if (m.Index > pos) list.Add(new Run(code[pos..m.Index]));
            string? key = null;
            var text = m.Value;
            if (m.Groups["comment"].Success) key = language is "python" or "py" or "bash" or "sh" || text.StartsWith("//") ? "Comment" : null;
            else if (m.Groups["string"].Success)
            {
                var after = code.AsSpan(m.Index + m.Length).TrimStart(' ');
                key = json && after.StartsWith(":") ? "Property" : "String";
            }
            else if (m.Groups["number"].Success) key = "Number";
            else if (m.Groups["word"].Success)
            {
                if (Keywords.Contains(text)) key = text is "true" or "false" or "null" or "None" or "True" or "False" ? "Constant" : text is "if" or "else" or "for" or "foreach" or "while" or "return" or "try" or "catch" or "except" or "throw" or "yield" or "await" ? "Control" : "Keyword";
                else if (char.IsUpper(text[0])) key = "Type";
                else if (m.Index + m.Length < code.Length && code[m.Index + m.Length] == '(') key = "Function";
            }
            else if (m.Groups["punct"].Success) key = "Punctuation";

            var run = new Run(text);
            if (key is not null) run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Syntax." + key);
            if (key == "Comment") run.FontStyle = System.Windows.FontStyles.Italic;
            list.Add(run);
            pos = m.Index + m.Length;
        }

        if (pos < code.Length) list.Add(new Run(code[pos..]));
        return list;
    }
}
