#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterNotepad.Core;
using BetterNotepad.Core.Commands;
using BetterNotepad.Core.Localization;
using BetterNotepad.Core.Settings;
using BetterNotepad.Documents;
using BetterNotepad.Documents.Session;
using BetterNotepad.Imaging;
using BetterNotepad.UI.Windows;

namespace BetterNotepad.Modules.Imaging;

/// <summary>
/// Debug self-test of the image tabs (part of SelfTest, hidden desktop, no clipboard, no file dialogs): PNG / JPEG / animated
/// GIF / BMP / ICO open in the image view (a broken file shows a calm message), the zoom shortcuts drive it, rotate and undo,
/// an edit (rectangle + text) makes the tab unsaved, its backup is on disk and a session restore brings the edits back with
/// undo, Ctrl+S writes it without EXIF / GPS with the orientation in the pixels and shows the one-time note once, undo after
/// saving, "Save as" PNG / JPEG with quality, changes on disk (quiet reload / keep mine), close with "Don't save", tray release,
/// the info panel, a locked tab, a split, a tab pinned on top.
/// </summary>
internal static class ImagingSelfTest
{
    public static async Task Run(MainWindow w, List<string> failures)
    {
        void Check(bool ok, string what)
        {
            if (!ok) failures.Add("image: " + what);
        }

        // every wait of this test ends: a task that does not finish in time is a failure, not a hang
        async Task<T?> Within<T>(Task<T> task, string what, int ms = 20000)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)) == task) return await task;
            failures.Add($"image: timed out after {ms / 1000} s: {what}");
            Log.Error($"selftest: image: timed out: {what}");
            return default;
        }

        void Step(string what) => Log.Info("selftest: image: " + what);

        // An active window asks about changes on disk at once (the app's real prompt), and its folder watcher notices the
        // same change again a moment later; a window that was never activated leaves both to us. Either way no question may
        // stay open: a later dialog (Save?, one frame?) would queue behind it for good.
        async Task AnswerExternalPrompts()
        {
            await Task.Delay(450); // the watcher's events arrive coalesced (250 ms)
            for (var i = 0; i < 6; i++)
            {
                await Idle(w, 60);
                if (!await PressIfShown(w, Loc.T("external.keep")) && !await PressIfShown(w, Loc.T("external.keepInApp"))) return;
                await Task.Delay(300);
            }
        }

        var appearance = SettingsService.I.Get<AppearanceSettings>();
        appearance.Language = "en";
        var settings = SettingsService.I.Get<ImagingSettings>();
        settings.MetadataNoticeShown = false;
        var dir = Path.Combine(AppEnv.LocalDir, "selftest-img");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        string F(string name) => Path.Combine(dir, name);
        ImagingFixtures.WriteLogo(F("logo.png"));
        ImagingFixtures.WritePhoto(F("photo.jpg"));
        ImagingFixtures.WriteRotatedJpeg(F("rotated.jpg"));
        ImagingFixtures.WriteAnimatedGif(F("ball.gif"));
        ImagingFixtures.WriteBitmap(F("chart.bmp"));
        ImagingFixtures.WriteIcon(F("app.ico"));
        ImagingFixtures.WriteBroken(F("broken.png"));
        var group = w.Model.ActiveGroup;
        var before = group.Active;

        // ── every format opens in the image view (decoded off the UI thread) ──
        Step("every format opens in the image view (decoded off the UI thread)");
        var png = await Open(w, F("logo.png"));
        Check(png is not null && png.Document is ImageDocument { Kind: DocumentKind.Image, IsDirty: false }, "PNG opens as an image tab");
        if (png is null) return;
        Check(png.Viewer.Info is { Width: 640, Height: 420, Format: "PNG" }, $"PNG info {png.Viewer.Info}");
        Check(png.StatusText?.Contains("640 × 420 px") == true && png.StatusText.Contains("PNG"), "status bar: " + png.StatusText);
        Check(png.StatusZoom?.EndsWith('%') == true, "status zoom " + png.StatusZoom);

        // zoom shortcuts go to the picture, not to the editor zoom
        var fitZoom = png.Viewer.ZoomPercent;
        Check(Press(w, Key.OemPlus) == "view.zoomIn", "Ctrl+= is view.zoomIn");
        await Idle(w, 250);
        Check(png.Viewer.ZoomPercent > fitZoom * 1.1 && !png.Viewer.IsFit, $"Ctrl+= zooms the picture ({fitZoom:0} → {png.Viewer.ZoomPercent:0})");
        var zoomed = png.Viewer.ZoomPercent;
        Press(w, Key.OemMinus);
        await Idle(w, 250);
        Check(png.Viewer.ZoomPercent < zoomed * 0.95, "Ctrl+- zooms out");
        Press(w, Key.D0);
        await Idle(w, 250);
        Check(png.Viewer.IsFit && Math.Abs(png.Viewer.ZoomPercent - fitZoom) < 0.6, $"Ctrl+0 fits ({png.Viewer.ZoomPercent:0})");
        Check(Math.Abs(SettingsService.I.Get<EditorSettings>().Zoom - 1) < 1e-6, "the editor zoom stayed untouched");
        png.StatusZoomClicked();
        await Idle(w, 250);
        Check(Math.Abs(png.Viewer.ZoomPercent - 100) < 0.6, "status zoom click → 100%");
        png.StatusZoomClicked();
        await Idle(w, 250);
        Check(png.Viewer.IsFit, "status zoom click again → fit");

        // rotate (an edit: the tab is unsaved), undo restores the clean state
        var pngDoc = (ImageDocument)png.Document;
        png.RotateBy(1);
        Check(png.Viewer.PixelSize == new Size(420, 640) && pngDoc.IsDirty, $"rotate swaps the size ({png.Viewer.PixelSize}) and marks the tab unsaved");
        png.Viewer.Undo();
        Check(png.Viewer.PixelSize == new Size(640, 420) && !pngDoc.IsDirty, "undo of the rotation: clean again");

        // copy goes to the test sink (never the real clipboard)
        ImagingModule.LastTestCopy = null;
        png.Copy();
        await Until(() => ImagingModule.LastTestCopy is not null);
        Check(ImagingModule.LastTestCopy?.GetDataPresent("PNG") == true, "copy image: PNG data");

        // the info panel: the Info tab's details for this file
        png.SetInfo(true);
        await Until(() => png.InfoPanel?.MetadataReady == true);
        Check(png.InfoShown && png.InfoPanel?.MetadataReady == true && png.Viewer.InfoOpen, "info panel shows the file's details");
        png.SetInfo(false);
        Check(!png.InfoShown && !png.Viewer.InfoOpen, "info panel closes");

        // the photo: decoded at display size, the full resolution only when zoomed in
        var photo = await Open(w, F("photo.jpg"));
        Check(photo?.Viewer.Info is { Width: 3000, Height: 2000, Format: "JPEG", HasMetadata: true }, $"JPEG info {photo?.Viewer.Info}");
        Check(photo is not null && (photo.Viewer.RasterScale < 0.999 || photo.Viewer.ProxySideForTest >= 3000),
            $"large JPEG decoded at display size (scale {photo?.Viewer.RasterScale:0.00}, display copy ≤ {photo?.Viewer.ProxySideForTest} px)");

        var gif = await Open(w, F("ball.gif"));
        Check(gif?.Viewer.Info?.Frames == 6 && gif.Viewer.IsAnimating, "animated GIF plays");
        Check(gif?.StatusText?.Contains("6 frames") == true, "status: frames " + gif?.StatusText);
        var bmp = await Open(w, F("chart.bmp"));
        Check(bmp?.Viewer.Info is { Width: 320, Height: 200, Format: "BMP" }, $"BMP info {bmp?.Viewer.Info}");
        var ico = await Open(w, F("app.ico"));
        Check(ico?.Viewer.Info is { Width: 256, Height: 256, Format: "ICO" }, $"ICO shows its largest size ({ico?.Viewer.Info})");
        var broken = await Open(w, F("broken.png"), ready: v => v.Viewer.Message is not null);
        Check(broken?.Viewer.Message == Loc.T("img.loadFailed") && !broken.Viewer.HasPicture, "a broken picture: a calm message, no crash");
        foreach (var v in new[] { gif, bmp, ico, broken, photo })
            if (v is not null) await Within(w.CloseTabAsync(w.Model.ActiveGroup, v.Document), "close " + v.Document.Title);

        // ── an edit: rectangle + text on the EXIF photo (orientation 6, camera, GPS) ──
        Step("an edit");
        var rotated = await Open(w, F("rotated.jpg"));
        if (rotated is null)
        {
            failures.Add("image: rotated.jpg did not open");
            return;
        }
        var doc = (ImageDocument)rotated.Document;
        var v1 = rotated.Viewer;
        Check(v1.Info is { Width: 200, Height: 400, Orientation: 6, HasMetadata: true }, $"EXIF orientation applied for display ({v1.Info})");
        v1.BeginEdit();
        Check(v1.IsEditing, "edit mode");
        Point S(double x, double y) => v1.ImageToStage(new Point(x, y));
        v1.SetTool(EditTool.Rectangle);
        v1.PointerDown(S(30, 60), 1, false);
        v1.PointerMove(S(120, 160), false);
        v1.PointerUp(S(170, 220), false);
        v1.SetTool(EditTool.Text);
        v1.PointerDown(S(40, 300), 1, false);
        v1.TypeText("Lab 3");
        Check(doc.Edits.Ops.Count == 2 && doc.IsDirty, $"rectangle + text recorded ({doc.Edits.Ops.Count}), tab unsaved");
        Check(doc.BasePath is { } basePath && File.Exists(basePath), "the file as it was is kept (base copy)");
        doc.FlushBackup();
        Check(File.Exists(ImageBackup.JournalFile(doc.Id)), "unsaved edits are on disk (journal)");

        // ── restart: the session's entry (path + id) gives the same document back, edits and undo included ──
        Step("restart");
        var sd = SessionService.I.Capture().Documents.FirstOrDefault(d => d.Id == doc.Id);
        Check(sd is not null && FileUtil.PathEquals(sd.Path, doc.FilePath), "the image tab is in the session");
        if (sd?.Path is not null)
        {
            var again = DocumentManager.I.CreateFor(sd.Path, sd.Id) as ImageDocument;
            Check(again is not null && again.IsDirty && again.Edits.Ops.Count == 2 && again.Edits.CanUndo && again.BasePath is not null,
                "restored after a restart: unsaved, both edits, undo, base copy");
            if (again is not null)
            {
                Check(again.Edits.Ops[1] is ShapeOp { Annotation.Text: "Lab 3" }, "the text came back");
                var a = await Within(ImageExport.RenderAsync(doc.SourcePath, doc.Edits.Ops), "render the live edits");
                var b = await Within(ImageExport.RenderAsync(again.SourcePath, again.Edits.Ops), "render the restored edits");
                Check(a is not null && b is not null && Hash(a) == Hash(b), "the restored picture is pixel for pixel the same");
                // not disposed: it shares the id (disposing would remove the live tab's backup)
            }
        }

        // ── Ctrl+S: same format, no EXIF / GPS, orientation in the pixels, the note once ──
        Step("Ctrl+S");
        v1.EndEdit();
        Check(await Within(w.SaveAsync(doc), "save the edited JPEG"), "save the edited JPEG");
        Step("saved; the note");
        Check(!doc.IsDirty && !File.Exists(ImageBackup.JournalFile(doc.Id)), "saved: clean, journal gone");
        var saved = Frame(F("rotated.jpg"));
        Check(saved.PixelWidth == 200 && saved.PixelHeight == 400, $"orientation baked into the pixels ({saved.PixelWidth}×{saved.PixelHeight})");
        Check(!HasExif(saved), "no EXIF / GPS in the saved file");
        Check(settings.MetadataNoticeShown, "the one-time note was remembered");
        Check(ImagingDialogs.MetadataNoticeOpen, "the note about metadata is shown");
        Check(await PressWhenShown(w, Loc.T("img.meta.ok")), "the note has its button");
        await Until(() => !w.DialogLayer.IsOpen);

        // undo after saving: unsaved again; redo: back to the saved state
        doc.Edits.Undo();
        Check(doc.IsDirty && doc.Edits.Ops.Count == 1, "undo after save → unsaved");
        doc.Edits.Redo();
        Check(!doc.IsDirty, "redo to the saved state → clean");

        // another save does not show the note again (the original here had metadata too: the base copy)
        v1.Rotate(1);
        Check(await Within(w.SaveAsync(doc), "second save"), "second save");
        await Idle(w, 150);
        Check(!ImagingDialogs.MetadataNoticeOpen && !w.DialogLayer.IsOpen, "the note shows only once");
        var twice = Frame(F("rotated.jpg"));
        Check(twice.PixelWidth == 400 && twice.PixelHeight == 200, "the second save rotated from the original");

        // "Save as" PNG (no dialog in test mode: the document's own save), then JPEG at two qualities
        Check(await Within(doc.SaveToAsync(F("rotated-copy.png"), 0), "save as PNG"), "save as PNG");
        Check(File.Exists(F("rotated-copy.png")) && FileUtil.PathEquals(doc.FilePath, F("rotated-copy.png")) && !doc.IsDirty, "the tab now shows the PNG");
        Check(Frame(F("rotated-copy.png")).Decoder is PngBitmapDecoder, "a real PNG");
        Check(await Within(doc.SaveWithQualityAsync(F("q95.jpg"), 95), "save as JPEG 95") && await Within(doc.SaveWithQualityAsync(F("q30.jpg"), 30), "save as JPEG 30"), "save as JPEG (two qualities)");
        Check(new FileInfo(F("q30.jpg")).Length < new FileInfo(F("q95.jpg")).Length, "JPEG quality is applied");
        Check(await Within(doc.SaveToAsync(F("small.ico"), 0), "save as ICO") && Frame(F("small.ico")) is { PixelWidth: <= 256, PixelHeight: <= 256 }, "save as an icon (≤ 256 px)");
        Check(await Within(doc.SaveToAsync(F("work.png"), 0), "save as PNG again") && !doc.IsDirty, "save as PNG again");
        await Idle(w, 200);
        w.Toasts.Children.Clear();

        // ── changed on disk: clean → read again quietly; unsaved → keep mine (the edits still on the original) ──
        Step($"changed on disk (window active: {w.IsActive})");
        File.Copy(F("logo.png"), doc.FilePath!, overwrite: true); // another program replaces the file (now 640 × 420)
        File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddMinutes(1));
        w.CheckExternalChanges([doc]);
        await Until(() => rotated.Viewer.Info is { Width: 640 } && doc.Edits.Ops.Count == 0);
        Check(!doc.IsDirty && doc.Edits.Ops.Count == 0 && rotated.Viewer.Info is { Width: 640, Height: 420 }, "clean tab: the new file shows (quiet reload)");
        rotated.RotateBy(1);
        Check(doc.IsDirty && doc.BasePath is not null, "edited again");
        ImagingFixtures.WriteRotatedJpeg(F("other.jpg"));
        File.Copy(F("other.jpg"), doc.FilePath!, overwrite: true);
        File.SetLastWriteTimeUtc(doc.FilePath!, DateTime.UtcNow.AddMinutes(2));
        w.CheckExternalChanges([doc]);
        Check(doc.External == ExternalChange.Modified && doc.IsDirty, "unsaved tab: the change is noticed, nothing reloaded");
        if (w.IsActive) Check(await PressWhenShown(w, Loc.T("external.keep")), "an active window asks: keep my version");
        else doc.KeepMine();
        await AnswerExternalPrompts();
        Check(doc.External == ExternalChange.None && doc.IsDirty && doc.Edits.Ops.Count == 1, "keep mine: still unsaved, edits kept");
        Check(await Within(w.SaveAsync(doc), "save over the changed file"), "save over the changed file");
        Check(Frame(doc.FilePath!) is { PixelWidth: 420, PixelHeight: 640 }, "saved from the original + rotation, not from the other program's file");

        // ── close an unsaved tab: "Don't save" → its backups go ──
        Step("close an unsaved tab");
        await AnswerExternalPrompts();
        rotated.RotateBy(1);
        doc.FlushBackup();
        var id = doc.Id;
        var closing = w.CloseTabAsync(w.Model.ActiveGroup, doc);
        Check(await PressWhenShown(w, Loc.T("close.discard")), "closing an unsaved image asks “Save?”");
        Check(await Within(closing, "close with Don't save"), "closed with Don't save");
        Check(!File.Exists(ImageBackup.JournalFile(id)) && !File.Exists(ImageBackup.BaseFile(id)), "no backup left after closing");

        // ── deleted while the app was closed: the session keeps the edited tab (marked deleted), keep mine → save recreates it ──
        Step("deleted while the app was closed");
        File.Copy(F("logo.png"), F("gone.png"), overwrite: true);
        var goneView = await Open(w, F("gone.png"));
        if (goneView?.Document is ImageDocument goneDoc)
        {
            goneView.RotateBy(1);
            goneDoc.FlushBackup();
            var entry = SessionService.I.Capture().Documents.FirstOrDefault(d => d.Id == goneDoc.Id);
            File.Delete(F("gone.png"));
            var kept = entry is null ? null : SessionService.RestoreDocument(entry) as ImageDocument;
            Check(kept is { IsDirty: true, External: ExternalChange.Deleted } && kept.Edits.Ops.Count == 1 && kept.BasePath is not null,
                "file deleted while the app was closed: the tab comes back with its edits, marked deleted");
            // (not disposed: it shares the live tab's id and backups)
            w.CheckExternalChanges([goneDoc]);
            Check(goneDoc.External == ExternalChange.Deleted && goneDoc.IsDirty, "deleted on disk: noticed");
            if (w.IsActive) Check(await PressWhenShown(w, Loc.T("external.keepInApp")), "an active window asks: keep in the app");
            else goneDoc.KeepMine();
            Check(await Within(w.SaveAsync(goneDoc), "save the kept picture") && Frame(F("gone.png")) is { PixelWidth: 420, PixelHeight: 640 }, "keep in the app → save writes the picture again");
            await AnswerExternalPrompts();
            await Within(w.CloseTabAsync(w.Model.ActiveGroup, goneDoc), "close gone.png");
        }
        else
        {
            failures.Add("image: gone.png did not open");
        }

        // ── an animated GIF edited and saved over itself: asked first (only one frame is written) ──
        Step("an animated GIF edited and saved over itself");
        await AnswerExternalPrompts();
        var gif2 = await Open(w, F("ball.gif"));
        if (gif2 is not null)
        {
            gif2.RotateBy(1);
            Check(!gif2.Viewer.IsAnimating, "an edited GIF shows the still being edited");
            var saving = w.SaveAsync(gif2.Document);
            Check(await PressWhenShown(w, Loc.T("img.save.framesOne")), "saving several frames as one asks first");
            Check(await Within(saving, "save one GIF frame"), "saved one frame");
            Check(BitmapDecoder.Create(new Uri(F("ball.gif")), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad).Frames.Count == 1, "the GIF now has one frame");
            await Within(w.CloseTabAsync(w.Model.ActiveGroup, gif2.Document), "close ball.gif");
        }

        // ── tray: the decoded picture goes, the edits stay, shown again → decoded again ──
        Step("tray");
        // (the first view of the PNG may have left the view cache meanwhile: take the one shown now)
        png = await Show(w, pngDoc);
        if (png is null)
        {
            failures.Add("image: the PNG tab is gone");
            return;
        }
        png.RotateBy(1);
        png.ReleaseRendering();
        Check(png.Viewer.IsReleased && !png.Viewer.HasPicture && pngDoc.Edits.Ops.Count == 1, "released for the tray (edits kept)");
        png.Visibility = Visibility.Collapsed;
        png.Visibility = Visibility.Visible;
        await Until(() => png.Viewer.HasPicture);
        Check(png.Viewer.HasPicture && png.Viewer.PixelSize == new Size(420, 640), "shown again: decoded with the edits");
        png.Viewer.Undo();

        // ── locked: no editing, no rotating ──
        Step("locked");
        pngDoc.IsLocked = true;
        Check(png.Viewer.ReadOnly, "a locked tab is read-only");
        png.Viewer.BeginEdit();
        png.Viewer.Rotate(1);
        Check(!png.Viewer.IsEditing && pngDoc.Edits.Ops.Count == 0, "no edits on a locked tab");
        pngDoc.IsLocked = false;
        await Idle(w, 100);
        w.Toasts.Children.Clear();

        // ── split: both views show the same edits ──
        Step("split");
        w.Split(System.Windows.Controls.Orientation.Horizontal);
        await Idle(w, 300);
        var second = w.ActiveView as ImageDocumentView;
        Check(second is not null && second != png && second.Document == pngDoc, "split: a second view of the picture");
        if (second is not null)
        {
            await Until(() => second.Viewer.HasPicture);
            png.RotateBy(1);
            Check(second.Viewer.PixelSize == new Size(420, 640), "the other view follows the edit");
            second.Viewer.Undo();
            Check(png.Viewer.PixelSize == new Size(640, 420) && !pngDoc.IsDirty, "undo in one view applies to both");
        }
        w.Unsplit();
        await Idle(w, 200);

        // ── pinned on top: compact, no editing ──
        Step("pinned on top");
        var g = w.Model.Groups.First(x => x.Tabs.Contains(pngDoc));
        w.PinOnTop(g, pngDoc);
        var model = Workspace.I.Windows.FirstOrDefault(m => m.Pinned && m.AllDocuments.Contains(pngDoc));
        var pinned = model is null ? null : App.Current.WindowFor(model);
        if (pinned is not null)
        {
            await Idle(pinned, 300);
            var pv = pinned.ActiveView as ImageDocumentView;
            Check(pv is not null && pv.Viewer.Compact, "pinned: compact image view");
            pv?.Viewer.BeginEdit();
            Check(pv is { Viewer.IsEditing: false }, "pinned: no editing");
            pinned.Toasts.Children.Clear();
            pinned.UnpinFromTop();
            await Idle(w, 200);
        }
        else
        {
            failures.Add("image: pinning did not open a window");
        }

        // ── Polish: the labels follow the language ──
        Step("Polish");
        appearance.Language = "pl";
        await Idle(w, 100);
        var back = w.Model.AllDocuments.Contains(pngDoc) ? pngDoc : null;
        if (back is not null)
        {
            w.Model.Groups.First(x => x.Tabs.Contains(back)).Active = back;
            await Idle(w, 150);
            Check((w.ActiveView as ImageDocumentView)?.StatusText?.Contains("640 × 420 px") == true, "status in Polish");
            await Within(w.CloseTabAsync(w.Model.Groups.First(x => x.Tabs.Contains(back)), back), "close logo.png");
        }
        appearance.Language = "en";
        w.Toasts.Children.Clear();
        if (before is not null && w.Model.AllDocuments.Contains(before)) group.Active = before;
        await Idle(w, 100);
    }

    private static async Task<ImageDocumentView?> Open(MainWindow w, string path, Func<ImageDocumentView, bool>? ready = null)
    {
        w.OpenFile(path);
        ready ??= v => v.Viewer.HasPicture;
        for (var i = 0; i < 160; i++)
        {
            if (w.ActiveView is ImageDocumentView v && FileUtil.PathEquals(v.Document.FilePath, path) && ready(v))
            {
                await Idle(w, 60);
                return v;
            }
            await Task.Delay(50);
        }
        return w.ActiveView as ImageDocumentView;
    }

    private static async Task<ImageDocumentView?> Show(MainWindow w, Document doc)
    {
        if (w.Model.Groups.FirstOrDefault(g => g.Tabs.Contains(doc)) is not { } g) return null;
        w.Model.ActiveGroup = g;
        g.Active = doc;
        for (var i = 0; i < 160; i++)
        {
            if (w.ActiveView is ImageDocumentView v && v.Document == doc && v.Viewer.HasPicture) return v;
            await Task.Delay(50);
        }
        return w.ActiveView as ImageDocumentView;
    }

    /// <summary>Clicks the open dialog's button with this text if there is one (no waiting).</summary>
    private static async Task<bool> PressIfShown(MainWindow w, string text)
    {
        var button = Descendants<System.Windows.Controls.Button>(w.DialogLayer).LastOrDefault(b => b.Content as string == text && b.IsVisible);
        if (button is null) return false;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(50);
        return true;
    }

    /// <summary>Waits for the in-window dialog's button with this text (an earlier dialog may still be fading out), then clicks it.</summary>
    private static async Task<bool> PressWhenShown(MainWindow w, string text)
    {
        for (var waited = 0; waited < 6000; waited += 50)
        {
            var button = Descendants<System.Windows.Controls.Button>(w.DialogLayer).LastOrDefault(b => b.Content as string == text && b.IsVisible);
            if (button is not null)
            {
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>Ctrl + key through the window's keymap, as a key press would go; returns the command run.</summary>
    private static string? Press(MainWindow w, Key key)
    {
        var ctx = w.Context();
        var cmd = Keymap.Match(new KeyChord(ModifierKeys.Control, key), ctx);
        cmd?.Execute(ctx);
        return cmd?.Id;
    }

    private static BitmapFrame Frame(string path) =>
        BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

    private static bool HasExif(BitmapFrame f)
    {
        try
        {
            return f.Metadata is BitmapMetadata md && (md.ContainsQuery("/app1") || md.ContainsQuery("/app1/ifd/gps") || md.GetQuery("System.Photo.Orientation") is not null);
        }
        catch
        {
            return false;
        }
    }

    private static string Hash(BitmapSource b)
    {
        var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var px = new byte[c.PixelWidth * c.PixelHeight * 4];
        c.CopyPixels(px, c.PixelWidth * 4, 0);
        return $"{c.PixelWidth}x{c.PixelHeight}:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(px));
    }

    private static async Task Idle(Window w, int ms)
    {
        await Task.Delay(ms);
        for (var i = 0; i < 2; i++) await Task.WhenAny(w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task, Task.Delay(2000));
        w.UpdateLayout();
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 6000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 50) await Task.Delay(50);
    }
}
#endif
