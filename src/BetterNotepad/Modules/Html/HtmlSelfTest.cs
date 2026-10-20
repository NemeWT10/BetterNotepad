#if DEBUG
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Html;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Modules.Html;

/// <summary>
/// Debug self-test of the HTML / SVG preview (hidden desktop, no network: the test browser resolves no host and an allowed
/// remote request is answered locally). A fixture page with relative CSS / JS / image and CDN links: the browser starts only
/// for a shown preview, local resources load, remote ones are blocked and counted (notice), the setting lets them through,
/// typing refreshes the page, edits to its CSS in another tab too, Preview | Split | Edit, scroll sync both ways,
/// double-click → line, links, DevTools command, airspace covers (palette, dialog, settings, toast), release when hidden
/// and in the tray, a pinned (visually hosted) preview, SVG, the empty page, a missing runtime, and the browser exits at the end.
/// </summary>
internal static class HtmlSelfTest
{
    private static List<string> _failures = null!;

    private static void Check(bool ok, string what)
    {
        if (!ok) _failures.Add("html: " + what);
    }

    public static async Task Run(MainWindow w, List<string> failures)
    {
        _failures = failures;
        var settings = SettingsService.I.Get<HtmlPreviewSettings>();
        settings.AllowRemoteContent = false;
        var version = WebViewRuntime.AvailableVersion();
        Check(version is not null, "WebView2 runtime not found");
        if (version is null) return;
        Log.Info("html selftest: WebView2 " + version);
        var site = HtmlFixtures.Write(Path.Combine(AppEnv.LocalDir, "selftest-html"));
        var release = HtmlPreviewView.ReleaseAfter;
        HtmlPreviewView.ReleaseAfter = TimeSpan.FromMilliseconds(500);
        try
        {
            Check(WebViewRuntime.ActiveViews == 0 && !WebViewRuntime.IsBrowserRunning, "a browser runs before any preview was shown");
            await PageChecks(w, site, settings);
            await SvgChecks(w, site);
            await EmptyAndMissing(w, site);

            // every preview gone → no WebView, and the browser processes end
            foreach (var d in DocumentManager.I.Documents.OfType<TextDocument>().Where(d => d.FilePath is { } p && p.StartsWith(site.Folder, StringComparison.OrdinalIgnoreCase)).ToList())
                Close(w, d);
            await Idle(w, 200);
            Check(HtmlModule.Live.Count == 0, $"previews still alive after closing their tabs: {HtmlModule.Live.Count}");
            Check(WebViewRuntime.ActiveViews == 0, $"WebViews left: {WebViewRuntime.ActiveViews}");
            Check(await WaitFor(() => !WebViewRuntime.IsBrowserRunning, 30000), "browser process still running after the last preview was closed");
        }
        finally
        {
            HtmlPreviewView.ReleaseAfter = release;
            settings.AllowRemoteContent = false;
            WebViewRuntime.SimulateMissingRuntime = false;
        }
    }

    // ───────────── the fixture page ─────────────

    private static async Task PageChecks(MainWindow w, HtmlFixtures.Site site, HtmlPreviewSettings settings)
    {
        var group = w.Model.ActiveGroup;
        w.OpenFile(site.Index);
        if (DocumentManager.I.FindByPath(site.Index) is not TextDocument doc)
        {
            Check(false, "index.html did not open");
            return;
        }
        doc.ViewMode = ViewMode.Edit; // lazy: a preview that is not on screen starts no browser
        await doc.LoadAsync();
        await Idle(w, 300);
        var pv = PreviewOf(w);
        Check(pv is not null, "no HTML preview registered");
        if (pv is null) return;
        Check(!pv.Preview.IsBrowserCreated && WebViewRuntime.ActiveViews == 0, "Edit mode created a WebView");

        var requests = new List<(Uri Uri, bool Allowed)>();
        var loads = 0;
        pv.Preview.RemoteRequested += (u, a) => requests.Add((u, a));
        pv.Preview.DocumentLoaded += () => loads++;

        doc.ViewMode = ViewMode.Split;
        Check(await Loaded(pv.Preview), "first render did not finish");
        Check(pv.Preview.IsBrowserCreated && WebViewRuntime.ActiveViews == 1 && pv.Preview.NeedsCover, "split view: one native WebView");
        Check(pv.Preview.ServedRoot is { } root && FileUtil.PathEquals(root, site.Folder), "served root is the file's folder: " + pv.Preview.ServedRoot);

        // local resources next to the file
        Check(await Js(pv, "document.getElementById('js-badge').textContent") == "\"script ran\"", "relative JS (js/app.js) did not run");
        Check(await Js(pv, "getComputedStyle(document.querySelector('.hero')).display") == "\"flex\"", "relative CSS (css/site.css) not applied");
        Check(await Js(pv, "document.querySelector('.logo').naturalWidth") == "96", "relative image (img/logo.png) not loaded");
        Check(await Js(pv, "document.getElementById('remote').naturalWidth") == "0", "remote image loaded");
        Check(await Js(pv, "location.host") == "\"bnpreview.local\"", "not served from the virtual host");
        Check(pv.Preview.LoadedFiles.Any(f => FileUtil.PathEquals(f, site.Css)), "css/site.css not recorded as a file the page uses");

        // internet content: blocked at the filter (and the web socket by the policy), counted, a calm notice
        string[] hosts = ["fonts.googleapis.com", "cdn.jsdelivr.net", "images.example.com", "live.example.com"];
        await WaitFor(() => hosts.All(h => pv.Preview.BlockedHosts.Contains(h)), 4000);
        Check(hosts.All(h => pv.Preview.BlockedHosts.Contains(h)), "blocked hosts: " + string.Join(", ", pv.Preview.BlockedHosts));
        Check(pv.Preview.BlockedCount >= 5, "blocked count " + pv.Preview.BlockedCount);
        Check(requests.Count > 0 && requests.All(r => !r.Allowed), $"remote requests let through while blocked: {requests.Count(r => r.Allowed)}");
        await Idle(w, 100);
        Check(pv.NoticeShown && pv.NoticeText.Contains(pv.Preview.BlockedCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), "notice: " + pv.NoticeText);

        // resource hints would connect on their own: renamed in the served copy (the editor text stays as written)
        const string hints = "<link rel=\"preconnect\" href=\"https://a.example\"><LINK REL='dns-prefetch' href=//b.example><link rel=stylesheet href=c.css><p rel=\"preconnect\">";
        var neutral = ResourceHints.Neutralize(hints);
        Check(neutral.Contains("rel=\"bn-off-preconnect\"", StringComparison.Ordinal) && neutral.Contains("REL='bn-off-dns-prefetch'", StringComparison.Ordinal)
              && neutral.Contains("rel=stylesheet", StringComparison.Ordinal) && neutral.Contains("<p rel=\"preconnect\">", StringComparison.Ordinal), "hint renaming: " + neutral);
        Check(await Js(pv, "document.querySelector('link[href*=\"gstatic\"]').rel") == "\"bn-off-preconnect\"", "preconnect hint not neutralized while blocking");
        Check(await Js(pv, "!!document.querySelector('meta[http-equiv=\"x-dns-prefetch-control\"][content=\"off\"]')") == "true", "DNS prefetching not switched off while blocking");
        Check(doc.Buffer.Text.Contains("rel=\"preconnect\"", StringComparison.Ordinal), "the editor text was changed");

        // find in the page (the browser's own find bar; Ctrl+F there when the page has the keyboard)
        Check(await pv.Preview.StartFindAsync("Reading") && await WaitFor(() => pv.Preview.FindMatchCount >= 24, 4000), "find in page: matches " + pv.Preview.FindMatchCount);
        pv.Preview.StopFind();
        pv.Preview.FocusPage();
        await Idle(w, 150);
        if (pv.Preview.IsPageFocused && Keymap.GestureFor("editor.find") is { } findChord)
        {
            Check(MainWindow.FocusedContentKey?.Invoke(w, findChord) == true, "Ctrl+F in the focused page did not open the page's find");
            await Idle(w, 150);
            Check(pv.Preview.IsFinding, "page find not running after Ctrl+F");
            pv.Preview.StopFind();
        }
        else
        {
            Log.Info("html selftest: the page cannot take the keyboard in this never-activated test window; find checked through the API only");
        }
        w.ActiveView?.FocusContent();

        // the setting lets them through (answered locally in test mode), the notice goes; and back
        requests.Clear();
        var before = loads;
        settings.AllowRemoteContent = true;
        Check(await WaitFor(() => loads > before, 8000) && await Loaded(pv.Preview), "no reload after allowing internet content");
        Check(requests.Any(r => r.Allowed && r.Uri.Host == "cdn.jsdelivr.net") && requests.All(r => r.Allowed), "allowed requests: " + string.Join(", ", requests.Select(r => $"{r.Uri.Host}={r.Allowed}")));
        Check(pv.Preview.BlockedCount == 0 && !pv.NoticeShown, "still blocking / notice shown after allowing");
        Check(await Js(pv, "document.querySelector('link[href*=\"gstatic\"]').rel") == "\"preconnect\"", "preconnect hint renamed although internet content is allowed");
        before = loads;
        settings.AllowRemoteContent = false;
        Check(await WaitFor(() => loads > before, 8000) && await Loaded(pv.Preview), "no reload after blocking again");
        await WaitFor(() => pv.NoticeShown, 3000);
        Check(pv.Preview.BlockedCount >= 4 && pv.NoticeShown, "blocking again: count " + pv.Preview.BlockedCount);

        // the notice opens the setting (Files page, the switch revealed)
        HtmlModule.OpenRemoteSetting(w);
        await Idle(w, 300);
        var page = SettingsPage.For(w);
        Check(page is { CurrentPage: SettingsPageIds.Files } && page.FindById<CheckBox>(SettingsPage.HtmlRemoteId) is { IsChecked: false }, "notice → settings: Files page with the switch");
        page?.Close();
        await WaitFor(() => SettingsPage.For(w) is null, 3000);

        // live refresh from the unsaved buffer
        var h1 = doc.Buffer.Text.IndexOf("<h1>Field notes</h1>", StringComparison.Ordinal) + 4;
        doc.Buffer.Replace(h1, "Field notes".Length, "Edited live");
        Check(await WaitFor(async () => await Js(pv, "document.querySelector('h1').textContent") == "\"Edited live\"", 5000), "typing did not refresh the page");
        doc.Buffer.UndoStack.Undo();
        Check(await WaitFor(async () => await Js(pv, "document.querySelector('h1').textContent") == "\"Field notes\"", 5000), "undo did not refresh the page");

        // its CSS edited (unsaved) in a tab next to it: the page follows
        w.Split(Orientation.Horizontal);
        var second = w.Model.Groups[1];
        w.OpenFile(site.Css, second);
        await Idle(w, 300);
        if (DocumentManager.I.FindByPath(site.Css) is TextDocument css)
        {
            await css.LoadAsync();
            var at = css.Buffer.Text.IndexOf("font-size: 28px", StringComparison.Ordinal);
            css.Buffer.Replace(at, "font-size: 28px".Length, "font-size: 40px");
            Check(await WaitFor(async () => await Js(pv, "getComputedStyle(document.querySelector('h1')).fontSize") == "\"40px\"", 6000), "unsaved CSS from the other tab not shown");
            css.Buffer.UndoStack.Undo();
            Check(await WaitFor(async () => await Js(pv, "getComputedStyle(document.querySelector('h1')).fontSize") == "\"28px\"", 6000), "CSS undo not shown");
            Close(w, css);
        }
        else
        {
            Check(false, "css/site.css did not open");
        }
        w.Unsplit();
        await Idle(w, 300);
        group = w.Model.ActiveGroup;
        group.Active = doc;
        await Idle(w, 200);
        pv = PreviewOf(w) ?? pv;

        // Preview | Split | Edit
        doc.ViewMode = ViewMode.Preview;
        await Idle(w, 200);
        Check(pv.Element.IsVisible && w.ActiveView?.Editor?.IsVisible != true, "Preview mode: page only");
        doc.ViewMode = ViewMode.Edit;
        await Idle(w, 200);
        Check(!pv.Element.IsVisible && w.ActiveView?.Editor?.IsVisible == true, "Edit mode: editor only");
        Check(!CanRun(w, "html.devTools"), "DevTools command available with the preview hidden");
        doc.ViewMode = ViewMode.Split;
        Check(await Loaded(pv.Preview), "Split again: page not ready");
        Check(pv.Element.IsVisible && w.ActiveView?.Editor?.IsVisible == true, "Split mode: both");
        Check(CanRun(w, "html.devTools") && Keymap.GestureText("html.devTools") == "F12", "DevTools command (F12) with the preview shown");

        await ScrollChecks(w, pv, doc);
        await LinkChecks(w, pv, doc, site);
        await AirspaceChecks(w, pv, doc);
        await ReleaseChecks(w, pv, doc);
        await PinnedChecks(w, doc);
    }

    private static async Task ScrollChecks(MainWindow w, HtmlPreviewView pv, TextDocument doc)
    {
        var text = doc.Buffer.Text;
        // editor → preview
        var target = HtmlFixtures.LineOf(text, "id=\"s16\"");
        pv.ScrollToLine(target, 0, 0.5);
        await Idle(w, 350);
        var top = await TopMark(pv);
        Check(Math.Abs(top - target) <= 3, $"editor→preview: line {target} at the top (got {top})");

        // preview → editor
        var ed = w.ActiveView?.Editor;
        var expect = HtmlFixtures.LineOf(text, "id=\"s8\"");
        await Js(pv, "window.scrollTo(0, document.querySelector('#s8').getBoundingClientRect().top + window.scrollY)");
        var synced = await WaitFor(() => ed is not null && Math.Abs(EditorTopLine(ed) - expect) <= 3, 4000);
        Check(synced, $"preview→editor: editor top line {(ed is null ? -1 : EditorTopLine(ed))}, expected ≈ {expect}");

        // double-click → source line in the editor
        await Js(pv, "document.querySelector('#s12 h2').dispatchEvent(new MouseEvent('dblclick', {bubbles: true}))");
        var h2 = HtmlFixtures.LineOf(text, "id=\"s12\"") + 1;
        Check(await WaitFor(() => ed?.TextArea.Caret.Line == h2, 3000), $"double-click → line {h2} (caret on {ed?.TextArea.Caret.Line})");
    }

    private static async Task LinkChecks(MainWindow w, HtmlPreviewView pv, TextDocument doc, HtmlFixtures.Site site)
    {
        HtmlModule.OpenedExternally.Clear();
        // ExecuteScript runs with a user gesture, like a click
        await Js(pv, "document.querySelector('a.ext').click()");
        Check(await WaitFor(() => HtmlModule.OpenedExternally.Contains("https://example.com/"), 3000) && await Js(pv, "location.host") == "\"bnpreview.local\"",
            "external link → system browser (logged), the preview stays: " + string.Join(", ", HtmlModule.OpenedExternally));
        await Js(pv, "document.querySelector('a.loc').click()");
        Check(await WaitFor(() => DocumentManager.I.FindByPath(site.Other) is not null, 3000), "local link did not open other.html as a tab");
        if (DocumentManager.I.FindByPath(site.Other) is TextDocument other) Close(w, other);
        // a script navigating away is cancelled
        await Js(pv, "setTimeout(function(){ location.href = 'https://example.org/'; }, 0)");
        await Idle(w, 400);
        Check(await Js(pv, "location.host") == "\"bnpreview.local\"", "a script navigated the preview away");
        w.Model.ActiveGroup.Active = doc;
        Check(await Loaded(pv.Preview), "back on the page: not ready");
    }

    private static async Task AirspaceChecks(MainWindow w, HtmlPreviewView pv, TextDocument doc)
    {
        // palette over the native page → still picture while it is open
        w.ShowPalette(PaletteMode.Commands);
        Check(await WaitFor(() => pv.Preview.IsCovered, 4000), "palette open: preview not covered");
        CommandPalette.Close(w);
        Check(await WaitFor(() => !pv.Preview.IsCovered, 4000), "palette closed: preview still covered");

        // an in-window dialog
        var answer = w.DialogLayer.Show("Airspace", "The page steps aside.", [new DialogButton("ok", "OK", DialogButtonKind.Primary, IsDefault: true, IsCancel: true)]);
        Check(await WaitFor(() => pv.Preview.IsCovered, 4000), "dialog open: preview not covered");
        SnapshotMode.PressDialogButton(w, "OK");
        await answer;
        Check(await WaitFor(() => !pv.Preview.IsCovered, 4000), "dialog closed: preview still covered");

        // the settings page
        w.ShowSettings();
        Check(await WaitFor(() => pv.Preview.IsCovered, 4000), "settings open: preview not covered");
        SettingsPage.For(w)?.Close();
        Check(await WaitFor(() => !pv.Preview.IsCovered, 4000), "settings closed: preview still covered");

        // a toast over the page (Preview mode: the page reaches the corner) covers softly; a click on the page wins
        doc.ViewMode = ViewMode.Preview;
        Check(await Loaded(pv.Preview), "Preview mode: page not ready");
        w.Toasts.Show("Saved", ToastKind.Success, seconds: 30);
        Check(await WaitFor(() => pv.Preview.IsCovered, 4000), "toast over the page: not covered");
        pv.Preview.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
        Check(await WaitFor(() => !pv.Preview.IsCovered, 4000), "a click on the softly covered page did not bring it back");
        w.Toasts.Clear();
        doc.ViewMode = ViewMode.Split;
        Check(await Loaded(pv.Preview), "Split: page not ready");
        // Split with the preview on the left: the toast corner is over the editor
        if (SettingsService.I.Get<FilesSettings>().PreviewOnLeft)
        {
            w.Toasts.Show("Saved", ToastKind.Success, seconds: 30);
            await Idle(w, 400);
            Check(!pv.Preview.IsCovered, "toast over the editor covered the preview");
            w.Toasts.Clear();
        }
        await Idle(w, 100);
        Check(!pv.Preview.IsCovered, "preview left covered");
    }

    private static async Task ReleaseChecks(MainWindow w, HtmlPreviewView pv, TextDocument doc)
    {
        var group = w.Model.ActiveGroup;
        // hidden (another tab) for a while → the WebView goes and the browser ends; shown again → back
        var scratch = w.NewTab(content: "scratch");
        Check(await WaitFor(() => pv.IsReleased, 5000), "hidden preview not released");
        Check(!pv.Preview.IsBrowserCreated && WebViewRuntime.ActiveViews == 0, $"WebViews after release: {WebViewRuntime.ActiveViews}");
        Check(await WaitFor(() => !WebViewRuntime.IsBrowserRunning, 30000), "browser process did not end after the last preview was released");
        group.Active = doc;
        Check(await Loaded(pv.Preview), "shown again: page not ready");
        Check(await Js(pv, "document.querySelector('h1').textContent") == "\"Field notes\"", "shown again: content missing");
        group.Remove(scratch);
        DocumentManager.I.Close(scratch);

        // the window goes to the tray: a hidden preview is freed at once
        doc.ViewMode = ViewMode.Edit;
        await Idle(w, 100);
        w.ReleaseForTray();
        Check(pv.IsReleased && !pv.Preview.IsBrowserCreated, "tray: preview not released at once");
        doc.ViewMode = ViewMode.Split;
        Check(await Loaded(pv.Preview), "after the tray: page not ready");
    }

    private static async Task PinnedChecks(MainWindow w, TextDocument doc)
    {
        // pinned on top = a per-pixel transparent window, where a native child window is never drawn: visually hosted there
        w.PinOnTop(w.Model.ActiveGroup, doc);
        var model = Workspace.I.Windows.FirstOrDefault(m => m.Pinned && m.AllDocuments.Contains(doc));
        var pinned = model is null ? null : App.Current.WindowFor(model);
        if (pinned is null)
        {
            Check(false, "pinned window missing");
            return;
        }
        await Idle(pinned, 300);
        doc.ViewMode = ViewMode.Preview;
        await Idle(pinned, 200);
        var pv = PreviewOf(pinned);
        Check(pv is not null, "pinned: no preview");
        if (pv is not null)
        {
            Check(await Loaded(pv.Preview, 30000), "pinned: page not ready");
            Check((pv.Preview.IsComposited || pv.Preview.IsPicture) && !pv.Preview.NeedsCover, "pinned: preview not visually hosted");
            Check(await WaitFor(() => pv.Preview.PictureSource is BitmapSource { PixelWidth: > 10 } || pv.Preview.IsComposited, 5000), "pinned: no picture of the page");
            if (pv.Preview.IsPicture && pv.Preview.PictureElement is { } picture)
            {
                // the wheel over the picture scrolls the page, and a new picture follows
                var first = pv.Preview.PictureSource;
                picture.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -360) { RoutedEvent = UIElement.MouseWheelEvent });
                Check(await WaitFor(async () => double.TryParse(await Js(pv, "window.scrollY"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && y > 100, 4000), "pinned: wheel did not scroll the page");
                Check(await WaitFor(() => !ReferenceEquals(pv.Preview.PictureSource, first), 4000), "pinned: no new picture after scrolling");
            }
            pinned.ShowPalette(PaletteMode.Commands);
            await Idle(pinned, 300);
            Check(!pv.Preview.IsCovered, "pinned: a visually hosted page needs no cover");
            CommandPalette.Close(pinned);
        }
        doc.ViewMode = ViewMode.Split;
        pinned.UnpinFromTop();
        await Idle(w, 400);
        w.Model.ActiveGroup.Active = doc;
        await Idle(w, 200);
        if (PreviewOf(w) is { } back) Check(await Loaded(back.Preview) && back.Preview.NeedsCover, "unpinned: native WebView again");
    }

    // ───────────── SVG ─────────────

    private static async Task SvgChecks(MainWindow w, HtmlFixtures.Site site)
    {
        w.OpenFile(site.Svg);
        if (DocumentManager.I.FindByPath(site.Svg) is not TextDocument doc)
        {
            Check(false, "badge.svg did not open");
            return;
        }
        await doc.LoadAsync();
        doc.ViewMode = ViewMode.Split;
        await Idle(w, 300);
        var pv = PreviewOf(w);
        Check(pv is { IsSvg: true }, "no SVG preview");
        if (pv is null) return;
        Check(await Loaded(pv.Preview), "svg: not ready");
        var width = double.Parse(await Js(pv, "document.querySelector('svg').getBoundingClientRect().width"), CultureInfo.InvariantCulture);
        Check(width > 240, $"svg not fitted to the pane ({width:0} px)");
        await Js(pv, "document.querySelector('#star').dispatchEvent(new MouseEvent('dblclick', {bubbles: true}))");
        var star = HtmlFixtures.LineOf(doc.Buffer.Text, "id=\"star\"");
        Check(await WaitFor(() => w.ActiveView?.Editor?.TextArea.Caret.Line == star, 3000), $"svg double-click → line {star}");

        // the stage follows the theme
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var theme = appearance.Theme;
        var loads = 0;
        pv.Preview.DocumentLoaded += () => loads++;
        appearance.Theme = "daylight";
        Check(await WaitFor(() => loads > 0, 6000) && await Loaded(pv.Preview), "svg not re-rendered for the light theme");
        var bg = await Js(pv, "getComputedStyle(document.body).backgroundColor");
        var c = UI.Themes.Palettes.All.First(p => p.Id == "daylight").EditorBg;
        Check(bg == $"\"rgb({c.R}, {c.G}, {c.B})\"", $"svg stage background {bg}");
        appearance.Theme = theme;
        await Idle(w, 300);
    }

    // ───────────── empty page, missing runtime ─────────────

    private static async Task EmptyAndMissing(MainWindow w, HtmlFixtures.Site site)
    {
        w.OpenFile(site.Empty);
        if (DocumentManager.I.FindByPath(site.Empty) is TextDocument empty)
        {
            await empty.LoadAsync();
            empty.ViewMode = ViewMode.Split;
            await Idle(w, 300);
            if (PreviewOf(w) is { } pv)
            {
                Check(await Loaded(pv.Preview), "empty page: not ready");
                Check(await Js(pv, "document.body.textContent") == $"\"{Loc.T("html.emptyPage")}\"", "empty page: no hint");
                var c = UI.Themes.ThemeManager.I.Current.EditorBg;
                Check(await Js(pv, "getComputedStyle(document.body).backgroundColor") == $"\"rgb({c.R}, {c.G}, {c.B})\"", "empty page: not in the theme's background");
            }
            else
            {
                Check(false, "empty page: no preview");
            }
        }

        // no WebView2 runtime: a calm message with the download link instead of a page
        WebViewRuntime.SimulateMissingRuntime = true;
        var path = Path.Combine(site.Folder, "missing.html");
        File.WriteAllText(path, "<p>needs a runtime</p>");
        HtmlModule.OpenedExternally.Clear();
        w.OpenFile(path);
        if (DocumentManager.I.FindByPath(path) is TextDocument missing)
        {
            await missing.LoadAsync();
            missing.ViewMode = ViewMode.Split;
            await Idle(w, 400);
            var pv = PreviewOf(w);
            Check(pv is { Preview.IsShowingMessage: true, Preview.IsBrowserCreated: false }, "missing runtime: no message");
            if (pv is not null && Descendants<Button>(pv.Preview).FirstOrDefault() is { } link)
            {
                link.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(HtmlModule.OpenedExternally.Any(u => u.Contains("webview2", StringComparison.OrdinalIgnoreCase)), "missing runtime: the link does not open the download page");
            }
            else
            {
                Check(false, "missing runtime: no download link");
            }
        }
        WebViewRuntime.SimulateMissingRuntime = false;
    }

    // ───────────── helpers ─────────────

    private static HtmlPreviewView? PreviewOf(MainWindow w) => (w.ActiveView as TextDocumentView)?.Preview as HtmlPreviewView;

    private static bool CanRun(MainWindow w, string id) => CommandRegistry.Get(id) is { } c && c.AppliesTo(w.PaletteContext());

    private static void Close(MainWindow w, Document d)
    {
        if (d is TextDocument t && t.IsDirty) t.MarkClean();
        if (Workspace.I.Locate(d) is { } where) where.Group.Remove(d);
        DocumentManager.I.Close(d);
    }

    private static int EditorTopLine(Editor.CodeEditor ed)
        => ed.TextArea.TextView.GetDocumentLineByVisualTop(ed.VerticalOffset)?.LineNumber ?? -1;

    private static async Task<string> Js(HtmlPreviewView pv, string script)
    {
        if (pv.Preview.Core is not { } core) return "null";
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    private static async Task<int> TopMark(HtmlPreviewView pv)
    {
        var r = await Js(pv, "(function(){var e=document.elementFromPoint(60,4);while(e&&!(e.dataset&&e.dataset.bnLine))e=e.parentElement;return e?+e.dataset.bnLine:-1})()");
        return int.TryParse(r, out var v) ? v : -1;
    }

    /// <summary>The current document is ready and everything it loads has arrived.</summary>
    private static async Task<bool> Loaded(WebPreview p, int timeoutMs = 20000)
    {
        if (!await WaitFor(() => p.IsReady && p.Core is not null, timeoutMs)) return false;
        return await WaitFor(async () => p.Core is { } core && await core.ExecuteScriptAsync("document.readyState") == "\"complete\"", 8000);
    }

    private static async Task<bool> WaitFor(Func<bool> probe, int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += 50)
        {
            if (probe()) return true;
            await Task.Delay(50);
        }
        return probe();
    }

    private static async Task<bool> WaitFor(Func<Task<bool>> probe, int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += 80)
        {
            if (await probe()) return true;
            await Task.Delay(80);
        }
        return await probe();
    }

    private static async Task Idle(Window w, int ms)
    {
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        w.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
#endif
