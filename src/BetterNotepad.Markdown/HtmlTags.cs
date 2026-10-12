using System.Text.RegularExpressions;

namespace BetterNotepad.Markdown;

/// <summary>One parsed HTML tag (the renderer only understands a small, safe subset).</summary>
internal sealed class HtmlTag
{
    private static readonly Regex AttrRx = new(
        "([a-zA-Z_:][-a-zA-Z0-9_:.]*)(?:\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'=<>`]+)))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name { get; private init; } = "";
    public bool Closing { get; private init; }
    public bool SelfClosing { get; private init; }
    public Dictionary<string, string>? Attrs { get; private init; }

    public string? Attr(string name) => Attrs is not null && Attrs.TryGetValue(name, out var v) ? v : null;

    /// <summary>Parses "&lt;name attr=...&gt;" / "&lt;/name&gt;". Returns null for comments, doctype and malformed text.</summary>
    public static HtmlTag? Parse(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        if (s.Length < 3 || s[0] != '<' || s[^1] != '>') return null;
        var i = 1;
        var closing = false;
        if (s[i] == '/')
        {
            closing = true;
            i++;
        }

        var start = i;
        while (i < s.Length - 1 && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == ':')) i++;
        if (i == start) return null;

        var name = s[start..i].ToString().ToLowerInvariant();
        var inner = s[i..^1];
        var self = !closing && inner.Length > 0 && inner[^1] == '/';
        Dictionary<string, string>? attrs = null;
        if (!closing && inner.Length > 0)
        {
            foreach (Match m in AttrRx.Matches(inner.ToString()))
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                var val = m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Success ? m.Groups[3].Value
                    : m.Groups[4].Success ? m.Groups[4].Value : "";
                (attrs ??= new Dictionary<string, string>())[key] = System.Net.WebUtility.HtmlDecode(val);
            }
        }

        return new HtmlTag { Name = name, Closing = closing, SelfClosing = self, Attrs = attrs };
    }

    /// <summary>Splits an HTML fragment into text and tag pieces (comments dropped).</summary>
    public static IEnumerable<(string? Text, HtmlTag? Tag)> Tokenize(string html)
    {
        var pos = 0;
        while (pos < html.Length)
        {
            var lt = html.IndexOf('<', pos);
            if (lt < 0)
            {
                yield return (html[pos..], null);
                yield break;
            }

            if (lt > pos) yield return (html[pos..lt], null);

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                pos = end < 0 ? html.Length : end + 3;
                continue;
            }

            var gt = html.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                yield return (html[lt..], null);
                yield break;
            }

            var tag = Parse(html.AsSpan(lt, gt - lt + 1));
            if (tag is null) yield return (html[lt..(gt + 1)], null);
            else yield return (null, tag);
            pos = gt + 1;
        }
    }
}
