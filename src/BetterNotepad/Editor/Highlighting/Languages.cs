using System.IO;
using System.Text.RegularExpressions;

namespace BetterNotepad.Editor.Highlighting;

public enum FoldingKind
{
    None,
    Braces,
    Xml,
    Indent,
    Markdown,
}

/// <summary>
/// A language the editor knows. <see cref="Definition"/> names an embedded .xshd in
/// Editor/Highlighting/Definitions (our own, theme-token based) or "builtin:Name" for an AvalonEdit one.
/// </summary>
public sealed record LanguageInfo(
    string Id,
    string Name,
    string[] Extensions,
    string? Definition,
    string? LineComment = null,
    string? BlockStart = null,
    string? BlockEnd = null,
    FoldingKind Folding = FoldingKind.None,
    bool IsCode = true,
    string[]? FileNames = null)
{
    /// <summary>Suggested extension for "Save as" of an untitled buffer.</summary>
    public string DefaultExtension => Extensions.Length > 0 ? Extensions[0] : ".txt";
}

/// <summary>Language registry: by file name, by content (untitled buffers), ASM dialects.</summary>
public static partial class Languages
{
    public const string PlainText = "text";

    private static readonly List<LanguageInfo> All = [];
    private static readonly Dictionary<string, LanguageInfo> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LanguageInfo> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LanguageInfo> ByFileName = new(StringComparer.OrdinalIgnoreCase);

    static Languages()
    {
        Add(new("text", "Plain text", [".txt", ".text"], null, IsCode: false));
        Add(new("log", "Log", [".log", ".out"], "Log", IsCode: false));
        Add(new("markdown", "Markdown", [".md", ".markdown", ".mdown", ".mkd", ".mkdn"], "Markdown", BlockStart: "<!--", BlockEnd: "-->", Folding: FoldingKind.Markdown, IsCode: false));
        Add(new("c", "C", [".c", ".h"], "C", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("cpp", "C++", [".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".ino", ".inl", ".ipp", ".tpp"], "Cpp", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("csharp", "C#", [".cs", ".csx"], "CSharp", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("java", "Java", [".java"], "Java", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("kotlin", "Kotlin", [".kt", ".kts"], "Kotlin", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("javascript", "JavaScript", [".js", ".mjs", ".cjs", ".jsx"], "JavaScript", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("typescript", "TypeScript", [".ts", ".mts", ".cts", ".tsx"], "TypeScript", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("python", "Python", [".py", ".pyw", ".pyi"], "Python", "#", "\"\"\"", "\"\"\"", FoldingKind.Indent));
        Add(new("html", "HTML", [".html", ".htm", ".xhtml"], "Html", null, "<!--", "-->", FoldingKind.Xml));
        Add(new("css", "CSS", [".css"], "Css", null, "/*", "*/", FoldingKind.Braces));
        Add(new("scss", "SCSS / Less", [".scss", ".sass", ".less"], "Scss", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("php", "PHP", [".php", ".phtml", ".php3", ".php4", ".php5", ".phps"], "Php", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("xml", "XML", [".xml", ".xaml", ".xsd", ".xsl", ".xslt", ".config", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".resx", ".nuspec", ".plist", ".svg", ".wsdl", ".xshd", ".manifest", ".storyboard", ".kml", ".gpx"], "Xml", null, "<!--", "-->", FoldingKind.Xml));
        Add(new("json", "JSON", [".json", ".jsonc", ".json5", ".geojson", ".webmanifest", ".har"], "Json", "//", "/*", "*/", FoldingKind.Braces, FileNames: [".babelrc", ".eslintrc", ".prettierrc"]));
        Add(new("yaml", "YAML", [".yml", ".yaml"], "Yaml", "#", Folding: FoldingKind.Indent));
        Add(new("toml", "TOML", [".toml"], "Toml", "#"));
        Add(new("ini", "INI / Config", [".ini", ".cfg", ".conf", ".properties", ".inf", ".reg", ".editorconfig", ".gitconfig", ".env"], "Ini", ";", FileNames: [".gitignore", ".gitattributes", ".npmrc", ".dockerignore"]));
        Add(new("sql", "SQL", [".sql", ".ddl", ".dml", ".psql", ".mysql"], "Sql", "--", "/*", "*/"));
        Add(new("shell", "Shell (bash)", [".sh", ".bash", ".zsh", ".ksh", ".fish"], "Shell", "#", Folding: FoldingKind.Braces, FileNames: [".bashrc", ".zshrc", ".profile", ".bash_profile"]));
        Add(new("batch", "Batch", [".bat", ".cmd"], "Batch", "REM "));
        Add(new("powershell", "PowerShell", [".ps1", ".psm1", ".psd1"], "PowerShell", "#", "<#", "#>", FoldingKind.Braces));
        Add(new("go", "Go", [".go"], "Go", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("rust", "Rust", [".rs"], "Rust", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("lua", "Lua", [".lua"], "Lua", "--", "--[[", "]]"));
        Add(new("ruby", "Ruby", [".rb", ".rake", ".gemspec"], "Ruby", "#", FileNames: ["Gemfile", "Rakefile"]));
        Add(new("swift", "Swift", [".swift"], "Swift", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("dart", "Dart", [".dart"], "Dart", "//", "/*", "*/", FoldingKind.Braces));
        Add(new("r", "R", [".r", ".rmd"], "R", "#"));
        Add(new("vb", "Visual Basic", [".vb", ".vbs", ".bas", ".vba"], "VB", "'"));
        Add(new("fsharp", "F#", [".fs", ".fsi", ".fsx"], "FSharp", "//", "(*", "*)"));
        Add(new("dockerfile", "Dockerfile", [".dockerfile"], "Dockerfile", "#", FileNames: ["Dockerfile", "Containerfile"]));
        Add(new("makefile", "Makefile", [".mk", ".mak"], "Makefile", "#", FileNames: ["Makefile", "GNUmakefile", "makefile"]));
        Add(new("cmake", "CMake", [".cmake"], "CMake", "#", FileNames: ["CMakeLists.txt"]));
        Add(new("diff", "Diff / Patch", [".diff", ".patch"], "Diff", IsCode: false));
        Add(new("latex", "LaTeX", [".tex", ".sty", ".cls", ".bib"], "Latex", "%"));
        Add(new("asm-x86", "Assembly x86 / x64", [".asm", ".s", ".S", ".nasm", ".inc"], "AsmX86", ";"));
        Add(new("asm-arm", "Assembly ARM", [".arm"], "AsmArm", "@"));
        Add(new("asm-avr", "Assembly AVR", [".avr"], "AsmAvr", ";"));
        Add(new("csv", "CSV / TSV", [".csv", ".tsv", ".tab", ".psv"], "Csv", IsCode: false));
    }

    public static void Add(LanguageInfo lang)
    {
        if (ById.TryGetValue(lang.Id, out var old)) All.Remove(old);
        All.Add(lang);
        ById[lang.Id] = lang;
        foreach (var e in lang.Extensions) ByExtension[e] = lang;
        if (lang.FileNames is not null)
            foreach (var f in lang.FileNames) ByFileName[f] = lang;
    }

    public static IReadOnlyList<LanguageInfo> List => All;

    public static LanguageInfo Get(string? id) => id is not null && ById.TryGetValue(id, out var l) ? l : ById[PlainText];

    public static string ForFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return PlainText;
        var file = Path.GetFileName(name);
        if (ByFileName.TryGetValue(file, out var byName)) return byName.Id;
        var ext = Path.GetExtension(file);
        if (ext == ".svg") return "xml";
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var lang) ? lang.Id : PlainText;
    }

    /// <summary>
    /// Guesses the language of pasted/typed text in an untitled buffer. Conservative: returns
    /// <see cref="PlainText"/> unless the evidence is clear.
    /// </summary>
    public static string Detect(string text)
    {
        var t = text.Length > 8000 ? text[..8000] : text;
        var s = t.TrimStart();
        if (s.Length == 0) return PlainText;

        if (s.StartsWith("#!"))
        {
            var first = s.Split('\n', 2)[0];
            if (first.Contains("python")) return "python";
            if (first.Contains("node")) return "javascript";
            if (first.Contains("pwsh") || first.Contains("powershell")) return "powershell";
            if (first.Contains("ruby")) return "ruby";
            return "shell";
        }
        if (s.StartsWith("<?xml") ) return "xml";
        if (Regex.IsMatch(s, @"^<!doctype\s+html|^<html[\s>]", RegexOptions.IgnoreCase)) return "html";
        if (s.StartsWith("<?php")) return "php";
        if ((s[0] == '{' || s[0] == '[') && LooksLikeJson(s)) return "json";
        if (s.StartsWith('<') && Regex.IsMatch(s, @"^<([A-Za-z][\w:.-]*)[^>]*>[\s\S]*</\1>\s*$")) return "xml";
        if (Regex.IsMatch(t, @"^diff --git |^--- .*\r?\n\+\+\+ ", RegexOptions.Multiline)) return "diff";

        var scores = new Dictionary<string, int>();
        void Score(string id, string pattern, int weight = 1, RegexOptions opt = RegexOptions.Multiline)
        {
            var n = Regex.Matches(t, pattern, opt).Count;
            if (n > 0) scores[id] = scores.GetValueOrDefault(id) + Math.Min(n, 5) * weight;
        }

        Score("csharp", @"^\s*using\s+System(\.[\w.]+)?;", 4);
        Score("csharp", @"\bnamespace\s+[\w.]+\s*[{;]|\bpublic\s+(sealed\s+|static\s+|partial\s+)*class\b|\bvar\s+\w+\s*=|\basync\s+Task\b", 2);
        Score("java", @"^\s*package\s+[\w.]+;|^\s*import\s+java\.|System\.out\.print|public\s+static\s+void\s+main\s*\(String", 4);
        Score("cpp", @"^\s*#include\s*[<""]|std::|\bcout\s*<<|\btemplate\s*<", 3);
        Score("c", @"^\s*#include\s*<(stdio|stdlib|string)\.h>|\bprintf\s*\(|\bmalloc\s*\(", 2);
        Score("python", @"^\s*def\s+\w+\s*\(.*\)\s*(->\s*[\w\[\], .]+)?:\s*$|^\s*(from\s+[\w.]+\s+)?import\s+\w+|^\s*if\s+__name__\s*==|\bself\.\w+|\bprint\(", 3);
        Score("javascript", @"\b(const|let)\s+\w+\s*=|=>\s*[{(]|\bfunction\s+\w+\s*\(|console\.log\(|\brequire\(|\bexport\s+(default\s+)?(function|const|class)", 2);
        Score("typescript", @":\s*(string|number|boolean|void|any|unknown)\b|\binterface\s+\w+\s*\{|\btype\s+\w+\s*=", 2);
        Score("go", @"^\s*package\s+main\b|\bfunc\s+\w+\s*\(|:=|\bfmt\.Print", 3);
        Score("rust", @"\bfn\s+\w+\s*\(|\blet\s+mut\b|\bimpl\b|println!\(|\bpub\s+fn\b", 3);
        Score("php", @"\$\w+\s*=|\becho\s+|->\w+\(", 1);
        Score("sql", @"^\s*(SELECT|INSERT\s+INTO|UPDATE|DELETE\s+FROM|CREATE\s+(TABLE|VIEW|INDEX)|ALTER\s+TABLE|DROP\s+TABLE)\b", 3, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Score("sql", @"\bFROM\s+\w+|\bWHERE\b|\bJOIN\b", 1, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Score("powershell", @"\$\w+\s*=|\b(Get|Set|New|Remove|Write|Invoke)-[A-Z]\w+|\bparam\s*\(", 2);
        Score("shell", @"^\s*(echo|export|cd|sudo|apt|chmod)\s|\$\{?\w+\}?|\bfi\b|\bdone\b|\bthen\b", 1);
        Score("batch", @"^\s*@?echo\s+off|^\s*(set|goto|rem)\s|%\w+%", 2, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Score("css", @"^[.#]?[\w-]+(\s*[,>+~]\s*[.#]?[\w-]+)*\s*\{[^}]*:[^}]*;", 3);
        Score("yaml", @"^\s*[\w-]+:\s+\S|^\s*-\s+[\w-]+:\s", 1);
        Score("ini", @"^\s*\[[\w .-]+\]\s*$", 2);
        Score("markdown", @"^#{1,6}\s+\S|^\s*[-*]\s+\[[ xX]\]\s|^```|\[[^\]]+\]\([^)]+\)|^\s*>\s+\S", 2);
        Score("log", @"^\s*\[?\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}.*\b(INFO|WARN(ING)?|ERROR|DEBUG|TRACE|FATAL)\b", 3);
        Score("asm-x86", @"^\s*(mov|push|pop|call|ret|jmp|jne|je|cmp|lea|xor|int)\s+\w|\b(e?[abcd]x|r[abcd]x|[er]sp|[er]bp|[er]si|[er]di)\b|^\s*section\s+\.(text|data|bss)", 2, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        // ARM / AVR evidence also triggers the asm branch; the dialect itself is decided by DetectAsmDialect.
        Score("asm-arm", @"^\s*(?:(?:ldr|str)[bhsd]?|ldm\w*|stm\w*|ldp|stp|blx?|bx|movw|movt|adrp?|cbn?z|svc|b\.[a-z]{2})\s+[\w{\[=#]|^\s*\.(?:thumb|arm|syntax\s+unified|cpu|fpu|thumb_func|arch)\b|^\s*[a-z]{2,6}\s+\w+\s*,\s*#-?(?:0x)?[0-9a-f]+\b|^\s*[a-z]{2,6}\s+[xw](?:[12]?\d|30|zr|sp)\s*,", 2, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Score("asm-avr", @"^\s*(?:ldi|lds|sts|rjmp|rcall|brne|breq|brsh|brlo|sbi|cbi|sbis|sbic|sbrc|sbrs|lpm|out|in)\s+[\w.(\-+]|^\s*(?:reti|sei|cli)\b|^\s*\.(?:include|equ|def|org|cseg|dseg|eseg|device)\b", 2, RegexOptions.Multiline | RegexOptions.IgnoreCase);

        if (scores.Count == 0) return PlainText;
        var best = scores.MaxBy(kv => kv.Value);
        if (best.Value < 4) return PlainText;
        if (best.Key == "c" && scores.GetValueOrDefault("cpp") >= best.Value) return "cpp";
        if (best.Key == "javascript" && scores.GetValueOrDefault("typescript") >= 4) return "typescript";
        if (best.Key.StartsWith("asm-", StringComparison.Ordinal)) return DetectAsmDialect(t);
        return best.Key;
    }

    /// <summary>x86 (NASM/MASM/GAS), ARM or AVR, by the registers and mnemonics used.</summary>
    public static string DetectAsmDialect(string text)
    {
        var t = text.Length > 20000 ? text[..20000] : text;
        int x86 = Regex.Matches(t, @"\b(e?[abcd]x|r[abcd]x|r(8|9|1[0-5])[dwb]?|[er]?[sb]p|[er]?[sd]i|[abcd][hl]|xmm\d+|ymm\d+)\b|%(e|r)[a-z]{2}\b|\b(mov[sz]?x?|lea|push|pop|int|syscall|cmov\w+|rep\w*)\b", RegexOptions.IgnoreCase).Count;
        int arm = Regex.Matches(t, @"\b(r1[0-5]|r\d|x\d{1,2}|w\d{1,2}|sp|lr|pc|fp)\b|\b(ldr|str|ldm|stm|bl|bx|blx|movw|movt|adr|cbz|cbnz|svc)\b|#\d+|\.thumb\b|\.arm\b", RegexOptions.IgnoreCase).Count;
        int avr = Regex.Matches(t, @"\b(r([12]?\d|3[01]))\b|\b(ldi|lds|sts|rjmp|rcall|brne|breq|sbi|cbi|sbis|sbic|sbrc|sbrs|out|in|lpm|reti)\b|\.(def|equ|org|include)\s|\bPORT[A-F]\b|\bDDR[A-F]\b", RegexOptions.IgnoreCase).Count;
        if (avr > x86 && avr >= arm) return "asm-avr";
        if (arm > x86 && arm > avr) return "asm-arm";
        return "asm-x86";
    }

    private static bool LooksLikeJson(string s)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(s.Length > 200_000 ? s[..200_000] : s,
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            return true;
        }
        catch
        {
            return s.Length > 200_000 || Regex.IsMatch(s, @"^\s*[\[{]\s*""[^""]+""\s*:");
        }
    }
}
