using System.IO;
using System.Windows;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Views;
using BetterNotepad.Html;
using BetterNotepad.Modules.Html;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;
using TextDocument = BetterNotepad.Documents.TextDocument;

namespace BetterNotepad.Core;

/// <summary>
/// 23. HTML / SVG preview: the split view (page with its blocked-content notice) in two themes and both languages, the notice
/// up close, Preview mode, the palette and a dialog over the page (airspace: the native page steps aside for a still picture
/// of itself), the setting in Settings → Files, SVG, the empty page, a missing runtime, a pinned window (picture hosting).
/// A render of the visual tree cannot see a native WebView window: before each shot the page is swapped for WebView2's own
/// capture of its content (the same picture the airspace cover uses), so the PNG shows what the user sees.
/// </summary>
internal static partial class SnapshotMode
{
    private static async Task HtmlShots(MainWindow w)
    {
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var remote = SettingsService.I.Get<HtmlPreviewSettings>();
        remote.AllowRemoteContent = false;
        if (WebViewRuntime.AvailableVersion() is null) throw new InvalidOperationException("WebView2 runtime missing");
        var site = HtmlFixtures.Write(Path.Combine(AppEnv.LocalDir, "snapshot-fixtures", "field-notes"));
        w.Width = 1100;
        w.Height = 700;
        await UseTheme(w, "aurora");
        appearance.Language = "en";

        w.OpenFile(site.Index);
        if (DocumentManager.I.FindByPath(site.Index) is not TextDocument doc) throw new InvalidOperationException("index.html not opened");
        await doc.LoadAsync();
        doc.ViewMode = ViewMode.Split;
        var pv = await HtmlReady(w);
        await WaitFor(() => pv.NoticeShown ? pv : null, 4000);
        PlaceCaret(w, doc, "<h1>");

        // split view, notice on top of the page
        foreach (var (id, lang) in new[] { ("aurora", "en"), ("daylight", "pl"), ("graphite", "en") })
        {
            await UseTheme(w, id);
            appearance.Language = lang;
            await Settle(w, 250);
            await ShootHtml(w, pv, $"23_html_split_{id}_{lang}");
            if (id == "aurora" || id == "daylight")
            {
                var notice = pv.Element.TransformToAncestor(w).TransformBounds(new Rect(0, 0, pv.Element.ActualWidth, 40));
                if (pv.Preview.NeedsCover) await pv.Preview.SetCoveredAsync(true);
                SaveRegion(w, $"23_html_notice_{id}_{lang}", notice, 2);
                if (pv.Preview.NeedsCover) await pv.Preview.SetCoveredAsync(false);
            }
        }
        appearance.Language = "en";
        await UseTheme(w, "aurora");

        // Preview mode: the page alone
        doc.ViewMode = ViewMode.Preview;
        pv = await HtmlReady(w);
        await ShootHtml(w, pv, "23_html_preview_aurora_en");

        // airspace: the palette and a dialog over the native page (the window covers it by itself)
        doc.ViewMode = ViewMode.Split;
        pv = await HtmlReady(w);
        CommandPalette.Show(w, PaletteMode.Commands, ">preview");
        await WaitFor(() => pv.Preview.IsCovered ? pv : null, 4000);
        if (!pv.Preview.IsCovered) Failures.Add("html: palette open, preview not covered");
        await Save(w, "23_html_palette_aurora_en");
        CommandPalette.Close(w);
        await WaitFor(() => pv.Preview.IsCovered ? null : pv, 4000);

        await UseTheme(w, "daylight");
        var answer = w.DialogLayer.Show(Loc.T("close.saveTitle", "index.html"), Loc.T("close.saveText"),
        [
            new DialogButton("save", Loc.T("close.save"), DialogButtonKind.Primary, IsDefault: true),
            new DialogButton("discard", Loc.T("close.discard"), DialogButtonKind.Danger),
            new DialogButton("cancel", Loc.T("common.cancel"), DialogButtonKind.Ghost, IsCancel: true),
        ], glyph: UI.Controls.Glyphs.Save);
        await WaitFor(() => pv.Preview.IsCovered ? pv : null, 4000);
        if (!pv.Preview.IsCovered) Failures.Add("html: dialog open, preview not covered");
        await Save(w, "23_html_dialog_daylight_en");
        PressDialogButton(w, Loc.T("common.cancel"));
        await answer;
        await WaitFor(() => pv.Preview.IsCovered ? null : pv, 4000);

        // the setting, reached from the notice (Files page, revealed)
        foreach (var (id, lang) in new[] { ("aurora", "en"), ("daylight", "pl") })
        {
            await UseTheme(w, id);
            appearance.Language = lang;
            await Settle(w, 200);
            HtmlModule.OpenRemoteSetting(w);
            await Settle(w, 400);
            await Save(w, $"23_html_settings_{id}_{lang}");
            SettingsPage.For(w)?.Close();
            await Settle(w, 300);
        }
        appearance.Language = "en";
        await UseTheme(w, "aurora");

        // SVG: fitted over the theme's checkerboard
        w.OpenFile(site.Svg);
        if (DocumentManager.I.FindByPath(site.Svg) is TextDocument svg)
        {
            await svg.LoadAsync();
            svg.ViewMode = ViewMode.Split;
            var sv = await HtmlReady(w);
            await ShootHtml(w, sv, "23_svg_split_aurora_en");
            await UseTheme(w, "daylight");
            sv = await HtmlReady(w, rerender: true);
            await ShootHtml(w, sv, "23_svg_split_daylight_en");
            await UseTheme(w, "aurora");
        }

        // a new, empty page follows the theme
        w.OpenFile(site.Empty);
        if (DocumentManager.I.FindByPath(site.Empty) is TextDocument empty)
        {
            await empty.LoadAsync();
            empty.ViewMode = ViewMode.Split;
            var ev = await HtmlReady(w);
            await ShootHtml(w, ev, "23_html_empty_aurora_en");
        }

        // no WebView2 runtime: a calm message with the way to get it
        WebViewRuntime.SimulateMissingRuntime = true;
        var missingPath = Path.Combine(site.Folder, "needs-runtime.html");
        File.WriteAllText(missingPath, "<!doctype html>\n<p>Hello</p>\n");
        w.OpenFile(missingPath);
        if (DocumentManager.I.FindByPath(missingPath) is TextDocument missing)
        {
            await missing.LoadAsync();
            missing.ViewMode = ViewMode.Split;
            await Settle(w, 400);
            await Save(w, "23_html_runtime_missing_aurora_en");
            appearance.Language = "pl";
            await Settle(w, 200);
            if ((w.ActiveView as TextDocumentView)?.Preview is HtmlPreviewView mv) mv.Preview.Refresh(now: true);
            await Settle(w, 300);
            await Save(w, "23_html_runtime_missing_aurora_pl");
            appearance.Language = "en";
        }
        WebViewRuntime.SimulateMissingRuntime = false;

        // pinned on top: a transparent window shows the page as a live picture
        w.Model.ActiveGroup.Active = doc;
        doc.ViewMode = ViewMode.Preview;
        await Settle(w, 200);
        var group = w.Model.Groups.First(g => g.Tabs.Contains(doc));
        w.PinOnTop(group, doc);
        var model = Workspace.I.Windows.First(m => m.Pinned && m.AllDocuments.Contains(doc));
        var pinned = App.Current.WindowFor(model)!;
        pinned.Left = -32000;
        pinned.Top = -32000;
        pinned.Width = 520;
        pinned.Height = 600;
        await Settle(pinned, 300);
        var pp = await HtmlReady(pinned);
        await WaitFor(() => pp.Preview.PictureSource is not null ? pp : null, 5000);
        pinned.HidePinBar(animate: false);
        await Settle(pinned, 300);
        await Save(pinned, "23_html_pinned_aurora_en");
        pinned.ShowPinBar();
        await Settle(pinned, 300);
        await Save(pinned, "23_html_pinned_bar_aurora_en");
        doc.ViewMode = ViewMode.Split;
        pinned.UnpinFromTop();
        await Settle(w, 300);

        // tidy up: the fixture tabs go (unchanged, nothing to ask)
        foreach (var d in DocumentManager.I.Documents.OfType<TextDocument>()
                     .Where(d => d.FilePath is { } p && p.StartsWith(site.Folder, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (d.IsDirty) d.MarkClean();
            if (Workspace.I.Locate(d) is { } where) where.Group.Remove(d);
            DocumentManager.I.Close(d);
        }
        await Settle(w, 200);
    }

    /// <summary>The active tab's HTML / SVG preview once its page has fully loaded.</summary>
    private static async Task<HtmlPreviewView> HtmlReady(MainWindow w, bool rerender = false)
    {
        await Settle(w, rerender ? 600 : 150);
        var pv = await WaitFor(() => (w.ActiveView as TextDocumentView)?.Preview as HtmlPreviewView, 6000)
                 ?? throw new InvalidOperationException("no HTML preview");
        for (var i = 0; i < 400; i++)
        {
            if (pv.Preview.IsReady && pv.Preview.Core is { } core && await core.ExecuteScriptAsync("document.readyState") == "\"complete\"") break;
            await Task.Delay(50);
        }
        await Task.Delay(300); // fonts, the last paint
        return pv;
    }

    /// <summary>A window render with the native page in it (its own capture stands in for it while the shot is taken).</summary>
    private static async Task ShootHtml(MainWindow w, HtmlPreviewView pv, string name)
    {
        await Settle(w, 120);
        var cover = pv.Preview.NeedsCover;
        if (cover) await pv.Preview.SetCoveredAsync(true);
        if (cover && !pv.Preview.IsCovered) Failures.Add("html: no page capture for " + name);
        await Save(w, name);
        if (cover) await pv.Preview.SetCoveredAsync(false);
    }

    private static void PlaceCaret(MainWindow w, TextDocument doc, string near)
    {
        if (w.ActiveView?.Editor is not { } ed) return;
        var at = doc.Buffer.Text.IndexOf(near, StringComparison.Ordinal);
        if (at >= 0) ed.CaretOffset = at + near.Length;
    }
}
