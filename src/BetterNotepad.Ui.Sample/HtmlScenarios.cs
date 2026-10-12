using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterNotepad.Html;
using static BetterNotepad.Ui.Sample.Program;

namespace BetterNotepad.Ui.Sample;

internal static class HtmlScenarios
{
    public static async Task Run(string only)
    {
        var fixtures = Path.Combine(Out, "fixtures", "site");
        var index = MakeSite(fixtures);
        var opened = new List<Uri>();
        var options = new WebPreviewOptions
        {
            UserDataFolder = Path.Combine(Out, "webview2-data"),
            Language = "en",
            OpenExternal = opened.Add,
        };
        if (Environment.GetEnvironmentVariable("BN_TRACE") is not null) WebPreview.Trace = m => Note("trace " + m);
        var exited = false;
        WebViewRuntime.BrowserProcessExited += _ => exited = true;
        Check(WebViewRuntime.AvailableVersion() is not null, "WebView2 runtime present: " + WebViewRuntime.AvailableVersion());

        // lazy: a preview that was never shown creates nothing
        var hidden = new HtmlPreview(options);
        hidden.Update("<p>x</p>", index);
        Check(!hidden.IsBrowserCreated && WebViewRuntime.ActiveViews == 0, "hidden preview creates no WebView");
        hidden.Dispose();

        UseTheme("aurora");
        if (only is "all" or "html") await HtmlPart(index, options, opened);
        if (only is "all" or "svg") await SvgPart(fixtures, options);

        Check(WebViewRuntime.ActiveViews == 0, "all previews released → no WebView left");
        Check(await WaitFor(() => exited, 20000), "browser process exited after the last release");
    }

    private static async Task HtmlPart(string index, WebPreviewOptions options, List<Uri> opened)
    {
        var text = File.ReadAllText(index);
        var preview = new HtmlPreview(options);
        var lines = new List<int>();
        var scrolls = new List<(int, double)>();
        preview.LineRequested += lines.Add;
        preview.Scrolled += (l, f) => scrolls.Add((l, f));
        var win = Host(preview, 1000, 720);

        var t0 = Environment.TickCount64;
        preview.Update(text, index);
        Check(await Loaded(preview), "first render ready");
        Note($"first render (cold browser) {Environment.TickCount64 - t0} ms");

        Check(await Js(preview, "document.compatMode") == "\"CSS1Compat\"", "doctype kept: standards mode");
        Check(await Js(preview, "document.getElementById('js-badge').textContent") == "\"JS ran\"", "relative JS ran (js/app.js)");
        Check(await Js(preview, "getComputedStyle(document.querySelector('h1')).letterSpacing") != "\"normal\"", "relative CSS applied (css/site.css)");
        Check(await Js(preview, "document.querySelector('.logo').naturalWidth") == "96", "relative image loaded (img/logo.png)");
        Check(await Js(preview, "document.getElementById('remote').naturalWidth") == "0", "remote image blocked (no network)");
        var marks = int.Parse(await Js(preview, "document.querySelectorAll('[data-bn-line]').length"));
        Check(marks > 80, $"line marks injected ({marks})");
        Check(await Js(preview, "location.host") == "\"bnpreview.local\"", "served from the virtual host");
        await Capture(preview, "html_aurora.png");

        // live refresh: unsaved text, not the file on disk
        var edited = text.Replace("Relative assets work", "Edited live — not saved yet");
        preview.Update(edited, index);
        await preview.FlushAsync();
        Check(await Loaded(preview), "live refresh ready");
        Check(await Js(preview, "document.querySelector('h1').textContent") == "\"Edited live — not saved yet\"", "live refresh shows the unsaved buffer");

        // editor → preview
        var target = LineOf(edited, "id=\"s20\"");
        preview.ScrollToLine(target, 0, 0.5);
        await Task.Delay(250);
        var topLine = await TopMark(preview);
        Check(Math.Abs(topLine - target) <= 3, $"editor→preview: line {target} at the top (got {topLine})");
        await Capture(preview, "html_aurora_synced.png");

        // scroll kept across a refresh
        var yBefore = double.Parse(await Js(preview, "window.scrollY"), System.Globalization.CultureInfo.InvariantCulture);
        preview.Update(edited.Replace("Section 3</h2>", "Section three</h2>"), index);
        await preview.FlushAsync();
        Check(await Loaded(preview), "refresh after scroll ready");
        await Task.Delay(200);
        var yAfter = double.Parse(await Js(preview, "window.scrollY"), System.Globalization.CultureInfo.InvariantCulture);
        Check(Math.Abs(yAfter - yBefore) < 40, $"scroll position kept across refresh ({yBefore:0} → {yAfter:0})");

        // preview → editor
        scrolls.Clear();
        await Js(preview, "window.scrollTo(0, document.querySelector('#s32').getBoundingClientRect().top + window.scrollY)");
        var gotScroll = await WaitFor(() => scrolls.Count > 0, 3000);
        var expect = LineOf(edited, "id=\"s32\"");
        Check(gotScroll && Math.Abs(scrolls[^1].Item1 - expect) <= 2, $"preview→editor: Scrolled({(gotScroll ? scrolls[^1].Item1 : -1)}) ≈ line {expect}");

        // double-click → source line
        await Js(preview, "document.querySelector('#s12 h2').dispatchEvent(new MouseEvent('dblclick', {bubbles: true}))");
        var h2Line = LineOf(edited, "id=\"s12\"") + 1;
        Check(await WaitFor(() => lines.Count > 0, 3000) && lines[^1] == h2Line, $"double-click → line {h2Line} (got {(lines.Count > 0 ? lines[^1] : -1)})");

        // external link (ExecuteScript runs with a user gesture, like a click): handed to the system browser, the page stays
        await Js(preview, "document.querySelector('a.ext').click()");
        await Task.Delay(400);
        Check(opened.Count == 1 && opened[0].Host == "example.com" && await Js(preview, "location.host") == "\"bnpreview.local\"",
            "external link → system browser, preview stays on the document");
        // a page script redirecting: cancelled, the preview never leaves the document (it would only open in the system browser
        // with fresh user activation, which ExecuteScript also grants, so the opened list is not checked here)
        await preview.Core!.ExecuteScriptAsync("setTimeout(function(){ location.href = 'https://example.org/'; }, 0)");
        await Task.Delay(400);
        Check(await Js(preview, "location.host") == "\"bnpreview.local\"", "script redirect cancelled, preview stays");

        // Daylight (the page keeps its own colors; only the loading color follows the theme)
        UseTheme("daylight");
        await Js(preview, "window.scrollTo(0, 0)");
        await Task.Delay(150);
        await Capture(preview, "html_daylight.png");
        UseTheme("aurora");

        // hidden → suspended; shown again → refreshes the pending text
        preview.Visibility = Visibility.Collapsed;
        preview.Update(edited.Replace("Edited live", "Changed while hidden"), index);
        await Task.Delay(300);
        preview.Visibility = Visibility.Visible;
        Check(await Loaded(preview), "shown again: pending refresh rendered");
        Check((await Js(preview, "document.querySelector('h1').textContent")).Contains("Changed while hidden"), "text changed while hidden is shown");

        // airspace cover
        await preview.SetCoveredAsync(true);
        Snapshot(preview, "html_covered_still.png", 1);
        await preview.SetCoveredAsync(false);

        preview.Dispose();
        win.Close();
    }

    private static async Task SvgPart(string fixtures, WebPreviewOptions options)
    {
        var path = Path.Combine(fixtures, "badge.svg");
        File.WriteAllText(path, SvgText, new UTF8Encoding(false));
        var svg = new SvgPreview(options);
        var lines = new List<int>();
        svg.LineRequested += lines.Add;
        var win = Host(svg, 900, 600);
        svg.Update(SvgText, path);
        Check(await Loaded(svg), "svg ready");
        var w = double.Parse(await Js(svg, "document.querySelector('svg').getBoundingClientRect().width"), System.Globalization.CultureInfo.InvariantCulture);
        Check(w > 500, $"svg fitted to the window ({w:0} px wide)");
        await Js(svg, "document.querySelector('#star').dispatchEvent(new MouseEvent('dblclick', {bubbles: true}))");
        var starLine = LineOf(SvgText, "id=\"star\"");
        Check(await WaitFor(() => lines.Count > 0, 3000) && lines[^1] == starLine, $"svg double-click → line {starLine}");
        await Capture(svg, "svg_aurora.png");

        UseTheme("daylight");
        await Task.Delay(700);
        Check(await Loaded(svg), "svg re-rendered for the light theme");
        await Capture(svg, "svg_daylight.png");
        UseTheme("aurora");
        svg.Dispose();
        win.Close();
    }

    // ───────────── helpers ─────────────

    private static async Task<bool> Loaded(WebPreview p)
    {
        if (!await WaitFor(() => p.IsReady, 15000)) return false;
        var until = Environment.TickCount64 + 5000;
        while (await Js(p, "document.readyState") != "\"complete\"")
        {
            if (Environment.TickCount64 > until) return false;
            await Task.Delay(40);
        }
        return true;
    }

    public static async Task<string> Js(WebPreview p, string script)
    {
        if (p.Core is null) return "null";
        return await p.Core.ExecuteScriptAsync(script);
    }

    private static async Task<int> TopMark(WebPreview p)
    {
        var r = await Js(p, "(function(){var e=document.elementFromPoint(60,4);while(e&&!(e.dataset&&e.dataset.bnLine))e=e.parentElement;return e?+e.dataset.bnLine:-1})()");
        return int.TryParse(r, out var v) ? v : -1;
    }

    private static async Task Capture(WebPreview p, string name)
    {
        await Task.Delay(250);
        var bmp = await p.CaptureAsync();
        if (bmp is null) Fail("capture " + name);
        else SavePng(bmp, name);
    }

    private static int LineOf(string text, string needle)
    {
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? -1 : text.AsSpan(0, at).Count('\n') + 1;
    }

    // ───────────── fixtures ─────────────

    private static string MakeSite(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "css"));
        Directory.CreateDirectory(Path.Combine(dir, "js"));
        Directory.CreateDirectory(Path.Combine(dir, "img"));
        File.WriteAllText(Path.Combine(dir, "css", "site.css"), Css);
        File.WriteAllText(Path.Combine(dir, "js", "app.js"),
            "document.addEventListener('DOMContentLoaded',function(){var b=document.getElementById('js-badge');b.textContent='JS ran';b.className='badge on';});");
        MakeLogo(Path.Combine(dir, "img", "logo.png"));
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Preview fixture</title>
<link rel="stylesheet" href="css/site.css">
<script src="js/app.js"></script>
</head>
<body>
<header class="hero">
  <img src="img/logo.png" alt="logo" class="logo" width="48" height="48">
  <div>
    <h1>Relative assets work</h1>
    <p class="lead">Styles from <code>css/site.css</code>, script from <code>js/app.js</code>, picture from <code>img/logo.png</code>.</p>
    <p><span id="js-badge" class="badge off">JS not run</span> <a class="ext" href="https://example.com/">example.com</a> · <a class="loc" href="other.html">other.html</a></p>
  </div>
  <img src="https://example.com/remote.png" id="remote" alt="" width="1" height="1">
</header>
<main>

""");
        for (var i = 1; i <= 40; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
<section id="s{i}">
  <h2>Section {i}</h2>
  <p>Paragraph {i}: the preview maps every start tag to its source line, so the editor and the preview scroll together.
  Double-click any block to jump to its line in the editor.</p>
  <ul><li>Item A{i}</li><li>Item B{i}</li></ul>
</section>

""");
        }
        sb.Append("</main>\n</body>\n</html>\n");
        var path = Path.Combine(dir, "index.html");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
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

    private const string Css = """
:root { --ink: #1d2233; --dim: #5b6276; --accent: #5b4bf0; }
* { box-sizing: border-box; }
body { margin: 0; font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", sans-serif; color: var(--ink); background: #fbfbfe; }
.hero { display: flex; gap: 18px; align-items: center; padding: 34px 44px; background: linear-gradient(135deg, #efeaff, #e3f6fb); border-bottom: 1px solid #e3e5ef; }
.logo { width: 48px; height: 48px; border-radius: 12px; }
h1 { margin: 0; font-size: 28px; letter-spacing: -0.02em; }
.lead { margin: 4px 0 6px; color: var(--dim); }
code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; background: #fff; padding: 1px 6px; border-radius: 5px; border: 1px solid #e3e5ef; }
.badge { display: inline-block; padding: 1px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; }
.badge.off { background: #fde2e2; color: #a12222; }
.badge.on { background: #d9f7ea; color: #0b7a4b; }
a { color: var(--accent); }
main { padding: 8px 44px 40px; max-width: 860px; }
section { border-bottom: 1px solid #eceef5; padding: 6px 0 10px; }
h2 { font-size: 18px; margin: 14px 0 4px; }
""";

    private const string SvgText = """
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
