using System.Text.RegularExpressions;

namespace BetterNotepad.UI.Palette;

public enum SymbolKind
{
    Function,
    Method,
    Class,
    Interface,
    Struct,
    Enum,
    Namespace,
    Type,
    Heading,
    Section,
    Label,
    Table,
    Key,
}

/// <summary>A symbol found in a file: 1-based line, 1-based column of the name, heading level for Markdown.</summary>
public sealed record Symbol(string Name, SymbolKind Kind, int Line, int Column, int Length, string? Container = null, int Level = 0);

/// <summary>
/// Regex-based "go to symbol": functions, classes, methods and friends for the common languages,
/// headings for Markdown / LaTeX, sections for INI / TOML. No parser — fast, forgiving, good enough
/// for a notepad. Runs on a plain string, so it can run off the UI thread.
/// </summary>
public static class SymbolScanner
{
    private const int MaxSymbols = 5000;
    private const int MaxLineLength = 600;

    /// <summary>One rule: a regex over a single line; group "name" is the symbol, optional group "kind" picks the kind.</summary>
    private sealed record Rule(Regex Re, SymbolKind Kind, Func<string, SymbolKind?>? KindOf = null);

    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static Rule R(string pattern, SymbolKind kind, Func<string, SymbolKind?>? kindOf = null, bool ignoreCase = false)
        => new(new Regex(pattern, Opt | (ignoreCase ? RegexOptions.IgnoreCase : 0), TimeSpan.FromMilliseconds(50)), kind, kindOf);

    private static SymbolKind? TypeWord(string word) => word.Trim().Split(' ')[^1].ToLowerInvariant() switch
    {
        "class" or "record" or "object" or "impl" or "extension" or "module" => SymbolKind.Class,
        "interface" or "protocol" or "trait" => SymbolKind.Interface,
        "struct" or "union" or "structure" => SymbolKind.Struct,
        "enum" => SymbolKind.Enum,
        "namespace" or "mod" or "package" => SymbolKind.Namespace,
        "type" or "typedef" => SymbolKind.Type,
        "sub" or "function" or "fn" or "def" or "func" => SymbolKind.Function,
        "table" or "view" => SymbolKind.Table,
        "procedure" or "trigger" => SymbolKind.Function,
        "index" => SymbolKind.Key,
        _ => null,
    };

    private const string CsMods = @"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|file|virtual|override|async|extern|required|final|synchronized|native|default|transient|strictfp|open|data|inline|suspend|operator|infix|tailrec|external|lateinit|const|actual|expect)\s+)";
    private const string TypeRef = @"(?:[\w.]+(?:<[^<>()]*(?:<[^<>()]*>[^<>()]*)*>)?(?:\[[\],\s]*\])*\??\s+)";

    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "else", "do", "try",
        "sizeof", "typeof", "nameof", "when", "with", "fixed", "checked", "unchecked", "function", "await", "throw",
        "elif", "except", "match", "case", "default", "delete", "void", "base", "this", "super",
    };

    private static readonly Rule[] CLike =
    [
        R(@"^\s*(?:typedef\s+)?(?<kind>class|struct|union|enum(?:\s+class)?|namespace)\s+(?:\[\[[^\]]*\]\]\s*)?(?<name>[A-Za-z_]\w*)(?!\s*;)\s*(?:[:{]|$|final\b)", SymbolKind.Class, TypeWord),
        // function definitions start at column 0 and do not end with ';'
        R(@"^(?!(?:if|for|while|switch|return|else|do|case|typedef|using)\b)(?:[\w:*&<>,~]+\s+[*&]*)+(?<name>[A-Za-z_~][\w:~]*)\s*\([^;]*$", SymbolKind.Function),
    ];

    private static readonly Rule[] CSharp =
    [
        R(@"^\s*(?:\[[^\]]*\]\s*)*" + CsMods + @"*(?<kind>class|struct|interface|enum|record(?:\s+struct|\s+class)?|namespace)\s+(?<name>@?[A-Za-z_][\w.]*)", SymbolKind.Class, TypeWord),
        R(@"^\s*" + CsMods + "+" + TypeRef + @"?(?<name>[A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\((?![^=]*\)\s*;\s*$)", SymbolKind.Method),
    ];

    private static readonly Rule[] Java =
    [
        R(@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*" + CsMods + @"*(?<kind>class|interface|enum|record|@interface)\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Class, TypeWord),
        R(@"^\s*" + CsMods + "+" + @"(?:<[^>]+>\s+)?" + TypeRef + @"?(?<name>[A-Za-z_]\w*)\s*\((?![^=]*\)\s*;\s*$)", SymbolKind.Method),
    ];

    private static readonly Rule[] Kotlin =
    [
        R(@"^\s*" + CsMods + @"*(?<kind>class|interface|object|enum\s+class|data\s+class|sealed\s+class|annotation\s+class)\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Class, TypeWord),
        R(@"^\s*" + CsMods + @"*fun\s+(?:<[^>]*>\s*)?(?:[\w.<>]+\.)?(?<name>[A-Za-z_]\w*)\s*\(", SymbolKind.Function),
    ];

    private static readonly Rule[] Swift =
    [
        R(@"^\s*(?:(?:public|private|fileprivate|internal|open|final|static|indirect)\s+)*(?<kind>class|struct|enum|protocol|extension|actor)\s+(?<name>[A-Za-z_][\w.]*)", SymbolKind.Class, TypeWord),
        R(@"^\s*(?:(?:public|private|fileprivate|internal|open|final|static|class|override|mutating|@\w+)\s+)*func\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Function),
    ];

    private static readonly Rule[] Dart =
    [
        R(@"^\s*(?:abstract\s+|sealed\s+|base\s+|final\s+)*(?<kind>class|mixin|enum|extension)\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Class, TypeWord),
        R(@"^\s*(?:static\s+|external\s+|@\w+\s+)*(?:[\w<>?,\s]+\s+)?(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*(?:async\s*)?(?:\{|=>)", SymbolKind.Function),
    ];

    private static readonly Rule[] Script =
    [
        R(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?<kind>class|interface|enum|namespace|module)\s+(?<name>[A-Za-z_$][\w$]*)", SymbolKind.Class, TypeWord),
        R(@"^\s*(?:export\s+)?(?:declare\s+)?(?<kind>type)\s+(?<name>[A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*=", SymbolKind.Type, TypeWord),
        R(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(?<name>[A-Za-z_$][\w$]*)", SymbolKind.Function),
        R(@"^\s*(?:export\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|[A-Za-z_$][\w$]*\s*=>)", SymbolKind.Function),
        R(@"^\s+(?:(?:public|private|protected|static|async|readonly|override|abstract|get|set)\s+)*\*?(?<name>[A-Za-z_$#][\w$]*)\s*(?:<[^>]*>)?\s*\([^)]*\)\s*(?::\s*[^{=;]+)?\{\s*(?:\}\s*)?$", SymbolKind.Method),
    ];

    private static readonly Rule[] Python =
    [
        R(@"^\s*class\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Class),
        R(@"^\s*(?:async\s+)?def\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Function),
    ];

    private static readonly Rule[] Go =
    [
        R(@"^type\s+(?<name>[A-Za-z_]\w*)\s+(?<kind>struct|interface)\b", SymbolKind.Struct, TypeWord),
        R(@"^type\s+(?<name>[A-Za-z_]\w*)\s+\S", SymbolKind.Type),
        R(@"^func\s+\([^)]*\)\s*(?<name>[A-Za-z_]\w*)", SymbolKind.Method),
        R(@"^func\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Function),
    ];

    private static readonly Rule[] Rust =
    [
        R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:unsafe\s+)?(?<kind>struct|enum|trait|mod|union|type)\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Struct, TypeWord),
        R(@"^\s*(?:unsafe\s+)?impl(?:<[^>]*>)?\s+(?:[\w:<>]+\s+for\s+)?(?<name>[A-Za-z_][\w:]*)", SymbolKind.Class),
        R(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:default\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?(?:extern\s+""[^""]*""\s+)?fn\s+(?<name>[A-Za-z_]\w*)", SymbolKind.Function),
    ];

    private static readonly Rule[] Php =
    [
        R(@"^\s*(?:abstract\s+|final\s+|readonly\s+)*(?<kind>class|interface|trait|enum|namespace)\s+(?<name>[A-Za-z_\\][\w\\]*)", SymbolKind.Class, TypeWord),
        R(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(?<name>[A-Za-z_]\w*)", SymbolKind.Function),
    ];

    private static readonly Rule[] Ruby =
    [
        R(@"^\s*(?<kind>class|module)\s+(?<name>[A-Z][\w:]*)", SymbolKind.Class, TypeWord),
        R(@"^\s*def\s+(?:self\.)?(?<name>[\w?!=\[\]+\-*/<>]+)", SymbolKind.Function),
    ];

    private static readonly Rule[] Lua =
    [
        R(@"^\s*(?:local\s+)?function\s+(?<name>[\w.:]+)", SymbolKind.Function),
        R(@"^\s*(?:local\s+)?(?<name>[\w.]+)\s*=\s*function\b", SymbolKind.Function),
    ];

    private static readonly Rule[] PowerShell =
    [
        R(@"^\s*(?<kind>class|enum)\s+(?<name>[\w-]+)", SymbolKind.Class, TypeWord, ignoreCase: true),
        R(@"^\s*(?:function|filter|workflow)\s+(?:[\w]+:)?(?<name>[\w-]+)", SymbolKind.Function, ignoreCase: true),
    ];

    private static readonly Rule[] Shell =
    [
        R(@"^\s*function\s+(?<name>[\w.:-]+)", SymbolKind.Function),
        R(@"^\s*(?<name>[A-Za-z_][\w.:-]*)\s*\(\)\s*(?:\{|$)", SymbolKind.Function),
    ];

    private static readonly Rule[] Batch = [R(@"^\s*:(?!:)(?<name>[\w.-]+)", SymbolKind.Label)];

    private static readonly Rule[] Vb =
    [
        R(@"^\s*(?:(?:Public|Private|Friend|Protected|Shared|Partial|MustInherit|NotInheritable)\s+)*(?<kind>Class|Module|Structure|Interface|Enum|Namespace)\s+(?<name>\w+)", SymbolKind.Class, TypeWord, ignoreCase: true),
        R(@"^\s*(?:(?:Public|Private|Friend|Protected|Shared|Overrides|Overridable|MustOverride|Overloads|Async|Static)\s+)*(?<kind>Sub|Function)\s+(?<name>\w+)", SymbolKind.Function, TypeWord, ignoreCase: true),
    ];

    private static readonly Rule[] FSharp =
    [
        R(@"^\s*(?:type|and)\s+(?:private\s+|internal\s+)?(?<name>[A-Za-z_]\w*)", SymbolKind.Type),
        R(@"^\s*module\s+(?:rec\s+)?(?<name>[\w.]+)", SymbolKind.Namespace),
        R(@"^\s*(?:let|member|static\s+member|override)\s+(?:rec\s+|inline\s+|private\s+|mutable\s+)*(?:\w+\.)?(?<name>[A-Za-z_]\w*)\s*(?:\(|[A-Za-z_(])", SymbolKind.Function),
    ];

    private static readonly Rule[] R_ = [R(@"^\s*(?<name>[\w.]+)\s*(?:<-|=)\s*function\s*\(", SymbolKind.Function)];

    private static readonly Rule[] Sql =
    [
        R(@"^\s*create\s+(?:or\s+(?:replace|alter)\s+)?(?:temp(?:orary)?\s+|unique\s+|materialized\s+)?(?<kind>table|view|procedure|proc|function|trigger|index|type)\s+(?:if\s+not\s+exists\s+)?(?<name>[\w.\[\]""`]+)", SymbolKind.Table,
          w => w.ToLowerInvariant() == "proc" ? SymbolKind.Function : TypeWord(w), ignoreCase: true),
    ];

    private static readonly Rule[] Ini = [R(@"^\s*\[\[?\s*(?<name>[^\]]+?)\s*\]\]?", SymbolKind.Section)];

    private static readonly Rule[] Yaml = [R(@"^(?<name>[A-Za-z_""'][\w .""'-]*?)\s*:(?:\s|$)", SymbolKind.Key)];

    private static readonly Rule[] Make = [R(@"^(?<name>[\w.\-/%$()]+(?:\s+[\w.\-/%$()]+)*)\s*::?(?!=)", SymbolKind.Label)];

    private static readonly Rule[] CMake = [R(@"^\s*(?:function|macro)\s*\(\s*(?<name>[\w-]+)", SymbolKind.Function, ignoreCase: true)];

    private static readonly Rule[] Asm =
    [
        R(@"^\s*(?<name>[A-Za-z_.$?@][\w.$?@]*):(?!:)", SymbolKind.Label),
        R(@"^\s*(?<name>[A-Za-z_.$?@][\w.$?@]*)\s+(?:proc|PROC)\b", SymbolKind.Function),
    ];

    private static readonly Rule[] Css =
    [
        R(@"^(?<name>[^\s@{}/][^{};]*?)\s*\{", SymbolKind.Label),
        R(@"^\s*@(?<kind>mixin|function)\s+(?<name>[\w-]+)", SymbolKind.Function),
    ];

    private static readonly Regex MdAtx = new(@"^ {0,3}(?<hash>#{1,6})[ \t]+(?<name>.*?)(?:[ \t]+#+)?[ \t]*$", Opt);
    private static readonly Regex MdFence = new(@"^ {0,3}(```|~~~)", Opt);
    private static readonly Regex MdSetext = new(@"^ {0,3}(=+|-+)[ \t]*$", Opt);
    private static readonly Regex Latex = new(@"\\(?<kind>part|chapter|section|subsection|subsubsection|paragraph)\*?\s*(?:\[[^\]]*\])?\{(?<name>[^}]*)\}", Opt);

    private static Rule[]? RulesFor(string languageId) => languageId switch
    {
        "c" or "cpp" => CLike,
        "csharp" => CSharp,
        "java" => Java,
        "kotlin" => Kotlin,
        "swift" => Swift,
        "dart" => Dart,
        "javascript" or "typescript" => Script,
        "python" => Python,
        "go" => Go,
        "rust" => Rust,
        "php" => Php,
        "ruby" => Ruby,
        "lua" => Lua,
        "powershell" => PowerShell,
        "shell" => Shell,
        "batch" => Batch,
        "vb" => Vb,
        "fsharp" => FSharp,
        "r" => R_,
        "sql" => Sql,
        "ini" or "toml" => Ini,
        "yaml" => Yaml,
        "makefile" => Make,
        "cmake" => CMake,
        "asm-x86" or "asm-arm" or "asm-avr" => Asm,
        "css" or "scss" => Css,
        _ => null,
    };

    /// <summary>True when the language has a symbol scanner at all.</summary>
    public static bool Supports(string languageId) => languageId is "markdown" or "latex" || RulesFor(languageId) is not null;

    public static List<Symbol> Scan(string text, string languageId)
    {
        if (languageId == "markdown") return ScanMarkdown(text);
        if (languageId == "latex") return ScanLatex(text);
        var rules = RulesFor(languageId);
        var result = new List<Symbol>();
        if (rules is null) return result;

        var indentLanguage = languageId is "python" or "ruby" or "csharp" or "java" or "kotlin" or "typescript" or "javascript" or "php" or "swift" or "dart" or "rust" or "vb" or "fsharp" or "cpp";
        // containers by indentation: (indent, name) — a method lives in the nearest less-indented type
        var containers = new List<(int Indent, string Name)>();
        var lineNo = 0;
        var inBlockComment = false;
        foreach (var raw in Lines(text))
        {
            lineNo++;
            var line = raw.Length > MaxLineLength ? raw[..MaxLineLength] : raw;
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (SkipComment(trimmed, languageId, ref inBlockComment)) continue;

            var indent = IndentOf(line);
            if (indentLanguage)
                while (containers.Count > 0 && containers[^1].Indent >= indent && trimmed[0] is not ('{' or '}' or '(' or ')' or ']' or '[' or '#' or '@'))
                    containers.RemoveAt(containers.Count - 1);

            foreach (var rule in rules)
            {
                Match m;
                try { m = rule.Re.Match(line); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success) continue;
                var g = m.Groups["name"];
                var name = g.Value.Trim();
                if (name.Length == 0 || NotNames.Contains(name)) continue;
                var kind = rule.Kind;
                if (rule.KindOf is not null && m.Groups["kind"] is { Success: true } kg && rule.KindOf(kg.Value) is { } k) kind = k;

                var container = containers.Count > 0 ? containers[^1].Name : null;
                if (kind == SymbolKind.Function && container is not null && indent > 0 && indentLanguage) kind = SymbolKind.Method;
                if (kind == SymbolKind.Method && container is null && languageId is not ("go" or "csharp" or "java")) kind = SymbolKind.Function;
                result.Add(new Symbol(name, kind, lineNo, g.Index + 1 + (g.Value.Length - g.Value.TrimStart().Length), name.Length, container));
                if (indentLanguage && kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Namespace)
                    containers.Add((indent, name));
                break;
            }
            if (result.Count >= MaxSymbols) break;
        }
        return result;
    }

    private static bool SkipComment(string trimmed, string languageId, ref bool inBlock)
    {
        var cStyle = languageId is "c" or "cpp" or "csharp" or "java" or "kotlin" or "swift" or "dart" or "javascript" or "typescript" or "go" or "rust" or "php" or "css" or "scss";
        if (!cStyle) return trimmed.StartsWith('#') && languageId is "python" or "ruby" or "shell" or "powershell" or "yaml" or "toml" or "makefile" or "cmake" or "r";
        if (inBlock)
        {
            if (trimmed.Contains("*/")) inBlock = false;
            return true;
        }
        if (trimmed.StartsWith("//") || trimmed.StartsWith('*')) return true;
        if (trimmed.StartsWith("/*"))
        {
            inBlock = !trimmed.Contains("*/");
            return true;
        }
        return false;
    }

    private static List<Symbol> ScanMarkdown(string text)
    {
        var result = new List<Symbol>();
        var lineNo = 0;
        string? fence = null;
        string? previous = null;
        foreach (var line in Lines(text))
        {
            lineNo++;
            var fm = MdFence.Match(line);
            if (fm.Success)
            {
                if (fence is null) fence = fm.Groups[1].Value;
                else if (fm.Groups[1].Value == fence) fence = null;
                previous = null;
                continue;
            }
            if (fence is not null) continue;

            var m = MdAtx.Match(line);
            if (m.Success && m.Groups["name"].Value.Length > 0)
            {
                var name = CleanInline(m.Groups["name"].Value);
                result.Add(new Symbol(name, SymbolKind.Heading, lineNo, m.Groups["name"].Index + 1, m.Groups["name"].Length, Level: m.Groups["hash"].Length));
                previous = null;
                continue;
            }
            // setext: a text line underlined by === (h1) or --- (h2)
            if (previous is { Length: > 0 } && !string.IsNullOrWhiteSpace(previous) && MdSetext.Match(line) is { Success: true } s
                && !previous.TrimStart().StartsWith('-') && !previous.TrimStart().StartsWith('>') && !previous.TrimStart().StartsWith('|'))
            {
                var name = CleanInline(previous.Trim());
                result.Add(new Symbol(name, SymbolKind.Heading, lineNo - 1, IndentOf(previous) + 1, previous.Trim().Length, Level: s.Groups[1].Value[0] == '=' ? 1 : 2));
                previous = null;
                continue;
            }
            previous = line;
            if (result.Count >= MaxSymbols) break;
        }
        // container = the parent heading, for context in the list
        var stack = new List<Symbol>();
        for (var i = 0; i < result.Count; i++)
        {
            var h = result[i];
            while (stack.Count > 0 && stack[^1].Level >= h.Level) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0) result[i] = h with { Container = stack[^1].Name };
            stack.Add(h);
        }
        return result;
    }

    private static List<Symbol> ScanLatex(string text)
    {
        var result = new List<Symbol>();
        var lineNo = 0;
        foreach (var line in Lines(text))
        {
            lineNo++;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('%')) continue;
            foreach (Match m in Latex.Matches(line))
            {
                var level = m.Groups["kind"].Value switch
                {
                    "part" => 1,
                    "chapter" => 1,
                    "section" => 2,
                    "subsection" => 3,
                    "subsubsection" => 4,
                    _ => 5,
                };
                var g = m.Groups["name"];
                result.Add(new Symbol(g.Value.Trim(), SymbolKind.Heading, lineNo, g.Index + 1, g.Length, Level: level));
            }
        }
        return result;
    }

    /// <summary>Strips the most common inline Markdown (emphasis, code ticks, links) from a heading.</summary>
    private static string CleanInline(string s)
    {
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        return s.Trim();
    }

    private static int IndentOf(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start <= text.Length) yield return text[start..];
    }
}
