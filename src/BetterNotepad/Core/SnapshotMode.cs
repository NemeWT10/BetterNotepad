using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.UI;
using BetterNotepad.UI.Controls;
using BetterNotepad.UI.Themes;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Core;

/// <summary>
/// UI snapshot mode for visual QA, active only with <c>--test-mode</c> and BETTERNOTEPAD_SNAPSHOT=&lt;dir&gt;.
/// After startup it drives the app programmatically (no keyboard or mouse input, nothing is ever read from the screen):
/// for every theme it sets up scenarios and renders each window's visual tree with RenderTargetBitmap at 1.25x
/// into PNG files in &lt;dir&gt;. Run it on a hidden desktop (tools\snapshot.ps1). Writes index.txt (or FAILED.txt) and quits.
/// </summary>
internal static class SnapshotMode
{
    private const double Scale = 1.25;
    private static string _dir = "";
    private static readonly List<string> Files = new();
    private static readonly List<string> Failures = new();

    public static bool Requested => AppEnv.TestMode && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT"));

    public static void Start()
    {
        _dir = Path.GetFullPath(Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT")!);
        Directory.CreateDirectory(_dir);
        var tries = 0;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            var w = App.Current.MainAppWindow;
            if (w?.ActiveView?.Editor is null && ++tries < 40) return;
            timer.Stop();
            try
            {
                if (w is null) Failures.Add("no main window");
                else await RunAll(w);
            }
            catch (Exception ex)
            {
                Failures.Add("exception: " + ex);
            }
            Finish();
        };
        timer.Start();
    }

    private static void Finish()
    {
        try
        {
            File.WriteAllLines(Path.Combine(_dir, "index.txt"), Files);
            var failed = Path.Combine(_dir, "FAILED.txt");
            if (Failures.Count > 0) File.WriteAllLines(failed, Failures);
            else File.Delete(failed);
        }
        catch (Exception ex)
        {
            Log.Error("snapshot: cannot write index", ex);
        }
        if (Failures.Count == 0) Log.Info($"SNAPSHOT OK ({Files.Count} files)");
        else foreach (var f in Failures) Log.Error("SNAPSHOT FAIL: " + f);
        App.Current.Quit();
    }

    /// <summary>
    /// BETTERNOTEPAD_SNAPSHOT_ONLY=17,19 renders only the scenarios with these number prefixes (quick iteration);
    /// unset = everything (the UiSnapshots test expects the full set).
    /// </summary>
    private static bool Want(string prefix)
    {
        var only = Environment.GetEnvironmentVariable("BETTERNOTEPAD_SNAPSHOT_ONLY");
        return string.IsNullOrWhiteSpace(only) || only.Split(',', StringSplitOptions.TrimEntries).Contains(prefix);
    }

    private static async Task Guard(string what, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Failures.Add($"{what}: {ex}");
        }
    }

    // ───────────── scenarios ─────────────

    private sealed record Fixtures(TextDocument Cs, TextDocument Py, TextDocument Md, TextDocument Json, TextDocument Log, TextDocument Asm);

    private static async Task RunAll(MainWindow w)
    {
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        appearance.Animations = false; // end states only: dialogs, toasts and tabs are drawn in their final position
        appearance.Language = "en";
        w.Width = 1100;
        w.Height = 700;
        await Settle(w);

        WriteContrastReport();

        // 1. empty untitled tab, every theme
        foreach (var p in Palettes.All.Where(_ => Want("01")))
            await Guard("empty " + p.Id, async () =>
            {
                await UseTheme(w, p.Id);
                await Save(w, $"01_empty_{p.Id}_en");
            });

        // 2. real files in several tabs
        var fx = await OpenFixtures(w);
        var group = w.Model.ActiveGroup;

        foreach (var p in Palettes.All.Where(_ => Want("02")))
        {
            var id = p.Id;
            await Guard("theme " + id, async () =>
            {
                await UseTheme(w, id);
                group.Active = fx.Cs;
                await Settle(w);
                SelectClass(w);
                await Settle(w);
                await Save(w, $"02_code_{id}_en");
                await Syntax(w, fx, id);
                await Split(w, fx, id);
                await FindBar(w, fx, id, "en");
                await CloseDialog(w, fx, id, "en");
                await Toasts(w, id);
            });
        }

        // 3. Polish UI (long labels) on one dark and one light theme, plus the 3-button external-change dialog
        foreach (var id in new[] { "aurora", "daylight" }.Where(_ => Want("02")))
            await Guard("polish " + id, async () =>
            {
                await UseTheme(w, id);
                appearance.Language = "pl";
                await Settle(w);
                group.Active = fx.Cs;
                await Settle(w);
                await Save(w, $"02_code_{id}_pl");
                await FindBar(w, fx, id, "pl");
                await CloseDialog(w, fx, id, "pl");
                await ExternalDialog(w, fx, id, "pl");
                appearance.Language = "en";
                await Settle(w);
                await ExternalDialog(w, fx, id, "en");
                await Menus(w, fx, id);
            });

        // 4. overlays and Markdown: command palette, Ctrl+Tab, settings pages, Markdown split / preview with contents
        foreach (var id in new[] { "aurora", "daylight" }.Where(_ => Want("12")))
            await Guard("overlays " + id, async () =>
            {
                await UseTheme(w, id);
                group.Active = fx.Cs;
                await Settle(w);
                await PaletteShots(w, id);
                await SettingsShots(w, id);
                await MarkdownShots(w, fx, id);
            });

        // 5. a torn-off window (all top-level windows are saved)
        if (Want("11"))
        await Guard("tear-off", async () =>
        {
            await UseTheme(w, "aurora");
            group.Active = fx.Cs;
            w.MoveToNewWindow(group, fx.Md);
            await Settle(w, 200);
            var windows = App.Current.OpenWindows.ToList();
            var n = 0;
            foreach (var win in windows)
            {
                win.Left = -32000;
                win.Top = -32000;
                win.Width = 900;
                win.Height = 600;
                await Settle(win);
                await Save(win, $"11_window{++n}_aurora_en");
            }
            foreach (var win in windows.Where(x => !x.Model.IsMain)) win.Close();
            await Settle(w);
        });

        // 6. narrow windows: the title bar and the tab strip must shrink without anything overlapping
        foreach (var id in new[] { "aurora", "daylight" }.Where(_ => Want("15")))
            await Guard("narrow " + id, async () =>
            {
                await UseTheme(w, id);
                group.Active = fx.Cs;
                foreach (var width in new[] { 600, 820 })
                {
                    w.Width = width;
                    await Settle(w);
                    await Save(w, $"15_width{width}_{id}_en");
                }
                appearance.Language = "pl";
                w.Width = 600;
                await Settle(w);
                await Save(w, $"15_width600_{id}_pl");
                appearance.Language = "en";
                w.Width = 1100;
                await Settle(w);
            });

        // 7. binary file as hex and a non-viewable file (docx) as the Info tab with hashes and metadata
        if (Want("16"))
        await Guard("hex/info", async () =>
        {
            var dir = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures");
            var bin = Path.Combine(dir, "firmware.bin");
            var bytes = new byte[4096];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 % 256);
            Encoding.ASCII.GetBytes("BetterNotepad hex view - read only").CopyTo(bytes, 64);
            File.WriteAllBytes(bin, bytes);
            var docx = Path.Combine(dir, "report.docx");
            WriteDocx(docx);
            w.OpenFile(bin);
            await Settle(w, 200);
            foreach (var id in new[] { "aurora", "daylight" })
            {
                await UseTheme(w, id);
                await Settle(w, 120);
                await Save(w, $"16_hex_{id}_en");
            }
            w.OpenFile(docx);
            await Settle(w, 200);
            if (w.ActiveView is Modules.Info.InfoView info)
            {
                for (var i = 0; i < 50 && !info.MetadataReady; i++) await Task.Delay(40);
                await info.ComputeHashesAsync();
            }
            foreach (var id in new[] { "aurora", "daylight" })
            {
                await UseTheme(w, id);
                await Settle(w, 120);
                await Save(w, $"16_info_{id}_en");
            }
            appearance.Language = "pl";
            await Settle(w, 120);
            await Save(w, "16_info_daylight_pl");
            if (w.ActiveView is Modules.Info.InfoView iv && iv.Children[0] is ScrollViewer sv)
            {
                sv.ScrollToEnd();
                await Settle(w, 120);
                await Save(w, "16_info_meta_daylight_pl");
                sv.ScrollToHome();
            }
            appearance.Language = "en";
            await Settle(w);
        });

        // 8. PDF: pages, thumbnails, search highlights, a selection across two pages
        if (Want("17"))
        await Guard("pdf", async () =>
        {
            var pdf = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures", "manual.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            Modules.Pdf.PdfFixtures.WriteManual(pdf, pages: 5);
            w.OpenFile(pdf);
            await Settle(w, 300);
            if (w.ActiveView is Modules.Pdf.PdfDocumentView pv)
            {
                for (var i = 0; i < 100 && pv.Handle is null; i++) await Task.Delay(50);
                await Task.Delay(600);
                await pv.Search("BetterNotepad");
                await Task.Delay(400);
                if (pv.Handle is null) Log.Error("SNAPSHOT FAIL: pdf did not open");
                else if (pv.Hits.Count == 0) Log.Error("SNAPSHOT FAIL: pdf search found nothing");
            }
            foreach (var id in new[] { "aurora", "daylight" })
            {
                await UseTheme(w, id);
                await Settle(w, 300);
                await Save(w, $"17_pdf_{id}_en");
            }
            appearance.Language = "pl";
            await Settle(w, 200);
            await Save(w, "17_pdf_daylight_pl");
            appearance.Language = "en";
            await Settle(w);

            // from line 18 of page 4 to line 6 of page 5, zoomed out so that the page break is in view
            if (w.ActiveView is Modules.Pdf.PdfDocumentView sv && sv.Handle is not null)
            {
                await sv.Search("");
                for (var i = 0; i < 12 && sv.ZoomFactor > 0.51; i++) sv.ZoomStep(-1);
                await Settle(w, 200);
                sv.ScrollToPage(3, sv.Handle.PageSizes[3].Height * sv.ZoomFactor * 96 / 72 * 0.45);
                await Settle(w, 300);
                var t3 = (await sv.Handle.GetPageTextLayoutAsync(3)).Text;
                var t4 = (await sv.Handle.GetPageTextLayoutAsync(4)).Text;
                sv.SelectRange(3, t3.IndexOf("Line 18 of", StringComparison.Ordinal) + 5, 4, t4.IndexOf("Line 6 of", StringComparison.Ordinal) + 24);
                var (text, first, last, _) = await sv.SelectedTextAsync();
                if (first != 3 || last != 4 || !text.Contains("18 of chapter 4") || !text.Contains("Chapter 5")) Failures.Add($"pdf selection text: {first}-{last} '{text}'");
                foreach (var id in new[] { "aurora", "daylight" })
                {
                    await UseTheme(w, id);
                    await Settle(w, 400);
                    if (!sv.PagesWithSelectionDrawn.Contains(3) || !sv.PagesWithSelectionDrawn.Contains(4)) Failures.Add("pdf selection drawn on pages " + string.Join(",", sv.PagesWithSelectionDrawn));
                    await Save(w, $"17_pdf_selection_{id}_en");
                }
                sv.ClearSelection();
                sv.ZoomReset();
                await Settle(w);
            }
        });

        // 9. password-protected PDF: prompt, wrong password (inline error), shown password, locked tab, unlocked
        if (Want("18"))
        await Guard("pdf password", async () =>
        {
            var path = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures", "secret.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Modules.Pdf.PdfFixtures.WriteProtected(path);
            await UseTheme(w, "aurora");
            w.OpenFile(path);
            var pv = await WaitFor(() => w.ActiveView is Modules.Pdf.PdfDocumentView v && v.Prompt is not null && w.Dialogs.IsOpen ? v : null)
                     ?? throw new InvalidOperationException("no password prompt");
            await Settle(w, 250);
            await Save(w, "18_pdf_password_aurora_en");
            pv.Prompt!.SetPassword("guess");
            PressDialogButton(w, Loc.T("pdf.pw.open"));
            if (await WaitFor(() => pv.Prompt?.ErrorShown == true ? pv : null) is null) Failures.Add("wrong password: no inline error");
            await Settle(w, 150);
            await Save(w, "18_pdf_password_error_aurora_en");
            pv.Prompt?.SetRevealed(true);
            await Settle(w, 120);
            await Save(w, "18_pdf_password_reveal_aurora_en");
            PressDialogButton(w, Loc.T("common.cancel"));
            if (await WaitFor(() => pv.IsLocked && !w.Dialogs.IsOpen ? pv : null) is null) Failures.Add("cancel: tab not locked");
            await Settle(w, 300);
            await Save(w, "18_pdf_locked_aurora_en");

            await UseTheme(w, "daylight");
            appearance.Language = "pl";
            await Settle(w, 200);
            await Save(w, "18_pdf_locked_daylight_pl");
            _ = pv.UnlockAsync();
            if (await WaitFor(() => pv.Prompt is not null && w.Dialogs.IsOpen ? pv : null) is null) throw new InvalidOperationException("unlock: no prompt");
            pv.Prompt!.SetPassword("zgadnij");
            PressDialogButton(w, Loc.T("pdf.pw.open"));
            await WaitFor(() => pv.Prompt?.ErrorShown == true ? pv : null);
            await Settle(w, 150);
            await Save(w, "18_pdf_password_error_daylight_pl");
            pv.Prompt!.SetPassword(Modules.Pdf.PdfFixtures.Password);
            PressDialogButton(w, Loc.T("pdf.pw.open"));
            if (await WaitFor(() => pv.Handle is not null && !w.Dialogs.IsOpen ? pv : null) is null) Failures.Add("right password did not open");
            await Settle(w, 600);
            await Save(w, "18_pdf_unlocked_daylight_pl");
            appearance.Language = "en";
            await Settle(w);
        });

        // 10. title bar: caption buttons 46 x 40 flush with the top / right edges (normal and maximized), glyphs on one axis
        if (Want("19"))
        await Guard("titlebar", async () =>
        {
            foreach (var id in new[] { "aurora", "daylight" })
            {
                await UseTheme(w, id);
                foreach (var max in new[] { false, true })
                {
                    w.WindowState = max ? WindowState.Maximized : WindowState.Normal;
                    await Settle(w, 400);
                    CheckTitleBar(w, max);
                    w.MinButton.Tag = "hot";
                    w.CloseButton.Tag = "hot";
                    await Settle(w, 60);
                    SaveRegion(w, $"19_titlebar_{(max ? "max" : "normal")}_{id}_en", new Rect(w.ActualWidth - 460, 0, 460, 100), 2);
                    w.MinButton.Tag = null;
                    w.CloseButton.Tag = null;
                }
                w.WindowState = WindowState.Normal;
                await Settle(w, 300);
            }
        });

        // 11. pinned on top: compact window with the hover bar, the ghost-opacity popover, ghost mode (composited over
        //     the main window with its real opacity: a render of the visual tree cannot show the layered alpha itself)
        if (Want("20"))
        await Guard("pinned", async () => await PinnedShots(w, fx));

        // reference sheets of icon glyphs (picking icons without guessing)
        await Guard("glyphs", async () =>
        {
            await UseTheme(w, "aurora");
            GlyphSheet(0xE700, "99_glyphs_e700");
            GlyphSheet(0xE900, "99_glyphs_e900");
        });
    }

    private static async Task PinnedShots(MainWindow w, Fixtures fx)
    {
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var pdf = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures", "lab-manual.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
        Modules.Pdf.PdfFixtures.WriteManual(pdf, pages: 3);
        w.Width = 1100;
        w.Height = 700;
        w.OpenFile(pdf);
        await Settle(w, 200);
        var group = w.Model.ActiveGroup;
        if (w.ActiveDocument is not { } pdfDoc) throw new InvalidOperationException("pdf not opened");

        MainWindow Pin(Document doc, double width, double height)
        {
            var g = w.Model.Groups.First(x => x.Tabs.Contains(doc));
            w.PinOnTop(g, doc);
            var model = Workspace.I.Windows.First(m => m.Pinned && m.AllDocuments.Contains(doc));
            var win = App.Current.WindowFor(model)!;
            win.Left = -32000;
            win.Top = -32000;
            win.Width = width;
            win.Height = height;
            return win;
        }

        var pdfWin = Pin(pdfDoc, 500, 640);
        var textWin = Pin(fx.Py, 460, 360);
        await Settle(pdfWin, 300);
        if (pdfWin.ActiveView is Modules.Pdf.PdfDocumentView pv)
        {
            for (var i = 0; i < 100 && pv.Handle is null; i++) await Task.Delay(50);
            await Task.Delay(500);
        }
        foreach (var id in new[] { "aurora", "daylight" })
        {
            await UseTheme(w, id);
            pdfWin.Toasts.Children.Clear();
            textWin.Toasts.Children.Clear();
            pdfWin.ShowPinBar();
            await Settle(pdfWin, 300);
            await Save(pdfWin, $"20_pinned_pdf_bar_{id}_en");
            pdfWin.HidePinBar(animate: false);
            await Settle(pdfWin, 200);
            await Save(pdfWin, $"20_pinned_pdf_{id}_en");

            textWin.ShowPinBar();
            var card = PinnedBar.CreateOpacityCard(textWin);
            card.HorizontalAlignment = HorizontalAlignment.Right;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Margin = new Thickness(0, 0, 8, 0);
            textWin.OverlayLayer.Children.Add(card);
            await Settle(textWin, 120);
            await Save(textWin, $"20_pinned_opacity_{id}_en");
            textWin.OverlayLayer.Children.Remove(card);
            textWin.HidePinBar(animate: false);

            pdfWin.SetGhost(true);
            await Settle(pdfWin, 200);
            await SaveGhost(w, pdfWin, $"20_ghost_{id}_en");
            if (id == "daylight")
            {
                pdfWin.SetGhost(false);
                pdfWin.Toasts.Children.Clear();
                appearance.Language = "pl";
                await Settle(pdfWin, 120);
                pdfWin.SetGhost(true);
                await Settle(pdfWin, 200);
                await SaveGhost(w, pdfWin, "20_ghost_daylight_pl");
                appearance.Language = "en";
            }
            pdfWin.SetGhost(false);
        }
        textWin.UnpinFromTop();
        pdfWin.UnpinFromTop();
        await Settle(w);
        group.Active = fx.Cs;
        await Settle(w);
    }

    /// <summary>A ghost window over the main window (what the user sees through it).</summary>
    private static async Task SaveGhost(MainWindow main, MainWindow ghost, string name)
    {
        await Settle(main, 40);
        await Settle(ghost, 40);
        static RenderTargetBitmap Render(FrameworkElement e)
        {
            e.UpdateLayout();
            var b = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * Scale), (int)Math.Ceiling(e.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            b.Render(e);
            return b;
        }
        var back = Render(main);
        var front = Render(ghost);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(back, new Rect(0, 0, main.ActualWidth, main.ActualHeight));
            // the ghost's card is already translucent (per-pixel alpha), its badge and toasts are not: draw it as is
            dc.DrawImage(front, new Rect(main.ActualWidth - ghost.ActualWidth - 36, 72, ghost.ActualWidth, ghost.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(back.PixelWidth, back.PixelHeight, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        WritePng(bmp, name);
    }

    private static async Task PaletteShots(MainWindow w, string id)
    {
        foreach (var (name, mode, text) in new (string, PaletteMode, string?)[]
                 {
                     ("commands", PaletteMode.Commands, null), ("commands_query", PaletteMode.Commands, "sort"),
                     ("files", PaletteMode.Files, null), ("symbols", PaletteMode.Symbols, null),
                 })
        {
            CommandPalette.Show(w, mode, text);
            await Settle(w, 250);
            await Save(w, $"12_palette_{name}_{id}_en");
            CommandPalette.Close(w);
            await Settle(w, 120);
        }
        var switcher = TabSwitcher.Open(w, reverse: false, held: true, showNow: true);
        await Settle(w, 250);
        await Save(w, $"12_palette_switcher_{id}_en");
        switcher.Cancel();
        await Settle(w, 120);
    }

    private static async Task SettingsShots(MainWindow w, string id)
    {
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        foreach (var lang in new[] { "en", "pl" })
        {
            appearance.Language = lang;
            await Settle(w);
            SettingsPage.Show(w, SettingsPageIds.Appearance);
            await Settle(w, 300);
            var page = SettingsPage.For(w) ?? throw new InvalidOperationException("settings page did not open");
            await Save(w, $"13_settings_appearance_{id}_{lang}");
            if (lang == "en")
                foreach (var pageId in new[] { SettingsPageIds.Shortcuts, SettingsPageIds.Editor, SettingsPageIds.About })
                {
                    page.Navigate(pageId);
                    await Settle(w, 300);
                    await Save(w, $"13_settings_{pageId}_{id}_en");
                }
            page.Close();
            await Settle(w, 300);
        }
        appearance.Language = "en";
        await Settle(w);
    }

    private static async Task MarkdownShots(MainWindow w, Fixtures fx, string id)
    {
        var group = w.Model.ActiveGroup;
        group.Active = fx.Md;
        fx.Md.ViewMode = ViewMode.Split;
        await Settle(w, 350);
        await Save(w, $"14_md_split_{id}_en");

        // preview only, with the table of contents
        fx.Md.ViewMode = ViewMode.Preview;
        await Settle(w, 200);
        var md = (w.ActiveView as Documents.Views.TextDocumentView)?.Preview as Modules.Markdown.MarkdownPreviewView
                 ?? throw new InvalidOperationException("no Markdown preview");
        if (!md.TocOpen) md.ToggleToc();
        await Settle(w, 300);
        await Save(w, $"14_md_preview_toc_{id}_en");
        md.ToggleToc();
        fx.Md.ViewMode = ViewMode.Split;

        // the same file in a narrow right group next to code (split window)
        group.Active = fx.Cs;
        w.Split(Orientation.Horizontal);
        var second = w.Model.Groups[1];
        second.Add(fx.Md);
        await Settle(w, 350);
        await Save(w, $"14_md_split_group_{id}_en");
        w.Unsplit();
        await Settle(w);
        group.Active = fx.Cs;
        await Settle(w);
    }

    /// <summary>A sheet of 512 Segoe Fluent Icons code points with their hex codes.</summary>
    private static void GlyphSheet(int start, string name)
    {
        var pal = ThemeManager.I.Current;
        const int cols = 32, rows = 16;
        const double cell = 46;
        var dv = new DrawingVisual();
        var icons = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ui = new Typeface("Segoe UI");
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(pal.Bg), null, new Rect(0, 0, cols * cell, rows * cell));
            for (var i = 0; i < cols * rows; i++)
            {
                var x = i % cols * cell;
                var y = i / cols * cell;
                var cp = start + i;
                var glyph = new FormattedText(char.ConvertFromUtf32(cp), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, icons, 18, new SolidColorBrush(pal.Text), 1.25);
                dc.DrawText(glyph, new Point(x + (cell - glyph.Width) / 2, y + 5));
                var label = new FormattedText(cp.ToString("X4"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ui, 9, new SolidColorBrush(pal.TextDim), 1.25);
                dc.DrawText(label, new Point(x + (cell - label.Width) / 2, y + cell - 14));
            }
        }
        var bmp = new RenderTargetBitmap((int)(cols * cell * Scale), (int)(rows * cell * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        WritePng(bmp, name);
    }

    /// <summary>WCAG contrast of text colors against their background, per theme (contrast.txt); weak pairs are marked.</summary>
    private static void WriteContrastReport()
    {
        static double Lum(Color c)
        {
            static double L(byte v)
            {
                var x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
        }
        static double Ratio(Color a, Color b)
        {
            var (hi, lo) = (Math.Max(Lum(a), Lum(b)), Math.Min(Lum(a), Lum(b)));
            return (hi + 0.05) / (lo + 0.05);
        }

        var sb = new StringBuilder();
        foreach (var p in Palettes.All)
        {
            sb.AppendLine($"== {p.Id} ==");
            void Pair(string what, Color fg, Color bg, double min)
            {
                var r = Ratio(fg, bg);
                sb.AppendLine($"{(r < min ? "LOW " : "ok  ")}{r,5:0.0}  {what}");
            }
            Pair("Text on Bg", p.Text, p.Bg, 7);
            Pair("TextDim on Bg", p.TextDim, p.Bg, 4.5);
            Pair("TextFaint on Bg", p.TextFaint, p.Bg, 3);
            Pair("OnAccent on Accent", p.OnAccent, p.Accent, 3); // button labels are semi-bold, 13 px+
            foreach (var g in p.AccentGradient) Pair("OnAccent on raw gradient stop (decor only)", p.OnAccent, g, 0);
            foreach (var g in ThemeManager.FillStops(p)) Pair("OnAccent on AccentFill stop", p.OnAccent, g, 3);
            Pair("PanelText on Panel", p.PanelText, p.Panel, 7);
            Pair("PanelTextDim on Panel", p.PanelTextDim, p.Panel, 4.5);
            Pair("Danger on Panel", p.Danger, p.Panel, 3);
            Pair("Warning on Panel", p.Warning, p.Panel, 3);
            Pair("Success on Panel", p.Success, p.Panel, 3);
            Pair("EditorText on EditorBg", p.EditorText, p.EditorBg, 7);
            Pair("LineNumber on EditorBg", p.LineNumber, p.EditorBg, 2.5);
            Pair("LineNumberActive on EditorBg", p.LineNumberActive, p.EditorBg, 4.5);
            Pair("EditorText on Selection (over EditorBg)", p.EditorText, ThemeManager.Blend(p.EditorBg, Color.FromRgb(p.Selection.R, p.Selection.G, p.Selection.B), p.Selection.A / 255.0), 4.5);
            foreach (var (token, style) in ThemeManager.SyntaxFor(p))
                Pair($"syntax {token}", style.Color, p.EditorBg, token is SyntaxToken.Comment or SyntaxToken.DocComment or SyntaxToken.Quote or SyntaxToken.Punctuation or SyntaxToken.LogTrace or SyntaxToken.LogTimestamp ? 3 : 4.5);
        }
        File.WriteAllText(Path.Combine(_dir, "contrast.txt"), sb.ToString());
    }

    private static async Task<T?> WaitFor<T>(Func<T?> probe, int timeoutMs = 6000) where T : class
    {
        for (var waited = 0; waited < timeoutMs; waited += 50)
        {
            if (probe() is { } hit) return hit;
            await Task.Delay(50);
        }
        return probe();
    }

    /// <summary>Clicks a button of the open in-window dialog by its text (no input devices involved).</summary>
    internal static void PressDialogButton(MainWindow w, string text)
    {
        var button = Descendants<Button>(w.DialogLayer).LastOrDefault(b => b.Content as string == text)
                     ?? throw new InvalidOperationException($"dialog button '{text}' not found");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>
    /// Caption buttons fill the 40 px title row (46 x 40, flush with the top and right edges), the maximized window
    /// covers exactly the monitor's work area, and every chrome glyph is centered on the same horizontal axis.
    /// </summary>
    private static void CheckTitleBar(MainWindow w, bool maximized)
    {
        var state = maximized ? "max" : "normal";
        foreach (var b in new FrameworkElement[] { w.MinButton, w.MaxButton, w.CloseButton })
        {
            var at = b.TranslatePoint(new Point(0, 0), w);
            if (Math.Abs(b.ActualWidth - 46) > 0.01 || Math.Abs(b.ActualHeight - 40) > 0.01 || Math.Abs(at.Y) > 0.01)
                Failures.Add($"titlebar {state}: {b.Name} {b.ActualWidth}x{b.ActualHeight} at y {at.Y}");
        }
        var closeRight = w.CloseButton.TranslatePoint(new Point(w.CloseButton.ActualWidth, 0), w).X;
        if (Math.Abs(closeRight - w.ActualWidth) > 0.01) Failures.Add($"titlebar {state}: close ends at {closeRight}, window {w.ActualWidth}");

        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (Native.GetWindowRect(hwnd, out var wr))
        {
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref info);
            var screen = w.CloseButton.PointToScreen(new Point(w.CloseButton.ActualWidth, 0));
            Log.Info($"titlebar {state}: window [{wr.Left},{wr.Top},{wr.Right},{wr.Bottom}] work [{info.rcWork.Left},{info.rcWork.Top},{info.rcWork.Right},{info.rcWork.Bottom}] close top-right {screen}");
            if (maximized && (wr.Left != info.rcWork.Left || wr.Top != info.rcWork.Top || wr.Right != info.rcWork.Right || wr.Bottom != info.rcWork.Bottom))
                Failures.Add($"titlebar max: window rect [{wr.Left},{wr.Top},{wr.Right},{wr.Bottom}] is not the work area [{info.rcWork.Left},{info.rcWork.Top},{info.rcWork.Right},{info.rcWork.Bottom}]");
            if (maximized && (Math.Abs(screen.X - info.rcWork.Right) > 1 || Math.Abs(screen.Y - info.rcWork.Top) > 1))
                Failures.Add($"titlebar max: close button top-right {screen} is not the work area corner");
        }

        // ink of every glyph (no hover): vertical center in DIPs
        const double k = 4;
        var bar = w.TitleBar;
        var pinVisibility = w.PinButton.Visibility; // shown on torn-off / pinned windows: measured too
        w.PinButton.Visibility = Visibility.Visible;
        bar.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth * k), (int)Math.Ceiling(bar.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        bmp.Render(bar);
        var stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);
        double? InkCenter(FrameworkElement e)
        {
            if (!e.IsVisible) return null;
            var r = e.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            int top = -1, bottom = -1;
            for (var y = Math.Max(0, (int)(r.Top * k)); y < Math.Min(bmp.PixelHeight, (int)(r.Bottom * k)); y++)
                for (var x = Math.Max(0, (int)(r.Left * k)); x < Math.Min(bmp.PixelWidth, (int)(r.Right * k)); x++)
                    if (px[y * stride + x * 4 + 3] > 80)
                    {
                        if (top < 0) top = y;
                        bottom = y;
                        break;
                    }
            return top < 0 ? null : (top + bottom + 1) / 2.0 / k;
        }
        var centers = new (string Name, double? Y)[]
        {
            ("pin", InkCenter(w.PinButton)), ("settings", InkCenter(w.SettingsButton)),
            ("min", InkCenter(w.MinButton)), ("max", InkCenter(w.MaxButton)), ("close", InkCenter(w.CloseButton)),
        };
        w.PinButton.Visibility = pinVisibility;
        Log.Info($"titlebar {state}: glyph centers " + string.Join(", ", centers.Select(c => $"{c.Name}={c.Y:0.00}")));
        foreach (var (name, y) in centers)
            if (y is { } v && Math.Abs(v - 20) > 0.6) Failures.Add($"titlebar {state}: {name} glyph centered at {v:0.00}, not 20");
    }

    /// <summary>A region of a window at a larger scale (detail views of small chrome).</summary>
    private static void SaveRegion(MainWindow w, string name, Rect region, double scale)
    {
        w.UpdateLayout();
        var full = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth * scale), (int)Math.Ceiling(w.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        full.Render(w);
        var crop = new Int32Rect((int)(region.X * scale), (int)(region.Y * scale), (int)(region.Width * scale), (int)(region.Height * scale));
        crop.Width = Math.Min(crop.Width, full.PixelWidth - crop.X);
        crop.Height = Math.Min(crop.Height, full.PixelHeight - crop.Y);
        WritePng(new CroppedBitmap(full, crop), name);
    }

    private static async Task UseTheme(MainWindow w, string id)
    {
        SettingsService.I.Get<AppearanceSettings>().Theme = id;
        await Settle(w);
    }

    /// <summary>A minimal Office package with document properties (title, author, company…).</summary>
    private static void WriteDocx(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Put(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Put("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/><Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/></Types>""");
        Put("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/></Relationships>""");
        Put("word/document.xml", """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Quarterly report</w:t></w:r></w:p></w:body></w:document>""");
        Put("docProps/core.xml", """<?xml version="1.0" encoding="UTF-8"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>Quarterly report</dc:title><dc:creator>Jan Kowalski</dc:creator><cp:lastModifiedBy>Anna Nowak</cp:lastModifiedBy><cp:revision>7</cp:revision><dcterms:created xsi:type="dcterms:W3CDTF">2026-03-02T09:15:00Z</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">2026-10-01T16:40:00Z</dcterms:modified></cp:coreProperties>""");
        Put("docProps/app.xml", """<?xml version="1.0" encoding="UTF-8"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Microsoft Office Word</Application><Company>Contoso</Company><Pages>3</Pages><Words>812</Words></Properties>""");
    }

    private static async Task<Fixtures> OpenFixtures(MainWindow w)
    {
        var dir = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures");
        Directory.CreateDirectory(dir);
        var paths = new Dictionary<string, string>();
        foreach (var (name, text) in SnapshotFixtures.All)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, text, new UTF8Encoding(false));
            paths[name] = p;
        }

        // order of opening = order of tabs
        foreach (var name in new[] { "NoteBook.cs", "tools.py", "README.md", "config.json", "server.log", "boot.asm" }) w.OpenFile(paths[name]);
        var group = w.Model.ActiveGroup;
        TextDocument Doc(string name) => group.Tabs.OfType<TextDocument>().First(d => d.FilePath is { } fp && FileUtil.PathEquals(fp, paths[name]));
        var fx = new Fixtures(Doc("NoteBook.cs"), Doc("tools.py"), Doc("README.md"), Doc("config.json"), Doc("server.log"), Doc("boot.asm"));
        foreach (var d in new[] { fx.Cs, fx.Py, fx.Md, fx.Json, fx.Log, fx.Asm })
        {
            group.Active = d;
            await d.LoadAsync();
            await Settle(w, 60);
        }

        fx.Py.Buffer.Insert(0, "# edited, not saved yet\n"); // dirty tab
        fx.Md.IsPinned = true;                                 // pinned tab
        group.Normalize(fx.Md);
        fx.Json.IsLocked = true;                               // locked (read-only) tab
        fx.Log.TailMode = true;

        group.Active = fx.Cs;
        await Settle(w, 120);
        if (w.ActiveView?.Editor is { } ed)
        {
            var line = ed.Document.GetLineByNumber(Math.Min(18, ed.Document.LineCount));
            ed.CaretOffset = line.Offset + 12;
        }
        SelectClass(w);
        await Settle(w);
        return fx;
    }

    private static void SelectClass(MainWindow w)
    {
        if (w.ActiveView?.Editor is not { } ed) return;
        var cls = ed.Document.Text.IndexOf("NoteBook", StringComparison.Ordinal);
        if (cls >= 0) ed.Select(cls, "NoteBook".Length);
    }

    private static async Task Syntax(MainWindow w, Fixtures fx, string id)
    {
        // one sheet per theme: the top-left part of the editor of each language, real highlighting
        var docs = new (TextDocument Doc, string Label)[]
        {
            (fx.Cs, "C#"), (fx.Py, "Python"), (fx.Md, "Markdown"), (fx.Json, "JSON"), (fx.Log, "Log"), (fx.Asm, "Assembly x86"),
        };
        var panels = new List<(RenderTargetBitmap Bmp, string Label)>();
        var group = w.Model.ActiveGroup;
        foreach (var (doc, label) in docs)
        {
            group.Active = doc;
            await Settle(w);
            if (w.ActiveView?.Editor is not { } ed) continue;
            ed.ScrollToHome();
            await Settle(w, 40);
            var panelW = Math.Min(ed.ActualWidth, 640);
            var panelH = Math.Min(ed.ActualHeight, 360);
            var bmp = new RenderTargetBitmap((int)(panelW * Scale), (int)(panelH * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bmp.Render(ed);
            panels.Add((bmp, label));
        }
        group.Active = fx.Cs;

        var pal = ThemeManager.I.Current;
        const double pw = 640, ph = 360, gap = 14, head = 24;
        var cols = 3;
        var rows = (panels.Count + cols - 1) / cols;
        var width = cols * pw + (cols + 1) * gap;
        var height = rows * (ph + head) + (rows + 1) * gap;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(pal.Bg), null, new Rect(0, 0, width, height));
            for (var i = 0; i < panels.Count; i++)
            {
                var x = gap + i % cols * (pw + gap);
                var y = gap + i / cols * (ph + head + gap);
                var text = new FormattedText($"{pal.Id} · {panels[i].Label}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, new SolidColorBrush(pal.TextDim), 1.25);
                dc.DrawText(text, new Point(x, y + 2));
                var r = new Rect(x, y + head, pw, ph);
                dc.DrawRectangle(new SolidColorBrush(pal.EditorBg), new Pen(new SolidColorBrush(pal.Border), 1), r);
                dc.DrawImage(panels[i].Bmp, new Rect(x, y + head, panels[i].Bmp.Width, panels[i].Bmp.Height));
            }
        }
        var sheet = new RenderTargetBitmap((int)(width * Scale), (int)(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        sheet.Render(dv);
        WritePng(sheet, $"07_syntax_{id}_en");
    }

    private static async Task Split(MainWindow w, Fixtures fx, string id)
    {
        var group = w.Model.ActiveGroup;
        group.Active = fx.Cs;
        w.Split(Orientation.Horizontal);
        var second = w.Model.Groups[1];
        second.Add(fx.Json, activate: false);
        second.Add(fx.Log, activate: false);
        second.Add(fx.Py);
        await Settle(w, 150);
        await Save(w, $"03_split_{id}_en");
        w.Unsplit();
        await Settle(w);
        group.Active = fx.Cs;
    }

    private static async Task FindBar(MainWindow w, Fixtures fx, string id, string lang)
    {
        w.Model.ActiveGroup.Active = fx.Cs;
        await Settle(w);
        if (w.ActiveView is not Documents.Views.TextDocumentView { Find: { } find }) throw new InvalidOperationException("no find bar");
        find.Open(replace: true);
        find.Query.Text = "string";
        await Settle(w, 200);
        find.Query.Select(find.Query.Text.Length, 0); // a selection would be drawn without its highlight in a window that is never active
        await Save(w, $"04_find_{id}_{lang}");
        find.Close();
        await Settle(w);
    }

    private static async Task CloseDialog(MainWindow w, Fixtures fx, string id, string lang)
    {
        var group = w.Model.ActiveGroup;
        group.Active = fx.Py; // the dirty tab
        await Settle(w);
        var closing = w.CloseTabAsync(group, fx.Py); // shows the "save changes?" dialog
        await Settle(w, 150);
        await Save(w, $"05_dialog_{id}_{lang}");
        ClickDialogButton(w, Loc.T("common.cancel"));
        await closing;
        await Settle(w);
        group.Active = fx.Cs;
    }

    private static async Task ExternalDialog(MainWindow w, Fixtures fx, string id, string lang)
    {
        var shown = w.DialogLayer.Show(Loc.T("external.modifiedTitle", "server.log"), Loc.T("external.modifiedText"),
        [
            new DialogButton("reload", Loc.T("external.reload"), DialogButtonKind.Primary, IsDefault: true),
            new DialogButton("keep", Loc.T("external.keep")),
            new DialogButton("compare", Loc.T("external.compare"), DialogButtonKind.Ghost),
        ], glyph: Glyphs.Refresh);
        await Settle(w, 150);
        await Save(w, $"08_dialog_external_{id}_{lang}");
        ClickDialogButton(w, Loc.T("external.keep"));
        await shown;
        await Settle(w);
    }

    private static void ClickDialogButton(MainWindow w, string text)
    {
        var button = Descendants<Button>(w.DialogLayer).FirstOrDefault(b => b.Content as string == text)
            ?? throw new InvalidOperationException("dialog button not found: " + text);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static async Task Toasts(MainWindow w, string id)
    {
        w.Toasts.Show(Loc.T("save.done", "NoteBook.cs"), ToastKind.Success, seconds: 600);
        w.Toasts.Show(Loc.T("save.locked"), ToastKind.Warning, Loc.T("save.unlock"), () => { }, seconds: 600);
        w.Toasts.Show(Loc.T("error.unexpected", "disk full"), ToastKind.Error, seconds: 600);
        await Settle(w, 150);
        await Save(w, $"06_toasts_{id}_en");
        w.Toasts.Children.Clear();
        w.Toasts.Show(Loc.T("lock.on", "config.json"), ToastKind.Info, seconds: 600);
        await Settle(w, 100);
        await Save(w, $"06_toast_info_{id}_en");
        w.Toasts.Children.Clear();
        await Settle(w);
    }

    /// <summary>Top menu (File) and a tab's context menu: popups are separate HWNDs on the hidden desktop; their content is rendered directly.</summary>
    private static async Task Menus(MainWindow w, Fixtures fx, string id)
    {
        // a top-level menu does not stay open in a window that is never activated, so its items are shown in a context menu (same styles)
        foreach (var top in w.TopMenu.Items.OfType<MenuItem>().Take(2))
        {
            top.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, top));
            var items = top.Items.Cast<object>().ToList();
            top.Items.Clear();
            var cm = new ContextMenu { PlacementTarget = w.ActiveGroupView, Placement = PlacementMode.Center };
            foreach (var item in items) cm.Items.Add(item);
            cm.IsOpen = true;
            await Settle(w, 200);
            SaveElement(cm, $"09_menu_{(top.Header as string ?? "x").ToLowerInvariant()}_{id}_en");
            cm.IsOpen = false;
            cm.Items.Clear();
            foreach (var item in items) top.Items.Add(item);
            await Settle(w);
        }

        var menu = w.BuildTabMenu(w.Model.ActiveGroup, fx.Cs);
        var chip = w.ActiveGroupView?.ChipFor(fx.Cs);
        if (chip is not null)
        {
            menu.PlacementTarget = chip;
            menu.IsOpen = true;
            await Settle(w, 200);
            SaveElement(menu, $"10_menu_tab_{id}_en");
            menu.IsOpen = false;
            await Settle(w);
        }
    }

    // ───────────── rendering ─────────────

    private static async Task Settle(Window w, int delayMs = 80)
    {
        for (var i = 0; i < 2; i++)
        {
            await w.Dispatcher.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
            await Task.Delay(delayMs);
            await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        w.UpdateLayout();
    }

    private static async Task Save(MainWindow w, string name)
    {
        await Settle(w, 40);
        w.UpdateLayout();
        SaveElement(w, name);
    }

    private static void SaveElement(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var width = Math.Max(1, element.ActualWidth);
        var height = Math.Max(1, element.ActualHeight);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * Scale), (int)Math.Ceiling(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        WritePng(bmp, name);
    }

    private static void WritePng(BitmapSource bmp, string name)
    {
        var path = Path.Combine(_dir, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) encoder.Save(fs);
        Files.Add(name + ".png");
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
