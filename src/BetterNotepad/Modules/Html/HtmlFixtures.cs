using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterNotepad.Modules.Html;

/// <summary>
/// A small web project for the self-test and the snapshots (written into the app's test data folder): a page with relative CSS,
/// JS and a picture, CDN links that the preview blocks, a web socket, an external and a local link, an SVG and an empty page.
/// </summary>
internal static class HtmlFixtures
{
    public sealed record Site(string Folder, string Index, string Other, string Svg, string Empty, string Css);

    public static Site Write(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "css"));
        Directory.CreateDirectory(Path.Combine(folder, "js"));
        Directory.CreateDirectory(Path.Combine(folder, "img"));
        var utf8 = new UTF8Encoding(false);
        var css = Path.Combine(folder, "css", "site.css");
        File.WriteAllText(css, Css, utf8);
        File.WriteAllText(Path.Combine(folder, "js", "app.js"), Js, utf8);
        MakeLogo(Path.Combine(folder, "img", "logo.png"));
        var index = Path.Combine(folder, "index.html");
        File.WriteAllText(index, IndexHtml(), utf8);
        var other = Path.Combine(folder, "other.html");
        File.WriteAllText(other, "<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"css/site.css\"></head>\n<body>\n<main><h1>Other page</h1>\n<p>Opened from a link in the preview.</p></main>\n</body>\n</html>\n", utf8);
        var svg = Path.Combine(folder, "badge.svg");
        File.WriteAllText(svg, SvgText, utf8);
        var empty = Path.Combine(folder, "empty.html");
        File.WriteAllText(empty, "", utf8);
        return new Site(folder, index, other, svg, empty, css);
    }

    public static int LineOf(string text, string needle)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? -1 : text.AsSpan(0, at).Count('\n') + 1;
    }

    private static string IndexHtml()
    {
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Field notes</title>
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter:wght@400;600&amp;display=swap">
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/water.css@2/out/water.css">
<link rel="stylesheet" href="css/site.css">
<script src="https://cdn.jsdelivr.net/npm/canvas-confetti@1.9.3/dist/confetti.browser.min.js"></script>
<script src="js/app.js"></script>
</head>
<body>
<header class="hero">
  <img src="img/logo.png" alt="" class="logo" width="52" height="52">
  <div>
    <h1>Field notes</h1>
    <p class="lead">Styles from <code>css/site.css</code>, a script from <code>js/app.js</code> and a picture from <code>img/logo.png</code> — all next to this file.</p>
    <p class="links"><span id="js-badge" class="badge off">script not run</span>
      <a class="ext" href="https://example.com/">example.com</a> · <a class="loc" href="other.html">other.html</a></p>
  </div>
</header>
<main>
  <img id="remote" class="banner" src="https://images.example.com/banner.png" alt="" width="1" height="1">

""");
        string[] topics = ["Measurements", "Calibration", "Observations", "Weather", "Equipment", "Samples", "Results", "Next steps"];
        for (var i = 1; i <= 24; i++)
        {
            var topic = topics[(i - 1) % topics.Length];
            sb.Append(CultureInfo.InvariantCulture, $"""
  <section id="s{i}">
    <h2>{i}. {topic}</h2>
    <p>Entry {i}: the preview maps every element to its source line, so the editor and the page scroll together. Double-click a block to jump to its line.</p>
    <ul><li>Reading A{i}: <strong>{12.5 + i * 0.75:0.00}</strong> mm</li><li>Reading B{i}: <strong>{3.1 + i * 0.2:0.0}</strong> °C</li></ul>
  </section>

""");
        }
        sb.Append("</main>\n<footer>Saved next to <code>index.html</code> · BetterNotepad preview</footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void MakeLogo(string path)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var g = new LinearGradientBrush(Color.FromRgb(0x8B, 0x5C, 0xFF), Color.FromRgb(0x22, 0xD3, 0xEE), 45);
            dc.DrawRoundedRectangle(g, null, new Rect(4, 4, 88, 88), 22, 22);
            var pen = new Pen(Brushes.White, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(28, 36), new Point(68, 36));
            dc.DrawLine(pen, new Point(28, 50), new Point(60, 50));
            dc.DrawLine(pen, new Point(28, 64), new Point(50, 64));
        }
        var rtb = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private const string Js = """
document.addEventListener('DOMContentLoaded', function () {
  var b = document.getElementById('js-badge');
  b.textContent = 'script ran';
  b.className = 'badge on';
});
// a live-reload style socket: blocked like every other request to the internet
try { new WebSocket('wss://live.example.com/socket'); } catch (e) { }
""";

    private const string Css = """
:root { --ink: #1d2233; --dim: #5b6276; --accent: #5b4bf0; --line: #e6e8f0; }
* { box-sizing: border-box; }
body { margin: 0; font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", sans-serif; color: var(--ink); background: #fbfbfe; }
.hero { display: flex; gap: 18px; align-items: center; padding: 30px 40px; background: linear-gradient(135deg, #efeaff, #e3f6fb); border-bottom: 1px solid var(--line); }
.logo { width: 52px; height: 52px; border-radius: 13px; box-shadow: 0 6px 18px rgba(91, 75, 240, .25); }
h1 { margin: 0; font-size: 28px; letter-spacing: -0.02em; }
.lead { margin: 4px 0 6px; color: var(--dim); }
.links { margin: 0; }
code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 12.5px; background: #fff; padding: 1px 6px; border-radius: 5px; border: 1px solid var(--line); }
.badge { display: inline-block; padding: 1px 10px; margin-right: 6px; border-radius: 999px; font-size: 12px; font-weight: 600; }
.badge.off { background: #fde2e2; color: #a12222; }
.badge.on { background: #d9f7ea; color: #0b7a4b; }
a { color: var(--accent); }
main { padding: 6px 40px 30px; max-width: 860px; }
section { border-bottom: 1px solid #eceef5; padding: 4px 0 8px; }
h2 { font-size: 18px; margin: 14px 0 4px; }
ul { margin: 6px 0; padding-left: 22px; }
.banner { display: block; }
footer { padding: 18px 40px 28px; color: var(--dim); font-size: 13px; }
""";

    public const string SvgText = """
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="240" height="160" viewBox="0 0 240 160">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#8B5CFF"/>
      <stop offset="1" stop-color="#22D3EE"/>
    </linearGradient>
  </defs>
  <rect x="16" y="16" width="208" height="128" rx="26" fill="url(#g)" opacity="0.92"/>
  <circle cx="64" cy="80" r="30" fill="#ffffff" fill-opacity="0.22"/>
  <path id="star" d="M64 58 l6.5 13.2 14.5 2.1 -10.5 10.2 2.5 14.5 -13-6.8 -13 6.8 2.5-14.5 -10.5-10.2 14.5-2.1z" fill="#fff"/>
  <text x="108" y="74" font-family="Segoe UI" font-size="16" font-weight="600" fill="#fff">BetterNotepad</text>
  <text x="108" y="98" font-family="Segoe UI" font-size="10.5" fill="#ffffff" fill-opacity="0.8">SVG preview</text>
</svg>
""";
}
