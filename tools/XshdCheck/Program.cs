using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

// XshdCheck — validates BetterNotepad's own syntax definitions (Editor/Highlighting/Definitions/*.xshd).
//   dotnet run --project tools/XshdCheck                  check everything
//   dotnet run --project tools/XshdCheck -- Python Html   check only these definitions
//   dotnet run --project tools/XshdCheck -- --dump Python print the token stream of the sample
// Samples/*.txt format:  "##### Name" starts a sample, "##E needle [~N] => Token" asserts the token of the N-th
// occurrence of needle (None = unhighlighted), every other line is sample code.

// Engine facts the definitions rely on (found by experiment, AvalonEdit 6.3):
//  * regexes ignore pattern whitespace (write [ ] or \s for a space, \# for #); a rule may not match the empty string;
//  * ties at the same index: span begin > end of the current span > rules in file order; a rule cannot extend past the
//    next span begin / the end of the enclosing span, and its lookahead cannot see that character either;
//  * a rule is first matched against the whole line (lookbehind and ^ work), but after the cached match is used up it is
//    re-matched on the rest of the line only, where ^ and lookbehind see just that remainder, and the same holds for the
//    first match inside a freshly entered span (lookbehind cannot see the span's own begin);
//  * end regexes may not use back-references to the begin; escaped quotes are therefore spans (begin \\. , end (?=.)).

const int TimeoutMs = 8000;

string[] tokenNames =
[
    "Keyword", "Control", "Type", "Function", "Variable", "Property", "String", "Number", "Constant", "Comment", "DocComment",
    "Preprocessor", "Operator", "Punctuation", "Attribute", "Tag", "AttrName", "AttrValue", "Entity", "Escape", "Regex",
    "Register", "Instruction", "Directive", "Label", "Heading", "Emphasis", "Strong", "Link", "Code", "Quote", "ListMarker",
    "Selector", "CssProperty", "Key", "Section", "Invalid", "LogError", "LogWarning", "LogInfo", "LogDebug", "LogTrace",
    "LogTimestamp", "DiffAdded", "DiffRemoved", "DiffHeader",
];
var tokenSet = new HashSet<string>(tokenNames, StringComparer.Ordinal);

var root = AppContext.BaseDirectory;
while (root is not null && !File.Exists(Path.Combine(root, "BetterNotepad.sln"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
if (root is null) return Fail("BetterNotepad.sln not found above " + AppContext.BaseDirectory);

var defsDir = Path.Combine(root, "src", "BetterNotepad", "Editor", "Highlighting", "Definitions");
var samplesDir = Path.Combine(root, "tools", "XshdCheck", "Samples");
var languagesPath = Path.Combine(root, "src", "BetterNotepad", "Editor", "Highlighting", "Languages.cs");

var argList = args.ToList();
var dump = argList.Remove("--dump");
var filter = new HashSet<string>(argList, StringComparer.OrdinalIgnoreCase);

var samples = ParseSamples(string.Join(Environment.NewLine,
    Directory.GetFiles(samplesDir, "*.txt").OrderBy(f => f, StringComparer.Ordinal).Select(f => File.ReadAllText(f, Encoding.UTF8))));
var failures = 0;
var warnings = 0;

// ── Languages.cs ↔ files ──
var files = Directory.GetFiles(defsDir, "*.xshd").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
if (File.Exists(languagesPath) && filter.Count == 0)
{
    var used = Regex.Matches(File.ReadAllText(languagesPath), @"Add\(new\(""[^""]+"",\s*""[^""]*"",\s*\[[^\]]*\],\s*""(\w+)""")
        .Select(m => m.Groups[1].Value).Distinct().ToList();
    foreach (var u in used.Where(u => !files.Contains(u, StringComparer.Ordinal)))
        failures += Report($"Languages.cs uses Definition '{u}' but {u}.xshd does not exist");
    foreach (var f in files.Where(f => !used.Contains(f, StringComparer.Ordinal)))
        warnings += Warn($"{f}.xshd is not referenced from Languages.cs");
    Console.WriteLine($"Languages.cs: {used.Count} definitions referenced, {files.Count} files");
}

var resolver = new DirResolver(defsDir);
foreach (var name in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
{
    if (filter.Count > 0 && !filter.Contains(name)) continue;
    failures += CheckOne(name);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"ALL PASSED ({warnings} warnings)" : $"FAILED: {failures} problem(s), {warnings} warning(s)");
return failures == 0 ? 0 : 1;

// ───────────────────────────────────────────────────────────────────────────

int CheckOne(string name)
{
    var path = Path.Combine(defsDir, name + ".xshd");
    var problems = new List<string>();
    XshdSyntaxDefinition xshd;
    try
    {
        using var reader = XmlReader.Create(path);
        xshd = HighlightingLoader.LoadXshd(reader);
    }
    catch (Exception ex)
    {
        return Report($"{name}: load failed: {ex.Message}");
    }

    if (xshd.Name != name) problems.Add($"SyntaxDefinition name '{xshd.Name}' != file stem '{name}'");
    var colors = xshd.Elements.OfType<XshdColor>().ToList();
    foreach (var c in colors)
    {
        if (c.Name is null || !tokenSet.Contains(c.Name)) problems.Add($"color '{c.Name}' is not a SyntaxToken name");
        if (c.Foreground is not null || c.Background is not null || c.FontWeight is not null || c.FontStyle is not null || c.Underline is not null || c.FontFamily is not null || c.FontSize is not null)
            problems.Add($"color '{c.Name}' carries foreground/weight/style");
    }

    IHighlightingDefinition? def = null;
    try { def = resolver.GetDefinition(name); }
    catch (Exception ex) { problems.Add("build failed: " + ex.Message); }
    if (def is null && problems.Count == 0) problems.Add("build returned null");

    string info = "";
    if (def is not null)
    {
        if (!samples.TryGetValue(name, out var sample)) problems.Add("no sample in Samples/*.txt");
        else info = RunSample(name, def, sample, problems);
    }

    if (problems.Count == 0)
    {
        Console.WriteLine($"OK    {name,-11} colors={colors.Count,-2} {info}");
        return 0;
    }
    Console.WriteLine($"FAIL  {name}");
    foreach (var p in problems) Console.WriteLine("      - " + p);
    return 1;
}

string RunSample(string name, IHighlightingDefinition def, Sample sample, List<string> problems)
{
    var text = sample.Code;
    string?[] tok = new string?[text.Length];
        var sw = Stopwatch.StartNew();
    Exception? error = null;
    var t = new Thread(() =>
    {
        try
        {
            var doc = new TextDocument(text);
            var hl = new DocumentHighlighter(doc, def);
            for (int i = 1; i <= doc.LineCount; i++)
            {
                var line = hl.HighlightLine(i);
                foreach (var s in line.Sections.Where(s => s.Length > 0 && s.Color?.Name is not null).OrderByDescending(s => s.Length))
                    for (int k = s.Offset; k < s.Offset + s.Length && k < tok.Length; k++) tok[k] = s.Color!.Name;
            }
            hl.Dispose();
        }
        catch (Exception ex) { error = ex; }
    }) { IsBackground = true };
    t.Start();
    if (!t.Join(TimeoutMs)) { problems.Add($"highlighting did not finish within {TimeoutMs} ms (endless loop / catastrophic regex?)"); return ""; }
    if (error is not null) { problems.Add("highlighting threw: " + error.Message); return ""; }
    var ms = sw.ElapsedMilliseconds;

    var distinct = tok.Where(x => x is not null).Distinct().Count();
    if (distinct < 3) problems.Add($"sample produced only {distinct} distinct token kinds");

    foreach (var e in sample.Expect)
    {
        int idx = -1;
        for (int n = 0; n < e.Nth; n++)
        {
            idx = text.IndexOf(e.Needle, idx + 1, StringComparison.Ordinal);
            if (idx < 0) break;
        }
        if (idx < 0) { problems.Add($"expectation needle not in sample: '{e.Needle}' ~{e.Nth}"); continue; }
        var bad = new List<string>();
        for (int k = idx; k < idx + e.Needle.Length; k++)
        {
            if (char.IsWhiteSpace(text[k])) continue;
            var got = tok[k] ?? "None";
            if (got != e.Token) bad.Add($"'{text[k]}'={got}");
        }
        if (bad.Count > 0) problems.Add($"'{e.Needle}' expected {e.Token} but {string.Join(" ", bad.Take(4))}");
    }

    // stress: many lines + one very long line must stay fast.
    var lines = text.Replace("\r", "").Split('\n');
    var big = string.Join("\n", Enumerable.Repeat(string.Join("\n", lines), Math.Max(1, 1500 / Math.Max(1, lines.Length))));
    var longLine = new StringBuilder();
    while (longLine.Length < 20000) longLine.Append(string.Join(" ", lines)).Append(' ');
    long stressMs = 0;
    foreach (var stressText in new[] { big, longLine.ToString() })
    {
        int lineCount = 0;
        var sw2 = Stopwatch.StartNew();
        var th = new Thread(() =>
        {
            try
            {
                var sd = new TextDocument(stressText);
                lineCount = sd.LineCount;
                var hl = new DocumentHighlighter(sd, def);
                for (int i = 1; i <= sd.LineCount; i++) hl.HighlightLine(i);
                hl.Dispose();
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        th.Start();
        if (!th.Join(TimeoutMs * 2)) { problems.Add("stress highlighting timed out"); break; }
        if (error is not null) { problems.Add("stress highlighting threw: " + error.Message); break; }
        stressMs += sw2.ElapsedMilliseconds;
        if (sw2.ElapsedMilliseconds > 2500) problems.Add($"stress too slow: {sw2.ElapsedMilliseconds} ms ({stressText.Length} chars, {lineCount} lines)");
        else if (sw2.ElapsedMilliseconds > 1000) warnings += Warn($"{name}: stress {sw2.ElapsedMilliseconds} ms ({stressText.Length} chars, {lineCount} lines)");
    }

    // fuzz: pathological punctuation soup (one very long line + many short ones) must not hang or blow up.
    string[] soups =
    [
        "\"'`{}()[]<>/*#;:,.-+=!?@$%&|^~\\  abcxyz019_ ABCXYZ",                                   // everything, quotes and comment marks included
        "{}()[]<>:,.-+=!?@$%&|^~  abcxyz019_ ABC_XYZ  if for def let end 1 2.5 0x1F  .  ::  ->",  // code-like, no string/comment starters
    ];
    var rng = new Random(12345);
    long fuzzMs = 0;
    foreach (var (soup, lineLen, lines2) in soups.SelectMany(s => new[] { (s, 12000, 1), (s, 60, 400) }))
    {
        var fb = new StringBuilder();
        for (int l = 0; l < lines2; l++)
        {
            for (int c = 0; c < lineLen; c++) fb.Append(soup[rng.Next(soup.Length)]);
            fb.Append('\n');
        }
        var fuzzText = fb.ToString();
        var sw3 = Stopwatch.StartNew();
        var fth = new Thread(() =>
        {
            try
            {
                var fd = new TextDocument(fuzzText);
                var hl = new DocumentHighlighter(fd, def);
                for (int i = 1; i <= fd.LineCount; i++) hl.HighlightLine(i);
                hl.Dispose();
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        fth.Start();
        if (!fth.Join(TimeoutMs * 2)) { problems.Add($"fuzz ({lineLen} chars x {lines2}) timed out"); break; }
        if (error is not null) { problems.Add("fuzz threw: " + error.Message); break; }
        fuzzMs += sw3.ElapsedMilliseconds;
        if (sw3.ElapsedMilliseconds > 3000) problems.Add($"fuzz too slow: {sw3.ElapsedMilliseconds} ms ({lineLen} chars x {lines2})");
        else if (sw3.ElapsedMilliseconds > 1000) warnings += Warn($"{name}: fuzz {sw3.ElapsedMilliseconds} ms ({lineLen} chars x {lines2})");
    }

    if (dump) DumpTokens(text, tok);
    return $"tokens={distinct,-2} asserts={sample.Expect.Count,-3} sample={ms}ms stress={stressMs}ms fuzz={fuzzMs}ms";
}

void DumpTokens(string text, string?[] tok)
{
    var sb = new StringBuilder();
    int i = 0;
    while (i < text.Length)
    {
        if (text[i] == '\n') { sb.AppendLine(); i++; continue; }
        if (text[i] == '\r') { i++; continue; }
        int j = i;
        while (j < text.Length && text[j] != '\n' && text[j] != '\r' && tok[j] == tok[i]) j++;
        sb.Append(tok[i] is null ? text[i..j] : $"[{tok[i]}:{text[i..j]}]");
        i = j;
    }
    Console.WriteLine(sb);
}

Dictionary<string, Sample> ParseSamples(string content)
{
    var result = new Dictionary<string, Sample>(StringComparer.Ordinal);
    Sample? cur = null;
    var code = new StringBuilder();
    void Flush() { if (cur is not null) { cur.Code = code.ToString().TrimEnd('\n', '\r') + "\n"; } code.Clear(); }
    foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
    {
        if (raw.StartsWith("##### "))
        {
            Flush();
            cur = new Sample();
            result[raw[6..].Trim()] = cur;
        }
        else if (raw.StartsWith("##E ") && cur is not null)
        {
            var m = Regex.Match(raw, @"^##E (.+?)(?: ~(\d+))? => (\w+)\s*$");
            if (!m.Success) throw new InvalidOperationException("bad expectation line: " + raw);
            if (!tokenSet.Contains(m.Groups[3].Value) && m.Groups[3].Value != "None") throw new InvalidOperationException("unknown token in: " + raw);
            cur.Expect.Add(new Expectation(m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1, m.Groups[3].Value));
        }
        else if (cur is not null) code.Append(raw.Replace("<TAB>", "\t")).Append('\n');   // <TAB> = a real tab (Makefile recipes, TSV)
    }
    Flush();
    return result;
}

int Report(string msg) { Console.WriteLine("FAIL  " + msg); return 1; }
int Warn(string msg) { Console.WriteLine("WARN  " + msg); return 1; }
int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

sealed class Sample
{
    public string Code = "";
    public List<Expectation> Expect = [];
}

sealed record Expectation(string Needle, int Nth, string Token);

sealed class DirResolver(string dir) : IHighlightingDefinitionReferenceResolver
{
    private readonly Dictionary<string, IHighlightingDefinition?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IHighlightingDefinition? GetDefinition(string name)
    {
        if (_cache.TryGetValue(name, out var def)) return def;
        var path = Path.Combine(dir, name + ".xshd");
        if (!File.Exists(path)) return null;
        _cache[name] = null;
        using var reader = XmlReader.Create(path);
        var xshd = HighlightingLoader.LoadXshd(reader);
        def = HighlightingLoader.Load(xshd, this);
        _cache[name] = def;
        return def;
    }
}
