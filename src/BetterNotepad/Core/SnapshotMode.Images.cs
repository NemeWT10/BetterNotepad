using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Imaging;
using BetterNotepad.Modules.Imaging;
using BetterNotepad.UI;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Core;

internal static partial class SnapshotMode
{
    /// <summary>
    /// 24. Images: the viewer (photo, transparency, GIF, a broken file), editing with a tool active and its colour panel, a crop
    /// in progress, the info panel with the photo's EXIF / GPS, the context menu, the JPEG quality and "saved without metadata"
    /// dialogs, a picture pinned on top (plain and with its hover toolbar), the toolbar's line icons at 3×.
    /// </summary>
    private static async Task ImageShots(MainWindow w)
    {
        var appearance = SettingsService.I.Get<AppearanceSettings>();
        var dir = Path.Combine(AppEnv.LocalDir, "snapshot-fixtures", "img");
        Directory.CreateDirectory(dir);
        string F(string name) => Path.Combine(dir, name);
        ImagingFixtures.WritePhoto(F("golden-hour.jpg"));
        ImagingFixtures.WriteLogo(F("logo.png"));
        ImagingFixtures.WriteAnimatedGif(F("loading.gif"));
        ImagingFixtures.WriteBroken(F("scan-0042.png"));
        w.Width = 1100;
        w.Height = 700;
        var group = w.Model.ActiveGroup;
        var before = group.Active;
        var opened = new List<Document>();

        async Task<ImageDocumentView> Show(string path, Func<ImageDocumentView, bool>? ready = null)
        {
            w.OpenFile(path);
            ready ??= v => v.Viewer.HasPicture;
            for (var i = 0; i < 200; i++)
            {
                if (w.ActiveView is ImageDocumentView v && FileUtil.PathEquals(v.Document.FilePath, path) && ready(v))
                {
                    if (!opened.Contains(v.Document)) opened.Add(v.Document);
                    await Settle(w, 120);
                    return v;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException("image did not open: " + path);
        }

        // ── viewing: aurora EN, daylight PL (status bar: size, file size, format, zoom) ──
        foreach (var (theme, lang) in new[] { ("aurora", "en"), ("daylight", "pl"), ("graphite", "en") })
        {
            appearance.Language = lang;
            await UseTheme(w, theme);
            var v = await Show(F("golden-hour.jpg"));
            v.Viewer.ZoomToFit(animate: false);
            await Save(w, $"24_image_view_{theme}_{lang}");
        }
        appearance.Language = "en";
        await UseTheme(w, "aurora");

        var photo = await Show(F("golden-hour.jpg"));
        var viewer = photo.Viewer;
        var doc = (ImageDocument)photo.Document;

        // ── info panel: the file, hashes on demand, the camera and GPS of the photo ──
        foreach (var (theme, lang) in new[] { ("aurora", "en"), ("daylight", "pl") })
        {
            appearance.Language = lang;
            await UseTheme(w, theme);
            photo.SetInfo(true);
            for (var i = 0; i < 120 && photo.InfoPanel?.MetadataReady != true; i++) await Task.Delay(50);
            await Settle(w, 200);
            await Save(w, $"24_image_info_{theme}_{lang}");
            // scrolled to the camera / GPS entries
            if (photo.InfoPanel is { } info && Descendants<ScrollViewer>(info).FirstOrDefault() is { } scroll)
            {
                scroll.ScrollToVerticalOffset(330);
                await Settle(w, 150);
                await Save(w, $"24_image_info_meta_{theme}_{lang}");
            }
            photo.SetInfo(false);
        }
        appearance.Language = "en";
        await UseTheme(w, "aurora");

        // ── context menu (drawn over the window where it opens) ──
        var menu = new ContextMenu { PlacementTarget = viewer, Placement = PlacementMode.RelativePoint, HorizontalOffset = 540, VerticalOffset = 250 };
        photo.FillMenuForTest(menu);
        menu.IsOpen = true;
        await Settle(w, 200);
        await SaveWithPopup(w, menu, new Point(540, 250), "24_image_menu_aurora_en");
        menu.IsOpen = false;
        await Settle(w);

        // ── editing: drawings in the photo, the rectangle tool active ──
        Point S(double x, double y) => viewer.ImageToStage(new Point(x, y));
        void Drag(EditTool tool, Point a, Point b, params Point[] via)
        {
            viewer.SetTool(tool);
            viewer.PointerDown(a, 1, false);
            foreach (var p in via) viewer.PointerMove(p, false);
            viewer.PointerMove(b, false);
            viewer.PointerUp(b, false);
        }
        viewer.BeginEdit();
        await Settle(w);
        viewer.SetColor(0);
        viewer.SetSize(1);
        Drag(EditTool.Arrow, S(1150, 1500), S(1700, 1180));
        viewer.SetColor(2);
        Drag(EditTool.Rectangle, S(1380, 830), S(2420, 1680));
        viewer.SetColor(7);
        viewer.SetSize(2);
        viewer.SetTool(EditTool.Text);
        viewer.PointerDown(S(260, 260), 1, false);
        viewer.TypeText("Golden hour");
        viewer.SetColor(2);
        viewer.SetSize(1);
        Drag(EditTool.Highlighter, S(260, 470), S(1100, 470));
        Drag(EditTool.Pixelate, S(2550, 1760), S(2950, 1960));
        viewer.SetColor(2);
        viewer.SetSize(1);
        viewer.SetTool(EditTool.Rectangle);
        await Settle(w, 150);
        foreach (var (theme, lang) in new[] { ("aurora", "en"), ("daylight", "pl"), ("sunset", "en") })
        {
            appearance.Language = lang;
            await UseTheme(w, theme);
            await Save(w, $"24_image_edit_{theme}_{lang}");
        }
        appearance.Language = "en";
        await UseTheme(w, "aurora");

        // the colour and thickness panel
        viewer.OpenStylePanel();
        await Settle(w, 150);
        await Save(w, "24_image_style_aurora_en");
        viewer.OpenStylePanel();
        await UseTheme(w, "daylight");
        viewer.OpenStylePanel();
        await Settle(w, 150);
        await Save(w, "24_image_style_daylight_en");
        viewer.OpenStylePanel();

        // ── crop in progress: the frame pulled in from two corners ──
        foreach (var theme in new[] { "daylight", "aurora" })
        {
            await UseTheme(w, theme);
            viewer.SetTool(EditTool.Crop);
            viewer.PointerDown(S(0, 0), 1, false);
            viewer.PointerMove(S(420, 260), false);
            viewer.PointerUp(S(420, 260), false);
            viewer.PointerDown(S(3000, 2000), 1, false);
            viewer.PointerMove(S(2640, 1820), false);
            viewer.PointerUp(S(2640, 1820), false);
            await Settle(w, 150);
            await Save(w, $"24_image_crop_{theme}_en");
            viewer.SetTool(EditTool.None);
        }

        // the toolbar's line icons, large (icon review)
        RenderElement(viewer.ToolbarElement, "24_image_toolbar_icons_x3", 3);
        viewer.EndEdit();
        await Settle(w);
        RenderElement(viewer.ToolbarElement, "24_image_viewbar_icons_x3", 3);

        // ── dialogs: JPEG quality ("Save as" JPEG) and the one-time "saved without metadata" note ──
        foreach (var (theme, lang) in new[] { ("aurora", "en"), ("daylight", "pl") })
        {
            appearance.Language = lang;
            await UseTheme(w, theme);
            var asking = ImagingDialogs.AskJpegQuality(w, 92);
            await Settle(w, 200);
            await Save(w, $"24_image_jpeg_quality_{theme}_{lang}");
            ClickDialogButton(w, Loc.T("common.cancel"));
            await asking;
            await Settle(w);
            w.Toasts.Show(Loc.T("save.done", "golden-hour.jpg"), ToastKind.Success, seconds: 600);
            var note = ImagingDialogs.ShowMetadataNotice(w);
            await Settle(w, 200);
            await Save(w, $"24_image_meta_notice_{theme}_{lang}");
            ClickDialogButton(w, Loc.T("img.meta.ok"));
            await note;
            w.Toasts.Children.Clear();
            await Settle(w);
        }
        appearance.Language = "en";

        // the edits are not kept: the fixture stays as it is
        doc.Edits.Reset();
        await Settle(w);

        // ── transparency (checkerboard), an animated GIF, a file that is not a picture ──
        await UseTheme(w, "daylight");
        await Show(F("logo.png"));
        await Save(w, "24_image_alpha_daylight_en");
        await UseTheme(w, "aurora");
        await Show(F("logo.png"));
        await Save(w, "24_image_alpha_aurora_en");
        var gif = await Show(F("loading.gif"));
        gif.Viewer.ZoomToActual();
        await Settle(w, 300);
        await Save(w, "24_image_gif_aurora_en");
        appearance.Language = "pl";
        await Show(F("scan-0042.png"), v => v.Viewer.Message is not null);
        await Save(w, "24_image_failed_aurora_pl");
        appearance.Language = "en";

        // ── pinned on top: the picture alone; pointed at: the pin bar and the view toolbar (no editing) ──
        w.Model.ActiveGroup.Active = doc;
        await Settle(w, 150);
        var g = w.Model.Groups.First(x => x.Tabs.Contains(doc));
        w.PinOnTop(g, doc);
        var model = Workspace.I.Windows.FirstOrDefault(m => m.Pinned && m.AllDocuments.Contains(doc));
        if (model is not null && App.Current.WindowFor(model) is { } pin)
        {
            pin.Left = -32000;
            pin.Top = -32000;
            pin.Width = 540;
            pin.Height = 400;
            await Settle(pin, 300);
            for (var i = 0; i < 100 && (pin.ActiveView as ImageDocumentView)?.Viewer.HasPicture != true; i++) await Task.Delay(50);
            var pv = pin.ActiveView as ImageDocumentView;
            foreach (var theme in new[] { "aurora", "daylight" })
            {
                await UseTheme(w, theme);
                pin.Toasts.Children.Clear();
                pin.HidePinBar(animate: false);
                pv?.Viewer.RevealToolbarForTest(false);
                pv?.Viewer.ZoomToFit(animate: false);
                await Settle(pin, 200);
                await Save(pin, $"24_image_pinned_{theme}_en");
                pin.ShowPinBar();
                pv?.Viewer.RevealToolbarForTest(true);
                await Settle(pin, 300);
                await Save(pin, $"24_image_pinned_hover_{theme}_en");
            }
            pin.HidePinBar(animate: false);
            pin.UnpinFromTop();
            await Settle(w, 200);
        }
        await UseTheme(w, "aurora");

        // back to how the scenarios found the window
        foreach (var d in opened.Where(d => DocumentManager.I.Documents.Contains(d)).ToList())
            if (Workspace.I.Locate(d) is { } where) await w.CloseTabAsync(where.Group, d);
        w.Toasts.Children.Clear();
        if (before is not null && w.Model.AllDocuments.Contains(before)) w.Model.Groups.First(x => x.Tabs.Contains(before)).Active = before;
        await Settle(w);
    }

    /// <summary>One element on its own at a larger scale (icon review).</summary>
    private static void RenderElement(FrameworkElement element, string name, double scale)
    {
        element.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        WritePng(bmp, name);
    }
}
